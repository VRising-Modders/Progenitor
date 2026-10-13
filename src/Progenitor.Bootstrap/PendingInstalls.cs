using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace Progenitor.Bootstrap;

// MIRROR: src/Progenitor.Core/Networking/ModManifestPromote.cs and
// src/Progenitor.Bootstrap/PendingInstalls.cs are the same file twice, for the same reason
// ReloadAllowPolicy.cs and ReloadAllowStore.cs are (Bootstrap cannot reference Core, and Core is
// not loaded yet when the promote runs). Everything below is deliberately self-contained — BCL,
// ReloadAllowPolicy, and an unqualified `Plugin.Logger` that binds to whichever Plugin the
// enclosing namespace has — so the two copies differ in exactly one token, the namespace, and
// ReloadHardeningTests.The_two_promote_copies_are_the_same_code fails the build when they do not
// (R36 finding 2: the promote that ran in production was a hand-written mirror that no test drove
// and that had already drifted from the tested copy on its first commit).

/// <summary>What one promote attempt did to one pending file (job FMAN4).</summary>
public enum PromoteOutcome
{
    /// <summary>Moved into the reload folder. It loads this start.</summary>
    Promoted = 0,

    /// <summary>A session-only install a crash left behind. Deleted, never promoted.</summary>
    SessionOnly = 1,

    /// <summary>The allowlist refused these bytes (or could not be parsed). Left in place, not moved.</summary>
    Refused = 2,

    /// <summary>The file could not be read, hashed or moved this start. Left in place.</summary>
    Failed = 3,

    /// <summary>
    /// A reload-root session marker whose neighboring file is not the package the marker was
    /// written for. The file is left exactly as it is; only the marker is removed
    /// (R38 finding 6).
    /// </summary>
    MarkerMismatch = 4,
}

/// <summary>One pending file's fate, so the caller can log and the tests can assert.</summary>
public readonly struct PromoteRow
{
    public PromoteRow(string fileName, string sha256, long size, AllowDecision decision, PromoteOutcome outcome, string detail)
    {
        FileName = fileName ?? "";
        Sha256 = sha256 ?? "";
        Size = size;
        Decision = decision;
        Outcome = outcome;
        Detail = detail ?? "";
    }

    public string FileName { get; }
    public string Sha256 { get; }
    public long Size { get; }
    public AllowDecision Decision { get; }
    public PromoteOutcome Outcome { get; }
    public string Detail { get; }
}

/// <summary>
/// Moves accepted server packages out of <c>BepInEx/progenitor-pending/</c> and into
/// <c>BepInEx/progenitor-reload/</c> — <b>once, at process start, before the first scan</b>
/// (job FMAN4, R35 finding 1).
///
/// <para><b>Why this exists at all.</b> The consent panel promises the player that accepting a
/// package does not run it in the session that downloaded it. That promise needs a mechanism, and
/// "the install file contains no loader call" is not one: both reloaders scan
/// <c>progenitor-reload</c> once a second for the whole process, so writing there <em>is</em>
/// handing the bytes to a loader. The pending folder is the mechanism — nothing enumerates it —
/// and this class is the single, start-time seam where a pending file becomes a reload-folder
/// file. Bootstrap's <c>Plugin.Load</c> calls <see cref="PromoteAtStart"/> before
/// <c>ProcessHost.Install</c>, which is what starts the tick that owns the first
/// <c>ReloaderHost.Scan</c>.</para>
///
/// <para><b>No privilege.</b> A promoted file passes exactly the gate a DLL dropped into the
/// reload folder by hand passes: read, SHA-256, <see cref="ReloadAllowList.Decide"/>, and the
/// hash on the log line either way. A file the allowlist refuses stays in the pending folder and
/// is never moved.</para>
///
/// <para><b>A session-only install is never promoted.</b> "Accept once" writes a
/// <see cref="SessionMarkerSuffix"/> marker beside the package; the join-end sweep deletes the
/// package and then the marker. A marker that survives to a process start is a crash artifact,
/// and promoting it would silently convert a one-join decision into a permanent one — so promote
/// deletes it instead. R36 finding 1: the marker is the <em>only</em> record on disk that these
/// bytes are session-only, so it is deleted only once the package is gone. A marker whose package
/// could not be deleted survives to the next start and refuses the promote again. R38 finding 6:
/// in the <em>reload</em> root the marker carries the package's sha256 and host key and is read
/// before it is acted on, because that folder is also where a player drops their own builds — a
/// marker whose neighbor hashes to something else is removed on its own and the file is left
/// alone. R39 finding 4: only a marker carrying a <em>readable</em> hash can be a mismatch. One
/// with no readable hash is session-only with an unknown identity, and the pair is deleted — the
/// marker is written temp-plus-move (<see cref="WriteSessionMarker"/>) and before the package, so
/// a half-written marker is not a state this sweep can observe and a session package without a
/// marker is not a state it can find. R40 finding 10: that ordering means the pending root can
/// hold an <em>older</em> package under a newer marker for the width of one swap, so the pending
/// sweep checks the marker's sha256 against the file exactly as the reload sweep does. A marker
/// only ever condemns the bytes it names; one that names anything else is removed on its own and
/// the package it was sitting beside is promoted normally.</para>
///
/// <para>Game-free by construction: every path <see cref="PromoteAll"/> touches is a parameter,
/// which is why the tests drive the same code the process start runs. Only
/// <see cref="PromoteAtStart"/> resolves folders itself, and only Bootstrap calls it — Core's copy
/// of it is never invoked and exists so the two files stay byte-identical.</para>
/// </summary>
public static class PendingInstalls
{
    /// <summary>Folder accepted packages are written to. Deliberately not the reload folder.</summary>
    public const string PendingFolderName = "progenitor-pending";

    /// <summary>The folder both reloaders scan once a second.</summary>
    public const string ReloadFolderName = "progenitor-reload";

    /// <summary>Marker written beside a package accepted for one join only.</summary>
    public const string SessionMarkerSuffix = ".progenitor-session";

    /// <summary>The line prefix that carries the host key inside a session marker.</summary>
    public const string MarkerHostPrefix = "host=";

    /// <summary>The name a session marker is written under before it is moved into place.</summary>
    public const string MarkerTempSuffix = ".part";

    /// <summary>The only extension a package may have.</summary>
    public const string ExpectedExtension = ".dll";

    /// <summary>Shared with <c>ModConsentLog.Prefix</c>, so one grep finds both halves.</summary>
    public const string LogPrefix = "ModManifest: ";

    private static readonly UTF8Encoding MarkerEncoding = new(encoderShouldEmitUTF8Identifier: false);

    private static bool _ran;

    /// <summary>The pending root. Never scanned by either reloader.</summary>
    public static string PendingFolder => Path.Combine(Paths.BepInExRootPath, PendingFolderName);

    /// <summary>The reload root, resolved the same way both reloaders resolve it.</summary>
    public static string ReloadFolder => Path.Combine(Paths.BepInExRootPath, ReloadFolderName);

    /// <summary>
    /// Promote everything promotable. Idempotent within a process: a second call is a no-op, so a
    /// mid-session caller cannot turn this into the live-watch path it exists to avoid. Bootstrap's
    /// <c>Plugin.Load</c> is the only caller in either assembly.
    /// </summary>
    public static void PromoteAtStart()
    {
        if (_ran) return;
        _ran = true;
        try
        {
            string allowPath = "";
            try
            {
                if (!ReloadAllowPolicy.TryConfigPath(Paths.ConfigPath, out allowPath, out _))
                    allowPath = "";
            }
            catch (Exception e)
            {
                allowPath = "";
                Plugin.Logger?.LogWarning(LogPrefix + "allowlist path could not be resolved: " + e.Message);
            }

            // INSTALL2 item 4: before anything is promoted and before the first reload scan, honor
            // the removals the menu asked for while the process that had them loaded was running.
            RemoveMarkedAtStart(
                ReloadFolder,
                l => Plugin.Logger?.LogInfo(l),
                l => Plugin.Logger?.LogWarning(l));

            PromoteAll(
                PendingFolder,
                ReloadFolder,
                allowPath,
                l => Plugin.Logger?.LogInfo(l),
                l => Plugin.Logger?.LogWarning(l));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(LogPrefix + "pending promote threw: " + e.Message);
        }
    }

    /// <summary>
    /// INSTALL2 item 4: the suffix of a pending-uninstall marker. Mirrors
    /// <c>Progenitor.Install.UninstallMarkers.MarkerSuffix</c>; Bootstrap and Core cannot reference
    /// each other, the same reason <see cref="SessionMarkerSuffix"/> is spelled in both.
    /// </summary>
    public const string UninstallMarkerSuffix = ".uninstall";

    /// <summary>
    /// INSTALL2 item 4: every pending-uninstall marker in the reload root, flat — the same depth
    /// both reloaders scan, so a marker can only ever name a file a reloader would have loaded.
    /// </summary>
    public static IReadOnlyList<string> UninstallMarkers(string reloadRoot)
    {
        var list = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(reloadRoot) || !Directory.Exists(reloadRoot)) return list;
            list.AddRange(Directory.GetFiles(reloadRoot, "*" + UninstallMarkerSuffix, SearchOption.TopDirectoryOnly));
        }
        catch
        {
            // A reload tree we cannot enumerate costs a removal, never an unwanted load: the file
            // stays exactly as it was and the next start tries again.
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// INSTALL2 item 4: delete every <c>progenitor-reload/*.dll</c> that has a <c>.uninstall</c>
    /// sibling, and the sibling with it. Called at process start <em>before</em>
    /// <see cref="PromoteAll"/>, which is before <c>ProcessHost.Install</c> starts the tick that owns
    /// the first reload scan — so a mod the player uninstalled last session never loads again.
    ///
    /// <para>The menu writes the marker instead of deleting the DLL because deleting the file of a
    /// loaded plugin does not unload it; the file has to survive until the process that loaded it is
    /// gone, which is exactly here. A marker whose DLL is already gone is an orphan and is swept. A
    /// DLL we cannot delete keeps its marker and is warned about in the terms that are true of that
    /// folder: it loads this start, and the next start tries again.</para>
    ///
    /// <para>Bootstrap cannot read Core's install ledger, so the flagged row is dropped afterwards by
    /// <c>ServerInstall.ReconcilePromoted</c>, which the Mods menu runs on every open: a row flagged
    /// <c>uninstallPending</c> whose file is gone is a removal that has been carried out.</para>
    /// </summary>
    /// <returns>The DLLs deleted, in the order they were deleted.</returns>
    public static IReadOnlyList<string> RemoveMarkedAtStart(string reloadRoot, Action<string>? info, Action<string>? warn)
    {
        var deleted = new List<string>();
        var markers = UninstallMarkers(reloadRoot);
        if (markers.Count == 0) return deleted;
        int orphans = 0, residue = 0;
        for (int i = 0; i < markers.Count; i++)
        {
            string markerPath = markers[i];
            string marked = markerPath.Substring(0, markerPath.Length - UninstallMarkerSuffix.Length);
            string name = Path.GetFileName(marked);

            if (!File.Exists(marked))
            {
                TryDelete(markerPath);
                orphans++;
                info?.Invoke(MarkedOrphanLine(name));
                continue;
            }

            if (!TryDelete(marked))
            {
                residue++;
                warn?.Invoke(MarkedResidueLine(name));
                continue;
            }

            TryDelete(markerPath);
            deleted.Add(marked);
            info?.Invoke(MarkedRemovedLine(name));
        }

        info?.Invoke(MarkedSummaryLine(deleted.Count, orphans, residue));
        return deleted;
    }

    /// <summary>The line one honored pending uninstall logs.</summary>
    public static string MarkedRemovedLine(string fileName) =>
        LogPrefix + "uninstalled " + fileName + " (the removal the menu asked for last session)";

    /// <summary>The line a marker whose package is already gone logs.</summary>
    public static string MarkedOrphanLine(string fileName) =>
        LogPrefix + "uninstall marker for " + fileName + " had nothing to remove; marker deleted";

    /// <summary>The line a marked package that could not be deleted logs.</summary>
    public static string MarkedResidueLine(string fileName) =>
        LogPrefix + fileName + " is marked for removal but could not be deleted; it loads this start and the next start tries again";

    /// <summary>The one summary line of the pending-uninstall step.</summary>
    public static string MarkedSummaryLine(int removed, int orphans, int kept) =>
        LogPrefix + "pending uninstalls: " + removed + " removed, " + orphans + " stale markers, " + kept + " kept";

    /// <summary>
    /// Every session marker sitting in the <em>reload</em> root — the folder both reloaders
    /// enumerate once a second (R37 finding 2).
    ///
    /// <para>The join-end sweep walks the pending root and this one, and its docstring promises
    /// that a marker it fails to act on is "refused by the promote at the next start". That was
    /// only ever true of the pending root: <see cref="PendingFiles"/> is the only thing the
    /// promote used to enumerate, so a marker-carrying DLL in <c>progenitor-reload</c> — one an
    /// older build wrote there directly, or one the sweep could not delete — kept an inert marker
    /// and was loaded on that start and every start after. The promote walks both roots now, so
    /// the sentence is true of both.</para>
    ///
    /// <para><b>Flat, like the reloaders (R38 finding 6).</b> This was the one recursive
    /// enumeration anywhere near this folder, and it drove a delete. Both reloaders scan the
    /// reload root with a single flat <c>*.dll</c> pattern — pinned by
    /// <c>ReloadHardeningTests.Both_reloaders_enumerate_exactly_one_flat_dll_pattern_on_the_reload_folder</c>
    /// — so a subfolder is not a place the reloader loads from, and FCOMPAT3's plugin-local
    /// dependency folders live under exactly this root. A marker down there used to delete a
    /// dependency the flat scan would never have seen and could never have explained. Top
    /// directory only, same rule as the scan it exists to get ahead of; the promote's own
    /// enumeration is pinned by
    /// <c>ReloadHardeningTests.The_promote_enumerates_the_reload_root_as_flat_as_the_reloaders</c>.</para>
    /// </summary>
    public static IReadOnlyList<string> ReloadSessionMarkers(string reloadRoot)
    {
        var list = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(reloadRoot) || !Directory.Exists(reloadRoot)) return list;
            list.AddRange(Directory.GetFiles(reloadRoot, "*" + SessionMarkerSuffix, SearchOption.TopDirectoryOnly));
        }
        catch
        {
            // A reload tree we cannot enumerate costs a cleanup, never a load we would not
            // otherwise do: the allowlist still gates whatever the reloader finds there.
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// The text a session marker carries (R38 finding 6): the sha256 of the package it was
    /// written beside on its own first line, then the host key that offered those bytes. The
    /// hash is what makes the marker an identity rather than a name — the sweep deletes the file
    /// beside a marker only when the file still hashes to this value.
    /// </summary>
    public static string FormatSessionMarker(string sha256, string hostKey) =>
        (sha256 ?? "").Trim().ToLowerInvariant() + "\n"
        + MarkerHostPrefix + (hostKey ?? "").Trim() + "\n";

    /// <summary>
    /// Write a session marker the way the package beside it is written: a <c>.part</c> file, then
    /// a move (R39 finding 4).
    ///
    /// <para>The marker used to land through a bare <c>File.WriteAllText</c> while the package it
    /// describes landed through a temp plus a move with a read-back hash check. A crash, a power
    /// loss or a full disk part-way through that write left a marker holding a truncated hash —
    /// a marker <see cref="TryReadMarker"/> cannot read, and therefore a state the promote could
    /// observe. It is not one now: the move is the only thing that makes the marker appear, so
    /// the marker on disk is either absent or complete.</para>
    ///
    /// <para>Throws on failure, because the caller's answer to a marker it could not write is to
    /// delete the package rather than leave it unowned (R35 finding 4).</para>
    /// </summary>
    public static void WriteSessionMarker(string markerPath, string sha256, string hostKey)
    {
        string tmp = markerPath + MarkerTempSuffix;
        try { if (File.Exists(tmp)) File.Delete(tmp); }
        catch { /* the write below fails loudly if it is still in the way */ }

        try
        {
            File.WriteAllText(tmp, FormatSessionMarker(sha256, hostKey), MarkerEncoding);
            File.Move(tmp, markerPath, overwrite: true);
        }
        catch
        {
            // A temp that never became a marker is not a marker, but it is litter in a folder the
            // sweep walks, so it does not outlive the attempt.
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Read a marker's sha256 and host key. False when the marker carries no usable sha256 —
    /// an older build's marker (a single <c>x</c>), or one a pre-FMAN8 build left half written.
    ///
    /// <para>False here is <em>not</em> "wrong hash" (R39 finding 4). A marker that names no
    /// bytes still says the one thing only a marker says: the file beside it was accepted for a
    /// single join. The caller's answer is the pre-identity one — delete the package, then the
    /// marker — because a session install is disposable by construction. Only a marker that
    /// carries a readable hash the file <em>fails</em> is a mismatch, and only that leaves the
    /// file alone.</para>
    /// </summary>
    public static bool TryReadMarker(string markerPath, out string sha256, out string hostKey)
    {
        sha256 = "";
        hostKey = "";
        try
        {
            if (string.IsNullOrEmpty(markerPath) || !File.Exists(markerPath)) return false;
            string[] lines = File.ReadAllLines(markerPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = (lines[i] ?? "").Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(MarkerHostPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (hostKey.Length == 0) hostKey = line.Substring(MarkerHostPrefix.Length).Trim();
                    continue;
                }

                if (sha256.Length == 0 && IsHashHex(line)) sha256 = line.ToLowerInvariant();
            }
        }
        catch
        {
            sha256 = "";
            hostKey = "";
            return false;
        }

        return sha256.Length == ReloadAllowPolicy.HashLength;
    }

    /// <summary>
    /// True when the file at <paramref name="packagePath"/> is the package
    /// <paramref name="markerSha256"/> was recorded for. R38 finding 6: the sweep used to delete
    /// whatever now sat under the marker's name, and the reload folder is the folder the dev loop
    /// and the docs tell a player to drop their own builds into — so an orphaned marker plus the
    /// player's own <c>X.dll</c> was a silent delete with a log line asserting a history nobody
    /// had checked.
    /// </summary>
    /// <param name="unreadable">
    /// True when the file could not be read at all — a handle held open, most often. That is not
    /// evidence of a different identity, so the caller keeps the marker and treats it as residue
    /// rather than deleting the marker and walking away.
    /// </param>
    public static bool MarkerNamesTheFile(string packagePath, string markerSha256, out string why, out bool unreadable)
    {
        why = "";
        unreadable = false;
        if (string.IsNullOrEmpty(markerSha256))
        {
            // Callers ask <see cref="TryReadMarker"/> first and never reach this with an empty
            // hash (R39 finding 4): "no hash" is the identity-unknown case, not a mismatch.
            why = "the marker records no sha256";
            return false;
        }

        string actual;
        try
        {
            actual = ReloadAllowPolicy.Sha256Hex(File.ReadAllBytes(packagePath));
        }
        catch (Exception e)
        {
            unreadable = true;
            why = "the file could not be hashed (" + e.GetType().Name + ": " + e.Message + ")";
            return false;
        }

        if (string.Equals(actual, markerSha256, StringComparison.OrdinalIgnoreCase)) return true;
        why = "marker sha256=" + ReloadAllowPolicy.Short(markerSha256)
            + " file sha256=" + ReloadAllowPolicy.Short(actual);
        return false;
    }

    private static bool IsHashHex(string s)
    {
        if (s.Length != ReloadAllowPolicy.HashLength) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }

        return true;
    }

    /// <summary>
    /// Every staging file an install left in the pending tree: the package it was writing
    /// (<c>&lt;id&gt;.dll.&lt;token&gt;.part</c>), the marker staged beside it (that name plus
    /// <see cref="SessionMarkerSuffix"/>) and <see cref="WriteSessionMarker"/>'s own
    /// <c>.part</c> temp. None of them is a name the promote, the sweep or either reloader
    /// enumerates, so they are inert litter — but a staging name carries a token of its install's
    /// own and so cannot be overwritten by the next attempt (R40 finding 10).
    ///
    /// <para><b>Why the sweep lives here and nowhere else (R41 finding 4).</b> The install used to
    /// clear staging itself, with a wildcard over <c>&lt;id&gt;.dll.*.part*</c> — every token for
    /// that id, not its own — so a second install of the same id deleted the file that guarantees
    /// the first one's isolation, and the first one's rollback then deleted a marker the second
    /// had swapped into place. An install now deletes only the two names it created, and the
    /// leftovers of a crashed one are swept exactly here: at process start, before
    /// <c>ProcessHost.Install</c> and therefore before <em>this</em> process can have an install in
    /// flight.</para>
    ///
    /// <para><b>The other process (R42 finding 6).</b> "No install can be in flight" was only ever
    /// true of this process. The install gate is in-process by construction — a second client
    /// instance sharing the profile is outside it — so a start here can land while an instance
    /// already running is mid-install, and deleting that install's staging pair makes its
    /// <c>File.Move</c> fail. Hence the age gate: a staging file is litter only once it is at least
    /// <see cref="MinLitterAgeMinutes"/> minutes old. An install writes its staging and swaps it
    /// into place in the same breath, so nothing live is ever that old, and a crashed install's
    /// leftovers become sweepable a few minutes later instead of at once. The sweep is bounded to
    /// this one call site for the same reason it always was.</para>
    /// </summary>
    public static IReadOnlyList<string> StagingLitter(string pendingRoot) =>
        StagingLitter(pendingRoot, DateTime.UtcNow);

    /// <summary>
    /// <see cref="StagingLitter(string)"/> against a caller-supplied clock, so the age gate is
    /// testable without sleeping for ten minutes.
    /// </summary>
    public static IReadOnlyList<string> StagingLitter(string pendingRoot, DateTime nowUtc)
    {
        var list = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(pendingRoot) || !Directory.Exists(pendingRoot)) return list;
            string[] all = Directory.GetFiles(pendingRoot, "*", SearchOption.AllDirectories);
            for (int i = 0; i < all.Length; i++)
            {
                string name = Path.GetFileName(all[i]);
                bool staging =
                    name.EndsWith(MarkerTempSuffix, StringComparison.OrdinalIgnoreCase)
                    || name.IndexOf(MarkerTempSuffix + ".", StringComparison.OrdinalIgnoreCase) >= 0;
                if (staging && IsOldEnoughToSweep(all[i], nowUtc)) list.Add(all[i]);
            }
        }
        catch
        {
            // Litter we could not enumerate costs disk, never a load: no staging name is a name
            // the promote, the sweep or a reloader looks at.
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// How old a staging file must be before the start-time sweep will delete it. The bound is the
    /// whole of what makes the sweep safe against a second client instance sharing the profile
    /// (R42 finding 6); it is minutes rather than seconds because the cost of waiting is a few
    /// stale bytes on disk and the cost of not waiting is another process's install failing.
    /// </summary>
    public const int MinLitterAgeMinutes = 10;

    /// <summary>
    /// True when this staging file was last written at least <see cref="MinLitterAgeMinutes"/> ago.
    /// A file whose timestamp cannot be read is left alone: an unreadable age is not evidence of
    /// staleness. A timestamp in the future (a clock that moved back) is treated the same way.
    /// </summary>
    private static bool IsOldEnoughToSweep(string path, DateTime nowUtc)
    {
        try
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (written == default || written > nowUtc) return false;
            return nowUtc - written >= TimeSpan.FromMinutes(MinLitterAgeMinutes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Every pending package still on disk, newest last. Used by promote and the sweep.</summary>
    public static IReadOnlyList<string> PendingFiles(string pendingRoot)
    {
        var list = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(pendingRoot) || !Directory.Exists(pendingRoot)) return list;
            list.AddRange(Directory.GetFiles(pendingRoot, "*" + ExpectedExtension, SearchOption.AllDirectories));
        }
        catch
        {
            // A pending tree we cannot enumerate costs a promote, never a load.
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>
    /// Move every promotable pending package into <paramref name="reloadRoot"/>. Called once, at
    /// process start, before the first reload scan. Returns one row per file inspected.
    ///
    /// <para>The gate is the reload folder's own gate: hash the bytes, ask the allowlist at
    /// <paramref name="allowListPath"/>, log the hash and the verdict, and move only on
    /// <see cref="ReloadAllowPolicy.MayLoad"/>. An absent allowlist answers
    /// <see cref="AllowDecision.NoList"/> exactly as it does for a file already in the reload
    /// folder — promotion never grants more than dropping the DLL in by hand would.</para>
    ///
    /// <para><b>The reload root is swept first (R37 finding 2).</b> A session marker in
    /// <paramref name="reloadRoot"/> names bytes accepted for one join that are sitting in the
    /// folder the reloaders scan, so the pair is deleted here — before <c>ProcessHost.Install</c>
    /// starts the tick that owns the first scan — rather than merely refused. Every leftover is
    /// logged, and one that cannot be deleted keeps its marker and is warned about in the terms
    /// that are actually true of that folder: it loads this start until the delete succeeds.</para>
    /// </summary>
    public static IReadOnlyList<PromoteRow> PromoteAll(
        string pendingRoot,
        string reloadRoot,
        string allowListPath,
        Action<string>? info,
        Action<string>? warn)
    {
        var rows = new List<PromoteRow>();
        int promoted = 0, refused = 0, swept = 0, mismatched = 0;

        // R41 finding 4: the only sweep of staging names in the system. Nothing in this process is
        // installing yet, and the age gate (R42 finding 6) covers the instance that is.
        var litter = StagingLitter(pendingRoot);
        int litterSwept = 0;
        for (int i = 0; i < litter.Count; i++)
        {
            if (TryDelete(litter[i])) litterSwept++;
        }

        if (litterSwept > 0) info?.Invoke(StagingLitterLine(litterSwept));

        var leftovers = ReloadSessionMarkers(reloadRoot);
        for (int i = 0; i < leftovers.Count; i++)
        {
            string markerPath = leftovers[i];
            string marked = markerPath.Substring(0, markerPath.Length - SessionMarkerSuffix.Length);
            string leftName = Path.GetFileName(marked);
            bool markerReadable = TryReadMarker(markerPath, out string markerSha, out string markerHost);

            if (!File.Exists(marked))
            {
                // Nothing left to delete: the package went first, as the join-end sweep does it.
                TryDelete(markerPath);
                swept++;
                rows.Add(new PromoteRow(leftName, markerSha, 0, AllowDecision.NoList, PromoteOutcome.SessionOnly, ""));
                info?.Invoke(ReloadOrphanLine(leftName, markerHost));
                continue;
            }

            // R38 finding 6: the marker is an identity claim about specific bytes, so check it
            // before acting on it. A marker whose neighbor is not those bytes — the player's own
            // build of the same mod under the same name, most likely — buys nothing by being
            // kept, and the file it points at is not ours to delete. Drop the marker, leave the
            // file to the allowlist and the reloader exactly as if the marker had never existed.
            // A file we could not read is not a mismatch, though: it is the residue case below,
            // where the marker is the one thing worth keeping.
            //
            // R39 finding 4: neither is a marker we could not read. Folding "no hash" into "wrong
            // hash" deleted the one record on disk saying these bytes are session-only and
            // promoted them from one join to permanent — by failing to read a file. A marker with
            // no readable hash is session-only with an unknown identity, and a session install is
            // disposable by construction, so the pair goes: package first, then the marker, said
            // plainly in the log so the delete is not mistaken for a verified one.
            if (!markerReadable)
            {
                bool droppedUnknown = TryDelete(marked);
                if (droppedUnknown) TryDelete(markerPath);
                swept++;
                rows.Add(new PromoteRow(leftName, "", 0, AllowDecision.NoList, PromoteOutcome.SessionOnly,
                    droppedUnknown ? "marker unreadable" : "package still on disk"));
                if (droppedUnknown) warn?.Invoke(ReloadUnreadableMarkerLine(leftName, markerHost));
                else warn?.Invoke(ReloadResidueLine(leftName));
                continue;
            }

            if (!MarkerNamesTheFile(marked, markerSha, out string why, out bool unreadable) && !unreadable)
            {
                TryDelete(markerPath);
                mismatched++;
                rows.Add(new PromoteRow(leftName, markerSha, 0, AllowDecision.NoList, PromoteOutcome.MarkerMismatch, why));
                warn?.Invoke(ReloadMismatchLine(leftName, why));
                continue;
            }

            bool cleared = !unreadable && TryDelete(marked);
            if (cleared) TryDelete(markerPath);
            swept++;
            rows.Add(new PromoteRow(leftName, markerSha, 0, AllowDecision.NoList, PromoteOutcome.SessionOnly,
                cleared ? "" : "package still on disk"));
            if (cleared) info?.Invoke(ReloadLeftoverLine(leftName, markerSha, markerHost));
            else warn?.Invoke(ReloadResidueLine(leftName));
        }

        var files = PendingFiles(pendingRoot);
        if (files.Count == 0)
        {
            if (swept > 0 || mismatched > 0) info?.Invoke(SummaryLine(promoted, refused, swept, mismatched));
            return rows;
        }

        ReloadAllowList? list = null;
        bool haveList = false;
        try
        {
            if (!string.IsNullOrEmpty(allowListPath) && File.Exists(allowListPath))
            {
                list = ReloadAllowPolicy.Parse(File.ReadAllText(allowListPath));
                haveList = true;
            }
        }
        catch (Exception e)
        {
            // Unreadable is Malformed everywhere else in this system. Fail closed here too.
            list = ReloadAllowPolicy.MalformedFor(e.Message);
            haveList = true;
        }

        for (int i = 0; i < files.Count; i++)
        {
            string file = files[i];
            string name = Path.GetFileName(file);
            string marker = file + SessionMarkerSuffix;
            if (File.Exists(marker))
            {
                // R40 finding 10: this sweep used to act on the marker's *presence*, which was
                // safe only while a marker could not exist beside bytes it was not written for.
                // The install writes both files under a staging name and swaps the marker in
                // first, so the one state a crash mid-swap leaves is the previous package under
                // the new package's marker — and deleting on presence alone would destroy a
                // permanently installed package on the strength of bytes that never landed. The
                // marker already carries its package's sha256, so the pair is checked here the
                // way the reload root's is: a marker that names other bytes is removed on its own
                // and the package falls through to the ordinary promote below, unmarked and
                // loadable.
                bool readable = TryReadMarker(marker, out string pendingSha, out _);
                bool mismatch = false;
                string mismatchWhy = "";
                if (readable
                    && !MarkerNamesTheFile(file, pendingSha, out string pendingWhy, out bool pendingUnreadable)
                    && !pendingUnreadable)
                {
                    mismatch = true;
                    mismatchWhy = pendingWhy;
                }

                if (mismatch)
                {
                    TryDelete(marker);
                    mismatched++;
                    rows.Add(new PromoteRow(name, pendingSha, 0, AllowDecision.NoList, PromoteOutcome.MarkerMismatch, mismatchWhy));
                    warn?.Invoke(PendingMismatchLine(name, mismatchWhy));
                }
                else
                {
                    // R36 finding 1: the marker outlives a package we failed to delete. Losing it
                    // would hand this file the default no-allowlist verdict at the next start,
                    // which is a load — the one-join decision turned permanent by a cleanup
                    // failure. A marker we could not read still says "one join only", and a
                    // package we could not hash is residue rather than a different identity, so
                    // both land here and the pair goes.
                    bool gone = !File.Exists(file) || TryDelete(file);
                    if (gone) TryDelete(marker);
                    swept++;
                    rows.Add(new PromoteRow(name, "", 0, AllowDecision.NoList, PromoteOutcome.SessionOnly, gone ? "" : "package still on disk"));
                    info?.Invoke(SessionOnlyLine(name));
                    if (!gone) warn?.Invoke(SessionResidueLine(name));
                    continue;
                }
            }

            byte[] bytes;
            long size;
            try
            {
                bytes = File.ReadAllBytes(file);
                size = bytes.LongLength;
            }
            catch (Exception e)
            {
                rows.Add(new PromoteRow(name, "", 0, AllowDecision.NoList, PromoteOutcome.Failed, e.Message));
                warn?.Invoke(FailedLine(name, "reading it threw: " + e.Message));
                continue;
            }

            string hash = ReloadAllowPolicy.Sha256Hex(bytes);
            AllowDecision decision = haveList && list != null ? list.Decide(name, hash) : AllowDecision.NoList;
            if (!ReloadAllowPolicy.MayLoad(decision))
            {
                refused++;
                rows.Add(new PromoteRow(name, hash, size, decision, PromoteOutcome.Refused, ""));
                warn?.Invoke(RefusedLine(name, hash, size, decision));
                continue;
            }

            try
            {
                Directory.CreateDirectory(reloadRoot);
                string target = Path.GetFullPath(Path.Combine(Path.GetFullPath(reloadRoot), name));
                if (!IsUnder(reloadRoot, target))
                {
                    rows.Add(new PromoteRow(name, hash, size, decision, PromoteOutcome.Failed, "path escaped the reload folder"));
                    warn?.Invoke(FailedLine(name, "the promote path escaped " + ReloadFolderName));
                    continue;
                }

                File.Move(file, target, overwrite: true);
                string readBack = ReloadAllowPolicy.Sha256Hex(File.ReadAllBytes(target));
                if (!string.Equals(readBack, hash, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(target);
                    rows.Add(new PromoteRow(name, hash, size, decision, PromoteOutcome.Failed, "read back as other bytes"));
                    warn?.Invoke(FailedLine(name, "it did not read back as the same bytes; deleted"));
                    continue;
                }

                promoted++;
                rows.Add(new PromoteRow(name, hash, size, decision, PromoteOutcome.Promoted, target));
                info?.Invoke(PromotedLine(name, hash, size, decision, target));
            }
            catch (Exception e)
            {
                rows.Add(new PromoteRow(name, hash, size, decision, PromoteOutcome.Failed, e.Message));
                warn?.Invoke(FailedLine(name, e.GetType().Name + ": " + e.Message));
            }
        }

        info?.Invoke(SummaryLine(promoted, refused, swept, mismatched));
        return rows;
    }

    /// <summary>True when <paramref name="candidate"/> resolves inside <paramref name="root"/>.</summary>
    public static bool IsUnder(string root, string candidate)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(candidate)) return false;
        string a = Path.GetFullPath(root);
        string b = Path.GetFullPath(candidate);
        if (!a.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            && !a.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            a += Path.DirectorySeparatorChar;
        return b.StartsWith(a, StringComparison.OrdinalIgnoreCase)
            || string.Equals(b, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one line that says a package left the folder nothing watches and entered the folder
    /// the reloader scans, with the bytes and the verdict that made it loadable.
    /// </summary>
    public static string PromotedLine(string fileName, string sha256, long size, AllowDecision decision, string target) =>
        LogPrefix + "promoted " + fileName + " sha256=" + ReloadAllowPolicy.Short(sha256)
        + " size=" + size.ToString(CultureInfo.InvariantCulture)
        + " allowlist=" + ReloadAllowPolicy.Token(decision)
        + " from " + PendingFolderName + " -> " + target
        + "; it loads this start";

    public static string RefusedLine(string fileName, string sha256, long size, AllowDecision decision) =>
        LogPrefix + "promote refused " + fileName + " sha256=" + ReloadAllowPolicy.Short(sha256)
        + " size=" + size.ToString(CultureInfo.InvariantCulture)
        + " allowlist=" + ReloadAllowPolicy.Token(decision)
        + "; left in " + PendingFolderName + ", not loaded";

    public static string SessionOnlyLine(string fileName) =>
        LogPrefix + "promote skipped " + fileName
        + ": it carries a session marker, so it was accepted for one join only; deleted, never loaded";

    /// <summary>R36 finding 1: the package survived its own deletion. The marker did too, on purpose.</summary>
    public static string SessionResidueLine(string fileName) =>
        LogPrefix + "promote could not delete " + fileName
        + ": its session marker was kept, so it is refused again at the next start and by the next join-end sweep";

    /// <summary>
    /// R37 finding 2: a session-only leftover found in the folder the reloaders scan. R38
    /// finding 6: the sentence now reports the identity that was actually checked — the marker's
    /// sha256, which these bytes matched, and the host that offered them — instead of asserting a
    /// history nothing had verified.
    /// </summary>
    public static string ReloadLeftoverLine(string fileName, string sha256, string hostKey) =>
        LogPrefix + "promote deleted " + fileName + " from " + ReloadFolderName
        + ": its session marker matches these bytes (sha256=" + ReloadAllowPolicy.Short(sha256) + ")"
        + (string.IsNullOrEmpty(hostKey) ? "" : " from host " + hostKey)
        + ", so it was accepted for one join only; removed before the first scan, never loaded";

    /// <summary>
    /// A marker with no package beside it: the join-end sweep deletes the package first, so a
    /// crash between the two leaves exactly this. Nothing to verify and nothing to delete but the
    /// marker itself.
    /// </summary>
    public static string ReloadOrphanLine(string fileName, string hostKey) =>
        LogPrefix + "promote removed a stray session marker for " + fileName + " in " + ReloadFolderName
        + (string.IsNullOrEmpty(hostKey) ? "" : " from host " + hostKey)
        + ": the package it named is already gone, so only the marker was deleted";

    /// <summary>
    /// R39 finding 4: a marker whose hash could not be read — an older build's marker, or one a
    /// pre-FMAN8 build left half written. It still says "session-only", so the package goes; it
    /// cannot say <em>which</em> bytes, so the line says the delete was made on the name alone.
    /// </summary>
    public static string ReloadUnreadableMarkerLine(string fileName, string hostKey) =>
        LogPrefix + "promote deleted " + fileName + " from " + ReloadFolderName
        + ": its session marker records no readable sha256"
        + (string.IsNullOrEmpty(hostKey) ? "" : " (host " + hostKey + ")")
        + ", so these bytes were accepted for one join only and are deleted on the marker's name alone; never loaded";

    /// <summary>
    /// R40 finding 10: the pending root's mismatch. The install swaps a new marker into place
    /// before the new package, so a crash between the two leaves the *previous* package under a
    /// marker written for bytes that never landed. The marker names its own package by sha256, so
    /// the pair is refused rather than deleted: the marker goes, the package stays, and it is
    /// promoted this start under the allowlist like any other pending file.
    /// </summary>
    public static string PendingMismatchLine(string fileName, string why) =>
        LogPrefix + "promote kept " + fileName + " in " + PendingFolderName
        + ": its session marker was written for other bytes, so only the marker was removed and the package"
        + " is promoted as a permanent install (" + why + ")";

    /// <summary>
    /// R38 finding 6: a marker that does not name the file sitting beside it. The file is not the
    /// package the marker was written for, so it is not this sweep's to delete.
    /// </summary>
    public static string ReloadMismatchLine(string fileName, string why) =>
        LogPrefix + "promote kept " + fileName + " in " + ReloadFolderName
        + ": its session marker does not match the file; left alone, and only the marker was removed (" + why + ")";

    /// <summary>
    /// R37 finding 2: the honest sentence for a leftover we could not delete. The pending root's
    /// residue is merely deferred; this one is in the folder both reloaders enumerate, so it is
    /// loaded until a delete succeeds.
    /// </summary>
    public static string ReloadResidueLine(string fileName) =>
        LogPrefix + "promote could not delete " + fileName + " from " + ReloadFolderName
        + ": its session marker was kept, but this is the folder the reloader scans, so these bytes load this start until the delete succeeds";

    /// <summary>
    /// R41 finding 4: staging files a crashed install left in the pending tree, deleted at process
    /// start and only once they are too old to belong to any install still running (R42 finding 6).
    /// The line says how many, because the only other place they could have gone is an install
    /// deleting another install's staging.
    /// </summary>
    public static string StagingLitterLine(int count) =>
        LogPrefix + "promote removed " + count.ToString(CultureInfo.InvariantCulture)
        + " staging file(s) left in " + PendingFolderName
        + " by an install that did not finish; none of them was a package anything loads";

    public static string FailedLine(string fileName, string why) =>
        LogPrefix + "promote failed " + fileName + ": " + why;

    /// <summary>The summary with no marker mismatches, for callers that only forward the line.</summary>
    public static string SummaryLine(int promoted, int refused, int swept) =>
        SummaryLine(promoted, refused, swept, 0);

    public static string SummaryLine(int promoted, int refused, int swept, int markerMismatched) =>
        LogPrefix + "pending promote at start: promoted=" + promoted.ToString(CultureInfo.InvariantCulture)
        + " refused=" + refused.ToString(CultureInfo.InvariantCulture)
        + " session-only removed=" + swept.ToString(CultureInfo.InvariantCulture)
        + " marker mismatched=" + markerMismatched.ToString(CultureInfo.InvariantCulture);

    private static bool TryDelete(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
