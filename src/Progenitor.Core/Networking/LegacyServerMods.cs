using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Progenitor.Reload;
#if !PROGENITOR_TESTS
using BepInEx;
#endif

namespace Progenitor.Networking;

/// <summary>One package an older Core installed because a server sent it (a row of <c>progenitor-server-mods.txt</c>). BCL strings only.</summary>
public sealed class InstalledServerMod
{
    public InstalledServerMod(
        string id,
        string version,
        string fileName,
        string sha256,
        string hostKey,
        string host,
        bool always,
        string installedUtc)
    {
        Id = id ?? "";
        Version = version ?? "";
        FileName = fileName ?? "";
        Sha256 = (sha256 ?? "").Trim().ToLowerInvariant();
        HostKey = hostKey ?? "";
        Host = host ?? "";
        Always = always;
        InstalledUtc = installedUtc ?? "";
    }

    public string Id { get; }
    public string Version { get; }
    public string FileName { get; }
    public string Sha256 { get; }
    public string HostKey { get; }
    public string Host { get; }

    /// <summary>True when the player had chosen "Always": an allowlist row may carry the package.</summary>
    public bool Always { get; }

    public string InstalledUtc { get; }

    public string ShaPrefix => ModText.ShaPrefix(Sha256);
}

/// <summary>What the one-time cleanup did.</summary>
internal sealed class LegacyCleanupResult
{
    /// <summary>Files deleted (server-sent packages and their session markers).</summary>
    public List<string> Deleted { get; } = new();

    /// <summary>Record rows dropped (their file deleted, already gone, or replaced by other bytes).</summary>
    public int RowsDropped { get; set; }

    /// <summary>Record rows kept for the next start because a file could not be deleted now.</summary>
    public int RowsKept { get; set; }

    /// <summary>One line per row, for the log.</summary>
    public List<string> Lines { get; } = new();

    public bool Nothing => RowsDropped == 0 && RowsKept == 0 && Deleted.Count == 0;
}

/// <summary>
/// The records an older Core kept of mods a <b>server sent</b> (removed 2026-10-05: mods come only from Thunderstore,
/// and a server never sends mod files). What is left is the record format, so the version store can still read the
/// rows parked copies carry, and the <b>one-time cleanup</b> that removes what those older builds installed.
///
/// <para><b>Why a cleanup at start rather than a read-only Remove list.</b> A server-sent DLL in
/// <c>progenitor-reload/</c> keeps loading on every start until something deletes it; a list that waits for the player
/// to press Remove would keep running code from a source this build no longer trusts, possibly forever (most players
/// never open that page). The cleanup is narrow: it acts only on rows of <c>progenitor-server-mods.txt</c>, which only
/// the server-copy install ever wrote, and deletes a file only when its bytes still hash to the sha256 that row
/// recorded <b>and</b> the install ledger (<c>progenitor-install.json</c>) does not record that path with those same
/// bytes. A file with other bytes under the same name (the player's own build), or a Thunderstore install the ledger
/// tracks (even one with identical bytes), is left alone with its allowlist row; only the record row is dropped. A
/// record row without a sha256 deletes nothing and is kept, with a warning. The ledger and the version store's user
/// copies are never written here. Each deletion is logged with the file and the server it came from.</para>
/// </summary>
internal static class LegacyServerMods
{
    /// <summary>Record of installs an older Core made from a server, next to the trust file. Tab separated.</summary>
    public const string RecordFileName = "progenitor-server-mods.txt";

    /// <summary>The marker an older Core wrote beside a session-only server install.</summary>
    public const string SessionMarkerSuffix = ".progenitor-session";

    private const string RecordHeader =
        "# progenitor-server-mods.txt — packages an older Progenitor installed from a server (job FMAN3; removed 2026-10-05).\n"
        + "# sha256\tfileName\tid\tversion\thostKey\thost\talways\tinstalledUtc\n";

    /// <summary>The log line's head, so one grep finds the whole cleanup.</summary>
    public const string LogPrefix = "ModManifest: legacy server mods: ";

#if PROGENITOR_TESTS
    internal static string? TestReloadDirectory;
#endif

    /// <summary>Path of the record file.</summary>
    public static string RecordPath => Path.Combine(ModManifest.ConfigDirectory, RecordFileName);

    /// <summary><c>BepInEx/progenitor-reload/</c>.</summary>
    public static string ReloadDirectory
    {
        get
        {
#if PROGENITOR_TESTS
            return TestReloadDirectory ?? Path.Combine(ModManifest.ConfigDirectory, ModFolders.ReloadFolderName);
#else
            try { return Path.Combine(Paths.BepInExRootPath, ModFolders.ReloadFolderName); }
            catch { return Path.Combine(ModManifest.ConfigDirectory, ModFolders.ReloadFolderName); }
#endif
        }
    }

    /// <summary>The reload allowlist an "Always" install appended a row to when one existed.</summary>
    public static string AllowListPath => Path.Combine(ModManifest.ConfigDirectory, ReloadAllowPolicy.FileName);

    /// <summary>Every row still recorded, oldest first.</summary>
    public static IReadOnlyList<InstalledServerMod> Installed() => ReadRecord(RecordPath);

    /// <summary>
    /// The one-time cleanup, live paths. Runs once per Core generation on the client, before the version store
    /// settles (so a server copy recorded as active and now gone gets the player's kept copy back). Logs one line per
    /// row and a summary; silent when there is no record.
    /// </summary>
    internal static LegacyCleanupResult CleanUpAtStart(Action<string>? info, Action<string>? warn)
    {
        string reload = ReloadDirectory;
        Func<string, string?>? ledger = null;
        try
        {
            // BepInEx/progenitor-reload → the game root, where the install ledger lives (BepInEx/progenitor-install.json).
            string? gameRoot = Directory.GetParent(Directory.GetParent(Path.GetFullPath(reload))?.FullName ?? "")?.FullName;
            if (!string.IsNullOrEmpty(gameRoot)) ledger = LedgerShaLookup(gameRoot!);
        }
        catch { ledger = null; }

        return CleanUp(RecordPath, ModManifestPending.PendingDirectory, reload, AllowListPath, info, warn, ledger);
    }

    /// <summary>
    /// The install ledger's sha256 per absolute file path (<c>progenitor-install.json</c> under <paramref name="gameRoot"/>),
    /// or null when there is no readable ledger. A file the ledger records is a Thunderstore (or other tracked) install.
    /// </summary>
    internal static Func<string, string?>? LedgerShaLookup(string gameRoot)
    {
        Install.ServerInstallLedger? ledger;
        try { ledger = Install.ServerInstallLedger.TryRead(gameRoot); }
        catch { ledger = null; }
        if (ledger == null) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ledger.Files)
        {
            if (e == null || string.IsNullOrEmpty(e.Path)) continue;
            try { map[Install.ServerInstallLedger.Absolute(gameRoot, e.Path)] = e.Sha256 ?? ""; }
            catch { /* a malformed row names no file */ }
        }

        return path =>
        {
            try { return map.TryGetValue(Path.GetFullPath(path), out var sha) ? sha : null; }
            catch { return null; }
        };
    }

    /// <summary>
    /// Remove what an older Core installed from a server: for each record row, the package in the reload folder and
    /// in <c>progenitor-pending/&lt;host&gt;/</c> when its bytes still hash to the row's sha256, the session markers
    /// beside them, and its allowlist row. A row whose files are gone (or were replaced by other bytes) is dropped; a
    /// row whose file could not be deleted stays for the next start. The record file is deleted once empty.
    /// </summary>
    internal static LegacyCleanupResult CleanUp(
        string recordPath, string pendingRoot, string reloadRoot, string allowListPath,
        Action<string>? info, Action<string>? warn, Func<string, string?>? ledgerShaOf = null)
    {
        var result = new LegacyCleanupResult();
        var rows = ReadRecord(recordPath);
        if (rows.Count == 0)
        {
            // A record with only its header (or nothing readable) is left as it is: nothing in it names a file.
            return result;
        }

        var keep = new List<InstalledServerMod>();
        foreach (var row in rows)
        {
            string name = SafeLeaf(row.FileName);
            if (name.Length == 0)
            {
                result.RowsDropped++;
                result.Lines.Add(row.Id + ": the record names no usable file; row dropped");
                continue;
            }

            if (row.Sha256.Length != ReloadAllowPolicy.HashLength)
            {
                // No hash: the server's bytes cannot be told from anything else under that name, so nothing is deleted,
                // and the row stays so the player (or a later build) can still see what a server once installed.
                string kept = row.Id + " " + row.Version + " (" + name + "): the record has no sha256, so nothing was deleted; row kept. "
                    + "Remove the file from BepInEx/" + ModFolders.ReloadFolderName + " yourself if a server sent it.";
                keep.Add(row);
                result.RowsKept++;
                result.Lines.Add(kept);
                warn?.Invoke(LogPrefix + kept);
                continue;
            }

            bool failed = false;
            bool tracked = false;
            var notes = new List<string>();
            var candidates = new List<string>
            {
                Path.Combine(reloadRoot, name),
                Path.Combine(pendingRoot, ModManifestIds.NormalizeHostKey(row.HostKey), name),
            };
            foreach (string path in candidates)
            {
                // A Thunderstore install of the same bytes under the same name is recorded in the install ledger: it is
                // the player's install now, not the server's copy, and is never deleted here.
                string? ledgerSha = null;
                try { ledgerSha = ledgerShaOf?.Invoke(path); }
                catch { ledgerSha = null; }
                if (ledgerSha != null && string.Equals(ledgerSha, row.Sha256, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    tracked = true;
                    notes.Add("left " + path + " (the install ledger records it with the same bytes: a tracked install, not the server's copy)");
                    continue;
                }

                switch (DeleteIfSame(path, row.Sha256, out string why))
                {
                    case DeleteOutcome.Deleted:
                        result.Deleted.Add(path);
                        notes.Add("deleted " + path);
                        break;
                    case DeleteOutcome.Different:
                        notes.Add("left " + path + " (" + why + ")");
                        break;
                    case DeleteOutcome.Failed:
                        failed = true;
                        notes.Add("could not delete " + path + " (" + why + ")");
                        break;
                }

                if (TryDeleteFile(path + SessionMarkerSuffix)) result.Deleted.Add(path + SessionMarkerSuffix);
            }

            // The allowlist row stays when a tracked install still uses those bytes.
            if (row.Always && !tracked && File.Exists(allowListPath))
            {
                try
                {
                    if (ReloadAllowStore.TryWriteAllowRow(allowListPath, row.Sha256, name, add: false, out var allowError))
                        notes.Add("its allowlist row was removed");
                    else if (allowError.Length > 0) notes.Add("its allowlist row was not removed (" + allowError + ")");
                }
                catch (Exception e) { notes.Add("its allowlist row was not removed (" + e.Message + ")"); }
            }

            string from = row.Host.Length > 0 ? row.Host : row.HostKey;
            string line = row.Id + " " + row.Version + " (" + name + ", sha256 " + row.ShaPrefix + ", sent by " + (from.Length > 0 ? from : "an unknown server")
                + "): " + (notes.Count == 0 ? "nothing on disk" : string.Join("; ", notes));
            if (failed)
            {
                keep.Add(row);
                result.RowsKept++;
                result.Lines.Add(line + "; kept for the next start");
                warn?.Invoke(LogPrefix + line + "; kept for the next start");
            }
            else
            {
                result.RowsDropped++;
                result.Lines.Add(line);
                info?.Invoke(LogPrefix + line);
            }
        }

        if (keep.Count == 0)
        {
            TryDeleteFile(recordPath);
            TryDeleteFile(recordPath + ".tmp");
        }
        else WriteRecordAt(recordPath, keep);

        info?.Invoke(LogPrefix + "servers no longer send mod files (mods come only from Thunderstore); removed "
            + result.Deleted.Count.ToString(CultureInfo.InvariantCulture) + " file(s) an older Progenitor installed from a server, "
            + result.RowsDropped.ToString(CultureInfo.InvariantCulture) + " record row(s) dropped"
            + (result.RowsKept > 0 ? ", " + result.RowsKept.ToString(CultureInfo.InvariantCulture) + " kept for the next start" : ""));
        return result;
    }

    private enum DeleteOutcome
    {
        Absent,
        Deleted,
        Different,
        Failed,
    }

    private static DeleteOutcome DeleteIfSame(string path, string sha256, out string why)
    {
        why = "";
        try
        {
            if (!File.Exists(path)) return DeleteOutcome.Absent;
            if (sha256.Length != ReloadAllowPolicy.HashLength)
            {
                why = "the record has no sha256, so its bytes cannot be told from another install";
                return DeleteOutcome.Different;
            }

            string live = ReloadAllowPolicy.Sha256Hex(File.ReadAllBytes(path));
            if (!string.Equals(live, sha256, StringComparison.OrdinalIgnoreCase))
            {
                why = "other bytes are there now (sha256 " + ModText.ShaPrefix(live) + "), so it is not the server's copy";
                return DeleteOutcome.Different;
            }

            File.Delete(path);
            return DeleteOutcome.Deleted;
        }
        catch (Exception e)
        {
            why = e.GetType().Name + ": " + e.Message;
            return DeleteOutcome.Failed;
        }
    }

    /// <summary>A plain file name (no folder part) that ends in .dll, or empty.</summary>
    private static string SafeLeaf(string fileName)
    {
        string leaf = (fileName ?? "").Trim();
        if (leaf.Length == 0 || leaf.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || leaf == "." || leaf == "..") return "";
        if (!leaf.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase)) return "";
        return string.Equals(ModManifestIds.SafeFileName(leaf), leaf, StringComparison.Ordinal) ? leaf : "";
    }

    // ---- record file ----

    internal static IReadOnlyList<InstalledServerMod> ReadRecord(string path)
    {
        var list = new List<InstalledServerMod>();
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;
            string[] lines = File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                // Spaces only: a row whose last columns are empty ends in tabs, and trimming those dropped the row.
                string line = lines[i].Trim(' ');
                if (line.Length == 0 || line[0] == '#') continue;
                string[] parts = line.Split('\t');
                if (parts.Length < 8) continue;
                list.Add(new InstalledServerMod(
                    parts[2], parts[3], parts[1], parts[0], parts[4], parts[5],
                    string.Equals(parts[6], "always", StringComparison.OrdinalIgnoreCase), parts[7]));
            }
        }
        catch
        {
            // A record we cannot read costs a list entry, never a decision.
        }

        return list;
    }

    /// <summary>
    /// JOINFLOW4: write the record at <paramref name="path"/> (the version store parks a copy's row with it and puts it
    /// back on a swap). Temp plus move.
    /// </summary>
    internal static void WriteRecordAt(string path, IReadOnlyList<InstalledServerMod> rows)
    {
        try
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            if (dir.Length > 0) Directory.CreateDirectory(dir);
            var sb = new StringBuilder(RecordHeader);
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append(r.Sha256).Append('\t').Append(r.FileName).Append('\t').Append(r.Id).Append('\t')
                  .Append(r.Version).Append('\t').Append(r.HostKey).Append('\t').Append(r.Host).Append('\t')
                  .Append(r.Always ? "always" : "once").Append('\t').Append(r.InstalledUtc).Append('\n');
            }

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            try { File.Move(tmp, path, overwrite: true); }
            catch
            {
                File.Copy(tmp, path, overwrite: true);
                TryDeleteFile(tmp);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(LogPrefix + "writing " + RecordFileName + " threw: " + e.Message);
        }
    }

    private static bool TryDeleteFile(string path)
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
