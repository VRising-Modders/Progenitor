using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Progenitor.Networking;

namespace Progenitor.Install.Admission;

/// <summary>
/// The catalog's admission seam: given the inspected zip, the entry and the target, produce an
/// <see cref="AdmissionResult"/> for the planner. Null means "no admission was attempted" and the
/// planner falls back to the legacy <see cref="IHotLoadEligibility"/> stub.
/// </summary>
internal interface IAdmissionEvaluator
{
    AdmissionResult? Evaluate(CatalogEntry entry, PackageInspection inspection, InstallTarget target, string zipPath);

    /// <summary>
    /// HOT2 (handoff item 4): capture the loader state again, immediately before the write the
    /// approval authorizes. Null means this evaluator has no snapshot source to ask, and then the
    /// executor performs no recheck — the pre-HOT2 behavior, which is only reachable where nothing
    /// could have been admitted in the first place.
    /// </summary>
    RuntimeGenerationSnapshot? Recapture(string pluginId, InstallTarget target) => null;

    /// <summary>
    /// HOTDEMO1: evaluate against a snapshot the caller built (the local server folder seen from a
    /// client). An evaluator without the seam answers its ordinary evaluation.
    /// </summary>
    AdmissionResult? EvaluateWithSnapshot(
        CatalogEntry entry, PackageInspection inspection, InstallTarget target, string zipPath, RuntimeGenerationSnapshot snapshot) =>
        Evaluate(entry, inspection, target, zipPath);
}

/// <summary>
/// Production pipeline: extract the installable image and bundled dependencies from the zip
/// (bytes stay in memory, bounded by the inspection's own caps), run
/// <see cref="ArtifactPreflight"/>, bind the request to the local <see cref="QualificationPolicy"/>,
/// the resolved compatibility identities and the runtime snapshot, and evaluate. With the default
/// <see cref="UnavailableAdmissionSnapshotSource"/> no image can reach
/// <see cref="AdmissionDecision.ApplyNowEligible"/>; that is the intended fail-closed state until
/// the Bootstrap snapshot API exists.
/// </summary>
internal sealed class AdmissionPipeline : IAdmissionEvaluator
{
    /// <summary>A package entry with this name is a self-certification attempt; it is reported and ignored.</summary>
    public const string EmbeddedRecordName = "qualification.json";

    private readonly IAdmissionSnapshotSource _snapshots;
    private readonly Func<QualificationPolicy> _policyProvider;
    private readonly IReadOnlyList<CompatibilityIdentity> _identities;
    private readonly PreflightOptions _options;

    /// <param name="policy">A fixed policy (tests, and any caller that wants one snapshot of it).</param>
    /// <param name="policyProvider">
    /// HOT2: read the policy afresh on every request, so a record the operator added — or that
    /// <c>install_local qualify</c> just wrote — is honored without a Core reload. Wins over
    /// <paramref name="policy"/> when both are given; production passes
    /// <see cref="QualificationPolicyCache.Current"/>, which re-reads only when the file's stamp moved.
    /// </param>
    public AdmissionPipeline(
        IAdmissionSnapshotSource? snapshots = null,
        QualificationPolicy? policy = null,
        IReadOnlyList<CompatibilityIdentity>? identities = null,
        PreflightOptions? options = null,
        Func<QualificationPolicy>? policyProvider = null)
    {
        _snapshots = snapshots ?? UnavailableAdmissionSnapshotSource.Instance;
        var fixedPolicy = policy ?? QualificationPolicy.Empty;
        _policyProvider = policyProvider ?? (() => fixedPolicy);
        _identities = identities ?? Array.Empty<CompatibilityIdentity>();
        _options = options ?? PreflightOptions.Default;
    }

    public AdmissionResult? Evaluate(CatalogEntry entry, PackageInspection inspection, InstallTarget target, string zipPath) =>
        EvaluateCore(entry, inspection, target, zipPath, null);

    public AdmissionResult? EvaluateWithSnapshot(
        CatalogEntry entry, PackageInspection inspection, InstallTarget target, string zipPath, RuntimeGenerationSnapshot snapshot) =>
        EvaluateCore(entry, inspection, target, zipPath, snapshot);

    private AdmissionResult? EvaluateCore(
        CatalogEntry entry, PackageInspection inspection, InstallTarget target, string zipPath, RuntimeGenerationSnapshot? snapshot)
    {
        if (entry == null || inspection == null || !inspection.Ok) return null;
        var manifest = inspection.ProgenitorManifest;
        if (manifest == null || string.IsNullOrEmpty(manifest.Id)) return null;

        if (!TryReadImages(zipPath, inspection, out var main, out var bundled, out var mainName, out var embedded))
            return null;

        var request = BuildRequest(entry, inspection, target, main, bundled, mainName, embedded, snapshot);
        return AdmissionEvaluator.Evaluate(request);
    }

    /// <summary>
    /// HOT2: the loader state right now, for the executor's pre-write recheck. No candidate is
    /// passed: the recheck compares generation, busy and pinned/blocked state, none of which depends
    /// on which bytes are being written, and re-reading the allowlist here would answer a different
    /// question than the one the plan was bound to.
    /// </summary>
    public RuntimeGenerationSnapshot? Recapture(string pluginId, InstallTarget target)
    {
        if (ReferenceEquals(_snapshots, UnavailableAdmissionSnapshotSource.Instance)) return null;
        try { return _snapshots.Capture(pluginId ?? "", target, null); }
        catch { return RuntimeGenerationSnapshot.Unavailable("the admission snapshot recapture threw"); }
    }

    /// <summary>The local reviewed policy this pipeline binds requests to (for the readback and the ops layer).</summary>
    public QualificationPolicy Policy => Read();

    /// <summary>The compatibility identities this pipeline binds requests to.</summary>
    public IReadOnlyList<CompatibilityIdentity> Identities => _identities;

    /// <summary>Builds the plain-data request; separated so tests can hand it bytes without a zip.</summary>
    public AdmissionRequest BuildRequest(
        CatalogEntry entry,
        PackageInspection inspection,
        InstallTarget target,
        ReadOnlyMemory<byte> main,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> bundled,
        string mainName,
        bool embeddedRecordPresent,
        RuntimeGenerationSnapshot? snapshotOverride = null)
    {
        var manifest = inspection.ProgenitorManifest!;
        var declaration = new PackageDeclaration(
            PackageDeclaration.LegacySchemaVersion,
            manifest.Id,
            manifest.Version ?? "",
            inspection.Side,
            InitialHotLoad: inspection.HotLoadableDeclared && manifest.HotLoadable,
            ReplaceHotLoad: false,
            RemoveHotLoad: false);

        var scan = ArtifactPreflight.Scan(main, bundled, _options);

        string mainSha = Hex(SHA256.HashData(main.Span));
        var closureRows = new List<KeyValuePair<string, string>>();
        foreach (var kv in bundled)
            closureRows.Add(new KeyValuePair<string, string>(kv.Key, Hex(SHA256.HashData(kv.Value.Span))));
        for (int i = 0; i < _identities.Count; i++)
        {
            // Framework identities that are not the three qualification keys ride in the closure
            // (e.g. "Progenitor" -> assembly hash), so a rewritten dependency changes the key.
            var id = _identities[i];
            if (id.Name == CompatibilityKeys.GameBuild || id.Name == CompatibilityKeys.BootstrapContract || id.Name == CompatibilityKeys.CoreContract)
                continue;
            if (QualificationPolicy.IsSha256(id.Value.ToLowerInvariant()))
                closureRows.Add(new KeyValuePair<string, string>(id.Name, id.Value.ToLowerInvariant()));
        }

        // HOT2: the snapshot is told which image is about to be written, so the reload allowlist is
        // asked about these bytes under this file name rather than about whatever is loaded now.
        var snapshot = snapshotOverride ?? _snapshots.Capture(
            manifest.Id, target, new AdmissionCandidate(mainName, mainSha, main.Length));
        var policy = Read();
        return new AdmissionRequest(
            AdmissionOperation.InitialInstall,
            target,
            declaration,
            new ArtifactIdentity(mainSha, main.Length),
            new DependencyClosure(closureRows),
            _identities,
            scan,
            AdmissionEvaluator.PolicyVersion + "+" + policy.Version,
            snapshot,
            policy.For(manifest.Id),
            embeddedRecordPresent);
    }

    /// <summary>
    /// Reads the installable DLL (by declared hash, else by the single DLL) and every other DLL in
    /// the zip as bundled dependencies. Bytes are copied once into memory and never written.
    /// </summary>
    internal static bool TryReadImages(
        string zipPath,
        PackageInspection inspection,
        out ReadOnlyMemory<byte> main,
        out IReadOnlyDictionary<string, ReadOnlyMemory<byte>> bundled,
        out string mainName,
        out bool embeddedRecordPresent)
    {
        main = default;
        mainName = "";
        embeddedRecordPresent = false;
        var deps = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase);
        bundled = deps;
        if (string.IsNullOrEmpty(zipPath) || !File.Exists(zipPath)) return false;

        string? wantHash = inspection.ProgenitorManifest?.Sha256;
        if (!string.IsNullOrEmpty(wantHash) && string.Equals(wantHash, inspection.ZipSha256, StringComparison.OrdinalIgnoreCase))
            wantHash = null;
        if (string.IsNullOrEmpty(wantHash) && inspection.DllHashes.Count == 1) wantHash = inspection.DllHashes[0].Sha256;
        if (string.IsNullOrEmpty(wantHash)) return false;

        try
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            bool found = false;
            foreach (var entry in zip.Entries)
            {
                if (PackageInspector.IsDirectory(entry) || PackageInspector.IsSymlink(entry)) continue;
                if (!PackageInspector.TryNormalizeEntryPath(entry.FullName ?? "", out var relative, out _)) continue;
                string fileName = Path.GetFileName(relative);
                if (string.Equals(fileName, EmbeddedRecordName, StringComparison.OrdinalIgnoreCase))
                {
                    embeddedRecordPresent = true;
                    continue;
                }
                if (!relative.EndsWith(ModFolders.ExpectedExtension, StringComparison.OrdinalIgnoreCase)) continue;

                byte[] bytes;
                using (var open = entry.Open())
                using (var ms = new MemoryStream())
                {
                    open.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                string hex = Hex(SHA256.HashData(bytes));
                if (!found && string.Equals(hex, wantHash, StringComparison.OrdinalIgnoreCase))
                {
                    main = bytes;
                    mainName = fileName;
                    found = true;
                }
                else
                {
                    string key = Path.GetFileNameWithoutExtension(fileName);
                    if (!deps.ContainsKey(key)) deps[key] = bytes;
                }
            }
            return found;
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string Hex(byte[] hash) => CatalogFetcher.ToHex(hash);

    /// <summary>
    /// The policy for this request. A provider that throws is the empty policy, not an exception out
    /// of the plan: nothing is qualified, and the install is the restart route.
    /// </summary>
    private QualificationPolicy Read()
    {
        try { return _policyProvider() ?? QualificationPolicy.Empty; }
        catch { return QualificationPolicy.Empty; }
    }
}
