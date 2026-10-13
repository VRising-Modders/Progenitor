using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// Writes approved artifacts under <c>BepInEx/progenitor-staging/index/…</c> and, on confirm,
/// into <c>BepInEx/progenitor-pending/index/</c> for restart installs. This job never writes
/// <c>progenitor-reload/</c> — same-session hot load is a later coordinator integration.
/// </summary>
internal static class InstallStaging
{
    public const string HostKey = InstallReasons.IndexHostKey;

    public static string StagingRoot(string bepInExRoot) =>
        Path.Combine(Path.GetFullPath(bepInExRoot), ModFolders.StagingFolderName);

    public static string PendingRoot(string bepInExRoot) =>
        Path.Combine(Path.GetFullPath(bepInExRoot), ModManifestPending.PendingFolderName);

    public static string ReloadRoot(string bepInExRoot) =>
        Path.Combine(Path.GetFullPath(bepInExRoot), ModFolders.ReloadFolderName);

    /// <summary>
    /// Copy the installable DLL from the inspected zip into staging under the <c>index</c> host
    /// key, then into pending. Returns the pending path. Idempotent: a matching file already
    /// present is left alone. A failed preflight writes nothing.
    /// </summary>
    /// <param name="ownRoot">
    /// INSTALLFAIL1: <paramref name="bepInExRoot"/> is the root of the process running this call
    /// (the client's own folder on a client). A restart install there is ledgered on that root
    /// whatever the plan's target, and the ledger is created when the root has none.
    /// </param>
    public static ExecuteCallResult ExecuteToPending(
        string bepInExRoot,
        InstallPlan plan,
        PackageInspection inspection,
        string zipPath,
        bool ownRoot = false)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (inspection == null) throw new ArgumentNullException(nameof(inspection));

        if (plan.Decision == InstallDecision.UnknownBlocked)
            return new ExecuteCallResult(false, InstallReasons.PlanBlocked, null, false);

        if (string.IsNullOrEmpty(plan.PluginId) || string.IsNullOrEmpty(plan.PluginFileName))
            return new ExecuteCallResult(false, InstallReasons.NotInstallableNoId, null, false);

        if (ModFolders.IsReservedFileName(plan.PluginFileName))
            return new ExecuteCallResult(false, InstallReasons.ReservedFileName, null, false);

        string? dllSha = InstallableDllHash(plan, inspection);
        if (string.IsNullOrEmpty(dllSha))
            return new ExecuteCallResult(false, InstallReasons.NotInstallableNoHash, null, false);

        if (!File.Exists(zipPath))
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);

        string liveZipSha = CatalogFetcher.HashFile(zipPath);
        if (!string.Equals(liveZipSha, inspection.ZipSha256, StringComparison.OrdinalIgnoreCase))
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);

        if (!TryPreflight(bepInExRoot, plan, out var stagingFile, out var pendingFile, out var error))
            return new ExecuteCallResult(false, error ?? InstallReasons.PreflightFailed, null, false);

        if (File.Exists(pendingFile))
        {
            string existing = CatalogFetcher.HashFile(pendingFile);
            if (string.Equals(existing, dllSha, StringComparison.OrdinalIgnoreCase))
            {
                AppendServerLedger(bepInExRoot, plan, pendingFile, dllSha, new FileInfo(pendingFile).Length, ownRoot);
                var companionError = WriteCompanions(bepInExRoot, plan, zipPath, pendingFile);
                if (companionError != null)
                    return new ExecuteCallResult(false, companionError, null, false);
                return new ExecuteCallResult(true, null, pendingFile, alreadyPresent: true);
            }

            // AUTOUPDATE2 (phase 1 open issue 1): a copy of the same package Progenitor queued here earlier (a pending
            // ledger row with these bytes) is superseded by this newer approved one; its row is replaced below. Anything
            // else under this name (other bytes, another package, no row) is still refused.
            string? game = ServerInstall.GameRootOf(bepInExRoot);
            if (string.IsNullOrEmpty(game) || PendingSupersede.OwnRow(game!, plan.Entry.Key, pendingFile, out string why) == null)
                return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);
            InstallLog.Info("Install: " + plan.Entry.Key + " replaces its own pending copy " + pendingFile + " (superseded, not loaded yet).");
            _ = why;
        }

        byte[]? bytes;
        try
        {
            bytes = ExtractInstallableDll(zipPath, plan, inspection);
        }
        catch (Exception e)
        {
            return new ExecuteCallResult(false, InstallReasons.PreflightFailed + ": " + e.Message, null, false);
        }

        if (bytes == null)
            return new ExecuteCallResult(false, InstallReasons.NotInstallableNoHash, null, false);

        string extractedSha;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            extractedSha = CatalogFetcher.ToHex(sha.ComputeHash(bytes));
        if (!string.Equals(extractedSha, dllSha, StringComparison.OrdinalIgnoreCase))
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);

        try
        {
            AtomicWrite(stagingFile, bytes);
            string stagedSha = CatalogFetcher.HashFile(stagingFile);
            if (!string.Equals(stagedSha, dllSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(stagingFile);
                return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);
            }

            AtomicWrite(pendingFile, bytes);
            string pendingSha = CatalogFetcher.HashFile(pendingFile);
            if (!string.Equals(pendingSha, dllSha, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(pendingFile);
                return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);
            }
        }
        catch (Exception e)
        {
            TryDelete(stagingFile);
            TryDelete(pendingFile);
            return new ExecuteCallResult(false, InstallReasons.FailedWriteNothing + ": " + e.Message, null, false);
        }

        if (IsUnderReload(bepInExRoot, stagingFile) || IsUnderReload(bepInExRoot, pendingFile))
        {
            TryDelete(stagingFile);
            TryDelete(pendingFile);
            return new ExecuteCallResult(false, InstallReasons.ReloadFolderForbidden, null, false);
        }

        AppendServerLedger(bepInExRoot, plan, pendingFile, dllSha, bytes.LongLength, ownRoot);

        // INSTORD1: the companions the approved layout found beside the plugin go next to it in
        // the same flat pending folder, so the promote lands them beside the plugin in
        // progenitor-reload/ where the loader's Resolving handler can find them.
        var companionFailure = WriteCompanions(bepInExRoot, plan, zipPath, pendingFile);
        if (companionFailure != null)
        {
            TryDelete(pendingFile);
            TryDelete(stagingFile);
            return new ExecuteCallResult(false, companionFailure, null, false);
        }

        return new ExecuteCallResult(true, null, pendingFile, alreadyPresent: false);
    }

    /// <summary>
    /// SERVERINST1: a pending file written for the <see cref="InstallTarget.LocalServer"/> (or
    /// Both) target under a root that carries Progenitor's server ledger is recorded there as
    /// <c>pending</c>, so the server uninstall can remove it. A client root has no ledger and
    /// is untouched.
    /// </summary>
    private static void AppendServerLedger(
        string bepInExRoot, InstallPlan plan, string pendingFile, string sha256, long size, bool ownRoot = false)
    {
        string version = string.IsNullOrWhiteSpace(plan.Entry.Manifest.Version) ? "0" : plan.Entry.Manifest.Version;

        // INSTORD1: the ordinary-package route owns every file it writes on whichever root it is
        // writing to, client or server, and the ledger is created when the root has none. The
        // source names the *package* (the thing a dependency and Remove refer to), not the plugin
        // GUID. The progenitor.json route keeps its existing server-only append.
        if (plan.Ordinary != null)
        {
            ServerInstall.TryAppendInstalledFile(
                bepInExRoot,
                pendingFile,
                sha256,
                size,
                LedgerSpelling.SourceFor(plan.Entry, version),
                ServerInstallLedger.CategoryPending,
                pluginId: plan.PluginId);
            return;
        }

        // INSTALLFAIL1 (live 2026-10-03): a progenitor.json package written for the restart route on
        // this process's own root (the Client cell's Install of HotDemo) used to return here
        // unledgered. The file was on disk and would be promoted at the next start, but the per-side
        // row reads its halves from the ledger, so the Client cell kept reading `Missing: the server
        // has it` with Install while the bar counted `1 mod installed - restart the game`, and
        // Uninstall could never take the copy back. The own root is ledgered exactly as the hot and
        // ordinary routes ledger it (the ledger is created when absent).
        if (ownRoot || plan.Target == InstallTarget.Client)
        {
            if (!ServerInstall.TryAppendInstalledFile(
                    bepInExRoot,
                    pendingFile,
                    sha256,
                    size,
                    LedgerSpelling.SourceFor(plan.Entry, version),
                    ServerInstallLedger.CategoryPending,
                    pluginId: plan.PluginId))
                InstallLog.Warn("Install: wrote " + pendingFile + " but could not record it in the install ledger;"
                    + " the row cannot show or remove that copy.");
            return;
        }

        // POLISH4 item 1: the listing key, exactly as the ordinary and hot routes write it, with the
        // plugin id in the row's own `pluginId` field. This line used to spell the source with the
        // plugin id, and live (deploy #76) HotDemo's local-server row — the only row of its Both
        // install on that root — was then unreachable from a fresh session's catalog entry: the
        // Install tab read `halves {server: none}` and POLISH3's per-half Uninstall did nothing.
        if (!ServerInstall.TryAppendPending(
                bepInExRoot,
                pendingFile,
                sha256,
                size,
                LedgerSpelling.SourceFor(plan.Entry, version),
                plan.PluginId))
            InstallLog.Warn("Install: wrote " + pendingFile + " but that server folder has no readable install ledger;"
                + " the row cannot show or remove that copy.");
    }

    /// <summary>
    /// INSTORD1: extract and write every companion DLL of an approved ordinary package beside the
    /// plugin's pending file, verify each by hash, and ledger it. Returns null on success or the
    /// refusal sentence; on a refusal the companions this call wrote are removed again.
    /// </summary>
    internal static string? WriteCompanions(string bepInExRoot, InstallPlan plan, string zipPath, string pendingFile)
    {
        var ordinary = plan.Ordinary;
        if (ordinary == null || ordinary.Companions.Count == 0) return null;

        string folder = Path.GetDirectoryName(pendingFile) ?? "";
        if (folder.Length == 0) return InstallReasons.PreflightFailed;

        var written = new List<string>();
        try
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: false);
            for (int i = 0; i < ordinary.Companions.Count; i++)
            {
                var companion = ordinary.Companions[i];
                if (ModFolders.IsReservedFileName(companion.FileName)
                    || !string.Equals(ModManifestIds.SafeFileName(companion.FileName), companion.FileName, StringComparison.Ordinal))
                {
                    RollBack(written);
                    return InstallReasons.ReservedFileName;
                }

                string target = Path.GetFullPath(Path.Combine(folder, companion.FileName));
                if (!ModManifestIds.IsUnder(PendingRoot(bepInExRoot), target) || IsUnderReload(bepInExRoot, target))
                {
                    RollBack(written);
                    return InstallReasons.ReloadFolderForbidden;
                }

                var entry = zip.GetEntry(companion.Destination);
                if (entry == null)
                {
                    RollBack(written);
                    return InstallReasons.ChangedArtifact;
                }

                byte[] bytes = PackageInspector.ReadEntry(entry);
                if (!string.Equals(PackageInspector.HashBytes(bytes), companion.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    RollBack(written);
                    return InstallReasons.ChangedArtifact;
                }

                if (File.Exists(target)
                    && string.Equals(CatalogFetcher.HashFile(target), companion.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    AppendServerLedger(bepInExRoot, plan, target, companion.Sha256, bytes.LongLength);
                    continue;
                }

                AtomicWrite(target, bytes);
                written.Add(target);
                if (!string.Equals(CatalogFetcher.HashFile(target), companion.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    RollBack(written);
                    return InstallReasons.ChangedArtifact;
                }

                AppendServerLedger(bepInExRoot, plan, target, companion.Sha256, bytes.LongLength);
            }
        }
        catch (Exception e)
        {
            RollBack(written);
            return InstallReasons.FailedWriteNothing + ": " + e.Message;
        }

        return null;
    }

    private static void RollBack(List<string> written)
    {
        for (int i = 0; i < written.Count; i++) TryDelete(written[i]);
    }

    internal static bool TryPreflight(
        string bepInExRoot,
        InstallPlan plan,
        out string stagingFile,
        out string pendingFile,
        out string? error)
    {
        stagingFile = "";
        pendingFile = "";
        error = null;

        string stagingRoot = StagingRoot(bepInExRoot);
        string pendingRoot = PendingRoot(bepInExRoot);
        string reloadRoot = ReloadRoot(bepInExRoot);
        string version = string.IsNullOrWhiteSpace(plan.Entry.Manifest.Version) ? "0" : plan.Entry.Manifest.Version;
        string host = ModManifestIds.NormalizeHostKey(HostKey);

        if (!ModManifestIds.TryStagingPath(
                stagingRoot,
                host,
                plan.PluginId!,
                version,
                plan.PluginFileName!,
                out _,
                out stagingFile,
                out error))
        {
            stagingFile = "";
            return false;
        }

        if (!ModManifestIds.TryStagingInspectionChain(stagingRoot, stagingFile, out _, out error))
        {
            stagingFile = "";
            return false;
        }

        pendingFile = Path.GetFullPath(Path.Combine(pendingRoot, host, plan.PluginFileName!));
        if (!ModManifestIds.IsUnder(pendingRoot, pendingFile))
        {
            error = "pending path escaped progenitor-pending";
            stagingFile = "";
            pendingFile = "";
            return false;
        }

        if (ModManifestIds.IsUnder(reloadRoot, stagingFile) || ModManifestIds.IsUnder(reloadRoot, pendingFile))
        {
            error = InstallReasons.ReloadFolderForbidden;
            stagingFile = "";
            pendingFile = "";
            return false;
        }

        return true;
    }

    internal static string? InstallableDllHash(InstallPlan plan, PackageInspection inspection)
    {
        string? declared = inspection.ProgenitorManifest?.Sha256;
        if (!string.IsNullOrEmpty(declared)
            && !string.Equals(declared, inspection.ZipSha256, StringComparison.OrdinalIgnoreCase))
            return declared;

        if (!string.IsNullOrEmpty(plan.PluginFileName))
        {
            for (int i = 0; i < inspection.DllHashes.Count; i++)
            {
                if (string.Equals(inspection.DllHashes[i].FileName, plan.PluginFileName, StringComparison.OrdinalIgnoreCase))
                    return inspection.DllHashes[i].Sha256;
            }
        }

        if (inspection.DllHashes.Count == 1) return inspection.DllHashes[0].Sha256;
        return declared;
    }

    internal static byte[]? ExtractInstallableDll(string zipPath, InstallPlan plan, PackageInspection inspection)
    {
        string? wantHash = InstallableDllHash(plan, inspection);
        using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in zip.Entries)
        {
            if (PackageInspector.IsDirectory(entry) || PackageInspector.IsSymlink(entry)) continue;
            if (!PackageInspector.TryNormalizeEntryPath(entry.FullName ?? "", out var relative, out _))
                continue;
            if (!relative.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase))
                continue;
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var open = entry.Open();
            using var ms = new MemoryStream();
            open.CopyTo(ms);
            byte[] bytes = ms.ToArray();
            string hex = CatalogFetcher.ToHex(sha.ComputeHash(bytes));
            if (wantHash != null && string.Equals(hex, wantHash, StringComparison.OrdinalIgnoreCase))
                return bytes;
            if (wantHash == null
                && plan.PluginFileName != null
                && string.Equals(Path.GetFileName(relative), plan.PluginFileName, StringComparison.OrdinalIgnoreCase))
                return bytes;
        }
        return null;
    }

    internal static void AtomicWrite(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        Directory.CreateDirectory(dir);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    internal static bool IsUnderReload(string bepInExRoot, string path) =>
        ModManifestIds.IsUnder(ReloadRoot(bepInExRoot), path);

    internal static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
