using System;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>
/// Folder names and the package extension the Thunderstore install paths share (moved here from the removed
/// server-to-client transfer, consent and promote files, 2026-10-05). Bootstrap owns the same names in
/// <c>Progenitor.Bootstrap.PendingInstalls</c>; a test pins that the two agree.
/// </summary>
internal static class ModFolders
{
    /// <summary><c>BepInEx/progenitor-staging/</c>: downloads are verified here before anything is written where it loads.</summary>
    public const string StagingFolderName = "progenitor-staging";

    /// <summary><c>BepInEx/progenitor-reload/</c>: the folder both reloaders scan once a second.</summary>
    public const string ReloadFolderName = "progenitor-reload";

    /// <summary><c>BepInEx/progenitor-pending/</c>: no reloader scans it; Bootstrap promotes it at the next start.</summary>
    public const string PendingFolderName = "progenitor-pending";

    /// <summary>Only a .NET assembly is a plugin image.</summary>
    public const string ExpectedExtension = ".dll";

    /// <summary>
    /// File names an install may never write, whatever id it claims (job FMAN21, R52 finding 5): the reloader loads
    /// every <c>*.dll</c> in its folder, so a package named for one of Progenitor's own images would replace the
    /// framework. Refused by name even when the file is absent.
    /// </summary>
    public static readonly string[] ReservedFileNames =
    {
        Progenitor.Reload.ReloadAllowStore.CoreImageFileName,
        "Progenitor.Bootstrap.dll",
        "Progenitor.Dev.dll",
    };

    public static bool IsReservedFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        for (int i = 0; i < ReservedFileNames.Length; i++)
            if (string.Equals(fileName, ReservedFileNames[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// Short display forms for keys, hashes and sizes, shared by the join screens, the deny report and the logs
/// (moved from the removed consent panel's text class, 2026-10-05).
/// </summary>
internal static class ModText
{
    /// <summary>Hex characters of a sha256 or key fingerprint shown on screen and in log lines.</summary>
    public const int ShaPrefixChars = 16;

    /// <summary>The operator key as the screens print it.</summary>
    public static string ShortKey(string? fingerprint)
    {
        string key = (fingerprint ?? "").Trim();
        if (key.Length == 0) return "(none)";
        return key.Length <= ShaPrefixChars ? key : key.Substring(0, ShaPrefixChars);
    }

    public static string ShaPrefix(string? sha256)
    {
        string hex = (sha256 ?? "").Trim().ToLowerInvariant();
        if (hex.Length == 0) return "(none)";
        return hex.Length <= ShaPrefixChars ? hex : hex.Substring(0, ShaPrefixChars);
    }

    /// <summary>Bytes as the screens show them. Invariant; a German keyboard reads the same string.</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "size unknown";
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        double kib = bytes / 1024.0;
        if (kib < 1024) return kib.ToString("0.#", CultureInfo.InvariantCulture) + " KiB";
        double mib = kib / 1024.0;
        return mib.ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
    }
}
