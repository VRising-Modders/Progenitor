using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// INDEX1 settings, cfg only (no player UI). <c>[Install] UseProgenitorIndex</c> (default on) lists and installs mods from
/// the Progenitor Index. Developer testing: <c>[Install] IndexDevUrl</c> (an <c>index.json</c> URL) and
/// <c>[Install] IndexDevKey</c> (an SPKI base64 public key) serve a local test Index signed with a throwaway key. They take
/// effect only in a developer tree (<c>[Reload] WatchCore = true</c>) or with <c>[Install] AllowDevThunderstore = true</c>,
/// the dev Thunderstore stand-in's own rule; plain http is accepted only on a loopback address.
/// </summary>
internal static class ProgenitorIndexConfig
{
    public const string Section = "Install";
    public const string UseKey = "UseProgenitorIndex";
    public const string DevUrlKey = "IndexDevUrl";
    public const string DevKeyKey = "IndexDevKey";

    public const string UseDescription =
        "List and install mods from the Progenitor Index (the signed, reviewed list of Progenitor mods). Default on. "
        + "Off: the Install mods tab and the join screens use Thunderstore only.";

    public const string DevUrlDescription =
        "Developer testing only. The index.json URL of a local test Progenitor Index (its signature at the same URL + .sig), "
        + "used in a developer tree ([Reload] WatchCore = true) or with AllowDevThunderstore = true. An http or https URL on a "
        + "loopback address. Empty (the default) reads the published Index.";

    public const string DevKeyDescription =
        "Developer testing only. A public key (SPKI DER, base64) trusted beside the built-in Index keys while IndexDevUrl is in effect. "
        + "Empty: only the built-in keys.";

    /// <summary>Tests: the values for this test flow only (null: the live config).</summary>
    internal static (bool Use, string? DevUrl, string? DevKey, bool Dev)? TestOverride
    {
        get => TestOverrideLocal.Value;
        set => TestOverrideLocal.Value = value;
    }

    private static readonly AsyncLocal<(bool Use, string? DevUrl, string? DevKey, bool Dev)?> TestOverrideLocal = new();

    /// <summary><c>[Install] UseProgenitorIndex</c>.</summary>
    public static bool Enabled
    {
        get
        {
            if (TestOverride is { } t) return t.Use;
#if !PROGENITOR_TESTS
            BindOnce();
            lock (Gate) return _use?.Value ?? true;
#else
            return false;
#endif
        }
    }

    /// <summary>The dev Index URL in effect, or null (not set, not a developer tree, or not an allowed URL).</summary>
    public static Uri? DevUrl
    {
        get
        {
            string? text;
            bool dev;
            if (TestOverride is { } t) { text = t.DevUrl; dev = t.Dev; }
            else
            {
#if !PROGENITOR_TESTS
                BindOnce();
                lock (Gate) text = _devUrl?.Value;
                dev = DevSignal();
#else
                text = null;
                dev = false;
#endif
            }

            if (!dev || string.IsNullOrWhiteSpace(text)) return null;
            if (!Uri.TryCreate(text!.Trim(), UriKind.Absolute, out var u)) return null;
            // L1: a loopback address only, like the dev Thunderstore stand-in (http or https).
            return (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.IsLoopback ? u : null;
        }
    }

    /// <summary>The dev Index's origin (scheme, host, port) while it is in effect, or null.</summary>
    public static string? DevOrigin => DevUrl?.GetLeftPart(UriPartial.Authority);

    /// <summary>The fetcher's https rule lets this http URL through: it is on the dev Index's loopback origin.</summary>
    public static bool AllowsHttp(Uri? u)
    {
        if (u == null || u.Scheme != Uri.UriSchemeHttp || !u.IsLoopback) return false;
        string? origin;
        try { origin = DevOrigin; }
        catch { return false; }
        return origin != null && ProgenitorIndex.SameOrigin(u.AbsoluteUri, origin);
    }

    /// <summary>The Index URL read now: the dev URL in effect, else <see cref="ProgenitorIndex.PrimaryUrl"/>.</summary>
    public static string IndexUrl => DevUrl?.AbsoluteUri ?? ProgenitorIndex.PrimaryUrl;

    /// <summary>The folder an icon's relative URL is resolved against (the dev Index's own folder while it is in effect).</summary>
    public static string PagesRoot
    {
        get
        {
            var dev = DevUrl;
            if (dev == null) return ProgenitorIndex.PagesRoot;
            string s = dev.AbsoluteUri;
            int slash = s.LastIndexOf('/');
            return slash > 0 ? s.Substring(0, slash + 1) : s + "/";
        }
    }

    /// <summary>
    /// L2: the keys a list from <paramref name="url"/> is verified with. The published URL: the built-in keys only, never the
    /// dev key. The dev URL: the built-in keys plus the dev key, and only while that URL is the one in effect.
    /// </summary>
    public static Func<IReadOnlyList<string>> KeysFor(string url) =>
        string.Equals(url, ProgenitorIndex.PrimaryUrl, StringComparison.Ordinal)
            ? () => ProgenitorIndexKeys.Trusted()
            : () => string.Equals(DevUrl?.AbsoluteUri, url, StringComparison.Ordinal) ? Keys() : ProgenitorIndexKeys.Trusted();

    /// <summary>The keys a list is verified with: the built-in ones, plus the dev key while the dev URL is in effect.</summary>
    public static IReadOnlyList<string> Keys()
    {
        var keys = new List<string>(ProgenitorIndexKeys.Trusted());
        string? devKey;
        if (TestOverride is { } t) devKey = t.DevKey;
        else
        {
#if !PROGENITOR_TESTS
            BindOnce();
            lock (Gate) devKey = _devKey?.Value;
#else
            devKey = null;
#endif
        }

        if (DevUrl != null && !string.IsNullOrWhiteSpace(devKey)) keys.Add(devKey!.Trim());
        return keys;
    }

#if !PROGENITOR_TESTS
    private static readonly object Gate = new();
    private static BepInEx.Configuration.ConfigEntry<bool>? _use;
    private static BepInEx.Configuration.ConfigEntry<string>? _devUrl;
    private static BepInEx.Configuration.ConfigEntry<string>? _devKey;
    private static bool _bindTried;

    private static void BindOnce()
    {
        lock (Gate)
        {
            if (_bindTried) return;
            _bindTried = true;
        }

        try
        {
            var file = Progenitor.Config.SharedConfig.TryGetOrLog("[" + Section + "] " + UseKey);
            if (file == null)
            {
                lock (Gate) _bindTried = false; // the shared file is not up yet: try again on the next read
                return;
            }

            var use = Progenitor.Config.ConfigHelper.Bind(file, Section, UseKey, true, UseDescription);
            var url = Progenitor.Config.ConfigHelper.Bind(file, Section, DevUrlKey, "", DevUrlDescription);
            var key = Progenitor.Config.ConfigHelper.Bind(file, Section, DevKeyKey, "", DevKeyDescription);
            lock (Gate)
            {
                _use = use;
                _devUrl = url;
                _devKey = key;
            }
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning("Install: binding [" + Section + "] " + UseKey + " threw (" + e.Message + "); the Progenitor Index stays on."); }
            catch { /* */ }
        }
    }

    /// <summary>The dev Thunderstore stand-in's dev signal: a developer tree, or AllowDevThunderstore.</summary>
    private static bool DevSignal()
    {
        try { return ThunderstoreMirror.DevProbe?.Invoke() ?? false; }
        catch { return false; }
    }
#endif
}

/// <summary>
/// INDEX1: one process's conditional read of the Progenitor Index (owner <c>progenitor index</c>):
/// <see cref="ConditionalIndexFetch"/> over the Index's own cache, so it is conditional (<c>If-None-Match</c> /
/// <c>If-Modified-Since</c>), gzip-compressed and backs off after failures like every index read. A 200 is verified
/// against the trusted keys before it is parsed, and cached only when it verifies, parses and is not older than the cached
/// copy (replay protection on the signed <c>generated</c> time). No fallback URL: GitHub Pages is the only place it is read.
/// </summary>
internal sealed class ProgenitorIndexFetch
{
    public const string Owner = "progenitor index";

    /// <summary>A text request: (url, validators, max bytes, token) → result.</summary>
    internal delegate Task<CatalogTextFetchResult> TextFetch(string url, IndexValidators validators, long maxBytes, CancellationToken ct);

    private readonly TextFetch _fetch;
    private readonly Func<IReadOnlyList<string>> _keys;
    private readonly string _url;
    private readonly List<string> _asked = new();
    private readonly ICatalogClock _clock;

    public ProgenitorIndexFetch(TextFetch fetch, IndexDiskCache cache, Func<IReadOnlyList<string>>? keys = null, ICatalogClock? clock = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _keys = keys ?? (() => ProgenitorIndexKeys.Trusted());
        _url = cache.Url;
        _clock = clock ?? new SystemCatalogClock();
        Conditional = new ConditionalIndexFetch(ReadSigned, cache, Owner, clock, accept: body => SelfFeed.TrySplit(body, out _, out _), label: "Progenitor Index check");
    }

    public ConditionalIndexFetch Conditional { get; }
    public IndexDiskCache Cache => Conditional.Cache;
    public string Url => _url;
    public IReadOnlyList<string> TrustedKeys() => _keys();

    /// <summary>The URLs asked so far (tests), oldest first, at most 32.</summary>
    public List<string> Asked
    {
        get { lock (_asked) return new List<string>(_asked); }
    }

    private void NoteAsked(string url)
    {
        lock (_asked)
        {
            _asked.Add(url);
            if (_asked.Count > 32) _asked.RemoveAt(0);
        }
    }

    /// <summary>One check; the result's <c>Text</c> is the cached form (signature line, then the list) of a verified list.</summary>
    public Task<CatalogTextFetchResult> FetchAsync(CancellationToken ct, bool force = false) => Conditional.FetchAsync(ct, force);

    /// <summary>The verified cached copy, or <see cref="IndexDocument.Empty"/> with <paramref name="error"/> set.</summary>
    public IndexDocument ReadCache(out string error)
    {
        if (!Cache.TryRead(ProgenitorIndex.MaxIndexBytes + ProgenitorIndex.MaxSignatureBytes, out string body, out _))
        {
            error = "no Index is cached";
            return IndexDocument.Empty;
        }

        return ProgenitorIndex.ReadVerified(body, _keys(), out error);
    }

    private async Task<CatalogTextFetchResult> ReadSigned(IndexValidators validators, CancellationToken ct)
    {
        var cached = ReadCache(out string cacheError);
        bool cachedOk = cacheError.Length == 0;
        // A cached copy that no longer verifies (the keys were rolled, or it was changed) is never revalidated with a 304: the
        // list and its signature are fetched again, so a list signed with the new key is picked up.
        if (!cachedOk && !validators.IsEmpty) validators = IndexValidators.None;

        NoteAsked(_url);
        CatalogTextFetchResult list;
        try { list = await _fetch(_url, validators, ProgenitorIndex.MaxIndexBytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new CatalogTextFetchResult(false, InstallReasons.FetchTransport + ": " + e.Message, null, 0, 0); }
        if (list == null) return new CatalogTextFetchResult(false, InstallReasons.FetchTransport, null, 0, 0);
        if (!list.Ok || list.NotModified || list.Text == null) return list;

        string sigUrl = _url + ProgenitorIndex.SignatureSuffix;
        NoteAsked(sigUrl);
        CatalogTextFetchResult sig;
        try { sig = await _fetch(sigUrl, IndexValidators.None, ProgenitorIndex.MaxSignatureBytes, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new CatalogTextFetchResult(false, "the Index signature could not be read: " + e.Message, null, 0, 0); }
        if (sig == null || !sig.Ok || sig.Text == null)
            return new CatalogTextFetchResult(false, "the Index signature could not be read: " + (sig?.Error ?? "no answer"), null, 0, sig?.StatusCode ?? 0);

        // Verified before anything else reads the list; a list that does not verify is never parsed or cached.
        if (!ProgenitorIndex.Verify(Encoding.UTF8.GetBytes(list.Text), sig.Text, _keys(), out string why))
            return new CatalogTextFetchResult(false, why, null, 0, list.StatusCode);
        var doc = ProgenitorIndex.Parse(list.Text, out string parseError);
        if (parseError.Length > 0) return new CatalogTextFetchResult(false, parseError, null, 0, list.StatusCode);

        // Replay protection: a list signed before the verified copy already cached is refused (a failed check), so an old
        // signed list served again cannot bring back a version that was taken down.
        // M2: a list dated more than a day ahead is never cached or adopted, and a future-dated cached copy is no replay floor.
        if (ProgenitorIndex.IsFutureDated(doc, _clock.UtcNow))
            return new CatalogTextFetchResult(false, ProgenitorIndex.FutureDatedError(doc), null, 0, list.StatusCode);
        if (cachedOk && !ProgenitorIndex.IsFutureDated(cached, _clock.UtcNow) && cached.Generated is { } have && (doc.Generated is not { } got || got < have))
            return new CatalogTextFetchResult(false, "the Index is older than the one already cached (generated "
                + (doc.Generated?.ToString("o", CultureInfo.InvariantCulture) ?? "missing") + ", cached "
                + have.ToString("o", CultureInfo.InvariantCulture) + "); not used", null, 0, list.StatusCode);

        return new CatalogTextFetchResult(true, null, SelfFeed.Envelope(sig.Text.Trim(), list.Text), list.ByteCount, list.StatusCode)
        {
            LastModified = list.LastModified,
            ETag = list.ETag,
            WireBytes = list.WireBytes,
            ContentEncoding = list.ContentEncoding,
        };
    }
}

/// <summary>
/// INDEX1: the Progenitor Index as this process knows it: the last good verified list (from the cache at first use, then
/// from each check that verifies), refreshed on the caller's cadence (the Install tab about every 30 minutes, the join
/// screens about every 10). A failed check keeps the last good list. One per BepInEx root (<see cref="ForRoot"/>), shared
/// by the Install tab, the join screens and the server's catalog.
/// </summary>
internal sealed class ProgenitorIndexSource
{
    public static readonly TimeSpan InstallTabAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan JoinAge = TimeSpan.FromMinutes(10);

    private readonly ProgenitorIndexFetch _fetch;
    private readonly Func<bool> _enabled;
    private readonly ICatalogClock _clock;
    private readonly string _pagesRoot;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private IndexDocument _current = IndexDocument.Empty;
    private bool _cacheRead;
    private DateTime? _lastCheckUtc;
    private int _version;
    private string _stamp = "";
    private string _lastError = "";
    private bool _fixed;

    public ProgenitorIndexSource(ProgenitorIndexFetch fetch, Func<bool>? enabled = null, ICatalogClock? clock = null, string? pagesRoot = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _enabled = enabled ?? (() => true);
        _clock = clock ?? new SystemCatalogClock();
        _pagesRoot = string.IsNullOrWhiteSpace(pagesRoot) ? ProgenitorIndex.PagesRoot : pagesRoot!;
    }

    public ProgenitorIndexFetch Fetch => _fetch;

    /// <summary>
    /// A source that holds <paramref name="doc"/> and never checks (tests, and a caller that already verified a list): its
    /// fetch has no cache and answers nothing.
    /// </summary>
    public static ProgenitorIndexSource Fixed(IndexDocument doc, string? pagesRoot = null)
    {
        var none = new ProgenitorIndexFetch((_, _, _, _) => Task.FromResult(new CatalogTextFetchResult(false, "a fixed list is never checked", null, 0, 0)),
            new IndexDiskCache(null, ProgenitorIndex.PrimaryUrl));
        var source = new ProgenitorIndexSource(none, pagesRoot: pagesRoot);
        lock (source._gate)
        {
            source._cacheRead = true;
            source._fixed = true;
        }

        source.Adopt(doc ?? IndexDocument.Empty, "fixed");
        return source;
    }

    public bool Enabled
    {
        get
        {
            try { return _enabled(); }
            catch { return false; }
        }
    }

    /// <summary>The last good verified list (empty while the Index is off or nothing verified yet).</summary>
    public IndexDocument Current
    {
        get
        {
            if (!Enabled) return IndexDocument.Empty;
            EnsureCache();
            lock (_gate) return _current;
        }
    }

    /// <summary>Moves each time a different list is adopted, so a catalog re-merges only when it changed.</summary>
    public int Version { get { lock (_gate) return _version; } }

    public string LastError { get { lock (_gate) return _lastError; } }

    public string PagesRoot => _pagesRoot;

    /// <summary>The newest-version rows of the current list (<see cref="IndexCatalog.ToEntries"/>).</summary>
    public IReadOnlyList<CatalogEntry> Entries(bool allVersions = false)
    {
        var doc = Current;
        string stamp;
        lock (_gate) stamp = _stamp;
        return IndexCatalog.ToEntries(doc, stamp, allVersions, _pagesRoot);
    }

    /// <summary>Read the verified cached copy once, when nothing is held yet. Never throws.</summary>
    public void EnsureCache()
    {
        lock (_gate)
        {
            if (_cacheRead) return;
            _cacheRead = true;
        }

        IndexDocument doc;
        string error;
        try { doc = _fetch.ReadCache(out error); }
        catch (Exception e) { doc = IndexDocument.Empty; error = e.Message; }
        if (error.Length > 0 || ProgenitorIndex.IsFutureDated(doc, _clock.UtcNow)) return;
        Adopt(doc, _fetch.Cache.TryReadStamp(out var stamp) ? CatalogFetcher.FormatStamp(stamp) : "cache");
    }

    /// <summary>
    /// Check the Index when the last check is older than <paramref name="maxAge"/> (or none was made this session, or
    /// <paramref name="force"/>), and adopt a list that verifies. Returns the list held afterwards: the last good one when the
    /// check failed. One check at a time; a caller arriving during one waits for it.
    /// </summary>
    public async Task<IndexDocument> RefreshAsync(TimeSpan maxAge, CancellationToken ct, bool force = false)
    {
        if (!Enabled) return IndexDocument.Empty;
        EnsureCache();
        lock (_gate) if (_fixed) return _current;
        await _refresh.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTime now = _clock.UtcNow;
            bool due;
            lock (_gate) due = force || _lastCheckUtc is not { } last || now - last >= maxAge || now < last;
            if (!due) return Current;
            CatalogTextFetchResult r;
            try { r = await _fetch.FetchAsync(ct, force).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { r = new CatalogTextFetchResult(false, e.Message, null, 0, 0); }

            lock (_gate)
            {
                // A paused check (backoff) does not count as made: the next caller past the pause asks again.
                if (!r.Paused) _lastCheckUtc = _clock.UtcNow;
                _lastError = r.Ok ? "" : r.Error ?? InstallReasons.FetchTransport;
            }

            if (r.Ok && r.Text != null)
            {
                var doc = ProgenitorIndex.ReadVerified(r.Text, _fetch.TrustedKeys(), out string error);
                if (error.Length == 0 && ProgenitorIndex.IsFutureDated(doc, _clock.UtcNow)) error = ProgenitorIndex.FutureDatedError(doc);
                if (error.Length == 0) Adopt(doc, CatalogFetcher.FormatStamp(_clock.UtcNow));
                else lock (_gate) _lastError = error;
            }

            return Current;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private void Adopt(IndexDocument doc, string stamp)
    {
        lock (_gate)
        {
            // L5: a different list is a different text hash.
            bool same = ReferenceEquals(doc, _current) || (doc.Sha256.Length > 0 && string.Equals(doc.Sha256, _current.Sha256, StringComparison.Ordinal));
            _current = doc;
            _stamp = stamp;
            if (same) return;
            _version++;
        }

        // The live test's evidence: what an adopted list contributes, and what it leaves out and why.
        string summary;
        try { summary = doc.Summary(); }
        catch (Exception e) { summary = "Progenitor Index: adopted a list (" + e.Message + ")."; }
        InstallLog.Info(summary + " (" + _fetch.Url + ", " + stamp + ")");
    }

    // ---- the process's own -----------------------------------------------------------------------------------

    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, ProgenitorIndexSource> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<IDisposable> Leases = new();

    /// <summary>L6: on Core unload, release the HTTP leases <see cref="ForRoot"/> took and forget the shared sources.</summary>
    public static void ReleaseShared()
    {
        List<IDisposable> leases;
        lock (SharedGate)
        {
            leases = new List<IDisposable>(Leases);
            Leases.Clear();
            Shared.Clear();
        }

        foreach (var l in leases)
        {
            try { l.Dispose(); }
            catch { /* tearing down */ }
        }
    }

    /// <summary>Tests: the source <see cref="ForRoot"/> answers in place of the live one (null: none in tests).</summary>
    internal static Func<string, ProgenitorIndexSource?>? TestFactory { get; set; }

    /// <summary>
    /// The shared source for one BepInEx root (its cache is that root's <c>progenitor-cache</c>), built on first use over the
    /// shared HTTPS client; rebuilt when the Index URL changes (the dev URL was set or cleared). Null when none can be built
    /// (in tests unless <see cref="TestFactory"/> gives one).
    /// </summary>
    public static ProgenitorIndexSource? ForRoot(string? bepInExRoot)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) return null;
        if (TestFactory is { } factory) return factory(bepInExRoot!);
#if PROGENITOR_TESTS
        return null;
#else
        string url;
        try { url = ProgenitorIndexConfig.IndexUrl; }
        catch { url = ProgenitorIndex.PrimaryUrl; }
        string key = System.IO.Path.GetFullPath(bepInExRoot!) + "|" + url;
        lock (SharedGate)
        {
            if (Shared.TryGetValue(key, out var have)) return have;
            var http = SharedCatalogHttp.Acquire();
            var fetcher = new CatalogFetcher(http, CatalogFetchOptions.Default);
            var fetch = new ProgenitorIndexFetch((u, v, max, ct) => fetcher.FetchIndexTextAsync(u, max, v, ct),
                new IndexDiskCache(bepInExRoot, url), ProgenitorIndexConfig.KeysFor(url));
            var source = new ProgenitorIndexSource(fetch, () => ProgenitorIndexConfig.Enabled, pagesRoot: ProgenitorIndexConfig.PagesRoot);
            Shared[key] = source;
            Leases.Add(http);
            return source;
        }
#endif
    }
}
