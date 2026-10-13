using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Progenitor.Install;

/// <summary>
/// INSTALLSIDES2 item 7: removes abandoned package downloads from the staging <c>tmp</c> folder
/// (<c>BepInEx/progenitor-staging/index/tmp/*.part</c>).
///
/// <para>Every download lands there as <c>&lt;name&gt;.zip.&lt;guid&gt;.part</c>. A failed fetch deletes its
/// own file, but a finished one stays as the zip the catalog inspects and installs from, and it is
/// only remembered in that catalog's memory. A game restart or a Core hot swap drops the memory, so
/// the file is orphaned; live (2026-10-03) the client held about 64 MB of them, 43 for HotDemo
/// alone.</para>
///
/// <para>Only <c>*.part</c> files directly in the folder are touched. A file is removed when it is
/// older than <see cref="DefaultMaxAge"/> by its last write (a running download writes as it goes, so
/// its file is never old) and the running catalog does not hold it (<c>keep</c>: the zips this
/// generation downloaded and may still install from).</para>
/// </summary>
internal static class StagingTempSweep
{
    /// <summary>How old an unheld <c>.part</c> must be before it counts as abandoned.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(2);

    public const string PartPattern = "*.part";

    internal sealed class Result
    {
        public List<string> Removed { get; } = new();
        public long RemovedBytes { get; set; }
        public int Kept { get; set; }
        public int Failed { get; set; }

        /// <summary>One log line: counts, size and the removed file names (capped).</summary>
        public string Describe(string folder)
        {
            var sb = new StringBuilder();
            sb.Append("Install: staging sweep removed ").Append(Removed.Count).Append(" abandoned download(s), ")
                .Append(FormatBytes(RemovedBytes)).Append(", from ").Append(folder)
                .Append("; kept ").Append(Kept);
            if (Failed > 0) sb.Append("; ").Append(Failed).Append(" could not be deleted");
            if (Removed.Count > 0)
            {
                sb.Append(": ");
                int shown = Math.Min(Removed.Count, 12);
                for (int i = 0; i < shown; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(Removed[i]);
                }
                if (Removed.Count > shown) sb.Append(", … ").Append(Removed.Count - shown).Append(" more");
            }
            return sb.ToString();
        }
    }

    public static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024L
            ? (bytes / (1024.0 * 1024.0)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " KB";

    /// <summary>
    /// Remove abandoned <c>.part</c> files from <paramref name="tempDir"/>. A missing folder is an empty
    /// result. Never throws.
    /// </summary>
    public static Result Sweep(string? tempDir, DateTime nowUtc, TimeSpan maxAge, IEnumerable<string>? keep = null)
    {
        var result = new Result();
        if (string.IsNullOrWhiteSpace(tempDir)) return result;
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (keep != null)
            foreach (string path in keep)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                try { held.Add(Path.GetFullPath(path)); }
                catch { /* an unreadable path holds nothing */ }
            }

        string[] files;
        try
        {
            if (!Directory.Exists(tempDir)) return result;
            files = Directory.GetFiles(tempDir!, PartPattern, SearchOption.TopDirectoryOnly);
        }
        catch { return result; }

        for (int i = 0; i < files.Length; i++)
        {
            string path = files[i];
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                bool old = nowUtc - info.LastWriteTimeUtc >= maxAge;
                if (!old || held.Contains(Path.GetFullPath(path)))
                {
                    result.Kept++;
                    continue;
                }

                long size = info.Length;
                info.Delete();
                result.Removed.Add(info.Name);
                result.RemovedBytes += size;
            }
            catch
            {
                result.Failed++;
            }
        }

        return result;
    }
}
