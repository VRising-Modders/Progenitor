using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>
/// How a <see cref="CatalogEntry"/> was produced. Provenance is assigned by the runtime
/// after inspection, never by an adapter. A Thunderstore row is always
/// <see cref="AdaptedListing"/> until a <c>progenitor.json</c> has been read from the
/// downloaded zip and a sha256 of the installable bytes is known.
/// </summary>
internal enum CatalogProvenance
{
    /// <summary>Adapted from a third-party listing or export. Not a Progenitor manifest.</summary>
    AdaptedListing = 0,

    /// <summary>
    /// Package contains a <c>progenitor.json</c> that parsed as a <see cref="ManifestMod"/>,
    /// carries a 64-hex sha256 of the bytes that will be installed, and that hash matched.
    /// </summary>
    ManifestVerified = 1,
}

/// <summary>Player-selected install target. Default is <see cref="Client"/>. Metadata never expands this.</summary>
internal enum InstallTarget
{
    Client = 0,
    LocalServer = 1,
    Both = 2,
}

/// <summary>Structured install decision shown to the player.</summary>
internal enum InstallDecision
{
    ApplyNow = 0,
    RestartClientRequired = 1,
    RestartServerRequired = 2,
    RestartBothRequired = 3,
    UnknownBlocked = 4,
}

/// <summary>Resolution of one plan dependency. Thunderstore strings are not plugin ids.</summary>
internal enum DependencyResolution
{
    /// <summary>No dependency information was supplied.</summary>
    Unknown = 0,

    /// <summary>A Thunderstore <c>{owner}-{name}-{version}</c> string, not a plugin GUID.</summary>
    ForeignListing = 1,

    /// <summary>A plugin-GUID dependency declared in <c>progenitor.json</c> that is not in the plan.</summary>
    Missing = 2,

    /// <summary>A plugin-GUID dependency that this plan already names.</summary>
    Present = 3,

    /// <summary>Declared but cannot be resolved (no id, bad version constraint).</summary>
    Unresolved = 4,
}

/// <summary>Explicit pipeline status for the UI. No Unity types.</summary>
internal enum InstallStatusKind
{
    Idle = 0,
    LoadingIndex = 1,
    Fetching = 2,
    Inspecting = 3,
    Planning = 4,
    AwaitingApproval = 5,
    Staging = 6,
    Pending = 7,
    Failed = 8,
    Cancelled = 9,
}

/// <summary>
/// One catalog row consumed by search, plan, consent and install. Adapters are the only
/// code that speaks Thunderstore. Extra listing fields sit beside <see cref="Manifest"/>
/// because additive <see cref="ManifestMod"/> keys are owned by the signed-catalog module.
/// </summary>
internal sealed class CatalogEntry
{
    public CatalogEntry(
        ManifestMod manifest,
        CatalogProvenance provenance,
        string sourceId,
        string sourceLabel,
        string? indexStamp,
        string? displayName = null,
        string? author = null,
        string? description = null,
        string? listingUrl = null,
        PackageSide side = PackageSide.Unknown,
        bool isDeprecated = false,
        IReadOnlyList<string>? foreignDependencies = null,
        IReadOnlyList<PluginDependency>? pluginDependencies = null,
        string? selectedVersionFullName = null,
        string? downloadUrl = null,
        long? listingFileSize = null,
        string? owner = null,
        string? packageName = null,
        IReadOnlyList<string>? categories = null,
        string? dateUpdated = null,
        string? releasedUtc = null,
        long? downloads = null,
        int? ratingScore = null,
        bool hasNsfwContent = false)
    {
        HasNsfwContent = hasNsfwContent;
        RatingScore = ratingScore is >= 0 ? ratingScore : null;
        Downloads = downloads is >= 0 ? downloads : null;
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Provenance = provenance;
        SourceId = sourceId ?? "";
        SourceLabel = sourceLabel ?? "";
        IndexStamp = string.IsNullOrWhiteSpace(indexStamp) ? null : indexStamp.Trim();
        DisplayName = EmptyToNull(displayName);
        Author = EmptyToNull(author);
        Description = EmptyToNull(description);
        ListingUrl = EmptyToNull(listingUrl);
        Side = side;
        IsDeprecated = isDeprecated;
        ForeignDependencies = foreignDependencies ?? Array.Empty<string>();
        PluginDependencies = pluginDependencies ?? Array.Empty<PluginDependency>();
        SelectedVersionFullName = EmptyToNull(selectedVersionFullName);
        DownloadUrl = EmptyToNull(downloadUrl);
        ListingFileSize = listingFileSize is > 0 ? listingFileSize : null;
        Owner = EmptyToNull(owner);
        PackageName = EmptyToNull(packageName);
        Categories = categories ?? Array.Empty<string>();
        DateUpdated = EmptyToNull(dateUpdated);
        ReleasedUtc = EmptyToNull(releasedUtc);
    }

    /// <summary>Canonical descriptor, possibly sparse. <see cref="ManifestMod.Id"/> is empty until a plugin GUID is known.</summary>
    public ManifestMod Manifest { get; }

    public CatalogProvenance Provenance { get; }

    /// <summary>Adapter id: <c>thunderstore</c>, <c>nexus</c>, or <c>file</c>.</summary>
    public string SourceId { get; }

    /// <summary>Player-facing source name.</summary>
    public string SourceLabel { get; }

    /// <summary>When the export was fetched or built, if known.</summary>
    public string? IndexStamp { get; }

    public string? DisplayName { get; }
    public string? Author { get; }
    public string? Description { get; }
    public string? ListingUrl { get; }
    public PackageSide Side { get; }
    public bool IsDeprecated { get; }

    /// <summary>Thunderstore <c>{owner}-{name}-{version}</c> strings. Never treated as plugin GUIDs.</summary>
    public IReadOnlyList<string> ForeignDependencies { get; }

    /// <summary>Plugin-GUID dependencies from <c>progenitor.json</c>, when present.</summary>
    public IReadOnlyList<PluginDependency> PluginDependencies { get; }

    public string? SelectedVersionFullName { get; }
    public string? DownloadUrl { get; }

    /// <summary>Provider-declared zip size. A <c>long</c>: listing sizes can exceed <see cref="int.MaxValue"/>.</summary>
    public long? ListingFileSize { get; }

    public string? Owner { get; }
    public string? PackageName { get; }

    /// <summary>
    /// Listing category tags as the provider publishes them (Thunderstore <c>categories</c>:
    /// <c>Server</c>, <c>Client</c>, <c>Mods</c>, <c>Tools</c>, <c>&lt;name&gt; Update</c>).
    /// Author-maintained and sometimes stale; a side or update filter derived from them warns, never blocks.
    /// </summary>
    public IReadOnlyList<string> Categories { get; }

    /// <summary>Provider <c>date_updated</c> of the listing (ISO 8601), when published.</summary>
    public string? DateUpdated { get; }

    /// <summary>
    /// <c>date_created</c> (ISO 8601) of the version this entry describes: the release date the
    /// listing header shows. Null when the provider published none; the header then falls back
    /// to <see cref="DateUpdated"/> and says "updated" instead of "released".
    /// </summary>
    public string? ReleasedUtc { get; }

    /// <summary>
    /// Provider download count across every published version (Thunderstore v1
    /// <c>versions[].downloads</c> summed), for the LIST2 Downloads column and sort. Null when
    /// the provider published none; the column then reads <c>—</c> and sorts last.
    /// </summary>
    public long? Downloads { get; }

    /// <summary>
    /// LINK1: the provider's like count for this listing (Thunderstore v1 <c>rating_score</c>, a
    /// package-level field, not a per-version one). Null when the provider published none — the Like
    /// control then draws only what this client stored, which is not a claim about the store's total.
    /// </summary>
    public int? RatingScore { get; }

    /// <summary>
    /// INSTALLSIDES1: the author flagged this package as having NSFW content (Thunderstore v1
    /// <c>has_nsfw_content</c>, a package-level field). The Install tab hides flagged listings unless
    /// <c>[Mods] ShowNsfwContent</c> is on; an unflagged package is never caught by it.
    /// </summary>
    public bool HasNsfwContent { get; }

    /// <summary>Stable listing identity for search and get-entry. Not a plugin GUID.</summary>
    public string Key
    {
        get
        {
            if (!string.IsNullOrEmpty(Manifest.Package)) return Manifest.Package;
            if (!string.IsNullOrEmpty(Owner) && !string.IsNullOrEmpty(PackageName))
                return Owner + "-" + PackageName;
            if (!string.IsNullOrEmpty(Manifest.Id)) return Manifest.Id;
            return SelectedVersionFullName ?? "";
        }
    }

    /// <summary>
    /// A listing is installable only after a Progenitor manifest has been verified against
    /// the installable bytes. Adapted Thunderstore rows are not.
    /// </summary>
    public bool IsInstallable =>
        Provenance == CatalogProvenance.ManifestVerified
        && !string.IsNullOrEmpty(Manifest.Id)
        && !string.IsNullOrEmpty(Manifest.Sha256);

    public string SearchLabel => DisplayName ?? PackageName ?? Manifest.Id ?? Key;

    public CatalogEntry WithInspection(
        CatalogProvenance provenance,
        ManifestMod manifest,
        PackageSide side,
        IReadOnlyList<PluginDependency>? pluginDependencies = null) =>
        new(
            manifest,
            provenance,
            SourceId,
            SourceLabel,
            IndexStamp,
            DisplayName,
            Author,
            Description,
            ListingUrl,
            side,
            IsDeprecated,
            ForeignDependencies,
            pluginDependencies ?? PluginDependencies,
            SelectedVersionFullName,
            DownloadUrl,
            ListingFileSize,
            Owner,
            PackageName,
            Categories,
            DateUpdated,
            ReleasedUtc,
            Downloads,
            RatingScore,
            HasNsfwContent)
        {
            Pin = Pin,
            ThunderstoreRow = ThunderstoreRow,
        };

    /// <summary>
    /// INDEX1: the signed file this listing installs (its URL, size and sha256 from the Progenitor Index, or a dependency
    /// pin), and for an Index listing its dependencies and any takedown. Null for an unpinned Thunderstore or local
    /// listing. A pinned listing installs only the file the pin names (<see cref="ProgenitorIndex.CheckDownload"/>).
    /// </summary>
    public IndexPin? Pin { get; init; }

    /// <summary>
    /// INDEX1: on an Index row, the Thunderstore row of the same key it shadows (null when Thunderstore lists none or is
    /// off). A mod installed from Thunderstore keeps updating from this row, never from the Index row.
    /// </summary>
    public CatalogEntry? ThunderstoreRow { get; init; }

    /// <summary>The same row carrying <paramref name="thunderstore"/> as the row it shadows.</summary>
    public CatalogEntry WithThunderstoreRow(CatalogEntry? thunderstore) =>
        new CatalogEntry(Manifest, Provenance, SourceId, SourceLabel, IndexStamp, DisplayName, Author, Description, ListingUrl, Side,
            IsDeprecated, ForeignDependencies, PluginDependencies, SelectedVersionFullName, DownloadUrl, ListingFileSize, Owner,
            PackageName, Categories, DateUpdated, ReleasedUtc, Downloads, RatingScore, HasNsfwContent)
        {
            Pin = Pin,
            ThunderstoreRow = thunderstore,
        };

    /// <summary>INDEX1: a listing read from the Progenitor Index (its installs may hot load; Thunderstore installs restart).</summary>
    public bool FromIndex => string.Equals(SourceId, ProgenitorIndex.SourceId, StringComparison.Ordinal) && Pin != null;

    internal static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A plugin-GUID dependency from <c>progenitor.json</c>.</summary>
internal readonly struct PluginDependency
{
    public PluginDependency(string id, string version)
    {
        Id = id ?? "";
        Version = version ?? "";
    }

    public string Id { get; }
    public string Version { get; }
}

/// <summary>
/// One hashed artifact. Zip hash and per-DLL hashes are separate values and must stay
/// labeled separately — never present a zip hash as the DLL hash or as a publisher signature.
/// </summary>
internal readonly struct ArtifactHash
{
    public ArtifactHash(string kind, string fileName, string sha256, long size)
    {
        Kind = kind ?? "";
        FileName = fileName ?? "";
        Sha256 = sha256 ?? "";
        Size = size;
    }

    /// <summary><c>zip</c> or <c>dll</c>. The label is part of the value.</summary>
    public string Kind { get; }

    public string FileName { get; }
    public string Sha256 { get; }
    public long Size { get; }
}

/// <summary>One dependency row on an <see cref="InstallPlan"/>.</summary>
internal readonly struct PlanDependency
{
    public PlanDependency(string identity, DependencyResolution resolution, string? note)
    {
        Identity = identity ?? "";
        Resolution = resolution;
        Note = note;
    }

    public string Identity { get; }
    public DependencyResolution Resolution { get; }
    public string? Note { get; }
}

/// <summary>
/// HOT4: one half of an install plan — the client process, or the local server — with the decision
/// admission reached for <em>that</em> process. A single-target plan has one half; a
/// <see cref="InstallTarget.Both"/> plan has two, and they may disagree: a reviewed record scoped to
/// the client makes the client half <see cref="InstallDecision.ApplyNow"/> while the server half is
/// <see cref="InstallDecision.RestartServerRequired"/>. Plain data; the readback is
/// <c>plan.halves [{target, decision, executorAvailable, reason}]</c>.
/// </summary>
internal sealed class InstallHalf
{
    public InstallHalf(InstallTarget target, InstallDecision decision, bool executorAvailable, string reason)
    {
        Target = target;
        Decision = decision;
        ExecutorAvailable = executorAvailable;
        Reason = reason ?? "";
    }

    public InstallTarget Target { get; }
    public InstallDecision Decision { get; }

    /// <summary>True only when this process is the one that would load this half's image.</summary>
    public bool ExecutorAvailable { get; }

    public string Reason { get; }
}

/// <summary>
/// Exact artifacts, hashes, side, decision and bound approval identity for one install.
/// Approval is bound to <see cref="PlanHash"/>; a changed artifact produces a different hash
/// and invalidates the previous approval.
/// </summary>
internal sealed class InstallPlan
{
    public InstallPlan(
        string planHash,
        CatalogEntry entry,
        InstallTarget target,
        InstallDecision decision,
        string reason,
        IReadOnlyList<ArtifactHash> artifacts,
        IReadOnlyList<PlanDependency> dependencies,
        string? pluginId,
        string? pluginFileName,
        AdmissionResult? admission = null,
        bool executorAvailable = false,
        string? executorReason = null,
        OrdinaryPackageLayout? ordinary = null,
        IReadOnlyList<OrdinaryDependency>? ordinaryDependencies = null,
        bool ledgerClient = false,
        bool ledgerServer = false,
        IReadOnlyList<InstallHalf>? halves = null)
    {
        Halves = halves ?? Array.Empty<InstallHalf>();
        PlanHash = planHash ?? "";
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Target = target;
        Decision = decision;
        Reason = reason ?? "";
        Artifacts = artifacts ?? Array.Empty<ArtifactHash>();
        Dependencies = dependencies ?? Array.Empty<PlanDependency>();
        PluginId = pluginId;
        PluginFileName = pluginFileName;
        Admission = admission;
        ExecutorAvailable = executorAvailable;
        ExecutorReason = executorReason;
        Ordinary = ordinary;
        OrdinaryDependencies = ordinaryDependencies ?? Array.Empty<OrdinaryDependency>();
        LedgerClient = ledgerClient;
        LedgerServer = ledgerServer;
    }

    public string PlanHash { get; }
    public CatalogEntry Entry { get; }
    public InstallTarget Target { get; }
    public InstallDecision Decision { get; }
    public string Reason { get; }
    public IReadOnlyList<ArtifactHash> Artifacts { get; }
    public IReadOnlyList<PlanDependency> Dependencies { get; }
    public string? PluginId { get; }
    public string? PluginFileName { get; }

    /// <summary>The structured admission result this plan was built from, when admission ran (ADMIT1). Null under the legacy eligibility stub.</summary>
    public AdmissionResult? Admission { get; }

    /// <summary>
    /// False in this increment for every plan, including <see cref="InstallDecision.ApplyNow"/>:
    /// the same-session executor is not implemented, so Confirm still executes to pending.
    /// <see cref="ExecutorReason"/> names the gap.
    /// </summary>
    public bool ExecutorAvailable { get; }

    public string? ExecutorReason { get; }

    /// <summary>
    /// INSTORD1: the approved ordinary-package layout this plan installs, or null when the plan is
    /// not on the ordinary restart route.
    /// </summary>
    public OrdinaryPackageLayout? Ordinary { get; }

    /// <summary>INSTORD1: every Thunderstore dependency of an ordinary package and how it is satisfied.</summary>
    public IReadOnlyList<OrdinaryDependency> OrdinaryDependencies { get; }

    /// <summary>INSTORD1: the client ledger records the files this plan writes.</summary>
    public bool LedgerClient { get; }

    /// <summary>INSTORD1: the local-server ledger records the files this plan writes.</summary>
    public bool LedgerServer { get; }

    /// <summary>
    /// HOT4: the per-process decisions this plan carries — one half for a single target, two for
    /// <see cref="InstallTarget.Both"/>. Empty for a plan built without admission (the legacy
    /// eligibility stub and the blocked routes), which is the same "nothing per half to say" as before.
    /// </summary>
    public IReadOnlyList<InstallHalf> Halves { get; }

    /// <summary>The half for one process, or null when this plan has none.</summary>
    public InstallHalf? HalfFor(InstallTarget target)
    {
        for (int i = 0; i < Halves.Count; i++)
            if (Halves[i].Target == target) return Halves[i];
        return null;
    }

    /// <summary>
    /// HOT4: a <see cref="InstallTarget.Both"/> plan whose halves disagree — the client half loads
    /// now, the local server's waits for its next start.
    /// </summary>
    public bool IsMixed =>
        Target == InstallTarget.Both
        && HalfFor(InstallTarget.Client)?.Decision == InstallDecision.ApplyNow
        && HalfFor(InstallTarget.LocalServer) is { } server
        && server.Decision != InstallDecision.ApplyNow;

    /// <summary>The plugin GUID an ordinary package's DLL declared, read from the bytes.</summary>
    public string? OrdinaryPluginGuid => Ordinary?.Plugin?.PluginGuid;

    /// <summary>INSTORD1: <c>client</c> | <c>server</c> | <c>both</c> | <c>unknown</c> from <c>[BepInProcess]</c>.</summary>
    public string OrdinaryLoadsIn => Ordinary?.LoadsIn ?? "unknown";

    /// <summary>The companion DLL names this plan places beside the plugin.</summary>
    public IReadOnlyList<string> CompanionFileNames => Ordinary?.CompanionNames ?? Array.Empty<string>();

    public bool CanExecuteToPending =>
        Decision is InstallDecision.RestartClientRequired
            or InstallDecision.RestartServerRequired
            or InstallDecision.RestartBothRequired
            or InstallDecision.ApplyNow;
}

/// <summary>UI-facing progress snapshot. Explicit states, no Unity types.</summary>
internal sealed class InstallProgress
{
    public InstallProgress(
        InstallStatusKind kind,
        string message,
        long bytesReceived = 0,
        long? bytesTotal = null,
        string? entryKey = null)
    {
        Kind = kind;
        Message = message ?? "";
        BytesReceived = bytesReceived;
        BytesTotal = bytesTotal;
        EntryKey = entryKey;
    }

    public InstallStatusKind Kind { get; }
    public string Message { get; }
    public long BytesReceived { get; }
    public long? BytesTotal { get; }
    public string? EntryKey { get; }

    public static InstallProgress Idle { get; } = new(InstallStatusKind.Idle, "");
}

/// <summary>
/// Result of a fetch. <see cref="Sha256"/> is a locally computed hash of the bytes that
/// landed on disk — an integrity measurement of what was received, not proof of publisher
/// authenticity. Thunderstore publishes no checksum and there is no signing authority on
/// this path.
/// </summary>
internal sealed class CatalogFetchResult
{
    public CatalogFetchResult(
        bool ok,
        string? error,
        string? path,
        long byteCount,
        string? sha256,
        int statusCode,
        string? indexStamp)
    {
        Ok = ok;
        Error = error;
        Path = path;
        ByteCount = byteCount;
        Sha256 = sha256;
        StatusCode = statusCode;
        IndexStamp = indexStamp;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public string? Path { get; }
    public long ByteCount { get; }

    /// <summary>
    /// SHA-256 of the received bytes, 64 lowercase hex. Integrity of what arrived, not a
    /// publisher signature — there is no signing authority on the public-index path.
    /// </summary>
    public string? Sha256 { get; }

    public int StatusCode { get; }
    public string? IndexStamp { get; }
}

/// <summary>In-memory text fetch. Used for listing README/changelog JSON, not zip downloads.</summary>
internal sealed class CatalogTextFetchResult
{
    public CatalogTextFetchResult(bool ok, string? error, string? text, long byteCount, int statusCode)
    {
        Ok = ok;
        Error = error;
        Text = text;
        ByteCount = byteCount;
        StatusCode = statusCode;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public string? Text { get; }
    public long ByteCount { get; }
    public int StatusCode { get; }

    /// <summary>
    /// TSTRAFFIC1: the server answered 304 to a conditional request. <see cref="Text"/> is then the
    /// cached body the caller supplied it from (<see cref="ConditionalIndexFetch"/>), or null from the fetcher itself.
    /// </summary>
    public bool NotModified { get; init; }

    /// <summary>TSTRAFFIC1: the response's <c>Last-Modified</c>, or null.</summary>
    public string? LastModified { get; init; }

    /// <summary>TSTRAFFIC1: the response's <c>ETag</c>, or null.</summary>
    public string? ETag { get; init; }

    /// <summary>TSTRAFFIC1: body bytes as they crossed the network (encoded); <see cref="ByteCount"/> is the decoded size.</summary>
    public long WireBytes { get; init; }

    /// <summary>TSTRAFFIC1: the body's <c>Content-Encoding</c>, or null for identity.</summary>
    public string? ContentEncoding { get; init; }

    /// <summary>INSTALLERR1: the transport cause of a failure, from the exception's type (never its localized text).</summary>
    public FetchFailureCause FailureCause { get; init; }

    /// <summary>INSTALLERR1: the check was skipped by the failure backoff (<see cref="ConditionalIndexFetch"/>).</summary>
    public bool Paused { get; init; }
}

/// <summary>Inspection of a downloaded zip. Never loads an assembly.</summary>
internal sealed class PackageInspection
{
    public PackageInspection(
        bool ok,
        string? refusal,
        CatalogProvenance provenance,
        string zipSha256,
        long zipSize,
        IReadOnlyList<ArtifactHash> dllHashes,
        ManifestMod? progenitorManifest,
        PackageSide side,
        bool hotLoadableDeclared,
        bool hasProgenitorJson,
        bool hasThunderstoreManifest,
        bool unsupportedLayout,
        string? layoutReason,
        IReadOnlyList<string> entryNames,
        IReadOnlyList<PluginDependency> pluginDependencies,
        string? thunderstoreName,
        OrdinaryPackageLayout? ordinaryLayout = null)
    {
        Ok = ok;
        Refusal = refusal;
        Provenance = provenance;
        ZipSha256 = zipSha256 ?? "";
        ZipSize = zipSize;
        DllHashes = dllHashes ?? Array.Empty<ArtifactHash>();
        ProgenitorManifest = progenitorManifest;
        Side = side;
        HotLoadableDeclared = hotLoadableDeclared;
        HasProgenitorJson = hasProgenitorJson;
        HasThunderstoreManifest = hasThunderstoreManifest;
        UnsupportedLayout = unsupportedLayout;
        LayoutReason = layoutReason;
        EntryNames = entryNames ?? Array.Empty<string>();
        PluginDependencies = pluginDependencies ?? Array.Empty<PluginDependency>();
        ThunderstoreName = thunderstoreName;
        OrdinaryLayout = ordinaryLayout;
    }

    public bool Ok { get; }
    public string? Refusal { get; }
    public CatalogProvenance Provenance { get; }

    /// <summary>Hash of the zip file as received. Labeled <c>zip</c>; not a DLL hash.</summary>
    public string ZipSha256 { get; }

    public long ZipSize { get; }

    /// <summary>Per-entry SHA-256 of each DLL. Labeled <c>dll</c>; not the zip hash.</summary>
    public IReadOnlyList<ArtifactHash> DllHashes { get; }

    public ManifestMod? ProgenitorManifest { get; }
    public PackageSide Side { get; }
    public bool HotLoadableDeclared { get; }
    public bool HasProgenitorJson { get; }
    public bool HasThunderstoreManifest { get; }
    public bool UnsupportedLayout { get; }
    public string? LayoutReason { get; }
    public IReadOnlyList<string> EntryNames { get; }
    public IReadOnlyList<PluginDependency> PluginDependencies { get; }
    public string? ThunderstoreName { get; }

    /// <summary>
    /// Mod compatibility C3: <c>compatibility.peers</c> as progenitor.json declares it (checked against the image's
    /// <c>[NetworkCompatibility]</c> <c>Peers</c>), or empty when none.
    /// </summary>
    public string DeclaredPeers { get; init; } = "";

    /// <summary>
    /// Mod compatibility C3: the range this version's own build uses for its peers (the declared range, else the image's
    /// explicit rule, else the same minor version), or null when the plugin image could not be read.
    /// </summary>
    public string? EffectivePeers { get; init; }

    /// <summary>
    /// INSTORD1: the approved-archive-layout verdict for an ordinary BepInEx package - the one
    /// plugin DLL, its companions and the processes it declares - or a refusal naming why this
    /// archive is not one.
    /// </summary>
    public OrdinaryPackageLayout? OrdinaryLayout { get; }

    /// <summary>True when this archive is an ordinary package the restart install path can place.</summary>
    public bool OrdinaryApproved => OrdinaryLayout is { Ok: true, Plugin: not null };
}

/// <summary>
/// Plain typed interface the UI (and later MCP) calls. Game-free: no Unity types.
/// Load, search, inspect, plan, approve and execute-to-pending are the whole surface.
/// Same-session writes into <c>progenitor-reload/</c> are not on this interface.
/// </summary>
internal interface IInstallCatalog
{
    InstallProgress Status { get; }

    Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken);

    IReadOnlyList<CatalogEntry> Search(string? query);

    bool TryGetEntry(string key, out CatalogEntry? entry);

    Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken);

    PlanCallResult Plan(string key, InstallTarget target);

    ApproveCallResult Approve(string planHash);

    ExecuteCallResult ExecuteToPending(string planHash);

    /// <summary>
    /// On-demand README and changelog for one listing. Does not walk the index.
    /// Cached per package version for this catalog instance.
    /// </summary>
    Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Already-fetched documents for one listing's current version, without a request.
    /// The default says nothing is cached; <see cref="InstallCatalogService"/> answers from
    /// its per-process <see cref="ThunderstoreListingDocuments"/> so a rebuilt pane can
    /// search README text that an earlier pane fetched.
    /// </summary>
    bool TryGetCachedListingDocuments(string key, out ListingDocumentSet? documents)
    {
        documents = null;
        return false;
    }
}

internal readonly struct CatalogLoadResult
{
    public CatalogLoadResult(bool ok, string? error, int count, string? indexStamp)
    {
        Ok = ok;
        Error = error;
        Count = count;
        IndexStamp = indexStamp;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public int Count { get; }
    public string? IndexStamp { get; }

    /// <summary>INSTALLERR1: the transport cause of a failed fetch, from the exception's type (never its localized text).</summary>
    public FetchFailureCause FailureCause { get; init; }

    /// <summary>INSTALLERR1: the last HTTP status of a failed fetch (0: none).</summary>
    public int StatusCode { get; init; }

    /// <summary>INSTALLERR1: the check was skipped by the failure backoff (<see cref="ConditionalIndexFetch"/>).</summary>
    public bool Paused { get; init; }
}

internal readonly struct InspectCallResult
{
    public InspectCallResult(bool ok, string? error, CatalogEntry? entry, PackageInspection? inspection)
    {
        Ok = ok;
        Error = error;
        Entry = entry;
        Inspection = inspection;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public CatalogEntry? Entry { get; }
    public PackageInspection? Inspection { get; }
}

internal readonly struct PlanCallResult
{
    public PlanCallResult(bool ok, string? error, InstallPlan? plan)
    {
        Ok = ok;
        Error = error;
        Plan = plan;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public InstallPlan? Plan { get; }
}

internal readonly struct ApproveCallResult
{
    public ApproveCallResult(bool ok, string? error, string? planHash, bool alreadyApproved)
    {
        Ok = ok;
        Error = error;
        PlanHash = planHash;
        AlreadyApproved = alreadyApproved;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public string? PlanHash { get; }
    public bool AlreadyApproved { get; }
}

internal readonly struct ExecuteCallResult
{
    public ExecuteCallResult(
        bool ok,
        string? error,
        string? pendingPath,
        bool alreadyPresent,
        bool hotLoaded = false,
        string? sha256 = null,
        string? serverPendingPath = null,
        bool rechecked = false)
    {
        Ok = ok;
        Error = error;
        PendingPath = pendingPath;
        AlreadyPresent = alreadyPresent;
        HotLoaded = hotLoaded;
        Sha256 = sha256;
        ServerPendingPath = serverPendingPath;
        Rechecked = rechecked;
    }

    /// <summary>
    /// HOT2: the Bootstrap admission snapshot was captured again immediately before this write and
    /// still described the loader state the approval was bound to (handoff item 4). False on the
    /// restart route, which needs no loader state, and false when no snapshot source was wired —
    /// in which case nothing could have been admitted for hot load either.
    /// </summary>
    public bool Rechecked { get; }

    public bool Ok { get; }
    public string? Error { get; }

    /// <summary>
    /// The file this call wrote: the <c>progenitor-pending/</c> path on the restart route, or the
    /// <c>progenitor-reload/</c> path when <see cref="HotLoaded"/> (HOT1).
    /// </summary>
    public string? PendingPath { get; }

    public bool AlreadyPresent { get; }

    /// <summary>
    /// HOT1: true when <see cref="PendingPath"/> is a <c>progenitor-reload/</c> file in this
    /// process, which the reloader picks up within a second. No restart is required.
    /// </summary>
    public bool HotLoaded { get; }

    /// <summary>HOT1: the sha256 of the bytes written, for the readback.</summary>
    public string? Sha256 { get; }

    /// <summary>
    /// HOT1: with the Both target on a client process, the local-server half is a restart install;
    /// this is the pending file written on that root, or null when there was none.
    /// </summary>
    public string? ServerPendingPath { get; }

    /// <summary>The readback object the bridge and the pane show for one executed install.</summary>
    public object ToReadback() => new
    {
        ok = Ok,
        error = Error,
        path = PendingPath,
        sha256 = Sha256,
        hotLoaded = HotLoaded,
        alreadyPresent = AlreadyPresent,
        serverPendingPath = ServerPendingPath,
        rechecked = Rechecked,
    };
}

/// <summary>
/// Running-framework eligibility for same-session hot load. The runtime implements this;
/// this job leaves it unimplemented so nothing writes <c>progenitor-reload/</c> here.
/// </summary>
internal interface IHotLoadEligibility
{
    bool TryEvaluate(CatalogEntry entry, PackageInspection inspection, out string reason);
}

/// <summary>Default: same-session load is not offered. Coordinator wires the live implementation.</summary>
internal sealed class UnimplementedHotLoadEligibility : IHotLoadEligibility
{
    public bool TryEvaluate(CatalogEntry entry, PackageInspection inspection, out string reason)
    {
        reason = InstallReasons.EligibilityNotWired;
        return false;
    }
}

/// <summary>Injectable clock so fetch backoff tests do not sleep for real intervals.</summary>
internal interface ICatalogClock
{
    DateTime UtcNow { get; }
    Task Delay(TimeSpan duration, CancellationToken cancellationToken);
}

internal sealed class SystemCatalogClock : ICatalogClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public Task Delay(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}

/// <summary>One outbound HTTP request. Tests supply a fake transport; nothing here calls the network.</summary>
internal sealed class CatalogTransportRequest
{
    public CatalogTransportRequest(Uri uri, string method = "GET", IReadOnlyDictionary<string, string>? headers = null)
    {
        Uri = uri ?? throw new ArgumentNullException(nameof(uri));
        Method = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        Headers = headers ?? NoHeaders;
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    public Uri Uri { get; }
    public string Method { get; }

    /// <summary>
    /// TSTRAFFIC1: extra request headers (<c>Accept-Encoding</c>, <c>If-Modified-Since</c>,
    /// <c>If-None-Match</c>). Empty for every request that does not ask for them.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public string? Header(string name)
    {
        foreach (var kv in Headers)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}

/// <summary>Transport response. The caller owns <see cref="Content"/> and must dispose it.</summary>
internal sealed class CatalogTransportResponse : IDisposable
{
    public CatalogTransportResponse(
        int statusCode,
        Stream content,
        Uri? location = null,
        long? contentLength = null,
        TimeSpan? retryAfter = null)
    {
        StatusCode = statusCode;
        Content = content ?? Stream.Null;
        Location = location;
        ContentLength = contentLength;
        RetryAfter = retryAfter;
    }

    public int StatusCode { get; }
    public Stream Content { get; }
    public Uri? Location { get; }
    public long? ContentLength { get; }
    public TimeSpan? RetryAfter { get; }

    /// <summary>TSTRAFFIC1: the response's <c>Last-Modified</c> header as sent, or null.</summary>
    public string? LastModified { get; init; }

    /// <summary>TSTRAFFIC1: the response's <c>ETag</c> header as sent, or null.</summary>
    public string? ETag { get; init; }

    /// <summary>
    /// TSTRAFFIC1: the response's <c>Content-Encoding</c> (for example <c>gzip</c>), or null for an
    /// identity body. <see cref="ContentLength"/> is then the encoded (wire) length.
    /// </summary>
    public string? ContentEncoding { get; init; }

    /// <summary>The same response over another body stream (a wrapper's counting or guarded stream); every header is kept.</summary>
    public CatalogTransportResponse WithContent(Stream content) =>
        new(StatusCode, content, Location, ContentLength, RetryAfter)
        {
            LastModified = LastModified,
            ETag = ETag,
            ContentEncoding = ContentEncoding,
        };

    public void Dispose() => Content.Dispose();
}

/// <summary>
/// Injectable HTTP seam. Production uses <see cref="HttpCatalogTransport"/> over BCL
/// <c>HttpClient</c>; tests use a fake. No live calls in tests.
/// </summary>
internal interface ICatalogTransport
{
    Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken);
}
