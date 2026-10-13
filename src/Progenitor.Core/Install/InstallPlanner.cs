using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Progenitor.Install.Admission;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// Builds an <see cref="InstallPlan"/> from a catalog entry, an inspection, and a selected
/// target. Default target is Client. Both is used only when explicitly selected. Dependencies
/// never expand targets. A Progenitor dependency alone never grants ApplyNow. Ordinary BepInEx
/// packages are restart-required at best, or UnknownBlocked when they cannot be placed.
/// </summary>
internal static class InstallPlanner
{
    public static InstallPlan Plan(
        CatalogEntry entry,
        PackageInspection inspection,
        InstallTarget target,
        IHotLoadEligibility? eligibility = null,
        bool localServerConfigured = false,
        AdmissionResult? admission = null,
        OrdinaryDependencyView? ordinaryView = null,
        bool thisProcessIsServer = false,
        AdmissionResult? serverAdmission = null,
        bool localServerHot = false)
    {
        if (entry == null) throw new ArgumentNullException(nameof(entry));
        if (inspection == null) throw new ArgumentNullException(nameof(inspection));

        eligibility ??= new UnimplementedHotLoadEligibility();
        var artifacts = BuildArtifacts(inspection);
        var dependencies = BuildDependencies(entry, inspection);

        if (!inspection.Ok)
            return Blocked(entry, inspection, target, artifacts, dependencies, inspection.Refusal ?? InstallReasons.PlanBlocked);

        string? pluginId = inspection.ProgenitorManifest?.Id;
        if (string.IsNullOrWhiteSpace(pluginId)) pluginId = entry.Manifest.Id;
        if (string.IsNullOrWhiteSpace(pluginId)) pluginId = null;

        string? fileName = null;
        string? fileNameError = null;
        if (pluginId != null
            && !ModManifestIds.TryPluginFileName(pluginId, ModFolders.ExpectedExtension, out fileName, out fileNameError))
        {
            fileName = null;
        }

        if (fileName != null && ModFolders.IsReservedFileName(fileName))
            return Blocked(entry, inspection, target, artifacts, dependencies, InstallReasons.ReservedFileName, pluginId, fileName);

        if (target == InstallTarget.LocalServer && !localServerConfigured)
            return Blocked(entry, inspection, target, artifacts, dependencies, InstallReasons.NoConfiguredLocalServer, pluginId, fileName);

        if (target == InstallTarget.Both && !localServerConfigured)
            return Blocked(entry, inspection, target, artifacts, dependencies, InstallReasons.NoConfiguredLocalServer, pluginId, fileName);

        bool hasForeignProgenitorDep = HasProgenitorForeignDependency(entry);
        bool verified = inspection.Provenance == CatalogProvenance.ManifestVerified
            && inspection.ProgenitorManifest != null
            && !string.IsNullOrEmpty(inspection.ProgenitorManifest.Sha256);

        // INSTORD1: the ordinary-package restart route. An archive whose layout and plugin
        // identity were both approved from the bytes installs the plugin DLL (and the companions
        // beside it) into Progenitor's own pending folder, to be promoted into
        // progenitor-reload/ at the next start - never into BepInEx/plugins/. Anything the
        // layout, the process declaration or a dependency refuses keeps the refusal below.
        if (!verified && inspection.OrdinaryApproved)
        {
            var ordinary = inspection.OrdinaryLayout!;
            var plugin = ordinary.Plugin!;
            if (!OrdinaryPackage.TargetRunsTheProcess(plugin.Processes, target, out var processRefusal))
                return Blocked(entry, inspection, target, artifacts, dependencies, processRefusal!, plugin.PluginGuid, plugin.FileName);

            var ordinaryDeps = OrdinaryDependencies.Evaluate(entry.ForeignDependencies, ordinaryView);
            string? depRefusal = OrdinaryDependencies.FirstRefusal(ordinaryDeps);
            if (depRefusal != null)
                return Blocked(entry, inspection, target, artifacts, dependencies, depRefusal, plugin.PluginGuid, plugin.FileName, ordinary: ordinary, ordinaryDependencies: ordinaryDeps);

            if (!ModManifestIds.TryValidateId(plugin.PluginGuid, out _)
                || !string.Equals(ModManifestIds.SafeFileName(plugin.FileName), plugin.FileName, StringComparison.Ordinal)
                || ModFolders.IsReservedFileName(plugin.FileName))
            {
                return Blocked(entry, inspection, target, artifacts, dependencies, InstallReasons.ReservedFileName, plugin.PluginGuid, plugin.FileName);
            }

            return Restart(
                entry, inspection, target, artifacts, dependencies,
                InstallReasons.OrdinaryRestartRoute + "; " + InstallReasons.OrdinaryHotSwapRefused,
                plugin.PluginGuid,
                plugin.FileName,
                ordinary: ordinary,
                ordinaryDependencies: ordinaryDeps);
        }

        if (inspection.UnsupportedLayout)
        {
            // INSTPLUG1: an ordinary package the restart route evaluated and refused says what it
            // refused (the entry it would have to place, or the patchers/core/config path) instead
            // of the generic layout sentence, which read as if BepInEx/plugins/ itself were the problem.
            string layout = !verified && inspection.OrdinaryLayout is { Ok: false, Refusal: { Length: > 0 } ordinaryRefusal }
                ? ordinaryRefusal
                : inspection.LayoutReason ?? InstallReasons.PluginsLayoutBlocked;
            return Blocked(entry, inspection, target, artifacts, dependencies, layout, pluginId, fileName);
        }

        if (!verified)
        {
            // Listing-only / ordinary BepInEx. A Progenitor dependency on the listing never
            // promotes this, and a missing hash of the installable bytes refuses Install.
            string reason;
            if (pluginId == null || fileName == null)
                reason = InstallReasons.OrdinaryBepInExBlocked;
            else if (string.IsNullOrEmpty(inspection.ProgenitorManifest?.Sha256) && inspection.DllHashes.Count == 0)
                reason = InstallReasons.NotInstallableNoHash;
            else
                reason = InstallReasons.OrdinaryBepInExBlocked;
            if (hasForeignProgenitorDep)
                reason = InstallReasons.OrdinaryBepInExBlocked;
            return Blocked(entry, inspection, target, artifacts, dependencies, reason, pluginId, fileName);
        }

        // First-class row. Missing/contradictory metadata already refused at inspect.
        if (string.IsNullOrEmpty(pluginId) || fileName == null)
            return Blocked(entry, inspection, target, artifacts, dependencies, InstallReasons.NotInstallableNoId, pluginId, fileName);

        bool declaredHot = inspection.HotLoadableDeclared && inspection.ProgenitorManifest!.HotLoadable;
        if (!declaredHot)
        {
            string reason = hasForeignProgenitorDep
                ? InstallReasons.ProgenitorDepDoesNotGrantHotLoad
                : InstallReasons.MissingMetadataNoHotLoad;
            return Restart(entry, inspection, target, artifacts, dependencies, reason, pluginId, fileName);
        }

        if (admission != null)
        {
            // HOT4: the per-process decisions. For Both, `admission` is the client half's own
            // evaluation (target Client) and `serverAdmission` the local server's (target
            // LocalServer), so a record reviewed for one half applies to that half alone and a
            // Both-scoped record still covers the two (QualificationRecord.TargetCovers).
            var halves = BuildHalves(target, admission, serverAdmission, thisProcessIsServer, localServerHot);
            // ADMIT1: the structured admission result decides. ApplyNowEligible is reachable only
            // through an exact qualification record plus an available runtime snapshot.
            // HOT1: that admission now reaches a real executor. The executor writes the reload
            // folder of the process that will load the image, so the flag is true only for a
            // target this process runs; a local-server half that is a different process keeps the
            // ApplyNow decision (admission decided it) with executorAvailable false, and Confirm
            // falls back to that root's pending folder and says so.
            switch (admission.Decision)
            {
                case AdmissionDecision.ApplyNowEligible:
                    bool hot = HotLoadExecutor.CanHotLoad(target, thisProcessIsServer, localServerHot);
                    string executorReason = hot ? InstallReasons.ExecutorHotLoad : InstallReasons.HotLoadNotThisProcess;
                    string applyReason = executorReason + " — " + admission.ReasonText;
                    // HOTDEMO1: the server half is hot too when the local server's reload folder is
                    // watched from here and its own admission agreed.
                    var serverHalf = target == InstallTarget.Both ? HalfFor(halves, InstallTarget.LocalServer) : null;
                    if (hot && target == InstallTarget.Both && !(serverHalf != null && serverHalf.ExecutorAvailable))
                        applyReason += "; " + InstallReasons.HotLoadServerHalfPending;
                    return new InstallPlan(
                        ComputePlanHash(entry, inspection, target, artifacts, InstallDecision.ApplyNow, pluginId, fileName, halves),
                        entry,
                        target,
                        InstallDecision.ApplyNow,
                        applyReason,
                        artifacts,
                        dependencies,
                        pluginId,
                        fileName,
                        admission,
                        executorAvailable: hot,
                        executorReason: executorReason,
                        ledgerClient: target != InstallTarget.LocalServer,
                        ledgerServer: target != InstallTarget.Client,
                        halves: halves);
                case AdmissionDecision.Blocked:
                    return Blocked(entry, inspection, target, artifacts, dependencies,
                        InstallReasons.AdmissionBlocked + ": " + admission.ReasonText, pluginId, fileName, admission, halves: halves);
                case AdmissionDecision.Unknown:
                    return Restart(entry, inspection, target, artifacts, dependencies,
                        InstallReasons.AdmissionUnknown + ": " + admission.ReasonText, pluginId, fileName, admission, halves: halves);
                default:
                    return Restart(entry, inspection, target, artifacts, dependencies,
                        InstallReasons.AdmissionRefusedHotLoad + ": " + admission.ReasonText, pluginId, fileName, admission, halves: halves);
            }
        }

        if (!eligibility.TryEvaluate(entry, inspection, out var eligibilityReason))
        {
            // Author claimed hot-load; runtime has not agreed. Restart path is still available.
            string reason = string.IsNullOrWhiteSpace(eligibilityReason)
                ? InstallReasons.EligibilityNotWired
                : eligibilityReason;
            return Restart(entry, inspection, target, artifacts, dependencies, reason, pluginId, fileName);
        }

        // ApplyNow is offered only when the injected eligibility agrees. This job does not
        // write progenitor-reload/; ExecuteToPending is still the restart path.
        return new InstallPlan(
            ComputePlanHash(entry, inspection, target, artifacts, InstallDecision.ApplyNow, pluginId, fileName),
            entry,
            target,
            InstallDecision.ApplyNow,
            InstallReasons.HotLoadableIsAuthorClaim,
            artifacts,
            dependencies,
            pluginId,
            fileName);
    }

    internal static InstallTarget NormalizeTarget(InstallTarget? selected) =>
        selected ?? InstallTarget.Client;

    internal static IReadOnlyList<ArtifactHash> BuildArtifacts(PackageInspection inspection)
    {
        var list = new List<ArtifactHash>(1 + inspection.DllHashes.Count)
        {
            new ArtifactHash(InstallReasons.ZipHashKind, "package.zip", inspection.ZipSha256, inspection.ZipSize),
        };
        for (int i = 0; i < inspection.DllHashes.Count; i++)
            list.Add(inspection.DllHashes[i]);
        return list;
    }

    internal static IReadOnlyList<PlanDependency> BuildDependencies(CatalogEntry entry, PackageInspection inspection)
    {
        var list = new List<PlanDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < entry.ForeignDependencies.Count; i++)
        {
            string id = entry.ForeignDependencies[i];
            if (!seen.Add(id)) continue;
            list.Add(new PlanDependency(id, DependencyResolution.ForeignListing, "Thunderstore {owner}-{name}-{version}, not a plugin GUID"));
        }

        var pluginDeps = inspection.PluginDependencies.Count > 0 ? inspection.PluginDependencies : entry.PluginDependencies;
        for (int i = 0; i < pluginDeps.Count; i++)
        {
            string id = pluginDeps[i].Id;
            if (!seen.Add("plugin:" + id)) continue;
            bool present = string.Equals(id, inspection.ProgenitorManifest?.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, entry.Manifest.Id, StringComparison.OrdinalIgnoreCase);
            list.Add(new PlanDependency(
                id,
                present ? DependencyResolution.Present : DependencyResolution.Missing,
                pluginDeps[i].Version));
        }

        if (list.Count == 0)
            list.Add(new PlanDependency("", DependencyResolution.Unknown, "dependencies unknown"));
        return list;
    }

    internal static bool HasProgenitorForeignDependency(CatalogEntry entry)
    {
        for (int i = 0; i < entry.ForeignDependencies.Count; i++)
        {
            string d = entry.ForeignDependencies[i];
            if (d.IndexOf("progenitor", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        for (int i = 0; i < entry.PluginDependencies.Count; i++)
        {
            if (string.Equals(entry.PluginDependencies[i].Id, "progenitor", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// HOT4: the per-process halves of one plan. A single target has one half; <c>Both</c> has the
    /// client's and the local server's, each decided by <em>its own</em> admission result. Mixed
    /// halves are a valid plan: client <see cref="InstallDecision.ApplyNow"/> with an executor plus
    /// server <see cref="InstallDecision.RestartServerRequired"/>.
    /// </summary>
    internal static IReadOnlyList<InstallHalf> BuildHalves(
        InstallTarget target,
        AdmissionResult clientAdmission,
        AdmissionResult? serverAdmission,
        bool thisProcessIsServer,
        bool localServerHot = false)
    {
        if (target != InstallTarget.Both)
            return new[] { HalfOf(target, clientAdmission, thisProcessIsServer, localServerHot) };
        return new[]
        {
            HalfOf(InstallTarget.Client, clientAdmission, thisProcessIsServer, localServerHot),
            // A caller that evaluated admission once (every pre-HOT4 caller, and the tests that
            // hand one result in) gets the same decision on both halves, exactly as before.
            HalfOf(InstallTarget.LocalServer, serverAdmission ?? clientAdmission, thisProcessIsServer, localServerHot),
        };
    }

    private static InstallHalf? HalfFor(IReadOnlyList<InstallHalf> halves, InstallTarget half)
    {
        for (int i = 0; i < halves.Count; i++)
            if (halves[i].Target == half) return halves[i];
        return null;
    }

    private static InstallHalf HalfOf(InstallTarget half, AdmissionResult admission, bool thisProcessIsServer, bool localServerHot = false)
    {
        switch (admission.Decision)
        {
            case AdmissionDecision.ApplyNowEligible:
                bool hot = HotLoadExecutor.CanHotLoad(half, thisProcessIsServer, localServerHot);
                return new InstallHalf(
                    half,
                    InstallDecision.ApplyNow,
                    hot,
                    (hot ? InstallReasons.ExecutorHotLoad : InstallReasons.HotLoadNotThisProcess)
                        + " — " + admission.ReasonText);
            case AdmissionDecision.Blocked:
                return new InstallHalf(
                    half, InstallDecision.UnknownBlocked, false,
                    InstallReasons.AdmissionBlocked + ": " + admission.ReasonText);
            case AdmissionDecision.Unknown:
                return new InstallHalf(
                    half, RestartDecision(half), false,
                    InstallReasons.AdmissionUnknown + ": " + admission.ReasonText + "; " + RestartReason(half));
            default:
                return new InstallHalf(
                    half, RestartDecision(half), false,
                    InstallReasons.AdmissionRefusedHotLoad + ": " + admission.ReasonText + "; " + RestartReason(half));
        }
    }

    internal static InstallDecision RestartDecision(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => InstallDecision.RestartServerRequired,
        InstallTarget.Both => InstallDecision.RestartBothRequired,
        _ => InstallDecision.RestartClientRequired,
    };

    internal static string RestartReason(InstallTarget target) => target switch
    {
        InstallTarget.LocalServer => InstallReasons.RestartServer,
        InstallTarget.Both => InstallReasons.RestartBoth,
        _ => InstallReasons.RestartClient,
    };

    internal static string ComputePlanHash(
        CatalogEntry entry,
        PackageInspection inspection,
        InstallTarget target,
        IReadOnlyList<ArtifactHash> artifacts,
        InstallDecision decision,
        string? pluginId,
        string? fileName,
        IReadOnlyList<InstallHalf>? halves = null)
    {
        var sb = new StringBuilder();
        sb.Append(entry.Key).Append('|')
            .Append(entry.Manifest.Version).Append('|')
            .Append((int)target).Append('|')
            .Append((int)decision).Append('|')
            .Append(pluginId ?? "").Append('|')
            .Append(fileName ?? "").Append('|')
            .Append(inspection.ZipSha256).Append('|');
        for (int i = 0; i < artifacts.Count; i++)
        {
            sb.Append(artifacts[i].Kind).Append(':')
                .Append(artifacts[i].FileName).Append(':')
                .Append(artifacts[i].Sha256).Append(':')
                .Append(artifacts[i].Size.ToString()).Append(';');
        }

        // HOT4: two halves that disagree are a different install from two that agree, so the
        // approval a mixed plan carries cannot be spent on a both-pending one. A single-half plan
        // restates the plan's own decision and is left out, so every existing hash is unchanged.
        if (halves != null && halves.Count > 1)
        {
            for (int i = 0; i < halves.Count; i++)
            {
                sb.Append((int)halves[i].Target).Append(':')
                    .Append((int)halves[i].Decision).Append(':')
                    .Append(halves[i].ExecutorAvailable ? '1' : '0').Append(';');
            }
        }

        using var sha = SHA256.Create();
        return CatalogFetcher.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static InstallPlan Blocked(
        CatalogEntry entry,
        PackageInspection inspection,
        InstallTarget target,
        IReadOnlyList<ArtifactHash> artifacts,
        IReadOnlyList<PlanDependency> dependencies,
        string reason,
        string? pluginId = null,
        string? fileName = null,
        AdmissionResult? admission = null,
        OrdinaryPackageLayout? ordinary = null,
        IReadOnlyList<OrdinaryDependency>? ordinaryDependencies = null,
        IReadOnlyList<InstallHalf>? halves = null) =>
        new(
            ComputePlanHash(entry, inspection, target, artifacts, InstallDecision.UnknownBlocked, pluginId, fileName, halves),
            entry,
            target,
            InstallDecision.UnknownBlocked,
            reason,
            artifacts,
            dependencies,
            pluginId,
            fileName,
            admission,
            ordinary: ordinary,
            ordinaryDependencies: ordinaryDependencies,
            halves: halves);

    private static InstallPlan Restart(
        CatalogEntry entry,
        PackageInspection inspection,
        InstallTarget target,
        IReadOnlyList<ArtifactHash> artifacts,
        IReadOnlyList<PlanDependency> dependencies,
        string reason,
        string pluginId,
        string fileName,
        AdmissionResult? admission = null,
        OrdinaryPackageLayout? ordinary = null,
        IReadOnlyList<OrdinaryDependency>? ordinaryDependencies = null,
        IReadOnlyList<InstallHalf>? halves = null)
    {
        var decision = RestartDecision(target);
        string combined = reason + "; " + RestartReason(target);
        return new InstallPlan(
            ComputePlanHash(entry, inspection, target, artifacts, decision, pluginId, fileName, halves),
            entry,
            target,
            decision,
            combined,
            artifacts,
            dependencies,
            pluginId,
            fileName,
            admission,
            ordinary: ordinary,
            ordinaryDependencies: ordinaryDependencies,
            ledgerClient: target != InstallTarget.LocalServer,
            ledgerServer: target != InstallTarget.Client,
            halves: halves);
    }
}
