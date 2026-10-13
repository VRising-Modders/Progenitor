using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.Plugins;

/// <summary>
/// The 2026-10 id rename (docs/hot-reload.md, "Renamed ids"). Progenitor's plugin GUIDs, Harmony ids, cfg file
/// names and per-plugin store files dropped a one-segment vendor prefix: <c>&lt;vendor&gt;.progenitor</c> became
/// <c>progenitor</c> and <c>&lt;vendor&gt;.progenitor.sample</c> became <c>progenitor.sample</c>. This type
/// recognizes the old form by its shape (one leading segment, then <c>progenitor</c> as a whole segment) so that state
/// an earlier build wrote is found again, without naming the retired prefix. Kept for one release.
///
/// <para>Compiled into Core and (linked) into Bootstrap, which renames the cfg files before any other Progenitor
/// plugin is constructed. Game-free.</para>
/// </summary>
internal static class LegacyIds
{
    /// <summary>The id every Progenitor id now starts with.</summary>
    internal const string Root = "progenitor";

    /// <summary>The suffix the old file keeps after it is migrated (<c>&lt;old name&gt;.bak</c>).</summary>
    internal const string BackupSuffix = ".bak";

    /// <summary>
    /// True for <c>&lt;vendor&gt;.progenitor</c> and <c>&lt;vendor&gt;.progenitor.&lt;rest&gt;</c>, where
    /// <c>&lt;vendor&gt;</c> is one segment of letters, digits, '_' or '-' that is not itself <c>progenitor</c>.
    /// Case-insensitive, like BepInEx GUID comparisons in this framework.
    /// </summary>
    internal static bool IsLegacy(string? id) => SplitAt(id) > 0;

    /// <summary>The current id for a legacy one (<c>&lt;vendor&gt;.progenitor.sample</c> → <c>progenitor.sample</c>); any other value unchanged.</summary>
    internal static string Normalize(string? id)
    {
        string v = id ?? "";
        int at = SplitAt(v);
        return at > 0 ? v.Substring(at) : v;
    }

    /// <summary>
    /// A setting id or audit row (<c>&lt;pluginId&gt;/&lt;Section&gt;/&lt;Key&gt;</c>) with its plugin id normalized.
    /// </summary>
    internal static string NormalizeRowId(string? rowId)
    {
        string v = rowId ?? "";
        int slash = v.IndexOf('/');
        if (slash <= 0) return Normalize(v);
        int at = SplitAt(v.Substring(0, slash));
        return at > 0 ? v.Substring(at) : v;
    }

    /// <summary>
    /// The current file name for a legacy one with this extension (<c>&lt;vendor&gt;.progenitor.ui.cfg</c> →
    /// <c>progenitor.ui.cfg</c>), or null when the name is not a legacy one.
    /// </summary>
    internal static string? CurrentFileName(string? fileName, string extension)
    {
        string name = fileName ?? "";
        if (string.IsNullOrEmpty(extension) || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return null;
        string stem = name.Substring(0, name.Length - extension.Length);
        int at = SplitAt(stem);
        return at > 0 ? stem.Substring(at) + extension : null;
    }

    /// <summary>
    /// Give every legacy-named file with <paramref name="extension"/> in <paramref name="folder"/> its current name.
    /// The values move with the file: it is copied to the current name, then the old file is renamed to
    /// <c>&lt;old name&gt;.bak</c> (or <c>.bak-&lt;utc&gt;</c> when that exists), so nothing is lost and nothing is
    /// read twice. A current file that already exists is never overwritten: the old one is left where it is and the
    /// returned line says so. Returns one line per file acted on, for the caller to log once.
    /// </summary>
    internal static IReadOnlyList<string> MigrateFolder(string? folder, string extension)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return lines;
        string[] files;
        try { files = Directory.GetFiles(folder, "*" + extension); }
        catch (Exception e)
        {
            lines.Add("Renamed ids: could not list " + folder + ": " + e.Message);
            return lines;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string old in files)
        {
            string oldName = Path.GetFileName(old);
            // Directory.GetFiles("*.cfg") also returns longer extensions on Windows; keep the exact one.
            string? current = CurrentFileName(oldName, extension);
            if (current == null) continue;
            string target = Path.Combine(folder, current);
            try
            {
                if (File.Exists(target))
                {
                    lines.Add("Renamed ids: kept " + current + "; the older " + oldName + " was left as it is (both exist).");
                    continue;
                }

                File.Copy(old, target, overwrite: false);
                string backup = old + BackupSuffix;
                if (File.Exists(backup))
                    backup = old + BackupSuffix + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
                File.Move(old, backup);
                lines.Add("Renamed ids: " + oldName + " -> " + current + " (the old file is kept as " + Path.GetFileName(backup) + ").");
            }
            catch (Exception e)
            {
                lines.Add("Renamed ids: could not move " + oldName + " to " + current + ": " + e.Message);
            }
        }

        return lines;
    }

    /// <summary>Index where the current id starts inside a legacy one, or -1.</summary>
    private static int SplitAt(string? id)
    {
        string v = id ?? "";
        int dot = v.IndexOf('.');
        if (dot <= 0) return -1;
        for (int i = 0; i < dot; i++)
        {
            char c = v[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return -1;
        }

        if (string.Compare(v, 0, Root, 0, Root.Length, StringComparison.OrdinalIgnoreCase) == 0 && dot == Root.Length) return -1;
        int start = dot + 1;
        if (v.Length - start < Root.Length) return -1;
        if (string.Compare(v, start, Root, 0, Root.Length, StringComparison.OrdinalIgnoreCase) != 0) return -1;
        int end = start + Root.Length;
        if (end != v.Length && v[end] != '.') return -1;
        return start;
    }
}
