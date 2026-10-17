using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Progenitor.Bootstrap;

/// <summary>What one honored (or refused) uninstall request did.</summary>
internal sealed class UninstallOutcome
{
    internal UninstallOutcome(bool requested, bool honoured, int deleted, int left, string reason)
    {
        Requested = requested;
        Honoured = honoured;
        Deleted = deleted;
        Left = left;
        Reason = reason ?? "";
    }

    /// <summary>Was a marker present at all?</summary>
    internal bool Requested { get; }

    /// <summary>True when the request was applied. False with <see cref="Requested"/> true is a refusal.</summary>
    internal bool Honoured { get; }

    internal int Deleted { get; }

    /// <summary>Listed files that are still there (locked, or scheduled for the next reboot).</summary>
    internal int Left { get; }

    /// <summary>The refusal, or the one line that was logged.</summary>
    internal string Reason { get; }
}

/// <summary>
/// MODS2: Bootstrap's half of <c>Uninstall Progenitor from this client</c>. Core writes
/// <c>BepInEx/progenitor-uninstall.json</c> while the game runs (it cannot delete the files it is
/// running from); <see cref="Honour"/> is called from <c>Plugin.Load</c> <em>before</em>
/// <c>CoreLoader</c> and before any Harmony patch, deletes what the marker lists, empties the
/// Progenitor-owned folders, schedules Bootstrap's own DLL for deletion at the next reboot, writes
/// <c>progenitor-uninstall.done</c>, logs one line and returns — the caller then returns from
/// <c>Load</c> without loading Core.
///
/// <para>This is the mirror of Core's <c>Progenitor.Install.ClientUninstall</c> (Bootstrap and
/// Core cannot reference each other). Both sides read the same schema and both check every path:
/// a marker that names anything outside the BepInEx root, or anything under BepInEx's own
/// <c>core/</c>, is refused <em>whole</em> — nothing is deleted. No path outside the BepInEx root
/// is ever touched.</para>
/// </summary>
internal static class UninstallAtStart
{
    internal const int CurrentSchema = 1;
    internal const string FileName = "progenitor-uninstall.json";
    internal const string DoneFileName = "progenitor-uninstall.done";
    internal const string LoaderCoreFolder = "core";
    internal const string BootstrapDllName = "Progenitor.Bootstrap.dll";
    internal const string PluginsFolder = "plugins";

    /// <summary>The single-folder home under <see cref="PluginsFolder"/> (<c>InstallLayout.HomeFolderName</c>).</summary>
    internal const string HomeFolderName = "Progenitor";

    internal const string LogPrefix = "Progenitor uninstall: ";
    internal const string RefusalOutsideRoot = "the request named a path outside BepInEx; nothing was removed";
    internal const string RefusalUnreadable = "the request could not be read; nothing was removed";

    /// <summary>Folders under the BepInEx root that are Progenitor's alone and are emptied.</summary>
    internal static readonly IReadOnlyList<string> OwnedFolders = new[]
    {
        "progenitor-core",
        "progenitor-pending",
        "progenitor-reload",
        "progenitor-cache",
        "progenitor-staging",
        "progenitor-hosted",
    };

    /// <summary>
    /// Test seam: schedule a file for deletion at the next reboot. Production is
    /// <c>MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT)</c>.
    /// </summary>
    internal static Func<string, bool>? ScheduleDeleteOverride { get; set; }

    internal static Func<DateTime>? ClockUtc { get; set; }

    /// <summary>
    /// Honor a pending request, if there is one. Returns true when the caller must stop: the
    /// request was present and was applied, so Core must not load into a half-removed install.
    /// A refusal returns false, leaves the marker in place and loads normally.
    /// </summary>
    internal static bool HonourIfRequested(string? bepInExRoot, Action<string>? log = null)
    {
        var outcome = Honour(bepInExRoot, log);
        return outcome.Honoured;
    }

    /// <summary>
    /// The whole removal, over one BepInEx root. Never throws: a start path that cannot delete a
    /// file reports it rather than failing the process.
    /// </summary>
    internal static UninstallOutcome Honour(string? bepInExRoot, Action<string>? log = null)
    {
        string root = (bepInExRoot ?? "").Trim();
        if (root.Length == 0) return new UninstallOutcome(false, false, 0, 0, "");
        string marker;
        try
        {
            marker = Path.Combine(root, FileName);
            if (!File.Exists(marker)) return new UninstallOutcome(false, false, 0, 0, "");
        }
        catch
        {
            return new UninstallOutcome(false, false, 0, 0, "");
        }

        if (!TryReadRequest(marker, out var files, out var requestedUtc))
            return Refuse(log, RefusalUnreadable);

        // One bad path refuses the whole request: a partial removal is worse than none.
        for (int i = 0; i < files.Count; i++)
        {
            if (!IsAllowedRelativePath(files[i])) return Refuse(log, RefusalOutsideRoot);
            if (!IsUnderRoot(root, files[i])) return Refuse(log, RefusalOutsideRoot);
        }

        int deleted = 0, left = 0;
        string markerRelative = FileName;
        for (int i = 0; i < files.Count; i++)
        {
            string rel = Normalize(files[i]);
            // The marker is deleted last, after the done note is written.
            if (string.Equals(rel, markerRelative, StringComparison.OrdinalIgnoreCase)) continue;
            string abs = Absolute(root, rel);
            if (IsBootstrapDll(rel))
            {
                // This process is running from it: delete-on-reboot is the only honest answer, and
                // the file is still on disk either way, so it counts as left rather than deleted.
                // PR #48 review L6: it is renamed out of the *.dll scan first (a loaded file can be
                // renamed), so the next start does not load it even when the reboot delete is refused.
                string aside = abs + ".removed";
                string scheduled = TryRenameAside(abs) ? aside : abs;
                ScheduleDelete(scheduled);
                left++;
                continue;
            }

            if (!Exists(abs)) { deleted++; continue; }
            TryDelete(abs);
            // The preloader is loaded by now, so it cannot be deleted; renamed out of the *.dll scan
            // it no longer runs from the next start.
            if (Exists(abs) && IsPatcherDll(rel)) TryRenameAside(abs);
            if (Exists(abs)) left++;
            else deleted++;
        }

        for (int i = 0; i < OwnedFolders.Count; i++)
            RemoveEmptyTree(Path.Combine(root, OwnedFolders[i]));
        // The single-folder home (BepInEx/plugins/Progenitor), emptied once its files are gone.
        RemoveEmptyTree(Path.Combine(root, PluginsFolder, HomeFolderName));

        WriteDone(root, requestedUtc, deleted, left);
        TryDelete(Path.Combine(root, FileName));

        string line = LogPrefix + "removed " + deleted.ToString(CultureInfo.InvariantCulture)
            + " files, " + left.ToString(CultureInfo.InvariantCulture)
            + " left for the next reboot; Core was not loaded.";
        Log(log, line);
        return new UninstallOutcome(true, true, deleted, left, line);
    }

    private static UninstallOutcome Refuse(Action<string>? log, string reason)
    {
        Log(log, LogPrefix + "refused: " + reason + ".");
        return new UninstallOutcome(true, false, 0, 0, reason);
    }

    private static void Log(Action<string>? log, string line)
    {
        try { log?.Invoke(line); }
        catch { /* a log is not a gate */ }
    }

    /// <summary>
    /// The evidence the removal ran. Bootstrap's own DLL can only go at the next reboot, so
    /// without this note there would be nothing on disk saying the request was honored.
    /// </summary>
    private static void WriteDone(string root, DateTime requestedUtc, int deleted, int left)
    {
        try
        {
            var now = (ClockUtc ?? (() => DateTime.UtcNow))();
            var sb = new StringBuilder();
            sb.Append("schema=").Append(CurrentSchema.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("requestedUtc=").Append(Stamp(requestedUtc)).Append('\n');
            sb.Append("completedUtc=").Append(Stamp(now)).Append('\n');
            sb.Append("deleted=").Append(deleted.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("left=").Append(left.ToString(CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllBytes(Path.Combine(root, DoneFileName), Encoding.UTF8.GetBytes(sb.ToString()));
        }
        catch { /* the note is evidence, not a gate */ }
    }

    private static string Stamp(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static bool TryReadRequest(string marker, out List<string> files, out DateTime requestedUtc)
    {
        files = new List<string>();
        requestedUtc = DateTime.MinValue;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(marker));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || schema.GetInt32() != CurrentSchema)
                return false;
            if (root.TryGetProperty("requestedUtc", out var stamp) && stamp.ValueKind == JsonValueKind.String
                && DateTime.TryParse(stamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                requestedUtc = parsed;
            if (!root.TryGetProperty("files", out var array) || array.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) return false;
                // Kept as the marker spells it: normalizing here would strip a leading "/" and
                // turn a rooted path into an innocent-looking relative one before Honour's check.
                string raw = (item.GetString() ?? "").Trim();
                if (raw.Length == 0) return false;
                files.Add(raw);
            }

            return files.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Mirror of Core's <c>ClientUninstall.IsAllowedRelativePath</c>.</summary>
    internal static bool IsAllowedRelativePath(string? relative)
    {
        string raw = (relative ?? "").Trim();
        // A rooted path ("/etc/hosts", a UNC share, "C:/...") is not relative to anything and
        // is refused on its raw spelling, before any normalization could strip the root away.
        if (raw.Length == 0 || raw.Length > 400) return false;
        if (raw[0] == '/' || raw[0] == '\\') return false;
        if (raw.Length > 1 && raw[1] == ':') return false;
        string rel = Normalize(raw);
        if (rel.Length == 0) return false;
        string[] parts = rel.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == "." || part == "..") return false;
        }

        return !string.Equals(parts[0], LoaderCoreFolder, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Normalize(string? relative) =>
        (relative ?? "").Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>Second, path-resolving check: the combined path must still sit under the root.</summary>
    internal static bool IsUnderRoot(string root, string relative)
    {
        try
        {
            string baseFull = Path.GetFullPath(root);
            if (!baseFull.EndsWith(Path.DirectorySeparatorChar)) baseFull += Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Absolute(root, relative));
            return full.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Absolute(string root, string relative) =>
        Path.Combine(root, Normalize(relative).Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// A Bootstrap image anywhere under <c>plugins/</c>: the legacy <c>plugins/Progenitor.Bootstrap.dll</c> or the
    /// single-folder <c>plugins/Progenitor/Progenitor.Bootstrap.dll</c>. The running one cannot be deleted.
    /// </summary>
    internal static bool IsBootstrapDll(string relative)
    {
        string rel = Normalize(relative);
        if (!rel.StartsWith(PluginsFolder + "/", StringComparison.OrdinalIgnoreCase)) return false;
        int slash = rel.LastIndexOf('/');
        return string.Equals(rel.Substring(slash + 1), BootstrapDllName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPatcherDll(string relative)
    {
        string rel = Normalize(relative);
        return rel.StartsWith("patchers/", StringComparison.OrdinalIgnoreCase)
            && rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryRenameAside(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string aside = path + ".removed";
            if (File.Exists(aside)) File.Delete(aside);
            File.Move(path, aside);
            return true;
        }
        catch { return false; /* reported as left */ }
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* a locked file is reported as left, never thrown */ }
    }

    private static void RemoveEmptyTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string child in Directory.GetDirectories(dir)) RemoveEmptyTree(child);
            if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { /* a folder that will not go is left in place */ }
    }

    private static bool ScheduleDelete(string path)
    {
        var over = ScheduleDeleteOverride;
        if (over != null)
        {
            try { return over(path); }
            catch { return false; }
        }

        try { return MoveFileEx(path, null, MoveFileDelayUntilReboot); }
        catch { return false; }
    }

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
