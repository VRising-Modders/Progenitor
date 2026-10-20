using System;
using System.IO;
using System.Security.Cryptography;
using Progenitor.Install.Admission;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// HOT1: the apply-now executor. Given a plan whose decision is
/// <see cref="InstallDecision.ApplyNow"/> with an <see cref="AdmissionDecision.ApplyNowEligible"/>
/// admission, it writes the approved plugin image into <c>BepInEx/progenitor-reload/</c> of the
/// process that will load it (temp file plus move, hash verified after the move) and records the
/// file in that root's install ledger as <c>mod</c> under the same
/// <c>package:&lt;Owner-Name&gt;@&lt;version&gt;</c> source the restart route uses, so Remove takes
/// it back through the ordinary ledger path. <see cref="Reload.PluginReloader"/> scans that folder
/// once a second and loads the image; nothing here loads, reflects over or executes the bytes.
/// <para>Only the root this process runs is hot loaded. A local-server root that is a different
/// process is not this process's reload folder: that half falls back to
/// <see cref="InstallStaging.ExecuteToPending"/> and the plan says so
/// (<see cref="InstallReasons.HotLoadNotThisProcess"/>).</para>
/// </summary>
internal static class HotLoadExecutor
{
    /// <summary>
    /// Can the executor hot load <paramref name="target"/> from this process? The client half is
    /// this process only on a client; a local-server half only when this process <em>is</em> that
    /// server. Anything else installs to pending for that process's next start.
    /// </summary>
    public static bool CanHotLoad(InstallTarget target, bool thisProcessIsServer, bool localServerHot = false) => target switch
    {
        // HOTDEMO1: or the local server folder's reload folder is watched by a running Progenitor
        // server this client can see (HotLoadTrust.ProbeLocalServer).
        InstallTarget.LocalServer => thisProcessIsServer || localServerHot,
        _ => !thisProcessIsServer,
    };

    /// <summary>
    /// Does this plan authorize a same-session write into <c>progenitor-reload/</c>? Both the
    /// decision, the executor flag and the admission decision have to agree; a
    /// <see cref="AdmissionDecision.Unknown"/> or <see cref="AdmissionDecision.Blocked"/>
    /// admission never reaches here.
    /// </summary>
    public static bool IsHotLoadPlan(InstallPlan? plan) =>
        plan != null
        && plan.Decision == InstallDecision.ApplyNow
        && plan.ExecutorAvailable
        && plan.Admission != null
        && plan.Admission.Decision == AdmissionDecision.ApplyNowEligible;

    /// <summary>
    /// HOT4: does the half for <paramref name="half"/> authorize a same-session write? A plan with no
    /// halves (the legacy eligibility route, and any plan built without admission) answers yes, which
    /// is the pre-HOT4 behavior; a plan with halves answers for that process alone, so a mixed Both
    /// plan hot loads the client and writes the local server's pending file.
    /// </summary>
    public static bool HalfApplies(InstallPlan? plan, InstallTarget half)
    {
        if (plan == null) return false;
        if (plan.Halves.Count == 0) return true;
        var row = plan.HalfFor(half);
        return row != null && row.Decision == InstallDecision.ApplyNow;
    }

    /// <summary>The reload file this plan's image would be written to under one root.</summary>
    public static bool TryReloadPath(string bepInExRoot, InstallPlan plan, out string reloadFile, out string? error)
    {
        reloadFile = "";
        error = null;
        if (string.IsNullOrEmpty(plan.PluginFileName))
        {
            error = InstallReasons.NotInstallableNoId;
            return false;
        }

        if (ModFolders.IsReservedFileName(plan.PluginFileName!)
            || !string.Equals(ModManifestIds.SafeFileName(plan.PluginFileName!), plan.PluginFileName, StringComparison.Ordinal))
        {
            error = InstallReasons.ReservedFileName;
            return false;
        }

        string reloadRoot = InstallStaging.ReloadRoot(bepInExRoot);
        string candidate = Path.GetFullPath(Path.Combine(reloadRoot, plan.PluginFileName!));
        if (!ModManifestIds.IsUnder(reloadRoot, candidate))
        {
            error = InstallReasons.PreflightFailed;
            return false;
        }

        reloadFile = candidate;
        return true;
    }

    /// <summary>
    /// Write the approved image into <paramref name="bepInExRoot"/>'s reload folder and ledger it.
    /// The zip is re-hashed first, so bytes that changed after approval are refused and nothing is
    /// written. Idempotent: an identical file already there is ledgered and reported as already
    /// present. The result carries the path, the sha256 and <c>hotLoaded: true</c>.
    /// </summary>
    public static ExecuteCallResult ExecuteToReload(
        string bepInExRoot,
        InstallPlan plan,
        PackageInspection inspection,
        string zipPath,
        Func<RuntimeGenerationSnapshot?>? recapture = null,
        string? approvedLoaderState = null,
        bool crossProcess = false)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (inspection == null) throw new ArgumentNullException(nameof(inspection));

        if (!IsHotLoadPlan(plan))
            return new ExecuteCallResult(false, InstallReasons.HotLoadNotAdmitted, null, false);

        // HOT2 (handoff item 4): recheck the loader state immediately before the write, so an
        // admission decided a moment ago cannot be written into a Core swap, a retiring plugin ALC
        // or a generation that moved. Nothing is written when it drifts.
        if (!TryRecheck(plan, recapture, approvedLoaderState, out bool rechecked, out string? driftError))
            return new ExecuteCallResult(false, driftError, null, false);

        string? dllSha = InstallStaging.InstallableDllHash(plan, inspection);
        if (string.IsNullOrEmpty(dllSha))
            return new ExecuteCallResult(false, InstallReasons.NotInstallableNoHash, null, false);

        if (!File.Exists(zipPath))
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);

        if (!string.Equals(CatalogFetcher.HashFile(zipPath), inspection.ZipSha256, StringComparison.OrdinalIgnoreCase))
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);

        if (!TryReloadPath(bepInExRoot, plan, out string reloadFile, out string? pathError))
            return new ExecuteCallResult(false, pathError ?? InstallReasons.PreflightFailed, null, false);

        if (File.Exists(reloadFile))
        {
            if (!string.Equals(CatalogFetcher.HashFile(reloadFile), dllSha, StringComparison.OrdinalIgnoreCase))
                return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);
            Ledger(bepInExRoot, plan, reloadFile, dllSha!, new FileInfo(reloadFile).Length, crossProcess);
            return new ExecuteCallResult(
                true, null, reloadFile, alreadyPresent: true, hotLoaded: true, sha256: dllSha, rechecked: rechecked);
        }

        byte[]? bytes;
        try
        {
            bytes = InstallStaging.ExtractInstallableDll(zipPath, plan, inspection);
        }
        catch (Exception e)
        {
            return new ExecuteCallResult(false, InstallReasons.PreflightFailed + ": " + e.Message, null, false);
        }

        if (bytes == null)
            return new ExecuteCallResult(false, InstallReasons.NotInstallableNoHash, null, false);

        string extracted;
        using (var sha = SHA256.Create())
            extracted = CatalogFetcher.ToHex(sha.ComputeHash(bytes));
        if (!string.Equals(extracted, dllSha, StringComparison.OrdinalIgnoreCase))
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);

        try
        {
            InstallStaging.AtomicWrite(reloadFile, bytes);
        }
        catch (Exception e)
        {
            InstallStaging.TryDelete(reloadFile);
            return new ExecuteCallResult(false, InstallReasons.FailedWriteNothing + ": " + e.Message, null, false);
        }

        if (!string.Equals(CatalogFetcher.HashFile(reloadFile), dllSha, StringComparison.OrdinalIgnoreCase))
        {
            InstallStaging.TryDelete(reloadFile);
            return new ExecuteCallResult(false, InstallReasons.ChangedArtifact, null, false);
        }

        Ledger(bepInExRoot, plan, reloadFile, dllSha!, bytes.LongLength, crossProcess);
        return new ExecuteCallResult(
            true, null, reloadFile, alreadyPresent: false, hotLoaded: true, sha256: dllSha, rechecked: rechecked);
    }

    /// <summary>
    /// HOT2: compare a freshly captured Bootstrap snapshot against the loader state the plan's
    /// admission was bound to (<see cref="BootstrapAdmissionSnapshotSource.LoaderStateFact"/>).
    ///
    /// <para>Three outcomes. No <paramref name="recapture"/> — the caller has no snapshot source, so
    /// there is nothing to recheck and nothing could have been admitted either: pass, with
    /// <paramref name="rechecked"/> false. A recapture that matches: pass, <paramref name="rechecked"/>
    /// true. Anything else — the key moved, the snapshot went away, or the plan carried no key —
    /// refuses with <see cref="AdmissionReasonCode.SnapshotChanged"/> and both keys in the text.</para>
    /// </summary>
    /// <param name="approvedLoaderState">
    /// The loader state the <b>approval</b> was bound to. It has to be passed in, because
    /// <paramref name="plan"/> is the plan the caller recomputed at execution time and its own
    /// <c>loaderState</c> fact therefore already describes the snapshot as it is <em>now</em> —
    /// comparing that against a fresh capture compares the new state with itself and can never drift.
    /// (The plan hash does not close this either: the generation is not one of its inputs, so a Core
    /// swap alone leaves the recomputed hash equal and the approval intact.) Null falls back to the
    /// plan's own fact, which is only correct when the caller passed the approved plan.
    /// </param>
    internal static bool TryRecheck(
        InstallPlan plan,
        Func<RuntimeGenerationSnapshot?>? recapture,
        string? approvedLoaderState,
        out bool rechecked,
        out string? error)
    {
        rechecked = false;
        error = null;
        if (recapture == null) return true;

        RuntimeGenerationSnapshot? now;
        try { now = recapture(); }
        catch (Exception e)
        {
            error = Refusal("the recapture threw: " + e.Message);
            return false;
        }

        // A source that answers null is one that has none to give; treat it as "no recheck wired"
        // rather than as drift, which is the same case as a null recapture delegate.
        if (now == null) return true;

        string? approved = approvedLoaderState;
        if (string.IsNullOrEmpty(approved))
            plan.Admission!.Facts.TryGetValue(BootstrapAdmissionSnapshotSource.LoaderStateFact, out approved);
        if (BootstrapAdmissionSnapshotSource.SameLoaderState(approved, now))
        {
            rechecked = true;
            return true;
        }

        error = Refusal(BootstrapAdmissionSnapshotSource.DescribeDrift(approved, now));
        return false;
    }

    private static string Refusal(string detail) =>
        AdmissionReason.Of(AdmissionReasonCode.SnapshotChanged, detail).Text;

    /// <summary>
    /// The ledger row for a hot-loaded file: category <c>mod</c> (the file is live, not waiting for
    /// a promote) under the package source the restart route and Remove both use.
    /// </summary>
    /// <param name="crossProcess">
    /// HOTDEMO1: the root belongs to another running process (the local server written from a
    /// client). That process loads the file, not this one, so the row carries no <c>hot</c> flag and
    /// that root's own flags are left for its process to sweep.
    /// </param>
    private static void Ledger(string bepInExRoot, InstallPlan plan, string file, string sha256, long size, bool crossProcess = false)
    {
        string version = string.IsNullOrWhiteSpace(plan.Entry.Manifest.Version) ? "0" : plan.Entry.Manifest.Version;
        ServerInstall.TryAppendInstalledFile(
            bepInExRoot,
            file,
            sha256,
            size,
            LedgerSpelling.SourceFor(plan.Entry, version),
            ServerInstallLedger.CategoryMod,
            // INSTALL2 defect A: this write is what makes the file hot — this process was already
            // running, so it loaded the assembly and can drop it again. Nothing else may claim that:
            // the flag is cleared for every row at the next start (ServerInstall.ClearHotFlags).
            hot: !crossProcess,
            // POLISH4 item 1: the plugin id this route already knows, recorded beside the listing key
            // rather than in place of it.
            pluginId: plan.PluginId,
            sweepHotFlags: !crossProcess,
            // INDEX1: the hot route is an Index install (or the local test source's); the row says which.
            origin: IndexCatalog.LedgerOriginOf(plan.Entry));
    }
}
