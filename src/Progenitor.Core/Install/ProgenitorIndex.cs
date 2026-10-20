using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// The keys built into Core that sign the Progenitor Index (<see cref="ProgenitorIndex"/>): the public halves (SPKI DER,
/// base64) of ECDSA P-256 keys. A list is accepted when its signature verifies with <see cref="Current"/> or
/// <see cref="Next"/>, so the key can be rolled: put the new key in <see cref="Next"/>, ship a release, sign with the new
/// key, then move it to <see cref="Current"/>. An empty slot is no key.
/// <para>The Index repository publishes the same key in <c>index-signing-key.pub.txt</c> (fingerprint
/// <c>eee964f144cb8d67</c>).</para>
/// </summary>
internal static class ProgenitorIndexKeys
{
    // progenitor-index:current (fingerprint eee964f144cb8d67)
    public static readonly string Current = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtSzh6ihdJ+fJ3R+OMxxa2p0hYfNz1eMat1Df1tgflUTq46fluX8cD1v5Gj4DfxuEevBNy+LS5Xz9z09KMYRpqw==";

    // progenitor-index:next
    public static readonly string Next = "";

    /// <summary>The keys a list may be signed with (empty slots left out).</summary>
    public static string[] Trusted()
    {
        var keys = new List<string>(2);
        if (Current.Length > 0) keys.Add(Current);
        if (Next.Length > 0) keys.Add(Next);
        return keys.ToArray();
    }
}

/// <summary>How a takedown in the Index's <c>revoked[]</c> treats a listed version.</summary>
internal enum IndexRevocationMode
{
    /// <summary>Not a takedown.</summary>
    None = 0,

    /// <summary>No longer offered for new installs; installed copies are left alone.</summary>
    Hidden = 1,

    /// <summary>Listed with a warning; installed copies are flagged with the reason.</summary>
    Warning = 2,

    /// <summary>Neither listed nor installable; installed copies are flagged with the reason.</summary>
    Disable = 3,
}

/// <summary>One <c>revoked[]</c> row: owner, name, version (<c>*</c> for every version), reason, severity and date.</summary>
internal sealed class IndexRevocation
{
    public string Owner { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Reason { get; init; } = "";
    public IndexRevocationMode Mode { get; init; }
    public string Date { get; init; } = "";

    public bool Covers(string? owner, string? name, string? version) =>
        string.Equals(Owner, owner ?? "", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Name, name ?? "", StringComparison.OrdinalIgnoreCase)
        && (Version == "*" || string.Equals(Version, version ?? "", StringComparison.Ordinal));
}

/// <summary>One dependency of a listed version, pinned to one exact file: <c>id</c> (<c>Owner-Name-Version</c>), where it comes from, its URL and sha256.</summary>
internal sealed class IndexDependency
{
    public const string FromIndex = "index";
    public const string FromThunderstore = "thunderstore";

    public string Id { get; init; } = "";
    public string From { get; init; } = "";
    public string Package { get; init; } = "";
    public string Sha256 { get; init; } = "";

    /// <summary>The <c>Owner-Name</c> listing key of <see cref="Id"/> (the version stripped), or empty.</summary>
    public string Key => ProgenitorIndex.SplitId(Id, out string key, out _) ? key : "";

    /// <summary>The exact version <see cref="Id"/> names, or empty.</summary>
    public string Version => ProgenitorIndex.SplitId(Id, out _, out string v) ? v : "";
}

/// <summary>
/// One listed mod version (a <c>packages[]</c> row). Fields this build does not know are ignored when it is read, so the
/// Index can add new ones.
/// </summary>
internal sealed class IndexPackage
{
    public const string SourceGitHubRelease = "github-release";
    public const string SourceThunderstore = "thunderstore";

    public string Id { get; init; } = "";
    public string Owner { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Description { get; init; } = "";
    public string WebsiteUrl { get; init; } = "";
    public string Repo { get; init; } = "";
    public string Commit { get; init; } = "";
    public string Source { get; init; } = "";
    public string Package { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    public bool Imported { get; init; }
    public bool Confirmed { get; init; }
    public IReadOnlyList<IndexDependency> Dependencies { get; init; } = Array.Empty<IndexDependency>();

    /// <summary>The icon's URL as listed (relative to the Index's Pages root) and its sha256.</summary>
    public string IconUrl { get; init; } = "";
    public string IconSha256 { get; init; } = "";

    /// <summary><c>client</c>, <c>server</c>, <c>both</c>, or empty when the package names none.</summary>
    public string Sides { get; init; } = "";

    /// <summary>The Progenitor version range the mod needs (<c>*</c> for any).</summary>
    public string Progenitor { get; init; } = "";
    public string Readme { get; init; } = "";
    public string? Changelog { get; init; }

    /// <summary>The package's <c>progenitor.json</c> carries one (copied into the Index when it was reviewed).</summary>
    public bool HasProgenitorJson { get; init; }

    /// <summary>The copied <c>progenitor.json</c> declares <c>hotLoadable: true</c>. A listing fact: the install still reads the package's own file.</summary>
    public bool DeclaresHotLoadable { get; init; }

    /// <summary>The listing key, <c>Owner-Name</c> (the Thunderstore spelling, so one mod is one row).</summary>
    public string Key => Owner + "-" + Name;

    public PackageSide Side => Sides.ToLowerInvariant() switch
    {
        "client" => PackageSide.Client,
        "server" => PackageSide.Server,
        "both" => PackageSide.Both,
        _ => PackageSide.Unknown,
    };
}

/// <summary>A verified, parsed Index: its signed time, packages (every listed version) and takedowns.</summary>
internal sealed class IndexDocument
{
    public static readonly IndexDocument Empty = new();

    public int Format { get; init; }
    public DateTime? Generated { get; init; }
    public IReadOnlyList<IndexPackage> Packages { get; init; } = Array.Empty<IndexPackage>();
    public IReadOnlyList<IndexRevocation> Revoked { get; init; } = Array.Empty<IndexRevocation>();

    /// <summary>The takedown that covers this version, or null.</summary>
    public IndexRevocation? RevocationOf(string? owner, string? name, string? version)
    {
        IndexRevocation? worst = null;
        foreach (var r in Revoked)
            if (r.Covers(owner, name, version) && (worst == null || r.Mode > worst.Mode)) worst = r;
        return worst;
    }

    /// <summary>The listed package with this exact id (<c>Owner-Name-Version</c>), or null.</summary>
    public IndexPackage? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        foreach (var p in Packages)
            if (string.Equals(p.Id, id!.Trim(), StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }
}

/// <summary>
/// The Progenitor Index (decided 2026-10-20: the signed, reviewed list Progenitor-based mods are listed and installed from;
/// Thunderstore does not host them). A JSON file on the Index repository's GitHub Pages site (<see cref="PrimaryUrl"/>) with
/// its signature beside it (<see cref="SignatureSuffix"/>). Nothing else is read: no GitHub API, no raw.githubusercontent.com,
/// and no CDN copy (jsDelivr cannot serve it: Pages is deployed by Actions, so there is no branch holding the files).
/// <para><b>Signature.</b> ECDSA P-256 + SHA-256 over the exact bytes of <c>index.json</c>, IEEE P1363 <c>r||s</c> as 128
/// hex characters in <c>index.json.sig</c>, keys as SPKI base64: the scheme of <see cref="ModManifestCrypto"/> and the
/// release list (<see cref="SelfFeed"/>). One that verifies with a key built into Core (<see cref="ProgenitorIndexKeys"/>)
/// accepts the list.</para>
/// <para><b>Cache.</b> A verified list is cached under <c>progenitor-cache</c> as one text, the signature line first and the
/// list after it (<see cref="SelfFeed.Envelope"/>), and verified again whenever it is read.</para>
/// </summary>
internal static class ProgenitorIndex
{
    public const int Format = 1;

    public const string PagesRoot = "https://vrising-modders.github.io/ProgenitorIndex/";
    public const string PrimaryUrl = PagesRoot + "index.json";
    public const string SignatureSuffix = ".sig";

    /// <summary>The catalog source id of a listing read from the Index (<see cref="CatalogEntry.SourceId"/>).</summary>
    public const string SourceId = "index";

    /// <summary>The player-facing name of the source.</summary>
    public const string SourceLabel = "Progenitor Index";

    /// <summary>The ledger's <c>origin</c> word of a file installed from the Index (<see cref="ServerInstallLedgerEntry.Origin"/>).</summary>
    public const string LedgerOrigin = "index";

    /// <summary>The list is about 1 MB with 184 versions (2026-10-20); this leaves room to grow.</summary>
    public const long MaxIndexBytes = 16L * 1024 * 1024;
    public const long MaxSignatureBytes = 4 * 1024;

    /// <summary>Thunderstore's download links, the only URL a <c>thunderstore</c> entry may name.</summary>
    public const string ThunderstoreDownloadPrefix = "https://thunderstore.io/package/download/";

    private static readonly JsonDocumentOptions ParseOptions = new() { AllowTrailingCommas = true, MaxDepth = 64 };

    // ---- verify ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="signature"/> (the <c>index.json.sig</c> text: 128 hex characters, surrounding whitespace
    /// ignored) is a signature of <paramref name="data"/> by one of <paramref name="keys"/> (SPKI base64).
    /// </summary>
    public static bool Verify(byte[] data, string? signature, IReadOnlyList<string> keys, out string error)
    {
        error = "";
        if (keys == null || keys.Count == 0) { error = "no Index key is built into this Progenitor"; return false; }
        string hex = (signature ?? "").Trim();
        if (hex.Length == 0) { error = "the Index is not signed"; return false; }
        if (hex.Length != ModManifestCrypto.SignatureHexLength || !ModManifestCrypto.TryParseHex(hex, out _))
        {
            error = "the Index signature is not " + ModManifestCrypto.SignatureHexLength + " hex characters";
            return false;
        }

        foreach (string key in keys)
        {
            if (!ModManifestCrypto.TryImportSpki(key, out var spki, out _, out _)) continue;
            if (ModManifestCrypto.Verify(spki, data, hex)) return true;
        }

        error = "the Index signature does not verify with a key this Progenitor trusts";
        return false;
    }

    /// <summary>A cached text verified and parsed (<see cref="IndexDocument.Empty"/>, with <paramref name="error"/> set, when it does not verify).</summary>
    public static IndexDocument ReadVerified(string? cached, IReadOnlyList<string> keys, out string error)
    {
        if (!SelfFeed.TrySplit(cached, out string sig, out string json)) { error = "no Index is cached"; return IndexDocument.Empty; }
        if (!Verify(Encoding.UTF8.GetBytes(json), sig, keys, out error)) return IndexDocument.Empty;
        return Parse(json, out error);
    }

    // ---- parse -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The packages and takedowns of a list (unsigned text: verify first). A package without an owner, name, version, a
    /// known source, a package URL, a sha256 or a size is left out. Unknown fields are ignored. An unreadable list or
    /// another format reads as <see cref="IndexDocument.Empty"/> with <paramref name="error"/> set.
    /// </summary>
    public static IndexDocument Parse(string? json, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(json)) { error = "the Index is empty"; return IndexDocument.Empty; }
        if (json!.Length > MaxIndexBytes) { error = "the Index is larger than " + MaxIndexBytes + " bytes"; return IndexDocument.Empty; }
        try
        {
            using var doc = JsonDocument.Parse(json, ParseOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "the Index is not an object"; return IndexDocument.Empty; }
            int format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out int n) ? n : 0;
            if (format != Format) { error = "the Index has format " + format + ", not " + Format; return IndexDocument.Empty; }

            var packages = new List<IndexPackage>();
            if (root.TryGetProperty("packages", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var p in arr.EnumerateArray())
                    if (TryReadPackage(p, out var package)) packages.Add(package);

            var revoked = new List<IndexRevocation>();
            if (root.TryGetProperty("revoked", out var rev) && rev.ValueKind == JsonValueKind.Array)
                foreach (var r in rev.EnumerateArray())
                    if (TryReadRevocation(r, out var revocation)) revoked.Add(revocation);

            return new IndexDocument
            {
                Format = format,
                Generated = TimeOf(Str(root, "generated")),
                Packages = packages,
                Revoked = revoked,
            };
        }
        catch (JsonException e)
        {
            error = "the Index could not be read: " + e.Message;
            return IndexDocument.Empty;
        }
    }

    /// <summary>The list's signed <c>generated</c> time (UTC), or null. A client never takes a list older than its cached one.</summary>
    public static DateTime? GeneratedOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json!, ParseOptions);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? TimeOf(Str(doc.RootElement, "generated")) : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool TryReadPackage(JsonElement p, out IndexPackage package)
    {
        package = null!;
        if (p.ValueKind != JsonValueKind.Object) return false;
        string owner = Str(p, "owner"), name = Str(p, "name"), version = Str(p, "version");
        string source = Str(p, "source"), url = Str(p, "package"), sha = Str(p, "sha256").ToLowerInvariant();
        long size = p.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long z) ? z : 0;
        if (owner.Length == 0 || name.Length == 0 || version.Length == 0 || url.Length == 0 || size <= 0 || !SelfFeed.IsSha256(sha)) return false;
        if (source != IndexPackage.SourceGitHubRelease && source != IndexPackage.SourceThunderstore) return false;

        var deps = new List<IndexDependency>();
        if (p.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Array)
            foreach (var dep in d.EnumerateArray())
            {
                if (dep.ValueKind != JsonValueKind.Object) continue;
                deps.Add(new IndexDependency
                {
                    Id = Str(dep, "id"),
                    From = Str(dep, "from"),
                    Package = Str(dep, "package"),
                    Sha256 = Str(dep, "sha256").ToLowerInvariant(),
                });
            }

        string iconUrl = "", iconSha = "";
        if (p.TryGetProperty("icon", out var icon) && icon.ValueKind == JsonValueKind.Object)
        {
            iconUrl = Str(icon, "url");
            iconSha = Str(icon, "sha256").ToLowerInvariant();
        }

        bool hasPj = p.TryGetProperty("progenitor_json", out var pj) && pj.ValueKind == JsonValueKind.Object;
        bool hot = hasPj && pj.TryGetProperty("hotLoadable", out var h) && h.ValueKind == JsonValueKind.True;
        // The list's `sides`; when it names none, the copied progenitor.json's own `side` (Progenitor's spelling).
        string sides = Str(p, "sides");
        if (sides.Length == 0 && hasPj) sides = Str(pj, "side");
        if (sides.Length == 0 && hasPj) sides = Str(pj, "sides");
        string id = Str(p, "id");
        package = new IndexPackage
        {
            Id = id.Length > 0 ? id : owner + "-" + name + "-" + version,
            Owner = owner,
            Name = name,
            Version = version,
            Description = Str(p, "description"),
            WebsiteUrl = Str(p, "website_url"),
            Repo = Str(p, "repo"),
            Commit = Str(p, "commit"),
            Source = source,
            Package = url,
            Sha256 = sha,
            Size = size,
            Imported = Bool(p, "imported"),
            Confirmed = Bool(p, "confirmed"),
            Dependencies = deps,
            IconUrl = iconUrl,
            IconSha256 = iconSha,
            Sides = sides,
            Progenitor = Str(p, "progenitor"),
            Readme = Str(p, "readme"),
            Changelog = p.TryGetProperty("changelog", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
            HasProgenitorJson = hasPj,
            DeclaresHotLoadable = hot,
        };
        return true;
    }

    private static bool TryReadRevocation(JsonElement r, out IndexRevocation revocation)
    {
        revocation = null!;
        if (r.ValueKind != JsonValueKind.Object) return false;
        string owner = Str(r, "owner"), name = Str(r, "name"), version = Str(r, "version");
        if (owner.Length == 0 || name.Length == 0) return false;
        revocation = new IndexRevocation
        {
            Owner = owner,
            Name = name,
            Version = version.Length == 0 ? "*" : version,
            Reason = Str(r, "reason"),
            // Accepts the Index's lowercase words and the capitalized ones; a severity this build does not know is
            // treated as the strictest, so a new kind of takedown never leaves a mod installable.
            Mode = Str(r, "severity").ToLowerInvariant() switch
            {
                "hidden" => IndexRevocationMode.Hidden,
                "warning" => IndexRevocationMode.Warning,
                _ => IndexRevocationMode.Disable,
            },
            Date = Str(r, "date"),
        };
        return true;
    }

    /// <summary>Split <c>Owner-Name-Version</c> into the listing key and the version (the last dash).</summary>
    public static bool SplitId(string? id, out string key, out string version)
    {
        key = version = "";
        string s = (id ?? "").Trim();
        int dash = s.LastIndexOf('-');
        if (dash <= 0 || dash == s.Length - 1) return false;
        key = s.Substring(0, dash);
        version = s.Substring(dash + 1);
        return key.IndexOf('-') > 0;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? TimeOf(string s) =>
        s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;

    // ---- where a package comes from --------------------------------------------------------------------------

    /// <summary>
    /// Is <paramref name="url"/> a download this entry may use? A <c>github-release</c> entry: an https release asset of
    /// its own repository (<c>&lt;repo&gt;/releases/download/...</c>). A <c>thunderstore</c> entry: an https Thunderstore
    /// download link. <paramref name="devOrigin"/>: the dev Index's origin, whose own URLs are allowed while it is active.
    /// </summary>
    public static bool IsAllowedPackageUrl(string? source, string? repo, string? url, string? devOrigin = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string u = url!.Trim();
        if (!string.IsNullOrEmpty(devOrigin) && SameOrigin(u, devOrigin!)) return true;
        if (u.IndexOf("..", StringComparison.Ordinal) >= 0 || u.IndexOf('\\') >= 0) return false;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (source == IndexPackage.SourceGitHubRelease)
        {
            string r = (repo ?? "").Trim().TrimEnd('/');
            if (!r.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return false;
            string prefix = r + "/releases/download/";
            return u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && u.Length > prefix.Length;
        }

        if (source == IndexPackage.SourceThunderstore)
            return u.StartsWith(ThunderstoreDownloadPrefix, StringComparison.OrdinalIgnoreCase) && u.Length > ThunderstoreDownloadPrefix.Length;
        return false;
    }

    /// <summary>
    /// Where a download of this source may be redirected (https only): GitHub's release-asset hosts for a release
    /// (<see cref="SelfFeed.AssetRedirectHosts"/>), thunderstore.io and its subdomains (its CDN) for Thunderstore.
    /// </summary>
    public static bool IsAllowedRedirect(string? source, Uri? target, string? devOrigin = null)
    {
        if (target == null) return false;
        if (!string.IsNullOrEmpty(devOrigin) && SameOrigin(target.AbsoluteUri, devOrigin!)) return true;
        if (!string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        string host = target.Host.ToLowerInvariant();
        if (source == IndexPackage.SourceGitHubRelease)
        {
            foreach (string h in SelfFeed.AssetRedirectHosts)
                if (string.Equals(host, h, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        if (source == IndexPackage.SourceThunderstore)
            return host == "thunderstore.io" || host.EndsWith(".thunderstore.io", StringComparison.Ordinal);
        return false;
    }

    /// <summary>
    /// What a downloaded package must match: the signed size (when known) and sha256. Empty when it does; the reason
    /// otherwise.
    /// </summary>
    public static string CheckDownload(long signedSize, string? signedSha256, long gotSize, string? gotSha256)
    {
        if (signedSize > 0 && gotSize != signedSize)
            return "the download is " + gotSize.ToString(CultureInfo.InvariantCulture) + " bytes; the Progenitor Index lists "
                + signedSize.ToString(CultureInfo.InvariantCulture);
        if (!SelfFeed.IsSha256(signedSha256)) return "the Progenitor Index lists no sha256 for this file";
        if (!string.Equals(gotSha256 ?? "", signedSha256, StringComparison.OrdinalIgnoreCase))
            return "the download's sha256 is not the one the Progenitor Index lists; it was not used";
        return "";
    }

    /// <summary>The icon's absolute URL (the listed one is relative to <paramref name="root"/>), or empty.</summary>
    public static string IconUrlOf(IndexPackage package, string root = PagesRoot)
    {
        string u = package?.IconUrl ?? "";
        if (u.Length == 0 || u.IndexOf("..", StringComparison.Ordinal) >= 0) return "";
        if (Uri.TryCreate(u, UriKind.Absolute, out _)) return u.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? u : "";
        string baseRoot = root.EndsWith("/", StringComparison.Ordinal) ? root : root + "/";
        return baseRoot + u.TrimStart('/');
    }

    internal static bool SameOrigin(string url, string origin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var a) || !Uri.TryCreate(origin, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
    }
}
