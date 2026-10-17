using System;
using System.IO;
using Progenitor.SelfUpdate;

namespace Progenitor.PluginActions;

/// <summary>
/// Single-folder install: Bootstrap places this patcher in <c>BepInEx/patchers</c> from the copy it ships, so the
/// patcher must not outlive Progenitor. It needs no other Progenitor file to run; when no
/// <c>Progenitor.Bootstrap.dll</c> is left anywhere under <c>BepInEx/plugins</c> (the player deleted the Progenitor
/// folder), it does nothing at all and renames itself to <c>Progenitor.Preloader.dll.removed</c>, out of BepInEx's
/// <c>*.dll</c> scan, so the start after that runs without it. A Bootstrap that comes back places it again.
/// Only the copy Bootstrap places (<c>BepInEx/patchers/Progenitor.Preloader.dll</c>) is ever renamed.
/// </summary>
internal static class ProgenitorPresence
{
    /// <summary>Suffix the retired patcher is renamed to.</summary>
    internal const string RetiredSuffix = ".removed";

    /// <summary>True when a <c>Progenitor.Bootstrap.dll</c> exists anywhere under <c>BepInEx/plugins</c>.</summary>
    internal static bool BootstrapInstalled(string bepInExRoot)
    {
        try
        {
            string plugins = Path.Combine(bepInExRoot, InstallLayout.PluginsFolderName);
            if (!Directory.Exists(plugins)) return false;
            foreach (string _ in Directory.EnumerateFiles(plugins, InstallLayout.BootstrapFileName, SearchOption.AllDirectories))
                return true;
            return false;
        }
        catch
        {
            // Unreadable: assume Progenitor is there, so nothing is retired on a guess.
            return true;
        }
    }

    /// <summary>True when <paramref name="ownPath"/> is the copy Bootstrap places in <c>BepInEx/patchers</c>.</summary>
    internal static bool IsPlacedCopy(string bepInExRoot, string? ownPath) =>
        !string.IsNullOrWhiteSpace(bepInExRoot)
        && InstallLayout.SamePath(ownPath, InstallLayout.PatchersPreloaderPath(bepInExRoot));

    /// <summary>
    /// The no-Progenitor path: the line to log, after renaming the placed copy aside. Never throws.
    /// </summary>
    internal static string Retire(string bepInExRoot, string? ownPath)
    {
        if (!IsPlacedCopy(bepInExRoot, ownPath))
            return "Progenitor preloader: Progenitor is not installed (no Progenitor.Bootstrap.dll under BepInEx/plugins); nothing was done.";
        string own = ownPath!;
        try
        {
            string aside = own + RetiredSuffix;
            if (File.Exists(aside)) File.Delete(aside);
            File.Move(own, aside);
            return "Progenitor preloader: Progenitor is not installed (no Progenitor.Bootstrap.dll under BepInEx/plugins); "
                + "nothing was done and this patcher was renamed to " + Path.GetFileName(aside) + ", so it no longer runs.";
        }
        catch (Exception e)
        {
            return "Progenitor preloader: Progenitor is not installed (no Progenitor.Bootstrap.dll under BepInEx/plugins); "
                + "nothing was done. Delete " + own + " (" + e.Message + ").";
        }
    }
}
