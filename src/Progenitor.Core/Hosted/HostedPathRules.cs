using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Hosted;

/// <summary>
/// Path-safety rules for hosted-server staging. Kept free of BepInEx and game types so the test
/// project can compile this file without interop assemblies.
///
/// The Start prefix used to fire for any path ending in <c>VRisingServer.exe</c>. Staging then
/// junctioned (or recursive-deleted and replaced) <c>BepInEx\plugins</c> under that folder. The
/// live dedicated server at <c>C:\VRisingServer</c> is a <c>VRisingServer.exe</c> too, so without
/// these checks the client would wipe a real operator's plugin tree. Every decision here is a
/// string-and-path test a unit test can exercise without touching disk or the game.
/// </summary>
public static class HostedPathRules
{
    /// <summary>
    /// Written inside a shared folder when the junction/symlink fallback copies files. Presence
    /// is the only thing that authorizes a recursive delete of a real directory: a leftover
    /// <c>plugins</c> folder we did not create must never be wiped.
    /// </summary>
    public const string StagedCopyMarkerName = ".progenitor-staged";

    /// <summary>
    /// Absolute root of the operator's live dedicated server. Any path equal to or under this
    /// (case-insensitive) is refused, even if it somehow also sits under the Steam game root.
    /// </summary>
    public const string ForbiddenLiveServerRoot = @"C:\VRisingServer";

    /// <summary>
    /// Client BepInEx folders that staging links (or copies) into the bundled server. A recursive
    /// delete is only considered for a directory whose name is in this list, under
    /// <c>serverDir\BepInEx</c>, and that already carries <see cref="StagedCopyMarkerName"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> SharedFolderNames = new[]
    {
        "core", "patchers", "unity-libs", "plugins", "progenitor-reload",
    };

    /// <summary>
    /// True only when <paramref name="serverDir"/> is a safe place to stage BepInEx for a hosted
    /// child process. The bundled server lives in a subdirectory of the Steam client install
    /// (typically <c>VRising_Server</c>). Staging into the game root itself, the client's BepInEx
    /// tree, anywhere outside the game root, or anything under
    /// <see cref="ForbiddenLiveServerRoot"/> would destroy operator or client data.
    /// </summary>
    /// <param name="serverDir">Folder that contains <c>VRisingServer.exe</c> (already resolved).</param>
    /// <param name="gameRoot">BepInEx <c>Paths.GameRootPath</c>.</param>
    /// <param name="clientBepInEx">BepInEx <c>Paths.BepInExRootPath</c>; extra refuse rules when set.</param>
    /// <param name="reason">Why the path was refused; empty on success.</param>
    public static bool TryValidateServerDirectory(
        string? serverDir,
        string? gameRoot,
        string? clientBepInEx,
        out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(serverDir))
            {
                reason = "server directory is empty";
                return false;
            }

            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                reason = "game root is empty; cannot verify the server directory is under the client install";
                return false;
            }

            string server;
            string root;
            try
            {
                server = Normalize(serverDir);
                root = Normalize(gameRoot);
            }
            catch (Exception e)
            {
                reason = "could not resolve paths: " + e.Message;
                return false;
            }

            if (IsForbiddenLiveServerPath(server))
            {
                reason =
                    $"refusing to stage into the live dedicated server path ({ForbiddenLiveServerRoot}): {server}";
                return false;
            }

            if (PathsEqual(server, root))
            {
                reason = $"refusing to stage into the client game root ({server})";
                return false;
            }

            if (!IsStrictlyUnder(server, root))
            {
                reason =
                    $"refusing to stage: server directory '{server}' is not strictly under the game root '{root}'";
                return false;
            }

            if (string.IsNullOrWhiteSpace(clientBepInEx))
                return true;

            string clientBep;
            try
            {
                clientBep = Normalize(clientBepInEx);
            }
            catch (Exception e)
            {
                reason = "could not resolve client BepInEx path: " + e.Message;
                return false;
            }

            var serverBep = Path.Combine(server, "BepInEx");
            if (PathsEqual(serverBep, clientBep))
            {
                reason = $"refusing to stage: server BepInEx would be the client's ({clientBep})";
                return false;
            }

            if (IsUnder(server, clientBep))
            {
                reason = $"refusing to stage inside the client's BepInEx ({clientBep})";
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            reason = "safety check threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is <see cref="ForbiddenLiveServerRoot"/> or a descendant,
    /// compared case-insensitively after <see cref="Normalize"/>. A sibling such as
    /// <c>C:\VRisingServerBackup</c> is not a match: the comparison uses a trailing separator so a
    /// prefix of the folder name cannot slip through.
    /// </summary>
    public static bool IsForbiddenLiveServerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Normalize(path);
            var forbidden = Normalize(ForbiddenLiveServerRoot);
            return PathsEqual(full, forbidden) || IsStrictlyUnder(full, forbidden);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when a real (non-reparse) directory may be <c>Directory.Delete(..., recursive: true)</c>
    /// so a link or a fresh copy can replace it. Reparse points are not this method's job: removing
    /// a junction does not delete the target. A real directory is only ours to wipe when Progenitor
    /// previously wrote <see cref="StagedCopyMarkerName"/> into it, it sits at
    /// <c>serverDir\BepInEx\{shared}</c>, and it is not under the live dedicated server.
    /// </summary>
    public static bool MayRecursiveDeleteRealDirectory(
        string? dest,
        string? serverDir,
        bool markerPresent,
        out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(dest) || string.IsNullOrWhiteSpace(serverDir))
            {
                reason = "path is empty";
                return false;
            }

            if (IsForbiddenLiveServerPath(dest) || IsForbiddenLiveServerPath(serverDir))
            {
                reason =
                    $"refusing to delete a path under the live dedicated server root ({ForbiddenLiveServerRoot}): {dest}";
                return false;
            }

            var destFull = Normalize(dest);
            var serverFull = Normalize(serverDir);
            if (PathsEqual(destFull, serverFull) || !IsUnder(destFull, serverFull))
            {
                reason = $"refusing to delete '{destFull}' which is not inside serverDir '{serverFull}'";
                return false;
            }

            var parent = Path.GetDirectoryName(destFull);
            var expectedParent = Path.Combine(serverFull, "BepInEx");
            var name = Path.GetFileName(destFull);
            if (parent == null || !PathsEqual(parent, expectedParent) || !IsSharedFolderName(name))
            {
                reason = $"refusing to delete unexpected path {destFull}";
                return false;
            }

            if (!markerPresent)
            {
                reason =
                    $"refusing to delete real directory {destFull} without {StagedCopyMarkerName} (not a previous Progenitor copy)";
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            reason = "delete-safety check threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether a shared folder on the bundled server already matches the current client tree, so
    /// staging can skip it. A leftover real directory with no marker must not count as staged:
    /// that is how a previous copy-fallback went stale, and how an operator's own <c>plugins</c>
    /// folder would be mistaken for our work.
    /// </summary>
    /// <param name="destExists">True when the destination path exists as a directory (including a junction).</param>
    /// <param name="destIsReparsePoint">True when the destination is a reparse point. Marker checks are skipped for those so we never read <see cref="StagedCopyMarkerName"/> through a junction into the client's tree.</param>
    /// <param name="destLinkPointsAtClientSource">True when the reparse target equals the client's shared folder.</param>
    /// <param name="markerPresent">True when <see cref="StagedCopyMarkerName"/> exists inside a real (non-reparse) destination.</param>
    /// <param name="markerContents">Bytes of the marker file, or null when absent.</param>
    /// <param name="expectedStamp">Current stamp token (core DLL + shim write times). A copy is current only when the marker equals this string.</param>
    public static bool SharedFolderLooksStaged(
        bool destExists,
        bool destIsReparsePoint,
        bool destLinkPointsAtClientSource,
        bool markerPresent,
        string? markerContents,
        string? expectedStamp)
    {
        if (!destExists) return false;
        if (destIsReparsePoint) return destLinkPointsAtClientSource;
        if (!markerPresent) return false;
        if (string.IsNullOrEmpty(expectedStamp)) return false;
        return string.Equals(markerContents, expectedStamp, StringComparison.Ordinal);
    }

    /// <summary>Absolute path of the copy-fallback marker inside <paramref name="sharedFolderDest"/>.</summary>
    public static string MarkerFilePath(string sharedFolderDest)
        => Path.Combine(sharedFolderDest, StagedCopyMarkerName);

    /// <summary>True when <paramref name="name"/> is one of <see cref="SharedFolderNames"/> (case-insensitive).</summary>
    public static bool IsSharedFolderName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var n in SharedFolderNames)
        {
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or a descendant. Used to
    /// bound writes to <c>serverDir</c> (the Doorstop shim sits next to the exe, so equality must
    /// count). Server-directory <em>selection</em> uses <see cref="IsStrictlyUnder"/> instead, so
    /// the game root itself cannot be chosen as the child-server folder.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        var full = Normalize(path);
        var fullRoot = Normalize(root);
        if (PathsEqual(full, fullRoot)) return true;
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="path"/> is a descendant of <paramref name="root"/>, not equal to
    /// it. The bundled server must live <em>inside</em> the Steam install (typically
    /// <c>GameRoot\VRising_Server</c>), never as the install folder itself.
    /// </summary>
    public static bool IsStrictlyUnder(string path, string root)
    {
        var full = Normalize(path);
        var fullRoot = Normalize(root);
        if (PathsEqual(full, fullRoot)) return false;
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Case-insensitive equality after <see cref="Normalize"/> (trailing separators ignored).</summary>
    public static bool PathsEqual(string? a, string? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Canonical absolute path for comparisons: <see cref="Path.GetFullPath"/>, optional
    /// <c>\\?\</c> prefix stripped, trailing separators trimmed. Does not follow junctions; the
    /// string the caller resolved is what we judge.
    /// </summary>
    public static string Normalize(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        var full = Path.GetFullPath(path);
        const string longPrefix = @"\\?\";
        if (full.StartsWith(longPrefix, StringComparison.OrdinalIgnoreCase))
            full = full.Substring(longPrefix.Length);
        // "C:\" must stay a root; trimming it to "C:" makes StartsWith checks lie.
        if (full.Length == 3 && full[1] == ':')
            return full;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
