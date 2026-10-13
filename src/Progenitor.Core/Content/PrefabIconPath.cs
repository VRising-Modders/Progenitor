using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Game-free checks for a plugin-supplied item or buff icon path. <c>Icon</c> stores a
/// path relative to the plugin's <c>assets</c> folder; the client applicator turns that into a
/// <c>Sprite</c>. Absolute paths and <c>..</c> are refused so a definition cannot read outside
/// the probed asset root. The same helpers format the one log line the live apply path emits.
/// </summary>
internal static class PrefabIconPath
{
    /// <summary>Logged when the PNG is not on disk (or the asset root could not be probed).</summary>
    public const string FileMissingReason = "file missing";

    /// <summary>Logged when <c>ImageConversion.LoadImage</c> returns false.</summary>
    public const string DecodeFailedReason = "decode failed";

    /// <summary>
    /// Logged when the clone has no client <c>ShowOnBuffBarData</c> (or an empty
    /// <c>Entries</c> list). The source must be a HUD-visible buff.
    /// </summary>
    public const string NoBuffBarDataReason = "no ShowOnBuffBarData on the clone";

    /// <summary>Logged when <c>&lt;dir&gt;/&lt;AssemblyName&gt;/assets</c> and the sibling <c>assets</c> are both missing.</summary>
    public const string AssetRootMissingReason = "asset root missing";

    /// <summary>
    /// Logged when the PNG cannot be read (asset root missing / file missing) but Bootstrap
    /// still has a live Sprite for this plugin+path from an earlier apply.
    /// </summary>
    public const string FromCacheReason = "from cache";

    /// <summary>
    /// Forward slashes, trimmed. Stored on the definition so the client log line matches
    /// what the author wrote in <c>Icon(...)</c>.
    /// </summary>
    public static string Normalize(string relativePath)
    {
        if (relativePath == null) throw new ArgumentNullException(nameof(relativePath));
        return relativePath.Trim().Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="relativePath"/> is empty,
    /// absolute, or contains a <c>..</c> segment. Same exception type as <c>CloneOf(string)</c>.
    /// </summary>
    public static void Validate(string relativePath, string paramName)
    {
        if (!TryValidate(relativePath, out var error))
            throw new ArgumentException(error, paramName);
    }

    /// <summary>
    /// Relative-path gate used by the builder and by the file combine so a crafted
    /// <c>Icon</c> string cannot walk out of the plugin asset root.
    /// </summary>
    public static bool TryValidate(string? relativePath, out string error)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "icon path is required and must be relative to the plugin assets folder.";
            return false;
        }

        var trimmed = relativePath.Trim();
        if (Path.IsPathRooted(trimmed) || StartsWithSeparator(trimmed))
        {
            error = "icon path must be relative (no absolute paths).";
            return false;
        }

        var normalized = trimmed.Replace('\\', '/');
        var parts = normalized.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == "..")
            {
                error = "icon path must not contain '..'.";
                return false;
            }
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Join <paramref name="assetRoot"/> and a validated relative path, then confirm the
    /// full path still lives under the root (defense in depth after <see cref="TryValidate"/>).
    /// </summary>
    public static bool TryCombine(string assetRoot, string relativePath, out string fullPath, out string error)
    {
        fullPath = "";
        error = "";
        if (string.IsNullOrEmpty(assetRoot))
        {
            error = AssetRootMissingReason;
            return false;
        }
        if (!TryValidate(relativePath, out error))
            return false;

        string combined;
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(assetRoot);
            combined = Path.GetFullPath(Path.Combine(assetRoot, relativePath.Trim()));
        }
        catch (Exception e)
        {
            error = FileMissingReason + " (" + e.Message + ")";
            return false;
        }

        var prefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            error = "icon path must be relative (no absolute paths).";
            return false;
        }

        fullPath = combined;
        error = "";
        return true;
    }

    /// <summary>
    /// Client-only, buff-only, and only when the definition stored a path. The same plugin
    /// DLL runs on the client and registers the same <c>DefineBuff</c>, so the path does not
    /// travel on <c>PrfD</c>.
    /// </summary>
    public static bool ShouldApply(bool isServerProcess, PrefabKind kind, string? iconPath) =>
        !isServerProcess && kind == PrefabKind.Buff && !string.IsNullOrEmpty(iconPath);

    /// <summary>Items write their separate cloned ManagedItemData, never buff HUD entries.</summary>
    public static bool ShouldApplyItem(bool isServerProcess, PrefabKind kind, string? iconPath) =>
        !isServerProcess && kind == PrefabKind.Item && !string.IsNullOrEmpty(iconPath);

    /// <summary>Live success line grepped from the client log.</summary>
    public static string FormatApplied(string path, int entries, string id) =>
        "PrefabRegistry: icon '" + path + "' -> ShowOnBuffBarData entries=" + entries + " on '" + id + "'";

    /// <summary>
    /// Swap-time line: the clone was written from the Bootstrap cache because the
    /// plugin asset root was not on disk yet. Grep <c>icon '</c> + <c>from cache</c>.
    /// </summary>
    public static string FormatFromCache(string path, string id) =>
        "PrefabRegistry: icon '" + path + "' from cache on '" + id + "'";

    /// <summary>
    /// Bootstrap cache key: plugin id, normalized relative path, SHA-256 of the PNG
    /// bytes. Must match <c>Progenitor.Bootstrap.IconCachePolicy.FormatKey</c>.
    /// </summary>
    public static string FormatCacheKey(string? pluginId, string relativePath, byte[] bytes)
    {
        string id = string.IsNullOrWhiteSpace(pluginId) ? "unowned" : pluginId.Trim();
        return id + "\n" + Normalize(relativePath) + "\n" + HashPng(bytes);
    }

    /// <summary>Lowercase hex SHA-256. Same encoding Bootstrap <c>IconCachePolicy.HashBytes</c> uses.</summary>
    public static string HashPng(byte[]? bytes)
    {
        bytes ??= Array.Empty<byte>();
        byte[] hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Live failure line: file missing, decode failed, or no ShowOnBuffBarData.</summary>
    public static string FormatFailed(string path, string reason, string id)
    {
        if (string.Equals(reason, NoBuffBarDataReason, StringComparison.Ordinal))
            return "PrefabRegistry: icon '" + path + "' " + reason + " '" + id + "'";
        return "PrefabRegistry: icon '" + path + "' " + reason + " on '" + id + "'";
    }

    private static bool StartsWithSeparator(string path) =>
        path.Length > 0 && (path[0] == '/' || path[0] == '\\');
}
