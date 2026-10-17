using System;
using System.IO;
using System.Reflection;
using Progenitor.SelfUpdate;

namespace Progenitor.Bootstrap;

/// <summary>
/// Single-folder install: what Bootstrap places or tidies outside its own folder at start, after the uninstall check
/// and before Core is read. Two jobs, each one log line at most and neither ever thrown into <c>Load</c>:
/// <list type="number">
/// <item><b>The preloader.</b> The shipped <c>Progenitor.Preloader.patcher</c> beside Bootstrap is copied to
/// <c>BepInEx/patchers/Progenitor.Preloader.dll</c> when that copy is missing, older, or the same version with other
/// bytes (<see cref="InstallLayout.DecidePreloader"/>). The bytes go to a new temp file in <c>patchers</c> and are
/// renamed into place; an existing copy is renamed aside first (a loaded patcher can be renamed, not overwritten),
/// so no write ever lands in an existing file or through a hard link. BepInEx loaded the patchers before this
/// plugin, so the copy runs from the next start; this start simply runs without it.</item>
/// <item><b>The legacy Bootstrap.</b> After an upgrade from the three-location layout,
/// <c>BepInEx/plugins/Progenitor.Bootstrap.dll</c> is a second plugin with the same GUID. The chainloader keeps the
/// higher version and skips the other without loading it, so this (newer) Bootstrap deletes the skipped file and its
/// <c>.next</c>/<c>.prev</c> (<see cref="InstallLayout.DecideLegacyBootstrap"/>). The running image is never touched.</item>
/// </list>
/// A hosted child does neither: its <c>plugins</c> and <c>patchers</c> are junctions at the client's folders.
/// </summary>
internal static class LayoutAtStart
{
    /// <summary>Suffix an installed preloader is renamed to while its replacement moves in.</summary>
    internal const string AsideSuffix = ".prev";

    /// <summary>Suffix a legacy Bootstrap is renamed to when it cannot be deleted.</summary>
    internal const string RemovedSuffix = ".removed";

    /// <summary>Test seam: an image's assembly version from its metadata (nothing is loaded). Null when unreadable.</summary>
    internal static Func<string, Version?> ReadVersion { get; set; } = DefaultReadVersion;

    /// <summary>Both jobs. Never throws.</summary>
    internal static void Run(string bepInExRoot, string? bootstrapPath, bool hostedChild, Action<string>? info, Action<string>? warn)
    {
        if (hostedChild || string.IsNullOrWhiteSpace(bepInExRoot)) return;
        try
        {
            var line = RemoveLegacyBootstrap(bepInExRoot, bootstrapPath, hostedChild, out bool ok);
            if (line != null) Log(ok ? info : warn, line);
        }
        catch (Exception e) { Log(warn, "Layout: legacy Bootstrap cleanup failed: " + e.Message); }

        try
        {
            string? dir = string.IsNullOrEmpty(bootstrapPath) ? null : Path.GetDirectoryName(bootstrapPath);
            if (string.IsNullOrEmpty(dir)) return;
            var line = PlacePreloader(bepInExRoot, Path.Combine(dir!, InstallLayout.ShippedPreloaderFileName), out bool ok);
            if (line != null) Log(ok ? info : warn, line);
        }
        catch (Exception e) { Log(warn, "Layout: preloader placement failed: " + e.Message); }
    }

    /// <summary>
    /// Delete a legacy <c>BepInEx/plugins/Progenitor.Bootstrap.dll</c> the chainloader skipped. Returns the log line,
    /// or null when there was nothing to do. <paramref name="ok"/> is false when a file had to be left.
    /// </summary>
    internal static string? RemoveLegacyBootstrap(string bepInExRoot, string? runningPath, bool hostedChild, out bool ok)
    {
        ok = true;
        string legacy = InstallLayout.LegacyBootstrapPath(bepInExRoot);
        var action = InstallLayout.DecideLegacyBootstrap(bepInExRoot, runningPath, File.Exists(legacy), hostedChild);
        if (action != InstallLayout.LegacyBootstrapAction.Remove) return null;

        int removed = 0;
        string left = "";
        foreach (string path in new[] { legacy, legacy + ".next", legacy + ".prev" })
        {
            if (!File.Exists(path)) continue;
            if (TryDelete(path)) { removed++; continue; }
            // Not deletable (another tool holds it): renamed out of the *.dll scan instead.
            string aside = path + RemovedSuffix;
            try
            {
                if (File.Exists(aside)) File.Delete(aside);
                File.Move(path, aside);
                removed++;
            }
            catch (Exception e)
            {
                ok = false;
                left = Path.GetFileName(path) + " (" + e.Message + ")";
            }
        }

        if (!ok)
            return "Layout: could not remove the old " + legacy + ": " + left
                + ". Delete it by hand; this start runs " + (runningPath ?? "") + ".";
        return "Layout: removed the old " + legacy + " (" + removed + " file(s)); this start runs "
            + (runningPath ?? "") + ".";
    }

    /// <summary>
    /// Copy the shipped preloader to <c>BepInEx/patchers</c> when <see cref="InstallLayout.DecidePreloader"/> says so.
    /// Returns the log line, or null when nothing changed. <paramref name="ok"/> is false on a failed placement.
    /// </summary>
    internal static string? PlacePreloader(string bepInExRoot, string shippedPath, out bool ok)
    {
        ok = true;
        string dest = InstallLayout.PatchersPreloaderPath(bepInExRoot);
        string aside = dest + AsideSuffix;
        // A copy set aside at an earlier start is no longer loaded by anything.
        if (File.Exists(aside)) TryDelete(aside);

        bool shippedExists = File.Exists(shippedPath);
        Version? shippedVersion = shippedExists ? SafeVersion(shippedPath) : null;
        bool installedExists = File.Exists(dest);
        Version? installedVersion = installedExists ? SafeVersion(dest) : null;
        byte[]? shipped = null;
        bool sameBytes = false;
        if (shippedExists && shippedVersion != null)
        {
            shipped = File.ReadAllBytes(shippedPath);
            if (installedExists)
            {
                try { sameBytes = BytesEqual(shipped, File.ReadAllBytes(dest)); }
                catch { sameBytes = false; }
            }
        }

        var action = InstallLayout.DecidePreloader(shippedExists, shippedVersion, installedExists, installedVersion, sameBytes);
        if (action == InstallLayout.PreloaderAction.Keep) return null;
        if (action == InstallLayout.PreloaderAction.NoSource)
        {
            if (!shippedExists) return null;
            ok = false;
            return "Layout: " + shippedPath + " is not a readable assembly; " + dest + " was left as it is.";
        }

        string dir = Path.GetDirectoryName(dest)!;
        Directory.CreateDirectory(dir);
        string tmp = dest + ".tmp-" + Guid.NewGuid().ToString("N");
        bool movedAside = false;
        try
        {
            File.WriteAllBytes(tmp, shipped!);
            if (installedExists)
            {
                File.Move(dest, aside);
                movedAside = true;
            }
            File.Move(tmp, dest);
        }
        catch (Exception e)
        {
            ok = false;
            TryDelete(tmp);
            if (movedAside && !File.Exists(dest))
            {
                try { File.Move(aside, dest); }
                catch { /* the next start places it again */ }
            }
            return "Layout: could not place " + dest + " (" + e.Message + "); the next start tries again.";
        }

        if (movedAside) TryDelete(aside);
        string version = shippedVersion!.ToString();
        return action == InstallLayout.PreloaderAction.Place
            ? "Layout: placed " + dest + " " + version + "; the preloader runs from the next start."
            : "Layout: updated " + dest + " " + (installedVersion?.ToString() ?? "(unreadable)") + " -> " + version
                + "; the new copy runs from the next start.";
    }

    private static Version? SafeVersion(string path)
    {
        try { return ReadVersion(path); }
        catch { return null; }
    }

    private static Version? DefaultReadVersion(string path) => AssemblyName.GetAssemblyName(path).Version;

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch { return false; }
    }

    private static void Log(Action<string>? sink, string line)
    {
        try { sink?.Invoke(line); }
        catch { /* a log is not a gate */ }
    }
}
