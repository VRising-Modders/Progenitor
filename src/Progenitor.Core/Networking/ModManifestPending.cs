using System.IO;
#if !PROGENITOR_TESTS
using BepInEx;
#endif

namespace Progenitor.Networking;

/// <summary>
/// <b>The folder no reloader watches</b> (job FMAN4, R35 finding 1) — Core's side of it: where a
/// package that loads only at the next start waits. Thunderstore installs that cannot hot-load are
/// written here; Bootstrap's <c>PendingInstalls.PromoteAtStart</c> moves them into
/// <c>BepInEx/progenitor-reload/</c> at the next process start, before the first scan. Neither
/// reloader enumerates this tree.
///
/// <para>The per-host subfolders (<see cref="HostFolder"/>) are where an older Core put packages a
/// server sent; servers no longer send mod files (2026-10-05), and <c>LegacyServerMods</c> removes
/// what is left of them at Core start. Core's mirror of Bootstrap's promote went with it: the tests
/// drive Bootstrap's own <c>PendingInstalls</c>.</para>
/// </summary>
public static class ModManifestPending
{
    /// <summary>The pending folder's name. Deliberately not the reload folder.</summary>
    public const string PendingFolderName = ModFolders.PendingFolderName;

#if PROGENITOR_TESTS
    /// <summary>Test override for the pending folder. Never used live.</summary>
    internal static string? TestPendingDirectory;
#endif

    /// <summary><c>BepInEx/progenitor-pending/</c>.</summary>
    public static string PendingDirectory
    {
        get
        {
#if PROGENITOR_TESTS
            return TestPendingDirectory ?? Path.Combine(ModManifest.ConfigDirectory, PendingFolderName);
#else
            try { return Path.Combine(Paths.BepInExRootPath, PendingFolderName); }
            catch { return Path.Combine(ModManifest.ConfigDirectory, PendingFolderName); }
#endif
        }
    }

    /// <summary>The per-server subfolder an older Core wrote a server's package to.</summary>
    public static string HostFolder(string? hostKey) =>
        Path.Combine(PendingDirectory, ModManifestIds.NormalizeHostKey(hostKey));
}
