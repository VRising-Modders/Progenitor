using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Install.Admission;

/// <summary>
/// Which lifecycle change is being admitted. The first increment implements the state machine
/// for <see cref="InitialInstall"/> only; <see cref="Replace"/> and <see cref="Remove"/> carry
/// their own capability declarations and acceptance and are refused here with a reason.
/// </summary>
internal enum AdmissionOperation
{
    InitialInstall = 0,
    Replace = 1,
    Remove = 2,
}

/// <summary>
/// Structured admission outcome. <see cref="ApplyNowEligible"/> is the only value that lets the
/// planner emit <see cref="InstallDecision.ApplyNow"/>; it is reachable solely through an
/// exact-image <see cref="QualificationRecord"/> plus an available runtime snapshot.
/// <see cref="RestartInstall"/> is the honest supported path when hot activation is refused;
/// <see cref="Blocked"/> means no supported path; <see cref="Unknown"/> means the inspection
/// could not decide (unresolved base chain, reflection escape, unknown schema, no snapshot).
/// </summary>
internal enum AdmissionDecision
{
    Unknown = 0,
    ApplyNowEligible = 1,
    RestartInstall = 2,
    Blocked = 3,
}

/// <summary>One reason row: the code plus its sentence (optionally with detail appended).</summary>
internal sealed record AdmissionReason(AdmissionReasonCode Code, string Text)
{
    public static AdmissionReason Of(AdmissionReasonCode code, string? detail = null) =>
        new(code, string.IsNullOrEmpty(detail) ? AdmissionReasonText.For(code) : AdmissionReasonText.For(code) + " (" + detail + ")");

    public override string ToString() => Code + ": " + Text;
}

/// <summary>Content identity of one image: sha256 (64 lowercase hex) and byte length.</summary>
internal sealed record ArtifactIdentity(string Sha256, long Size)
{
    public string Sha256 { get; } = (Sha256 ?? "").Trim().ToLowerInvariant();

    public bool Matches(ArtifactIdentity? other) =>
        other != null
        && Size == other.Size
        && string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
}

/// <summary>A name → sha256 map with a canonical text form so two closures compare as one string.</summary>
internal sealed class DependencyClosure
{
    public static DependencyClosure Empty { get; } = new(Array.Empty<KeyValuePair<string, string>>());

    private readonly SortedDictionary<string, string> _rows = new(StringComparer.OrdinalIgnoreCase);

    public DependencyClosure(IEnumerable<KeyValuePair<string, string>> rows)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        foreach (var kv in rows)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            _rows[kv.Key.Trim()] = (kv.Value ?? "").Trim().ToLowerInvariant();
        }
    }

    public int Count => _rows.Count;

    public IReadOnlyDictionary<string, string> Rows => _rows;

    public bool TryGet(string name, out string sha256) => _rows.TryGetValue(name ?? "", out sha256!);

    /// <summary><c>name=sha;name=sha</c> sorted case-insensitively by name. Equal closures render equal text.</summary>
    public string Canonical
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var kv in _rows)
                sb.Append(kv.Key.ToLowerInvariant()).Append('=').Append(kv.Value).Append(';');
            return sb.ToString();
        }
    }

    public bool SameAs(DependencyClosure? other) =>
        other != null && string.Equals(Canonical, other.Canonical, StringComparison.Ordinal);

    public override string ToString() => Canonical;
}

/// <summary>
/// One resolved compatibility identity the admission is bound to: <c>gameBuild</c>,
/// <c>bootstrapContract</c>, <c>coreContract</c>, or a framework assembly version.
/// </summary>
internal sealed record CompatibilityIdentity(string Name, string Value)
{
    public string Name { get; } = (Name ?? "").Trim();
    public string Value { get; } = (Value ?? "").Trim();
}

/// <summary>Well-known <see cref="CompatibilityIdentity.Name"/> values a qualification record is keyed by.</summary>
internal static class CompatibilityKeys
{
    public const string GameBuild = "gameBuild";
    public const string BootstrapContract = "bootstrapContract";
    public const string CoreContract = "coreContract";
}

/// <summary>Process side of a runtime snapshot or target. Mirrors <see cref="Progenitor.Plugins.Side"/> without the game dependency.</summary>
internal enum AdmissionSide
{
    Unknown = 0,
    Client = 1,
    Server = 2,
}

/// <summary>State of the currently loaded generation of one plugin id, as the snapshot reports it.</summary>
internal sealed record LoadedPluginState(
    string PluginId,
    ArtifactIdentity? Image,
    bool UnsafeHarmonyMarked,
    bool PriorGenerationPinned,
    bool ProcessLifetimeMarked = false);

/// <summary>
/// Plain-data snapshot of the Bootstrap loader state for one admission. HOT2 implemented the
/// Bootstrap side (<c>Progenitor.Bootstrap.AdmissionSnapshot</c>, read through
/// <see cref="BootstrapAdmissionSnapshotSource"/>); a Bootstrap that predates it, or a process with
/// no Bootstrap at all, still yields <see cref="Unavailable"/>, and then no image can reach
/// <see cref="AdmissionDecision.ApplyNowEligible"/>. Nothing here references an Assembly, Type
/// or delegate: a snapshot may outlive the generation it describes.
/// </summary>
internal sealed record RuntimeGenerationSnapshot(
    bool Available,
    string? UnavailableReason,
    AdmissionSide ProcessSide,
    long Generation,
    bool LoaderBusy,
    bool AllowlistAllows,
    bool LocalServerRuntimeConnected,
    LoadedPluginState? CurrentPlugin,
    IReadOnlyList<CompatibilityIdentity> Identities)
{
    public const string NotImplementedReason =
        "the Bootstrap admission snapshot API is not implemented in this Bootstrap generation";

    public static RuntimeGenerationSnapshot Unavailable(string? reason = null) => new(
        Available: false,
        UnavailableReason: string.IsNullOrWhiteSpace(reason) ? NotImplementedReason : reason,
        ProcessSide: AdmissionSide.Unknown,
        Generation: -1,
        LoaderBusy: false,
        AllowlistAllows: false,
        LocalServerRuntimeConnected: false,
        CurrentPlugin: null,
        Identities: Array.Empty<CompatibilityIdentity>());

    public IReadOnlyList<CompatibilityIdentity> Identities { get; } = Identities ?? Array.Empty<CompatibilityIdentity>();
}

/// <summary>
/// HOT2: the image the caller intends to write, so the snapshot can ask the reload allowlist about
/// <em>these</em> bytes under <em>this</em> file name rather than about whatever is loaded now. The
/// allowlist matches both halves of one row, so the pair travels together.
/// </summary>
internal sealed record AdmissionCandidate(string FileName, string Sha256, long Size)
{
    public string FileName { get; } = (FileName ?? "").Trim();
    public string Sha256 { get; } = (Sha256 ?? "").Trim().ToLowerInvariant();
}

/// <summary>Supplies the runtime snapshot for one plugin and target. The default says "not available".</summary>
internal interface IAdmissionSnapshotSource
{
    RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target);

    /// <summary>
    /// HOT2: the same capture, told which image is about to be written. The default forwards to
    /// <see cref="Capture(string,InstallTarget)"/> so a source that does not care about the
    /// candidate (and every existing test fake) is unchanged.
    /// </summary>
    RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target, AdmissionCandidate? candidate) =>
        Capture(pluginId, target);
}

/// <summary>
/// The fail-closed source: every capture is unavailable, so nothing can reach
/// <see cref="AdmissionDecision.ApplyNowEligible"/>. Still the default of
/// <see cref="AdmissionPipeline"/> and the answer in any process with no Bootstrap
/// (<see cref="BootstrapAdmissionSnapshotSource"/> is the wired one).
/// </summary>
internal sealed class UnavailableAdmissionSnapshotSource : IAdmissionSnapshotSource
{
    public static UnavailableAdmissionSnapshotSource Instance { get; } = new();

    public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) =>
        RuntimeGenerationSnapshot.Unavailable();
}

/// <summary>Author declaration relevant to admission, lifted from <c>progenitor.json</c>.</summary>
internal sealed record PackageDeclaration(
    int SchemaVersion,
    string PluginId,
    string Version,
    PackageSide Side,
    bool InitialHotLoad,
    bool ReplaceHotLoad,
    bool RemoveHotLoad)
{
    /// <summary>The only declaration schema this policy understands: the legacy <c>hotLoadable</c> boolean document.</summary>
    public const int LegacySchemaVersion = 1;

    public bool Declares(AdmissionOperation operation) => operation switch
    {
        AdmissionOperation.InitialInstall => InitialHotLoad,
        AdmissionOperation.Replace => ReplaceHotLoad,
        AdmissionOperation.Remove => RemoveHotLoad,
        _ => false,
    };
}

/// <summary>
/// Everything an admission decision is made from. Immutable plain data: hashes, names, the
/// inspection outcome, the policy version and the runtime snapshot. Never holds the bytes.
/// </summary>
internal sealed record AdmissionRequest(
    AdmissionOperation Operation,
    InstallTarget Target,
    PackageDeclaration Declaration,
    ArtifactIdentity Artifact,
    DependencyClosure Closure,
    IReadOnlyList<CompatibilityIdentity> Identities,
    PreflightOutcome Inspection,
    string PolicyVersion,
    RuntimeGenerationSnapshot Snapshot,
    IReadOnlyList<QualificationRecord>? Records = null,
    bool EmbeddedQualificationPresent = false)
{
    public IReadOnlyList<CompatibilityIdentity> Identities { get; } = Identities ?? Array.Empty<CompatibilityIdentity>();

    /// <summary>Local reviewed records for this plugin id (from <see cref="QualificationPolicy.For"/>). Never from the package.</summary>
    public IReadOnlyList<QualificationRecord> Records { get; } = Records ?? Array.Empty<QualificationRecord>();

    public string? Identity(string name)
    {
        for (int i = 0; i < Identities.Count; i++)
            if (string.Equals(Identities[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return Identities[i].Value;
        return null;
    }
}

/// <summary>Decision, ordered reasons and the structural facts the decision rests on.</summary>
internal sealed record AdmissionResult(
    AdmissionDecision Decision,
    IReadOnlyList<AdmissionReason> Reasons,
    IReadOnlyDictionary<string, string> Facts)
{
    public IReadOnlyList<AdmissionReason> Reasons { get; } = Reasons ?? Array.Empty<AdmissionReason>();
    public IReadOnlyDictionary<string, string> Facts { get; } = Facts ?? new Dictionary<string, string>();

    public bool Has(AdmissionReasonCode code)
    {
        for (int i = 0; i < Reasons.Count; i++)
            if (Reasons[i].Code == code) return true;
        return false;
    }

    /// <summary>The first (decisive) reason's sentence, or empty.</summary>
    public string PrimaryText => Reasons.Count == 0 ? "" : Reasons[0].Text;

    /// <summary>All reason sentences joined with <c>; </c>.</summary>
    public string ReasonText
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Reasons.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(Reasons[i].Text);
            }
            return sb.ToString();
        }
    }
}
