using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Networking;
using Progenitor.SelfUpdate;

namespace Progenitor.Install;

/// <summary>
/// Progenitor's own release list (decided 2026-10-20: Progenitor ships from GitHub Releases). A signed JSON file on the
/// repository's GitHub Pages site, <see cref="PrimaryUrl"/>, with a detached signature beside it (<see cref="SignatureSuffix"/>);
/// the same two files through jsDelivr (<see cref="FallbackUrl"/>) are read only when the primary read fails. Clients never
/// use the GitHub API or raw.githubusercontent.com.
/// <para><b>Signature.</b> ECDSA P-256 + SHA-256 over the exact bytes of <c>releases.json</c>, the scheme the signed mod
/// manifests use (<see cref="ModManifestCrypto"/>): IEEE P1363 <c>r||s</c> as hex, keys as SPKI base64. The signature file
/// holds one or more signatures, each naming its key's fingerprint; one that verifies with a key built into Core
/// (<see cref="SelfFeedKeys"/>) accepts the list.</para>
/// <para><b>Cache.</b> A verified list is cached under <c>progenitor-cache</c> as one text, the signature file's line first and
/// the list after it (<see cref="Envelope"/>), and verified again whenever it is read.</para>
/// </summary>
internal static class SelfFeed
{
    public const int Schema = 1;

    /// <summary>The package id the list, the self-install record and the version store name Progenitor by.</summary>
    public const string Package = "VRising_Modders-Progenitor";

    public const string Channel = "release";

    /// <summary>The self-install record's <c>source</c> after Progenitor installed a release itself.</summary>
    public const string RecordSource = "github";

    /// <summary>The record a release zip carries (<c>tools/package.ps1</c>) names this source.</summary>
    public const string PackageRecordSource = "package";

    /// <summary>
    /// A kept release's stored source word. It stays <c>thunderstore</c> (the word phase 5 wrote) because Bootstrap's
    /// start-loop guard reads it to decide whether a rollback writes the record (<c>SelfUpdate/BootHealth.cs</c>), and that
    /// file is compiled into Bootstrap; changing it would need a Bootstrap update for no behavior change.
    /// </summary>
    public const string KeptSource = "thunderstore";

    public const string PrimaryUrl = "https://vrising-modders.github.io/Progenitor/updates/releases.json";
    public const string FallbackUrl = "https://cdn.jsdelivr.net/gh/VRising-Modders/Progenitor@main/docs/updates/releases.json";
    public const string SignatureSuffix = ".sig";

    /// <summary>The release assets' URL prefix; a listed package outside it is not used.</summary>
    public const string AssetPrefix = "https://github.com/VRising-Modders/Progenitor/releases/download/";

    /// <summary>Hosts a release download may be redirected to (GitHub serves release assets from these).</summary>
    public static readonly string[] AssetRedirectHosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    public const long MaxFeedBytes = 1024 * 1024;
    public const long MaxSignatureBytes = 64 * 1024;

    /// <summary>The cache's line between the signature and the list.</summary>
    private const char EnvelopeSeparator = '\n';

    // ---- parse -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The releases of a list (unsigned text; verify first). Entries without a version, an asset URL, a sha256 or a size are
    /// left out. An unreadable list or another schema reads as no releases with <paramref name="error"/> set.
    /// </summary>
    public static List<SelfRelease> Parse(string? json, out string error)
    {
        error = "";
        var list = new List<SelfRelease>();
        if (string.IsNullOrWhiteSpace(json)) { error = "the release list is empty"; return list; }
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "the release list is not an object"; return list; }
            int schema = root.TryGetProperty("schema", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out int n) ? n : 0;
            if (schema != Schema) { error = "the release list has schema " + schema + ", not " + Schema; return list; }
            string package = SelfState.Str(root, "package");
            if (package.Length == 0) package = Package;
            if (!root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) { error = "the release list has no releases"; return list; }
            foreach (var r in releases.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var release = new SelfRelease
                {
                    Package = package,
                    Version = SelfState.Str(r, "version"),
                    DownloadUrl = SelfState.Str(r, "url"),
                    Size = Int64(r, "size"),
                    Sha256 = SelfState.Str(r, "sha256").ToLowerInvariant(),
                    CoreSha = SelfState.Str(r, "coreSha256").ToLowerInvariant(),
                    BootstrapSha = SelfState.Str(r, "bootstrapSha256").ToLowerInvariant(),
                    Channel = SelfState.Str(r, "channel") is { Length: > 0 } c ? c : Channel,
                    Published = SelfState.Str(r, "published"),
                    NotesUrl = SelfState.Str(r, "notes"),
                    BootstrapProtocol = Int32(r, "bootstrapProtocol"),
                    CoreProtocol = Int32(r, "coreProtocol"),
                    HandshakeProtocol = Int32(r, "handshakeProtocol"),
                    Active = !(r.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False),
                };
                if (release.Version.Length == 0 || release.DownloadUrl.Length == 0 || release.Size <= 0 || !IsSha256(release.Sha256)) continue;
                list.Add(release);
            }
        }
        catch (JsonException e)
        {
            error = "the release list could not be read: " + e.Message;
            return new List<SelfRelease>();
        }

        return list;
    }

    /// <summary>
    /// The list's signed <c>updated</c> time (UTC), or null when it has none or cannot be read. The release tool sets it each
    /// time it signs; a client never takes a list older than its cached one (replay protection).
    /// </summary>
    public static DateTime? UpdatedOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string s = SelfState.Str(doc.RootElement, "updated");
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static long Int64(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

    private static int Int32(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

    public static bool IsSha256(string? s)
    {
        if (s == null || s.Length != 64) return false;
        foreach (char c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }

    // ---- verify ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="signatureJson"/> carries a signature of <paramref name="feed"/> by one of <paramref name="keys"/>
    /// (SPKI base64). The signature file: <c>{"schema":1,"signatures":[{"key":"&lt;fingerprint&gt;","signature":"&lt;hex&gt;"}]}</c>.
    /// </summary>
    public static bool Verify(byte[] feed, string? signatureJson, IReadOnlyList<string> keys, out string error)
    {
        error = "";
        if (keys == null || keys.Count == 0) { error = "no release key is built into this Progenitor"; return false; }
        var sigs = ParseSignatures(signatureJson, out error);
        if (sigs.Count == 0) { if (error.Length == 0) error = "the release list is not signed"; return false; }
        bool knownKey = false;
        foreach (string key in keys)
        {
            if (!ModManifestCrypto.TryImportSpki(key, out var spki, out string fp, out _)) continue;
            foreach (var (keyFp, hex) in sigs)
            {
                if (keyFp.Length > 0 && !string.Equals(keyFp, fp, StringComparison.OrdinalIgnoreCase)) continue;
                knownKey = true;
                if (ModManifestCrypto.Verify(spki, feed, hex)) return true;
            }
        }

        error = knownKey ? "the release list's signature does not verify" : "the release list is signed by a key this Progenitor does not trust";
        return false;
    }

    private static List<(string Key, string Signature)> ParseSignatures(string? json, out string error)
    {
        error = "";
        var list = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("signatures", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                error = "the signature file has no signatures";
                return list;
            }

            foreach (var s in arr.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object) continue;
                string hex = SelfState.Str(s, "signature");
                if (hex.Length == 0) continue;
                list.Add((SelfState.Str(s, "key"), hex));
            }
        }
        catch (JsonException e) { error = "the signature file could not be read: " + e.Message; }

        return list;
    }

    // ---- the cached form ------------------------------------------------------------------------------------

    /// <summary>The cached text: the signature file on one line, then the list's own text.</summary>
    public static string Envelope(string signatureJson, string feed)
    {
        string line = (signatureJson ?? "").Replace("\r", "").Replace("\n", "");
        return line + EnvelopeSeparator + (feed ?? "");
    }

    /// <summary>Split a cached text into its signature and list; false when it is not one.</summary>
    public static bool TrySplit(string? cached, out string signatureJson, out string feed)
    {
        signatureJson = "";
        feed = "";
        if (string.IsNullOrEmpty(cached)) return false;
        int i = cached!.IndexOf(EnvelopeSeparator);
        if (i <= 0) return false;
        signatureJson = cached.Substring(0, i);
        feed = cached.Substring(i + 1);
        return true;
    }

    /// <summary>A cached text verified and parsed (no releases, with <paramref name="error"/> set, when it does not verify).</summary>
    public static List<SelfRelease> ReadVerified(string? cached, IReadOnlyList<string> keys, out string error)
    {
        if (!TrySplit(cached, out string sig, out string feed)) { error = "no release list is cached"; return new List<SelfRelease>(); }
        if (!Verify(Encoding.UTF8.GetBytes(feed), sig, keys, out error)) return new List<SelfRelease>();
        return Parse(feed, out error);
    }

    // ---- where a release comes from -------------------------------------------------------------------------

    /// <summary>A listed asset URL Progenitor downloads: https on <see cref="AssetPrefix"/> (or the dev origin while one is set).</summary>
    public static bool IsAssetUrl(string? url, string? devOrigin = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string u = url!.Trim();
        if (u.StartsWith(AssetPrefix, StringComparison.Ordinal) && u.Length > AssetPrefix.Length && u.IndexOf("..", StringComparison.Ordinal) < 0) return true;
        return !string.IsNullOrEmpty(devOrigin) && SameOrigin(u, devOrigin!);
    }

    /// <summary>A redirect of a release download may go to these hosts only (https), or to the dev origin while one is set.</summary>
    public static bool IsAllowedRedirect(Uri? target, string? devOrigin = null)
    {
        if (target == null) return false;
        if (!string.IsNullOrEmpty(devOrigin) && SameOrigin(target.AbsoluteUri, devOrigin!)) return true;
        if (!string.Equals(target.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string h in AssetRedirectHosts)
            if (string.Equals(target.Host, h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool SameOrigin(string url, string origin)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var a) || !Uri.TryCreate(origin, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
    }

    /// <summary>
    /// What a downloaded package must match: its size and sha256 from the list. Empty when it does; the reason otherwise.
    /// </summary>
    public static string CheckDownload(SelfRelease release, byte[] bytes)
    {
        if (release == null || bytes == null) return "nothing was downloaded";
        if (release.Size > 0 && bytes.LongLength != release.Size)
            return "the download is " + bytes.LongLength.ToString(CultureInfo.InvariantCulture) + " bytes, the release list says " + release.Size.ToString(CultureInfo.InvariantCulture);
        if (release.Sha256.Length > 0 && !string.Equals(Progenitor.SelfUpdate.SelfFiles.Sha256(bytes), release.Sha256, StringComparison.OrdinalIgnoreCase))
            return "the download's sha256 is not the one the release list names";
        return "";
    }

    /// <summary>The release's images agree with what the list names (Core and Bootstrap sha256 when it names them).</summary>
    public static string CheckImages(SelfRelease release, SelfImage image)
    {
        if (release == null || image == null || !image.Ok) return "";
        if (release.CoreSha.Length > 0 && !string.Equals(release.CoreSha, image.CoreSha, StringComparison.OrdinalIgnoreCase))
            return "its Core is not the one the release list names";
        if (release.BootstrapSha.Length > 0 && !string.Equals(release.BootstrapSha, image.BootstrapSha, StringComparison.OrdinalIgnoreCase))
            return "its Bootstrap is not the one the release list names";
        return "";
    }

    /// <summary>Two package ids that both name Progenitor (the list's, a Thunderstore listing's, an older record's).</summary>
    public static bool SamePackage(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return IsProgenitor(a!) && IsProgenitor(b!);
    }

    private static bool IsProgenitor(string id) =>
        string.Equals(id, Package, StringComparison.OrdinalIgnoreCase) || LoaderPackages.IsProgenitorPackage(null, null, id);
}

/// <summary>
/// When the release list is read (pure). A client asks at the first start of a session when its copy is older than
/// <see cref="StartMaxAge"/>, and whenever the Install tab or the join screens read the mod index, at most every
/// <see cref="MinInterval"/>: the moments Progenitor already read the index for its own update before. A server follows its
/// update cadence (<c>[AutoUpdate] CheckHours</c>, <see cref="ServerUpdateCadence"/>).
/// </summary>
internal static class SelfFeedCadence
{
    public static readonly TimeSpan StartMaxAge = TimeSpan.FromHours(6);
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    /// <summary>Why a request is due now, or empty.</summary>
    public static string Due(bool server, DateTime nowUtc, DateTime? lastCheckUtc, DateTime? cacheStampUtc, bool indexReadSinceLast, int checkHours)
    {
        if (server)
            return ServerUpdateCadence.Due(nowUtc, lastCheckUtc, cacheStampUtc, checkHours) == ServerUpdateCadence.Request ? "cadence" : "";
        if (lastCheckUtc is not { } last)
        {
            bool fresh = cacheStampUtc is { } stamp && nowUtc >= stamp && nowUtc - stamp < StartMaxAge;
            if (!fresh) return "start";
            // The copy was fresh at start, so nothing was asked yet: an index read still checks, at most every 10 minutes
            // after the copy was last fetched or confirmed.
            var since = nowUtc - cacheStampUtc!.Value;
            return indexReadSinceLast && since >= MinInterval ? "index read" : "";
        }

        if (indexReadSinceLast && (nowUtc - last >= MinInterval || nowUtc < last)) return "index read";
        return "";
    }
}

/// <summary>
/// One process's conditional read of the release list (owner <c>self-update</c>): <see cref="ConditionalIndexFetch"/> over the
/// list's own cache, so it is conditional (<c>If-None-Match</c> / <c>If-Modified-Since</c>), gzip-compressed and backs off
/// after failures like every index read. A 200 is cached only with a signature that verifies. When the primary read or its
/// signature fails, the fallback URL is read once (unconditionally); otherwise it is never asked.
/// </summary>
internal sealed class SelfFeedFetch
{
    public const string Owner = "self-update";

    /// <summary>A text request: (url, validators, max bytes, token) → result.</summary>
    internal delegate Task<CatalogTextFetchResult> TextFetch(string url, IndexValidators validators, long maxBytes, CancellationToken ct);

    private readonly TextFetch _fetch;
    private readonly Func<IReadOnlyList<string>> _keys;
    private readonly string _primary;
    private readonly string _fallback;
    private readonly ConditionalIndexFetch _conditional;

    public SelfFeedFetch(TextFetch fetch, IndexDiskCache cache, Func<IReadOnlyList<string>> keys, string? fallbackUrl, ICatalogClock? clock = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _keys = keys ?? (() => SelfFeedKeys.Trusted());
        _primary = cache.Url;
        _fallback = fallbackUrl ?? "";
        _conditional = new ConditionalIndexFetch(FetchBoth, cache, Owner, clock, accept: body => SelfFeed.TrySplit(body, out _, out _), label: "Release list check");
    }

    public IndexDiskCache Cache => _conditional.Cache;
    public ConditionalIndexFetch Conditional => _conditional;

    /// <summary>How many of the last URLs <see cref="Asked"/> keeps.</summary>
    public const int AskedKept = 16;

    private readonly List<string> _asked = new();

    /// <summary>The last <see cref="AskedKept"/> URLs asked (tests and the readback), oldest first.</summary>
    public List<string> Asked
    {
        get { lock (_asked) return new List<string>(_asked); }
    }

    private void NoteAsked(string url)
    {
        lock (_asked)
        {
            _asked.Add(url);
            if (_asked.Count > AskedKept) _asked.RemoveAt(0);
        }
    }

    public Task<CatalogTextFetchResult> FetchAsync(CancellationToken ct, bool force = false) => _conditional.FetchAsync(ct, force);

    /// <summary>
    /// Whether the cached copy verifies with the trusted keys, and the <c>updated</c> time it was signed with (null when there
    /// is no verified copy).
    /// </summary>
    private bool CachedVerified(out DateTime? updated)
    {
        updated = null;
        if (!Cache.TryRead(CatalogFetcher.IndexMaxBytes, out string body, out _)) return false;
        if (!SelfFeed.TrySplit(body, out string sig, out string feed)) return false;
        if (!SelfFeed.Verify(Encoding.UTF8.GetBytes(feed), sig, _keys(), out _)) return false;
        updated = SelfFeed.UpdatedOf(feed);
        return true;
    }

    private async Task<CatalogTextFetchResult> FetchBoth(IndexValidators validators, CancellationToken ct)
    {
        bool cachedOk = CachedVerified(out DateTime? cachedUpdated);
        // A cached copy that no longer verifies (the keys were rolled, or it was changed) is never revalidated with a 304: the
        // list and its signature are fetched again, so a list re-signed with the new key is picked up.
        if (!cachedOk && !validators.IsEmpty) validators = IndexValidators.None;
        var first = Fresh(await ReadSigned(_primary, validators, ct).ConfigureAwait(false), cachedUpdated);
        if (first.Ok || ct.IsCancellationRequested || _fallback.Length == 0 || string.Equals(_fallback, _primary, StringComparison.Ordinal)) return first;
        var second = Fresh(await ReadSigned(_fallback, IndexValidators.None, ct).ConfigureAwait(false), cachedUpdated);
        if (second.Ok)
        {
            InstallLog.Info("Release list: " + _primary + " failed (" + first.Error + "); read " + _fallback + " instead.");
            // Another server's validators are never sent to the primary: the fallback's copy is cached without them.
            return new CatalogTextFetchResult(true, null, second.Text, second.ByteCount, second.StatusCode)
            {
                WireBytes = second.WireBytes,
                ContentEncoding = second.ContentEncoding,
            };
        }

        return new CatalogTextFetchResult(false, first.Error + "; fallback: " + second.Error, null, 0, first.StatusCode) { FailureCause = first.FailureCause };
    }

    /// <summary>
    /// Replay protection: a verified list signed earlier than the verified copy already cached (its <c>updated</c> time) is
    /// refused, so an old signed list served again cannot hide newer releases. It counts as a failed check.
    /// </summary>
    private static CatalogTextFetchResult Fresh(CatalogTextFetchResult r, DateTime? cachedUpdated)
    {
        if (!r.Ok || r.NotModified || r.Text == null || cachedUpdated is not { } cached) return r;
        if (!SelfFeed.TrySplit(r.Text, out _, out string feed)) return r;
        if (SelfFeed.UpdatedOf(feed) is { } got && got >= cached) return r;
        return new CatalogTextFetchResult(false, "the release list is older than the one already cached (updated "
            + (SelfFeed.UpdatedOf(feed)?.ToString("o", CultureInfo.InvariantCulture) ?? "missing") + ", cached "
            + cached.ToString("o", CultureInfo.InvariantCulture) + "); not used", null, 0, r.StatusCode);
    }

    /// <summary>The list and its signature from one place; a 304 passes through; a 200 comes back as the cached form.</summary>
    private async Task<CatalogTextFetchResult> ReadSigned(string url, IndexValidators validators, CancellationToken ct)
    {
        NoteAsked(url);
        CatalogTextFetchResult feed;
        try { feed = await _fetch(url, validators, SelfFeed.MaxFeedBytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new CatalogTextFetchResult(false, InstallReasons.FetchTransport + ": " + e.Message, null, 0, 0); }
        if (feed == null) return new CatalogTextFetchResult(false, InstallReasons.FetchTransport, null, 0, 0);
        if (!feed.Ok || feed.NotModified || feed.Text == null) return feed;
        CatalogTextFetchResult sig;
        NoteAsked(url + SelfFeed.SignatureSuffix);
        try { sig = await _fetch(url + SelfFeed.SignatureSuffix, IndexValidators.None, SelfFeed.MaxSignatureBytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new CatalogTextFetchResult(false, "the signature could not be read: " + e.Message, null, 0, 0); }
        if (sig == null || !sig.Ok || sig.Text == null) return new CatalogTextFetchResult(false, "the signature could not be read: " + (sig?.Error ?? "no answer"), null, 0, sig?.StatusCode ?? 0);
        if (!SelfFeed.Verify(Encoding.UTF8.GetBytes(feed.Text), sig.Text, _keys(), out string why))
            return new CatalogTextFetchResult(false, why, null, 0, feed.StatusCode);
        SelfFeed.Parse(feed.Text, out string parseError);
        if (parseError.Length > 0) return new CatalogTextFetchResult(false, parseError, null, 0, feed.StatusCode);
        return new CatalogTextFetchResult(true, null, SelfFeed.Envelope(sig.Text, feed.Text), feed.ByteCount, feed.StatusCode)
        {
            LastModified = feed.LastModified,
            ETag = feed.ETag,
            WireBytes = feed.WireBytes,
            ContentEncoding = feed.ContentEncoding,
        };
    }
}
