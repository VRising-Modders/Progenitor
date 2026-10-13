using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Install;

/// <summary>
/// FIX-NONMODS (live 2026-10-03): listings the Install tab never installs although Thunderstore does
/// not tag them <c>Tools</c> — mod manager applications, and packages too large for the download
/// limit. Data-driven like <see cref="LoaderPackages"/>: one constant list of package full names
/// (owner-name, from the index's own metadata), never a display name; and the listing's published
/// <c>file_size</c> against the fetcher's ceiling.
///
/// <para>Live: <c>ebkr-r2modman</c> (categories <c>Gloomrot Update</c>, <c>1.0 Update</c>, so not a
/// tool by tag; 363,105,843 bytes) reached the confirmation, and the download ran up to the 32 MiB
/// ceiling before it failed with "the download exceeded the size ceiling".</para>
/// </summary>
internal static class NonModPackages
{
    /// <summary>
    /// Mod manager applications by Thunderstore full name. <c>ebkr-r2modman</c> is in V Rising's index
    /// (checked in the client's cached index, 2026-10-03: the only listing whose description calls it
    /// a mod manager); <c>Kesomannen-GaleModManager</c> is the other Thunderstore manager and is listed
    /// for when it appears. Add a name here rather than teaching a pane about a package.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownModManagers = new[]
    {
        "ebkr-r2modman",
        "Kesomannen-GaleModManager",
    };

    /// <summary>Is this listing a mod manager application (by its owner-name full name)?</summary>
    public static bool IsModManager(string? owner, string? packageName, string? key)
    {
        string full = !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(packageName)
            ? owner!.Trim() + "-" + packageName!.Trim()
            : "";
        for (int i = 0; i < KnownModManagers.Count; i++)
        {
            string known = KnownModManagers[i];
            if (string.Equals(full, known, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(key) && string.Equals(key!.Trim(), known, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// LIMITS1: the package download ceiling a listing is judged against and the download stops at —
    /// <c>[Install] MaxDownloadMB</c> (default 128, clamped 8–1024), in bytes.
    /// </summary>
    public static long DownloadLimit => ClampMb(ResolveMb()) * 1024L * 1024L;

    /// <summary>Tests: the MB value in place of the config file (null: the config or the default).</summary>
    internal static int? TestMaxDownloadMb { get; set; }

    /// <summary>The MiB value clamped to <c>8..1024</c>.</summary>
    public static int ClampMb(int mb) =>
        mb < Config.ConfigHelper.MaxDownloadMbMin ? Config.ConfigHelper.MaxDownloadMbMin
        : mb > Config.ConfigHelper.MaxDownloadMbMax ? Config.ConfigHelper.MaxDownloadMbMax
        : mb;

    private static int ResolveMb()
    {
        if (TestMaxDownloadMb is int test) return test;
        int? bound = Config.ConfigHelper.MaxDownloadMbValue;
        if (bound is int value) return value;
#if !PROGENITOR_TESTS
        try
        {
            var file = Progenitor.Config.SharedConfig.TryGetOrLog("[" + Config.ConfigHelper.InstallSection + "] " + Config.ConfigHelper.MaxDownloadMbKey);
            if (file != null) return Config.ConfigHelper.BindMaxDownloadMb(file).Value;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: binding [Install] MaxDownloadMB threw (" + e.Message + "); the default stands.");
        }
#endif
        return Config.ConfigHelper.MaxDownloadMbDefault;
    }

    /// <summary>Does the listing's published package exceed <paramref name="limit"/>? Unknown sizes never do.</summary>
    public static bool TooLarge(long? listingFileSize, long limit) =>
        listingFileSize is long size && size > 0 && limit > 0 && size > limit;

    /// <summary><c>346 MB</c>: bytes in binary megabytes, whole numbers (one decimal under 10 MB).</summary>
    public static string Megabytes(long bytes)
    {
        double mb = bytes / (1024.0 * 1024.0);
        return (mb < 10 ? mb.ToString("0.#", CultureInfo.InvariantCulture) : Math.Round(mb).ToString("0", CultureInfo.InvariantCulture)) + " MB";
    }

    /// <summary>The row's words: <c>Too large to install (346 MB; limit 32 MB)</c>.</summary>
    public static string TooLargeText(long size, long limit) =>
        "Too large to install (" + Megabytes(size) + "; limit " + Megabytes(limit) + ")";

    /// <summary>The drawer's and the refusal's sentence.</summary>
    public static string TooLargeReason(long size, long limit) =>
        "Its published package is " + Megabytes(size) + ", over the " + Megabytes(limit)
        + " download limit, so this menu does not download it. Install it another way.";

    public const string ModManagerText = "Mod manager app, not a game mod";

    public const string ModManagerReason =
        "This is a mod manager application, not a game mod. It runs outside the game; get it from its own site.";
}
