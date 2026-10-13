using System;
using System.Collections.Generic;

namespace Progenitor.Install.Admission;

/// <summary>
/// The admission state machine over plain data. <see cref="Evaluate"/> turns one
/// <see cref="AdmissionRequest"/> into an <see cref="AdmissionResult"/>; <see cref="Recheck"/>
/// compares the request an approval was bound to against the request rebuilt at activation
/// time and refuses on any drift. Neither touches the file system, a process, or the bytes.
///
/// Order: image validity → identity → declaration schema → operation → declared capability →
/// scanner findings → qualification record → runtime snapshot. The first decisive reason sets the
/// decision; later non-decisive reasons (an ignored embedded record, interop instantiation counts)
/// are appended as facts so the UI and MCP show the same rows.
/// </summary>
internal static class AdmissionEvaluator
{
    public const string PolicyVersion = "admission/1";

    public static AdmissionResult Evaluate(AdmissionRequest request)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var reasons = new List<AdmissionReason>();
        var facts = new Dictionary<string, string>(StringComparer.Ordinal);
        var scan = request.Inspection;

        facts["operation"] = request.Operation.ToString();
        facts["target"] = request.Target.ToString();
        facts["artifactSha256"] = request.Artifact.Sha256;
        facts["artifactSize"] = request.Artifact.Size.ToString();
        facts["dependencyClosure"] = request.Closure.Canonical;
        facts["policyVersion"] = request.PolicyVersion ?? "";
        facts["snapshotAvailable"] = request.Snapshot.Available ? "true" : "false";
        // HOT2: the loader state this decision is bound to, so the executor's recheck immediately
        // before the write has something exact to compare against (handoff item 4).
        facts[BootstrapAdmissionSnapshotSource.LoaderStateFact] =
            BootstrapAdmissionSnapshotSource.LoaderStateKey(request.Snapshot);

        // 1. Image validity.
        if (!scan.Valid)
        {
            reasons.Add(AdmissionReason.Of(scan.Refusal ?? AdmissionReasonCode.MalformedImage, scan.RefusalDetail));
            return Done(AdmissionDecision.Blocked, reasons, facts);
        }
        facts["assembly"] = scan.AssemblyName + " " + scan.AssemblyVersion;
        facts["mvid"] = scan.Mvid;
        facts["entryPoints"] = scan.EntryPoints.Count.ToString();
        facts["interopInstantiations"] = scan.InteropInstantiations.Count.ToString();
        facts["managedGenericInstantiations"] = scan.ManagedGenericInstantiations.ToString();
        facts["baseChain"] = string.Join(" > ", scan.BaseChain);
        facts["baseIsProgenitorPlugin"] = scan.BaseIsProgenitorPlugin ? "true" : "false";

        // 2. Entry point count and identity.
        if (scan.EntryPoints.Count == 0)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.NoPluginEntryPoint));
            return Done(AdmissionDecision.RestartInstall, reasons, facts);
        }
        if (scan.EntryPoints.Count > 1)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.MultiplePluginEntryPoints, Describe(scan.EntryPoints)));
            return Done(AdmissionDecision.RestartInstall, reasons, facts);
        }
        var entry = scan.EntryPoints[0];
        facts["pluginGuid"] = entry.Guid;
        facts["pluginVersion"] = entry.Version;
        if (!string.Equals(entry.Guid, request.Declaration.PluginId, StringComparison.OrdinalIgnoreCase)
            || (request.Declaration.Version.Length > 0 && !string.Equals(entry.Version, request.Declaration.Version, StringComparison.Ordinal)))
        {
            reasons.Add(AdmissionReason.Of(
                AdmissionReasonCode.IdentityMismatch,
                "declared " + request.Declaration.PluginId + "@" + request.Declaration.Version + ", image " + entry.Guid + "@" + entry.Version));
            return Done(AdmissionDecision.Blocked, reasons, facts);
        }

        // 3. Declaration schema and operation.
        if (request.Declaration.SchemaVersion != PackageDeclaration.LegacySchemaVersion)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.UnknownDeclarationSchema, "schema " + request.Declaration.SchemaVersion));
            return Done(AdmissionDecision.Unknown, reasons, facts);
        }
        if (request.Operation != AdmissionOperation.InitialInstall)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.UnsupportedOperation, request.Operation.ToString()));
            return Done(AdmissionDecision.RestartInstall, reasons, facts);
        }
        if (!request.Declaration.Declares(request.Operation))
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.HotLoadNotDeclared));
            return Done(AdmissionDecision.RestartInstall, reasons, facts);
        }

        // 4. Scanner findings. Unresolved or dynamic → Unknown; a known unsafe surface → refuse hot activation.
        if (!scan.BaseChainResolved)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.UnresolvedBaseChain, string.Join(" > ", scan.BaseChain)));
            return Done(AdmissionDecision.Unknown, reasons, facts);
        }
        if (scan.UnresolvedReferences.Count > 0)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.UnresolvedDependency, string.Join(", ", scan.UnresolvedReferences)));
            return Done(AdmissionDecision.Unknown, reasons, facts);
        }
        AdmissionReasonCode[] refusals =
        {
            AdmissionReasonCode.UnsafeHarmonyReference,
            AdmissionReasonCode.HarmonyReference,
            AdmissionReasonCode.ClassInjectorReference,
            AdmissionReasonCode.NativeImport,
            AdmissionReasonCode.MalformedImage,
        };
        for (int i = 0; i < refusals.Length; i++)
        {
            if (!scan.HasFinding(refusals[i])) continue;
            reasons.Add(AdmissionReason.Of(refusals[i], FirstSite(scan, refusals[i])));
            return Done(AdmissionDecision.RestartInstall, reasons, facts);
        }
        if (scan.HasFinding(AdmissionReasonCode.ReflectionEscape))
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.ReflectionEscape, FirstSite(scan, AdmissionReasonCode.ReflectionEscape)));
            return Done(AdmissionDecision.Unknown, reasons, facts);
        }

        // 5. Side agreement between declaration and image, when the image declares one.
        if (scan.SideDeclared && scan.DeclaredSide != PackageSide.Unknown && request.Declaration.Side != PackageSide.Unknown
            && scan.DeclaredSide != request.Declaration.Side)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.SideMismatch, "progenitor.json " + request.Declaration.Side + ", [ProgenitorSide] " + scan.DeclaredSide));
            return Done(AdmissionDecision.RestartInstall, reasons, facts);
        }

        // 6. Qualification: local reviewed records only. An embedded record is a fact, never an input.
        var embedded = request.EmbeddedQualificationPresent ? AdmissionReason.Of(AdmissionReasonCode.EmbeddedQualificationIgnored) : null;
        if (!TryQualify(request, reasons, facts))
        {
            // HOTDEMO1 (decided 2026-10-03: "trust the hotLoadable flag"). A record is extra
            // evidence, no longer a requirement: a package that declares hotLoadable (step 3) and whose
            // image passed every check above is admitted when the image is a Progenitor plugin. The
            // record's mismatch rows stay in the reasons as facts. Anything else keeps the restart route.
            if (!scan.BaseIsProgenitorPlugin)
            {
                reasons.Insert(0, AdmissionReason.Of(AdmissionReasonCode.NotProgenitorPlugin, string.Join(" > ", scan.BaseChain)));
                if (embedded != null) reasons.Add(embedded);
                return Done(AdmissionDecision.RestartInstall, reasons, facts);
            }

            reasons.Insert(0, AdmissionReason.Of(AdmissionReasonCode.TrustedHotLoadable, request.Declaration.PluginId));
            facts["trust"] = "hotLoadable";
        }
        if (!request.Snapshot.Available)
        {
            // Qualified bytes, but no running-process facts to check them against: Unknown, and the
            // planner keeps the restart path. This is every production request until the Bootstrap
            // snapshot API lands.
            reasons.Insert(0, AdmissionReason.Of(AdmissionReasonCode.SnapshotUnavailable, request.Snapshot.UnavailableReason));
            if (embedded != null) reasons.Add(embedded);
            return Done(AdmissionDecision.Unknown, reasons, facts);
        }

        // 7. Runtime snapshot checks (only reachable when a record matched and the snapshot is available).
        var snap = request.Snapshot;
        facts["generation"] = snap.Generation.ToString();
        facts["processSide"] = snap.ProcessSide.ToString();
        if (snap.LoaderBusy)
            return Refuse(AdmissionReasonCode.LoaderBusy, null, reasons, facts, embedded);
        if (!snap.AllowlistAllows)
            return Refuse(AdmissionReasonCode.AllowlistDenied, request.Declaration.PluginId, reasons, facts, embedded);
        if (snap.CurrentPlugin != null)
        {
            if (snap.CurrentPlugin.PriorGenerationPinned)
                return Refuse(AdmissionReasonCode.PriorGenerationPinned, request.Declaration.PluginId, reasons, facts, embedded);
            if (snap.CurrentPlugin.UnsafeHarmonyMarked)
                return Refuse(AdmissionReasonCode.UnsafeHarmonyMarked, request.Declaration.PluginId, reasons, facts, embedded);
            if (snap.CurrentPlugin.ProcessLifetimeMarked)
                return Refuse(AdmissionReasonCode.ProcessLifetimeMarked, request.Declaration.PluginId, reasons, facts, embedded);
            return Refuse(AdmissionReasonCode.AlreadyLoaded, request.Declaration.PluginId, reasons, facts, embedded);
        }
        if (!SideIncludes(request.Declaration.Side, snap.ProcessSide))
            return Refuse(AdmissionReasonCode.SideMismatch, "package " + request.Declaration.Side + ", process " + snap.ProcessSide, reasons, facts, embedded);
        if ((request.Target == InstallTarget.LocalServer || request.Target == InstallTarget.Both) && !snap.LocalServerRuntimeConnected)
            return Refuse(AdmissionReasonCode.LocalServerRuntimeUnavailable, null, reasons, facts, embedded);

        if (embedded != null) reasons.Add(embedded);
        return Done(AdmissionDecision.ApplyNowEligible, reasons, facts);
    }

    /// <summary>
    /// Refuses when the request rebuilt at activation differs from the one approved: image bytes,
    /// closure or compatibility identities, side, runtime generation, policy version. A refusal
    /// carries the drift reasons only; nothing here can mutate any install state.
    /// </summary>
    public static AdmissionResult Recheck(AdmissionRequest approved, AdmissionRequest current)
    {
        if (approved == null) throw new ArgumentNullException(nameof(approved));
        if (current == null) throw new ArgumentNullException(nameof(current));
        var reasons = new List<AdmissionReason>();
        var facts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["approvedSha256"] = approved.Artifact.Sha256,
            ["currentSha256"] = current.Artifact.Sha256,
        };

        if (!approved.Artifact.Matches(current.Artifact))
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.ImageChanged, approved.Artifact.Sha256 + " -> " + current.Artifact.Sha256));
        if (!approved.Closure.SameAs(current.Closure) || !SameIdentities(approved, current))
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.DependencyRewritten));
        if (approved.Declaration.Side != current.Declaration.Side || approved.Target != current.Target
            || (approved.Snapshot.Available && current.Snapshot.Available && approved.Snapshot.ProcessSide != current.Snapshot.ProcessSide))
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.SideChanged));
        if (!current.Snapshot.Available)
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.SnapshotUnavailable, current.Snapshot.UnavailableReason));
        else if (approved.Snapshot.Generation != current.Snapshot.Generation)
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.GenerationChanged, approved.Snapshot.Generation + " -> " + current.Snapshot.Generation));
        if (!string.Equals(approved.PolicyVersion, current.PolicyVersion, StringComparison.Ordinal))
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.PolicyChanged));
        if (approved.Operation != current.Operation)
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.UnsupportedOperation, "operation changed"));

        if (reasons.Count > 0)
            return Done(AdmissionDecision.RestartInstall, reasons, facts);

        // Nothing drifted: the current request must still evaluate eligible on its own merits.
        return Evaluate(current);
    }

    private static bool TryQualify(AdmissionRequest request, List<AdmissionReason> reasons, Dictionary<string, string> facts)
    {
        var records = request.Records;
        if (records.Count == 0)
        {
            reasons.Add(AdmissionReason.Of(AdmissionReasonCode.NoQualificationRecord, request.Declaration.PluginId));
            return false;
        }
        var mismatches = new List<string>();
        for (int i = 0; i < records.Count; i++)
        {
            var rows = records[i].Mismatches(request);
            if (rows.Count == 0)
            {
                facts["qualificationRecord"] = records[i].Id;
                facts["qualifiedUtc"] = records[i].QualifiedUtc;
                reasons.Add(AdmissionReason.Of(AdmissionReasonCode.Qualified, records[i].Id));
                return true;
            }
            mismatches.Add(records[i].Id + ": " + string.Join(", ", rows));
        }
        reasons.Add(AdmissionReason.Of(AdmissionReasonCode.QualificationMismatch, string.Join("; ", mismatches)));
        return false;
    }

    private static AdmissionResult Refuse(
        AdmissionReasonCode code,
        string? detail,
        List<AdmissionReason> reasons,
        Dictionary<string, string> facts,
        AdmissionReason? embedded)
    {
        // The Qualified row stays as a fact; the refusal becomes the primary reason.
        reasons.Insert(0, AdmissionReason.Of(code, detail));
        if (embedded != null) reasons.Add(embedded);
        return Done(AdmissionDecision.RestartInstall, reasons, facts);
    }

    private static AdmissionResult Done(AdmissionDecision decision, List<AdmissionReason> reasons, Dictionary<string, string> facts)
    {
        facts["decision"] = decision.ToString();
        return new AdmissionResult(decision, reasons.ToArray(), facts);
    }

    private static bool SameIdentities(AdmissionRequest a, AdmissionRequest b)
    {
        if (a.Identities.Count != b.Identities.Count) return false;
        for (int i = 0; i < a.Identities.Count; i++)
        {
            string? other = b.Identity(a.Identities[i].Name);
            if (!string.Equals(a.Identities[i].Value, other, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    internal static bool SideIncludes(PackageSide package, AdmissionSide process) => process switch
    {
        AdmissionSide.Client => package == PackageSide.Client || package == PackageSide.Both,
        AdmissionSide.Server => package == PackageSide.Server || package == PackageSide.Both,
        _ => false,
    };

    private static string FirstSite(PreflightOutcome scan, AdmissionReasonCode code)
    {
        for (int i = 0; i < scan.Findings.Count; i++)
            if (scan.Findings[i].Code == code) return scan.Findings[i].Image + ": " + scan.Findings[i].Site;
        return "";
    }

    private static string Describe(IReadOnlyList<PluginEntryPoint> entries)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(entries[i].TypeName).Append('=').Append(entries[i].Guid);
        }
        return sb.ToString();
    }
}
