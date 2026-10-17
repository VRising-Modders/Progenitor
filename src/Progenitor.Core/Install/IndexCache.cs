using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// TSTRAFFIC1: the validators of one cached index body: the <c>Last-Modified</c> and <c>ETag</c>
/// the server sent with it, echoed back as <c>If-Modified-Since</c> / <c>If-None-Match</c>.
/// </summary>
internal readonly struct IndexValidators
{
    public IndexValidators(string? lastModified, string? etag)
    {
        LastModified = string.IsNullOrWhiteSpace(lastModified) ? null : lastModified!.Trim();
        ETag = string.IsNullOrWhiteSpace(etag) ? null : etag!.Trim();
    }

    public string? LastModified { get; }
    public string? ETag { get; }
    public bool IsEmpty => LastModified == null && ETag == null;

    public static IndexValidators None => default;
}

/// <summary>
/// TSTRAFFIC1: the on-disk copy of one package index under <c>BepInEx/progenitor-cache/</c>:
/// <c>index.json</c> (the body), <c>index.stamp</c> (when it was last fetched or confirmed current,
/// ISO-8601 UTC; the Install tab's 30-minute rule reads it) and <c>index.validators</c>
/// (<c>url:</c> it was fetched from, <c>last-modified:</c>, <c>etag:</c> and <c>sha256:</c> of the body bytes).
///
/// <para><b>One copy per URL.</b> The default index (<see cref="CatalogFetcher.DefaultIndexUrl"/>) keeps the
/// plain names above, which the join and server fallbacks read. Any other URL (a server's
/// <c>[RemoteInstall] ThunderstoreBaseUrl</c> mirror) gets its own files,
/// <c>index-&lt;12 hex of the URL's SHA-256&gt;.json</c> / <c>.stamp</c> / <c>.validators</c>, so two owners on
/// different URLs never overwrite each other. The validators are also used only while their <c>url:</c>
/// equals the request URL and their <c>sha256:</c> equals the body bytes on disk: a body replaced by
/// another writer (or by hand, or with a BOM) is never revalidated with someone else's
/// <c>Last-Modified</c>, and the request then goes out unconditional.</para>
///
/// <para>The validators sit in their own file rather than in <c>index.stamp</c> so a Core
/// generation that predates them still parses the stamp. Every process writes its own root (the
/// client its BepInEx folder, a server its own); writers in one process share a lock per folder.
/// Every failure degrades to "no cache".</para>
/// </summary>
internal sealed class IndexDiskCache
{
    public const string CacheFolderName = "progenitor-cache";
    public const string IndexFileName = "index.json";
    public const string StampFileName = "index.stamp";
    public const string ValidatorsFileName = "index.validators";

    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _baseName;

    /// <param name="bepInExRoot">The BepInEx folder; null keeps nothing on disk.</param>
    /// <param name="url">The index URL this copy is for; null is the default Thunderstore index.</param>
    public IndexDiskCache(string? bepInExRoot, string? url = null)
    {
        Dir = string.IsNullOrWhiteSpace(bepInExRoot) ? null : Path.Combine(Path.GetFullPath(bepInExRoot), CacheFolderName);
        Url = string.IsNullOrWhiteSpace(url) ? CatalogFetcher.DefaultIndexUrl : url!.Trim();
        _baseName = string.Equals(Url, CatalogFetcher.DefaultIndexUrl, StringComparison.Ordinal)
            ? "index"
            : "index-" + Sha256(Encoding.UTF8.GetBytes(Url)).Substring(0, 12);
    }

    public string? Dir { get; }

    /// <summary>The index URL this copy belongs to.</summary>
    public string Url { get; }

    public string? IndexPath => Dir == null ? null : Path.Combine(Dir, _baseName + ".json");
    public string? StampPath => Dir == null ? null : Path.Combine(Dir, _baseName + ".stamp");
    public string? ValidatorsPath => Dir == null ? null : Path.Combine(Dir, _baseName + ".validators");

    private object Gate => Locks.GetOrAdd(Dir ?? "", _ => new object());

    /// <summary>The cached body and its validators (empty when they do not belong to this body and URL), or false.</summary>
    public bool TryRead(long maxBytes, out string body, out IndexValidators validators)
    {
        body = "";
        validators = IndexValidators.None;
        if (Dir == null) return false;
        lock (Gate)
        {
            try
            {
                if (!File.Exists(IndexPath!)) return false;
                if (maxBytes > 0 && new FileInfo(IndexPath!).Length > maxBytes) return false;
                byte[] bytes = File.ReadAllBytes(IndexPath!);
                int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                body = Utf8NoBom.GetString(bytes, skip, bytes.Length - skip);
                validators = ReadValidatorsFor(Sha256(bytes));
                return true;
            }
            catch
            {
                body = "";
                validators = IndexValidators.None;
                return false;
            }
        }
    }

    /// <summary>When the cached body was last fetched or confirmed current; false without one.</summary>
    public bool TryReadStamp(out DateTime stampUtc)
    {
        stampUtc = default;
        if (Dir == null) return false;
        try
        {
            if (!File.Exists(StampPath!) || !File.Exists(IndexPath!)) return false;
            string text = File.ReadAllText(StampPath!).Trim();
            if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
            stampUtc = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Replace the body (UTF-8, no BOM), its validators and the stamp, each through a temp file.</summary>
    public bool Write(string body, IndexValidators validators, DateTime checkedUtc)
    {
        if (Dir == null || body == null) return false;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                byte[] bytes = Utf8NoBom.GetBytes(body);
                WriteAtomically(IndexPath!, bytes);
                // The hash is of the bytes just written, so the read-back check compares like with like.
                WriteAtomically(ValidatorsPath!, Utf8NoBom.GetBytes(FormatValidators(Url, validators, Sha256(bytes))));
                WriteAtomically(StampPath!, Utf8NoBom.GetBytes(CatalogFetcher.FormatStamp(checkedUtc)));
                return true;
            }
            catch (Exception e)
            {
                InstallLog.Warn("Thunderstore: index cache write failed: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>A 304: the body on disk is current as of <paramref name="checkedUtc"/>.</summary>
    public void Touch(DateTime checkedUtc)
    {
        if (Dir == null) return;
        lock (Gate)
        {
            try
            {
                if (!File.Exists(IndexPath!)) return;
                WriteAtomically(StampPath!, Utf8NoBom.GetBytes(CatalogFetcher.FormatStamp(checkedUtc)));
            }
            catch { /* the next open merely checks again sooner; nothing else depends on it */ }
        }
    }

    internal static string FormatValidators(string url, IndexValidators validators, string sha256)
    {
        var sb = new StringBuilder();
        sb.Append("url: ").Append(url).Append('\n');
        if (validators.LastModified != null) sb.Append("last-modified: ").Append(validators.LastModified).Append('\n');
        if (validators.ETag != null) sb.Append("etag: ").Append(validators.ETag).Append('\n');
        sb.Append("sha256: ").Append(sha256).Append('\n');
        return sb.ToString();
    }

    private IndexValidators ReadValidatorsFor(string bodySha256)
    {
        try
        {
            if (!File.Exists(ValidatorsPath!)) return IndexValidators.None;
            string? url = null, lastModified = null, etag = null, sha = null;
            foreach (string raw in File.ReadAllLines(ValidatorsPath!))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string key = raw.Substring(0, colon).Trim().ToLowerInvariant();
                string value = raw.Substring(colon + 1).Trim();
                if (key == "url") url = value;
                else if (key == "last-modified") lastModified = value;
                else if (key == "etag") etag = value;
                else if (key == "sha256") sha = value;
            }

            if (!string.Equals(url, Url, StringComparison.Ordinal)) return IndexValidators.None;
            if (sha == null || !string.Equals(sha, bodySha256, StringComparison.OrdinalIgnoreCase)) return IndexValidators.None;
            return new IndexValidators(lastModified, etag);
        }
        catch { return IndexValidators.None; }
    }

    internal static string Sha256(byte[] bytes) => CatalogFetcher.ToHex(SHA256.HashData(bytes));

    private static void WriteAtomically(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// TSTRAFFIC1: one owner's conditional read of the package index (the Install tab's refresher, the
/// join screens, the server's install-window availability, the server's remote installs). Each
/// check sends the cached body's validators (<see cref="CatalogFetcher.FetchIndexTextAsync"/>):
/// a 304 costs no body and answers the cached copy with its stamp refreshed; a 200 replaces the
/// cache. One log line per network check names the owner, the status and the bytes.
///
/// <para><b>Backoff.</b> After <see cref="FailuresBeforeBackoff"/> failed checks in a row this
/// owner stops asking for <see cref="FirstBackoff"/>, doubling per further failure up to
/// <see cref="MaxBackoff"/>; a success clears it. A <c>force</c> check (the player's Retry, an
/// admin's install) still goes out. A cancellation is not a failure.</para>
/// </summary>
internal sealed class ConditionalIndexFetch
{
    public const int FailuresBeforeBackoff = 3;
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(60);

    private readonly Func<IndexValidators, CancellationToken, Task<CatalogTextFetchResult>> _fetch;
    private readonly ICatalogClock _clock;
    private readonly Func<string, bool> _accept;
    private readonly object _gate = new();
    private int _failures;
    private DateTime _pausedUntil = DateTime.MinValue;
    private int _requests;

    /// <param name="fetch">One conditional request (<see cref="CatalogFetcher.FetchIndexTextAsync"/>).</param>
    /// <param name="cache">The owner's disk copy.</param>
    /// <param name="owner">Who checks, for the log line (<c>install tab</c>, <c>join</c>, ...).</param>
    /// <param name="accept">Whether a 200 body may be cached; default: a JSON array.</param>
    public ConditionalIndexFetch(
        Func<IndexValidators, CancellationToken, Task<CatalogTextFetchResult>> fetch,
        IndexDiskCache cache,
        string owner,
        ICatalogClock? clock = null,
        Func<string, bool>? accept = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        Cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Owner = string.IsNullOrWhiteSpace(owner) ? "index" : owner.Trim();
        _clock = clock ?? new SystemCatalogClock();
        _accept = accept ?? LooksLikeIndex;
    }

    /// <summary>The usual wiring: <paramref name="fetcher"/>'s conditional index request on <paramref name="url"/>.</summary>
    public static ConditionalIndexFetch Over(CatalogFetcher fetcher, string url, IndexDiskCache cache, string owner, ICatalogClock? clock = null)
    {
        if (cache == null) throw new ArgumentNullException(nameof(cache));
        if (!string.Equals((url ?? "").Trim(), cache.Url, StringComparison.Ordinal))
            throw new ArgumentException("the index cache is for " + cache.Url + ", not " + url, nameof(cache));
        return new((validators, ct) => fetcher.FetchIndexTextAsync(cache.Url, CatalogFetcher.IndexMaxBytes, validators, ct), cache, owner, clock);
    }

    public IndexDiskCache Cache { get; }
    public string Owner { get; }

    /// <summary>Network checks this owner made (tests and the log).</summary>
    public int Requests { get { lock (_gate) return _requests; } }

    public int ConsecutiveFailures { get { lock (_gate) return _failures; } }

    /// <summary>Until when automatic checks are skipped, or null.</summary>
    public DateTime? PausedUntilUtc
    {
        get
        {
            lock (_gate) return _pausedUntil > _clock.UtcNow ? _pausedUntil : null;
        }
    }

    public static bool LooksLikeIndex(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (char.IsWhiteSpace(c) || c == '﻿') continue;
            return c == '[';
        }
        return false;
    }

    /// <summary>
    /// One check. The result's <c>Text</c> is the current index body (fetched, or the cached copy on a
    /// 304); a failure leaves the cache untouched. <see cref="OperationCanceledException"/> propagates.
    /// </summary>
    public Task<CatalogTextFetchResult> FetchAsync(CancellationToken cancellationToken, bool force = false) =>
        // Off the caller's thread: the cache read (about 1 MB, hashed) must not run on the game's main thread.
        Task.Run(() => FetchCoreAsync(cancellationToken, force), cancellationToken);

    private async Task<CatalogTextFetchResult> FetchCoreAsync(CancellationToken cancellationToken, bool force)
    {
        DateTime now = _clock.UtcNow;
        if (!force)
        {
            DateTime until;
            int failures;
            lock (_gate)
            {
                until = _pausedUntil;
                failures = _failures;
            }

            if (until > now)
            {
                string why = "Thunderstore index checks paused after " + failures + " failures in a row; the next automatic check is after "
                    + until.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";
                InstallLog.Info("Thunderstore: index check (" + Owner + ") skipped: " + why + ".");
                return new CatalogTextFetchResult(false, why, null, 0, 0) { Paused = true };
            }
        }

        bool cached = Cache.TryRead(CatalogFetcher.IndexMaxBytes, out string body, out var validators);
        if (!cached) validators = IndexValidators.None;

        CatalogTextFetchResult fetched;
        lock (_gate) _requests++;
        try
        {
            fetched = await _fetch(validators, cancellationToken).ConfigureAwait(false)
                ?? new CatalogTextFetchResult(false, InstallReasons.FetchTransport, null, 0, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // INSTALLERR1: the cause from the exception's type, never its localized text.
            fetched = new CatalogTextFetchResult(false, InstallReasons.FetchTransport + ": " + e.Message, null, 0, 0)
            {
                FailureCause = RecordingCatalogTransport.Classify(e, cancellationToken.IsCancellationRequested),
            };
        }

        now = _clock.UtcNow;
        string conditional = validators.IsEmpty ? "unconditional" : "conditional";
        if (fetched.Ok && fetched.NotModified)
        {
            if (!cached)
            {
                // Cannot happen (no validators without a body); never hand back an empty index.
                NoteFailure(now);
                InstallLog.Warn("Thunderstore: index check (" + Owner + ") HTTP 304 without a cached copy; ignored.");
                return new CatalogTextFetchResult(false, "HTTP 304 without a cached copy", null, 0, 304);
            }

            Cache.Touch(now);
            NoteSuccess();
            InstallLog.Info("Thunderstore: index check (" + Owner + ", " + conditional + ") HTTP 304 not modified, 0 body bytes; reused the cached copy ("
                + body.Length.ToString(CultureInfo.InvariantCulture) + " chars).");
            return new CatalogTextFetchResult(true, null, body, Encoding.UTF8.GetByteCount(body), 304)
            {
                NotModified = true,
                LastModified = fetched.LastModified ?? validators.LastModified,
                ETag = fetched.ETag ?? validators.ETag,
            };
        }

        if (fetched.Ok && fetched.Text != null)
        {
            NoteSuccess();
            bool stored = _accept(fetched.Text) && Cache.Write(fetched.Text, new IndexValidators(fetched.LastModified, fetched.ETag), now);
            InstallLog.Info("Thunderstore: index check (" + Owner + ", " + conditional + ") HTTP " + fetched.StatusCode.ToString(CultureInfo.InvariantCulture)
                + ", " + fetched.WireBytes.ToString(CultureInfo.InvariantCulture) + " bytes on the wire ("
                + (fetched.ContentEncoding ?? "identity") + "), " + fetched.ByteCount.ToString(CultureInfo.InvariantCulture) + " decoded; "
                + (stored ? "cached" : "not cached")
                + (fetched.LastModified != null ? "; Last-Modified " + fetched.LastModified : "; no Last-Modified")
                + (fetched.ETag != null ? "; ETag " + fetched.ETag : "") + ".");
            return fetched;
        }

        if (string.Equals(fetched.Error, InstallReasons.FetchCancelled, StringComparison.Ordinal) || cancellationToken.IsCancellationRequested)
        {
            InstallLog.Info("Thunderstore: index check (" + Owner + ") canceled.");
            return fetched;
        }

        int inRow = NoteFailure(now);
        InstallLog.Warn("Thunderstore: index check (" + Owner + ", " + conditional + ") failed"
            + (fetched.StatusCode > 0 ? " (HTTP " + fetched.StatusCode.ToString(CultureInfo.InvariantCulture) + ")" : "")
            + ": " + (fetched.Error ?? InstallReasons.FetchTransport) + "; " + inRow + " in a row"
            + (PausedUntilUtc is { } paused ? "; automatic checks paused until " + paused.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC" : "") + ".");
        return fetched;
    }

    private void NoteSuccess()
    {
        lock (_gate)
        {
            _failures = 0;
            _pausedUntil = DateTime.MinValue;
        }
    }

    private int NoteFailure(DateTime now)
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= FailuresBeforeBackoff)
            {
                int doublings = Math.Min(_failures - FailuresBeforeBackoff, 10);
                long ticks = FirstBackoff.Ticks << doublings;
                if (ticks <= 0 || ticks > MaxBackoff.Ticks) ticks = MaxBackoff.Ticks;
                _pausedUntil = now + TimeSpan.FromTicks(ticks);
            }

            return _failures;
        }
    }
}
