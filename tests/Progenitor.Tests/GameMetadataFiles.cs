using System;
using System.IO;
using System.Linq;

namespace Progenitor.Tests;

/// <summary>
/// Where the game's exported metadata lives on this machine, for the tests that compare
/// Progenitor's layout facts against it.
///
/// <para>The metadata is never committed. Its folder comes from one setting,
/// <c>PROGENITOR_METADATA_DIR</c>; when that is unset it is <c>_tools\interop-dump</c> beside the
/// repository (or up to four parents above it, for a worktree under <c>_build</c> or <c>.claude/worktrees</c>). Each side has
/// its own folder (<c>server</c>, <c>client</c>), and the C#-shaped type listing is the one
/// <c>.cs</c> file in it. <c>tools/deploy.ps1</c> resolves the same setting the same way.</para>
/// </summary>
internal static class GameMetadataFiles
{
    /// <summary>The environment variable naming the metadata folder.</summary>
    internal const string DirectoryVariable = "PROGENITOR_METADATA_DIR";

    /// <summary>
    /// The metadata folder: <see cref="DirectoryVariable"/> when set, otherwise
    /// <c>_tools\interop-dump</c> beside <paramref name="repoRoot"/>. Returned whether or not it exists.
    /// </summary>
    internal static string Directory(string repoRoot)
    {
        var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        string relative = Path.Combine("_tools", "interop-dump");
        string? dir = Path.GetDirectoryName(repoRoot);
        string first = Path.Combine(dir ?? repoRoot, relative);
        for (int i = 0; i < 4 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, relative);
            if (System.IO.Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return first;
    }

    /// <summary>
    /// The type listing for <paramref name="side"/> (<c>server</c> or <c>client</c>): the one
    /// <c>.cs</c> file in that side's folder, the largest if there are several. When the folder or
    /// file is missing, a path inside the side folder that does not exist, so callers can name
    /// what they looked for.
    /// </summary>
    internal static string TypeListing(string repoRoot, string side)
    {
        string sideDir = Path.Combine(Directory(repoRoot), side);
        if (System.IO.Directory.Exists(sideDir))
        {
            var listing = new DirectoryInfo(sideDir).GetFiles("*.cs")
                .OrderByDescending(f => f.Length)
                .FirstOrDefault();
            if (listing != null) return listing.FullName;
        }
        return Path.Combine(sideDir, "type-listing.cs");
    }
}
