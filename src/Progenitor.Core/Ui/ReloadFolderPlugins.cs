using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Install;
using Progenitor.PluginActions;

namespace Progenitor.Ui;

/// <summary>
/// FWALLOW1 (user, 2026-10-06): which plugin each DLL in <c>BepInEx/progenitor-reload</c> declares,
/// and the one removal the Mods page may make there for a plugin no install ledger lists — a sample,
/// an example or a build copied in by hand.
///
/// <para>The folder is read the way Bootstrap's reloader reads it: top-level <c>*.dll</c> only. Each
/// file's <c>[BepInPlugin]</c> GUIDs come from its metadata without loading it
/// (<see cref="PluginFileActions.TryReadPluginGuids"/>), cached by path, size and write time, so a
/// list build re-reads only files that changed. Strings only: no assembly, type or plugin instance
/// is kept, so nothing here can pin a collectible plugin generation.</para>
///
/// <para>A GUID two files declare is ambiguous and is left out of <see cref="Map"/>: nothing is
/// offered for it rather than a guess. A framework id (<see cref="ModMenuInventory.IsFrameworkId"/>)
/// is never removable, wherever its file is.</para>
/// </summary>
internal static class ReloadFolderPlugins
{
    /// <summary>Refusal: no file in the reload folder declares the plugin (or it was already removed).</summary>
    internal const string WhyNoFile = "No file in BepInEx/progenitor-reload declares this mod, so nothing was removed.";

    /// <summary>Refusal: two or more files declare the plugin.</summary>
    internal const string WhyAmbiguous = "More than one file in BepInEx/progenitor-reload declares this mod, so nothing was removed.";

    /// <summary>Refusal: the framework's own plugins are never removed from this menu.</summary>
    internal const string WhyFramework = "Progenitor needs this to run, so it is not removed from this menu.";

    /// <summary>Refusal: the file the page was built with is not the file the folder holds now.</summary>
    internal const string WhyChanged = "The mod's file in BepInEx/progenitor-reload changed since this page was opened, so nothing was removed. Open the page again.";

    /// <summary>Refusal: the path is outside the reload folder, not a DLL, or a link.</summary>
    /// <summary>Refusal: the file also declares other plugins and the request named only one.</summary>
    internal static string WhySharedFile(IReadOnlyList<string> others) =>
        "The same file in BepInEx/progenitor-reload also holds " + string.Join(", ", others) + ", so nothing was removed.";

    internal const string WhyUnsafe = "The mod's file is not a plain DLL directly inside BepInEx/progenitor-reload, so nothing was removed.";

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, DateTime Write, string[] Guids)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default GUID reader: the DLL's own metadata, never a load.</summary>
    internal static IReadOnlyList<string> ReadGuids(string path) =>
        PluginFileActions.TryReadPluginGuids(path, out var guids) ? guids : Array.Empty<string>();

    /// <summary>
    /// Every plugin GUID a top-level DLL in <paramref name="folder"/> declares, mapped to that DLL's
    /// full path. A GUID declared by more than one file is left out. Never throws.
    /// </summary>
    internal static Dictionary<string, string> Map(string? folder, Func<string, IReadOnlyList<string>>? guidsOf = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, guids) in Scan(folder, guidsOf))
            foreach (var guid in guids)
            {
                string id = (guid ?? "").Trim();
                if (id.Length == 0 || ambiguous.Contains(id)) continue;
                if (map.ContainsKey(id)) { map.Remove(id); ambiguous.Add(id); continue; }
                map[id] = file;
            }

        return map;
    }

    /// <summary>Each top-level DLL in <paramref name="folder"/> with the GUIDs it declares.</summary>
    private static List<(string File, IReadOnlyList<string> Guids)> Scan(
        string? folder, Func<string, IReadOnlyList<string>>? guidsOf)
    {
        var result = new List<(string, IReadOnlyList<string>)>();
        if (string.IsNullOrWhiteSpace(folder)) return result;
        string[] files;
        try
        {
            if (!Directory.Exists(folder)) return result;
            files = Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch { return result; }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in files)
        {
            string file;
            try { file = Path.GetFullPath(raw); }
            catch { continue; }
            result.Add((file, GuidsOf(file, guidsOf)));
        }

        return result;
    }

    private static IReadOnlyList<string> GuidsOf(string file, Func<string, IReadOnlyList<string>>? guidsOf)
    {
        if (guidsOf != null)
        {
            try { return guidsOf(file) ?? Array.Empty<string>(); }
            catch { return Array.Empty<string>(); }
        }

        long length;
        DateTime write;
        try
        {
            var info = new FileInfo(file);
            length = info.Length;
            write = info.LastWriteTimeUtc;
        }
        catch { return Array.Empty<string>(); }

        lock (Gate)
        {
            if (Cache.TryGetValue(file, out var hit) && hit.Length == length && hit.Write == write) return hit.Guids;
        }

        string[] read;
        try
        {
            var list = ReadGuids(file);
            read = new string[list.Count];
            for (int i = 0; i < list.Count; i++) read[i] = list[i];
        }
        catch { read = Array.Empty<string>(); }

        lock (Gate)
        {
            if (Cache.Count > 512) Cache.Clear();
            Cache[file] = (length, write, read);
        }

        return read;
    }

    /// <summary>Tests only: forget the cached reads.</summary>
    internal static void ResetForTests()
    {
        lock (Gate) Cache.Clear();
    }

    /// <summary>
    /// The other plugin GUIDs <paramref name="file"/> declares besides <paramref name="id"/>: removing
    /// the file removes them too, so the prompt names them.
    /// </summary>
    internal static List<string> AlsoDeclaredBy(string? file, string? id, Func<string, IReadOnlyList<string>>? guidsOf = null)
    {
        var others = new List<string>();
        if (string.IsNullOrWhiteSpace(file)) return others;
        foreach (var guid in GuidsOf(file!, guidsOf))
            if (!string.Equals(guid, id, StringComparison.OrdinalIgnoreCase) && !others.Contains(guid)) others.Add(guid);
        return others;
    }

    /// <summary>
    /// Is <paramref name="file"/> a plain <c>.dll</c> directly inside <paramref name="folder"/>
    /// (no subfolder, no <c>..</c>, not a link)? The one place the confinement is decided.
    /// </summary>
    internal static bool IsConfined(string? folder, string? file)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(file)) return false;
        try
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string full = Path.GetFullPath(file);
            if (!string.Equals(Path.GetExtension(full), ".dll", StringComparison.OrdinalIgnoreCase)) return false;
            string? dir = Path.GetDirectoryName(full);
            if (dir == null) return false;
            if (!string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(full)) return false;
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return false;
            var folderInfo = new DirectoryInfo(root);
            if ((folderInfo.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Delete the one DLL in <paramref name="folder"/> that declares <paramref name="id"/>. The folder
    /// is scanned again now: the file must still be the only one declaring the plugin, must be
    /// <paramref name="expectedFile"/> when one is given (the file the page was built with), and must
    /// pass <see cref="IsConfined"/>. Nothing else is touched — no sibling, no config, no ledger.
    /// Bootstrap's reloader sees the file gone at its next scan (about a second) and unloads the plugin
    /// with the <c>Removal</c> reason, which also ends a pause.
    /// </summary>
    internal static ServerInstallResult Remove(
        string? folder,
        string? id,
        string? expectedFile = null,
        Func<string, IReadOnlyList<string>>? guidsOf = null,
        bool refuseShared = false)
    {
        string plugin = (id ?? "").Trim();
        if (plugin.Length == 0) return ServerInstallResult.Refuse(WhyNoFile);
        if (ModMenuInventory.IsFrameworkId(plugin)) return ServerInstallResult.Refuse(WhyFramework);

        string? file = null;
        int declaring = 0;
        foreach (var (path, guids) in Scan(folder, guidsOf))
        {
            bool declares = false;
            foreach (var guid in guids)
                if (string.Equals((guid ?? "").Trim(), plugin, StringComparison.OrdinalIgnoreCase)) { declares = true; break; }
            if (!declares) continue;
            declaring++;
            file = path;
        }

        if (declaring == 0 || file == null) return ServerInstallResult.Refuse(WhyNoFile);
        if (declaring > 1) return ServerInstallResult.Refuse(WhyAmbiguous);
        if (!string.IsNullOrWhiteSpace(expectedFile))
        {
            string expected;
            try { expected = Path.GetFullPath(expectedFile); }
            catch { return ServerInstallResult.Refuse(WhyChanged); }
            if (!string.Equals(expected, file, StringComparison.OrdinalIgnoreCase)) return ServerInstallResult.Refuse(WhyChanged);
        }

        // Another framework plugin in the same file would go with it.
        foreach (var other in AlsoDeclaredBy(file, plugin, guidsOf))
            if (ModMenuInventory.IsFrameworkId(other)) return ServerInstallResult.Refuse(WhyFramework);

        // SRVUNINST1 review: a caller whose request named only this plugin (the server's remote uninstall)
        // refuses a file that also declares others, read in this same step, right before the delete.
        if (refuseShared)
        {
            var others = AlsoDeclaredBy(file, plugin, guidsOf);
            if (others.Count > 0) return ServerInstallResult.Refuse(WhySharedFile(others));
        }

        if (!IsConfined(folder, file)) return ServerInstallResult.Refuse(WhyUnsafe);

        try { File.Delete(file); }
        catch (Exception e) { return ServerInstallResult.Refuse(InstallModsCopy.RemoveFailed + ": " + e.Message); }

        lock (Gate) Cache.Remove(file);
        return new ServerInstallResult(
            true, "", new[] { file }, Array.Empty<string>(), noOp: false, hotUnloaded: true);
    }
}
