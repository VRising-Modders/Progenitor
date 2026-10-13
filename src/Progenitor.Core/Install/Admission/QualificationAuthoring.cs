using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Install.Admission;

/// <summary>
/// HOT2: author one <see cref="QualificationRecord"/> from a package on disk and append it to
/// <c>BepInEx/config/progenitor-qualification.json</c>. Drives the developer/operator bridge op
/// <c>install_local {action:"qualify"}</c>.
///
/// <para><b>A record is a review claim, not a certificate.</b> Writing one says "a person looked at
/// these exact bytes on this exact game build against this exact Bootstrap/Core pairing and accepted
/// the consequences of loading them into a live process". Nothing here inspects behavior, and
/// nothing here can: the offline scan proves structure and finds known forbidden references, and the
/// review doc is explicit that metadata cannot prove tracked-facade discipline or complete cleanup.
/// The op exists so the coordinator can author a record reproducibly instead of hand-editing hashes,
/// and <see cref="Evidence"/> is required precisely so the file names where the review lives.</para>
///
/// <para><b>The embedded-record rule is untouched.</b> This writes to the local reviewed policy file
/// under the process's own BepInEx root. A record found <em>inside</em> a package is still ignored
/// and reported (<see cref="AdmissionReasonCode.EmbeddedQualificationIgnored"/>) — a package cannot
/// self-certify, and running this op over a package does not change that.</para>
///
/// <para>Reads bytes and writes one JSON file. It loads nothing, executes nothing, contacts no game
/// process and reaches no network.</para>
/// </summary>
internal static class QualificationAuthoring
{
    /// <summary>Refused when the caller named no zip.</summary>
    public const string NoZip = "zip is required: the package to qualify";

    /// <summary>Refused when evidence is missing. A record with no evidence is not a review.</summary>
    public const string NoEvidence =
        "evidence is required: name where the review of these exact bytes lives "
        + "(a research path, a review id) — a record is a review claim, never self-certification";

    /// <summary>Refused when the zip is not a readable Progenitor package.</summary>
    public const string NotAPackage = "the zip is not an inspectable Progenitor package";

    /// <summary>Refused when the package declares no plugin id.</summary>
    public const string NoPluginId = "the package's progenitor.json declares no id";

    /// <summary>Refused when the installable image cannot be picked out of the zip by its declared hash.</summary>
    public const string NoImage =
        "the installable DLL could not be identified in the zip by the hash progenitor.json declares";

    /// <summary>
    /// Inspect <paramref name="zipPath"/> with the ordinary non-executing pipeline and return the
    /// record it implies, or the reason there is none. Nothing is written.
    /// </summary>
    public static QualificationAuthorResult Compose(
        string? zipPath,
        InstallTarget target,
        string? evidence,
        IReadOnlyList<CompatibilityIdentity>? identities = null,
        string? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(zipPath)) return QualificationAuthorResult.Failed(NoZip);
        if (string.IsNullOrWhiteSpace(evidence)) return QualificationAuthorResult.Failed(NoEvidence);
        string zip = zipPath!.Trim();
        if (!File.Exists(zip)) return QualificationAuthorResult.Failed(NotAPackage + ": " + zip + " does not exist");

        PackageInspection inspection;
        try { inspection = PackageInspector.InspectFile(zip); }
        catch (Exception e) { return QualificationAuthorResult.Failed(NotAPackage + ": " + e.Message); }
        if (!inspection.Ok) return QualificationAuthorResult.Failed(NotAPackage + ": " + inspection.Refusal);

        var manifest = inspection.ProgenitorManifest;
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
            return QualificationAuthorResult.Failed(NoPluginId);

        if (!AdmissionPipeline.TryReadImages(zip, inspection, out var main, out var bundled, out string mainName, out bool embedded))
            return QualificationAuthorResult.Failed(NoImage);

        string sha = AdmissionPipeline.Hex(System.Security.Cryptography.SHA256.HashData(main.Span));
        var closureRows = new List<KeyValuePair<string, string>>();
        foreach (var kv in bundled)
            closureRows.Add(new KeyValuePair<string, string>(
                kv.Key, AdmissionPipeline.Hex(System.Security.Cryptography.SHA256.HashData(kv.Value.Span))));

        var ids = identities ?? CompatibilityIdentities.Current();
        string build = Identity(ids, CompatibilityKeys.GameBuild);
        string bootstrap = Identity(ids, CompatibilityKeys.BootstrapContract);
        string core = Identity(ids, CompatibilityKeys.CoreContract);

        var record = new QualificationRecord(
            Id: IdFor(manifest.Id, target),
            PluginId: manifest.Id,
            Artifact: new ArtifactIdentity(sha, main.Length),
            Closure: new DependencyClosure(closureRows),
            GameBuild: build,
            BootstrapContract: bootstrap,
            CoreContract: core,
            Side: inspection.Side,
            Operation: AdmissionOperation.InitialInstall,
            Target: target,
            QualifiedUtc: string.IsNullOrWhiteSpace(nowUtc)
                ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
                : nowUtc!.Trim(),
            Evidence: evidence!.Trim());

        var scan = ArtifactPreflight.Scan(main, bundled, PreflightOptions.Default);
        return QualificationAuthorResult.Composed(record, mainName, inspection, scan, embedded);
    }

    /// <summary>
    /// <see cref="Compose"/> then append. The file is rewritten whole under
    /// <see cref="InstallStaging.AtomicWrite"/> (temp file plus move), so a crash mid-write leaves the
    /// previous file intact and never a truncated one. Existing records are preserved verbatim by
    /// re-serializing the parsed set; a record whose <c>id</c> and artifact both match an existing
    /// row replaces it rather than accumulating duplicates.
    /// </summary>
    public static QualificationAuthorResult Append(
        string bepInExRoot,
        string? zipPath,
        InstallTarget target,
        string? evidence,
        IReadOnlyList<CompatibilityIdentity>? identities = null,
        string? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot))
            return QualificationAuthorResult.Failed("no BepInEx root: this process has no config folder to write");

        var composed = Compose(zipPath, target, evidence, identities, nowUtc);
        if (!composed.Ok) return composed;

        string path = QualificationPolicy.PathUnder(bepInExRoot);
        var existing = QualificationPolicy.LoadLocal(bepInExRoot);
        if (existing.Errors.Count > 0 && existing.Records.Count == 0 && File.Exists(path))
        {
            // Never silently discard a file this parser could not read: the operator's own rows
            // would go with it.
            return QualificationAuthorResult.Failed(
                "the existing " + QualificationPolicy.FileName + " does not parse ("
                + string.Join("; ", existing.Errors) + "); fix or move it before appending");
        }

        var rows = new List<QualificationRecord>();
        bool replaced = false;
        for (int i = 0; i < existing.Records.Count; i++)
        {
            var row = existing.Records[i];
            if (string.Equals(row.Id, composed.Record!.Id, StringComparison.Ordinal))
            {
                rows.Add(composed.Record!);
                replaced = true;
                continue;
            }
            rows.Add(row);
        }
        if (!replaced) rows.Add(composed.Record!);

        try
        {
            InstallStaging.AtomicWrite(path, Encoding.UTF8.GetBytes(Serialize(rows)));
        }
        catch (Exception e)
        {
            return QualificationAuthorResult.Failed("writing " + path + " failed: " + e.Message);
        }

        // The pipeline's cached policy is keyed by the file stamp; drop it so this record is honored
        // on the very next plan rather than on whichever open happens to see a new stamp.
        QualificationPolicyCache.Invalidate();
        return composed.WithWrite(path, replaced, rows.Count);
    }

    /// <summary>The whole policy file, schema 1, one object per record, stable key order.</summary>
    public static string Serialize(IReadOnlyList<QualificationRecord> records)
    {
        var options = new JsonWriterOptions { Indented = true };
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, options))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", QualificationPolicy.SchemaVersion);
            w.WriteStartArray("records");
            for (int i = 0; records != null && i < records.Count; i++) Write(w, records[i]);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter w, QualificationRecord r)
    {
        w.WriteStartObject();
        w.WriteString("id", r.Id);
        w.WriteString("pluginId", r.PluginId);
        w.WriteString("artifactSha256", r.Artifact.Sha256);
        w.WriteNumber("artifactSize", r.Artifact.Size);
        w.WriteStartObject("dependencyClosure");
        foreach (var kv in r.Closure.Rows) w.WriteString(kv.Key, kv.Value);
        w.WriteEndObject();
        w.WriteString(CompatibilityKeys.GameBuild, r.GameBuild);
        w.WriteString(CompatibilityKeys.BootstrapContract, r.BootstrapContract);
        w.WriteString(CompatibilityKeys.CoreContract, r.CoreContract);
        w.WriteString("side", SideText(r.Side));
        w.WriteString("operation", r.Operation.ToString());
        w.WriteString("target", r.Target.ToString());
        w.WriteString("qualifiedUtc", r.QualifiedUtc);
        w.WriteString("evidence", r.Evidence);
        w.WriteEndObject();
    }

    /// <summary>The record id: plugin id, operation and target, so a second target is a second row.</summary>
    public static string IdFor(string pluginId, InstallTarget target) =>
        (pluginId ?? "").Trim().ToLowerInvariant() + "/initialinstall/" + target.ToString().ToLowerInvariant();

    /// <summary>The three spellings <see cref="QualificationPolicy.Parse"/> accepts.</summary>
    public static string SideText(PackageSide side) => side switch
    {
        PackageSide.Client => "client",
        PackageSide.Server => "server",
        PackageSide.Both => "both",
        _ => "unknown",
    };

    /// <summary>Parse a <c>target</c> argument. Unknown text is refused rather than defaulted.</summary>
    public static bool TryTarget(string? text, out InstallTarget target) =>
        Enum.TryParse((text ?? "").Trim(), ignoreCase: true, out target)
        && (target == InstallTarget.Client || target == InstallTarget.LocalServer || target == InstallTarget.Both);

    private static string Identity(IReadOnlyList<CompatibilityIdentity> identities, string name)
    {
        for (int i = 0; i < identities.Count; i++)
            if (string.Equals(identities[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(identities[i].Value) ? CompatibilityIdentities.Unknown : identities[i].Value;
        return CompatibilityIdentities.Unknown;
    }
}

/// <summary>The outcome of one authoring call: the record, what it was read from, and where it went.</summary>
internal sealed class QualificationAuthorResult
{
    private QualificationAuthorResult(bool ok, string? error, QualificationRecord? record)
    {
        Ok = ok;
        Error = error;
        Record = record;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public QualificationRecord? Record { get; }

    /// <summary>The installable DLL's file name inside the zip.</summary>
    public string ImageName { get; private set; } = "";

    /// <summary>The package side the declaration carries, for the readback.</summary>
    public PackageSide Side { get; private set; }

    /// <summary>True when the package shipped its own <c>qualification.json</c>. It was ignored.</summary>
    public bool EmbeddedRecordPresent { get; private set; }

    /// <summary>The offline scan's own verdict over these bytes, so the op can show it beside the record.</summary>
    public PreflightOutcome? Scan { get; private set; }

    /// <summary>The file the record was appended to, or null when nothing was written.</summary>
    public string? Path { get; private set; }

    /// <summary>True when an existing row with the same id was replaced rather than a row added.</summary>
    public bool Replaced { get; private set; }

    /// <summary>How many records the file holds after the write.</summary>
    public int RecordCount { get; private set; }

    public static QualificationAuthorResult Failed(string error) => new(false, error, null);

    public static QualificationAuthorResult Composed(
        QualificationRecord record,
        string imageName,
        PackageInspection inspection,
        PreflightOutcome scan,
        bool embedded) =>
        new(true, null, record)
        {
            ImageName = imageName ?? "",
            Side = inspection.Side,
            Scan = scan,
            EmbeddedRecordPresent = embedded,
        };

    public QualificationAuthorResult WithWrite(string path, bool replaced, int count)
    {
        Path = path;
        Replaced = replaced;
        RecordCount = count;
        return this;
    }
}
