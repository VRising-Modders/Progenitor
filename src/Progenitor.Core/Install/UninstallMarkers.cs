using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Progenitor.Install;

/// <summary>
/// INSTALL2 item 4: the pending uninstall of an ordinary (non-hot) mod that is <em>loaded</em>.
///
/// <para>Deleting the file of a loaded plugin does not unload it — only a hot-loadable Progenitor mod
/// can be dropped in process — so the old Remove deleted the reload copy while the assembly stayed
/// live, and the row had to say "removed — still loaded until this process restarts". That left the
/// player with no way back, and a crash before the restart lost the mod for nothing. So the removal
/// is now a <em>request</em>: the file stays exactly where it is, a marker named
/// <c>&lt;file&gt;.uninstall</c> is written beside it, and the ledger row is flagged
/// <c>uninstallPending</c>. Nothing is deleted while the process runs.</para>
///
/// <para>Bootstrap honors the marker at the next start — <c>PendingInstalls.RemoveMarkedAtStart</c>,
/// which runs before the promote and therefore before the first reload scan — and deletes the DLL
/// and the marker together. Bootstrap cannot read this ledger (it cannot reference Core), so the
/// flagged row is dropped afterwards by <see cref="ServerInstall.ReconcilePromoted"/>, which the menu
/// runs on every open: a flagged row whose file is gone is a removal that has been carried out.</para>
///
/// <para>The marker's name is deliberately <c>&lt;name&gt;.dll.uninstall</c>: both reloaders
/// enumerate the reload root with one flat <c>*.dll</c> pattern, and a name whose extension is
/// <c>.uninstall</c> is not matched by it, so no reloader has to learn about this file.
/// <c>The_reload_glob_ignores_an_uninstall_marker</c> pins that.</para>
/// </summary>
internal static class UninstallMarkers
{
    /// <summary>The suffix appended to the reload copy's own file name. Mirrored by <c>PendingInstalls.UninstallMarkerSuffix</c>.</summary>
    public const string MarkerSuffix = ".uninstall";

    /// <summary>The status of a marking call that found nothing of the package to mark.</summary>
    public const string NothingToMark = "nothing of that package is installed here";

    /// <summary>The status of a cancel that found no pending removal.</summary>
    public const string NothingPending = "no pending removal for that package";

    /// <summary>The marker that condemns <paramref name="filePath"/>.</summary>
    public static string MarkerFor(string filePath) => (filePath ?? "") + MarkerSuffix;

    /// <summary>Is this a marker path?</summary>
    public static bool IsMarker(string? path) =>
        !string.IsNullOrEmpty(path) && path!.EndsWith(MarkerSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The file a marker condemns, or null when <paramref name="markerPath"/> is not a marker.</summary>
    public static string? MarkedFile(string? markerPath) =>
        IsMarker(markerPath) ? markerPath!.Substring(0, markerPath!.Length - MarkerSuffix.Length) : null;

    /// <summary>
    /// Every marker in one folder, flat — the same depth both reloaders scan, so this never names a
    /// file a reloader would not have loaded.
    /// </summary>
    public static IReadOnlyList<string> MarkersIn(string? folder)
    {
        var list = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return list;
            list.AddRange(Directory.GetFiles(folder, "*" + MarkerSuffix, SearchOption.TopDirectoryOnly));
        }
        catch
        {
            // A folder we cannot enumerate costs a cleanup, never a load.
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>The marker's one line: when the removal was asked for, and which bytes it condemns.</summary>
    public static string MarkerText(string sha256, DateTime requestedUtc) =>
        requestedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        + " " + (sha256 ?? "").Trim().ToLowerInvariant() + "\n";

    /// <summary>Test seam for the marker timestamp; <see cref="DateTime.UtcNow"/> in the game.</summary>
    internal static Func<DateTime>? ClockUtc { get; set; }

    private static DateTime Now()
    {
        var clock = ClockUtc;
        if (clock == null) return DateTime.UtcNow;
        try { return clock(); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>
    /// Ask for <paramref name="source"/>'s installed files under this root to be removed at the next
    /// start: a marker beside each one, and the ledger row flagged. The files are not touched.
    /// Only <c>mod</c> rows are marked — a <c>pending</c> row is a file that never loaded, and
    /// canceling that install is a plain delete (<see cref="ServerInstall.RemovePackage"/>).
    /// </summary>
    public static ServerInstallResult Mark(string? bepInExRoot, string? source) =>
        Apply(bepInExRoot, source, mark: true);

    /// <summary>Reactivate: drop the markers and the flag, so the next start changes nothing.</summary>
    public static ServerInstallResult Cancel(string? bepInExRoot, string? source) =>
        Apply(bepInExRoot, source, mark: false);

    /// <summary>
    /// Does this root hold a pending removal for <paramref name="source"/>? The flag first — it is
    /// what the ledger records — and the marker on disk as well, so a ledger that could not be
    /// rewritten still reads as pending.
    /// </summary>
    public static bool IsPending(string? bepInExRoot, string? source)
    {
        string? root = RootOf(bepInExRoot);
        if (root == null || string.IsNullOrWhiteSpace(source)) return false;
        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(root); }
        catch { ledger = null; }
        if (ledger == null) return false;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var entry = ledger.Files[i];
            if (!ServerInstall.SourceNamesSamePackage(entry.Source, source)) continue;
            if (entry.UninstallPending) return true;
            try
            {
                if (File.Exists(MarkerFor(ServerInstallLedger.Absolute(root, entry.Path)))) return true;
            }
            catch { /* an unreadable path is not evidence of a pending removal */ }
        }

        return false;
    }

    /// <summary>
    /// Every ledger <c>source</c> under this root with a pending removal — the flag, or a marker on
    /// disk beside the file the row names. One ledger read, so a pane can ask once per build instead
    /// of once per listing.
    /// </summary>
    public static IReadOnlyList<string> PendingSources(string? bepInExRoot)
    {
        var list = new List<string>();
        string? root = RootOf(bepInExRoot);
        if (root == null) return list;
        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(root); }
        catch { ledger = null; }
        if (ledger == null) return list;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var entry = ledger.Files[i];
            if (entry.Source.Length == 0 || list.Contains(entry.Source)) continue;
            bool pending = entry.UninstallPending;
            if (!pending)
            {
                try { pending = File.Exists(MarkerFor(ServerInstallLedger.Absolute(root, entry.Path))); }
                catch { pending = false; }
            }

            if (pending) list.Add(entry.Source);
        }

        return list;
    }

    private static string? RootOf(string? bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return null;
        try { return ServerInstall.GameRootOf(bepInExRoot!); }
        catch { return null; }
    }

    private static ServerInstallResult Apply(string? bepInExRoot, string? source, bool mark)
    {
        string? root = RootOf(bepInExRoot);
        if (root == null)
            return ServerInstallResult.Refuse(ServerInstall.RefusalNoFolder);
        if (string.IsNullOrWhiteSpace(source))
            return ServerInstallResult.Refuse(ServerInstall.NotInstalledStatus);

        ServerInstallLedger? ledger;
        try { ledger = ServerInstallLedger.TryRead(root); }
        catch { ledger = null; }
        if (ledger == null)
            return new ServerInstallResult(true, ServerInstall.NotInstalledStatus, Array.Empty<string>(), Array.Empty<string>(), noOp: true);

        var acted = new List<string>();
        var skipped = new List<string>();
        var keep = new List<ServerInstallLedgerEntry>(ledger.Files.Count);
        bool changed = false;
        bool matched = false;
        DateTime now = Now();

        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var entry = ledger.Files[i];
            bool mine = ServerInstall.SourceNamesSamePackage(entry.Source, source)
                && string.Equals(entry.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal);
            if (!mine)
            {
                keep.Add(entry);
                continue;
            }

            matched = true;
            string abs;
            try { abs = ServerInstallLedger.Absolute(root, entry.Path); }
            catch
            {
                skipped.Add(entry.Path);
                keep.Add(entry);
                continue;
            }

            // The same containment rule the removal uses: never write or delete outside the root the
            // ledger belongs to.
            if (!Networking.ModManifestIds.IsUnder(root, abs))
            {
                skipped.Add(entry.Path);
                keep.Add(entry);
                continue;
            }

            string marker = MarkerFor(abs);
            bool ok;
            if (mark)
            {
                ok = TryWriteMarker(marker, entry.Sha256, now);
            }
            else
            {
                InstallStaging.TryDelete(marker);
                ok = !File.Exists(marker);
            }

            if (!ok)
            {
                skipped.Add(entry.Path);
                keep.Add(entry);
                continue;
            }

            acted.Add(entry.Path);
            if (entry.UninstallPending != mark) changed = true;
            keep.Add(new ServerInstallLedgerEntry(
                entry.Path, entry.Sha256, entry.Size, entry.Category, entry.Source, entry.WrittenUtc, mark,
                hot: entry.Hot, pluginId: entry.PluginId, route: entry.Route));
        }

        if (!matched)
            return new ServerInstallResult(
                true, mark ? NothingToMark : NothingPending, Array.Empty<string>(), Array.Empty<string>(), noOp: true);

        if (changed)
        {
            try
            {
                new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, keep)
                    .Write(root);
            }
            catch
            {
                // The markers are the evidence Bootstrap acts on, so a ledger we could not rewrite
                // costs the menu's fast read of the state, not the removal itself.
            }
        }

        if (acted.Count == 0)
            return new ServerInstallResult(
                true, mark ? NothingToMark : NothingPending, Array.Empty<string>(), skipped, noOp: true);

        string message = mark
            ? "Marked " + acted.Count + " files for removal at the next restart"
            : "Canceled the pending removal of " + acted.Count + " files";
        if (skipped.Count > 0) message += "; left alone: " + string.Join(", ", skipped);
        return new ServerInstallResult(true, message, acted, skipped);
    }

    private static bool TryWriteMarker(string marker, string sha256, DateTime now)
    {
        try
        {
            InstallStaging.AtomicWrite(marker, new UTF8Encoding(false).GetBytes(MarkerText(sha256, now)));
            return File.Exists(marker);
        }
        catch
        {
            return false;
        }
    }
}
