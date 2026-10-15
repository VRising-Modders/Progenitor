using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// Background fetch of the Thunderstore package index and of one <c>download_url</c>.
/// Transport is injectable so tests never touch the network. Bytes land in a temp file
/// under isolated staging; the returned sha256 is a locally computed hash of what was
/// received — an integrity measurement, not proof of publisher authenticity. There is
/// no signing authority on this path.
/// </summary>
internal sealed class CatalogFetcher
{
    public const string DefaultIndexUrl = "https://thunderstore.io/c/v-rising/api/v1/package/";
    public const int DefaultMaxRedirects = 5;
    public const int DefaultMaxRetries = 3;
    /// <summary>
    /// The ceiling of one package download (<c>[Install] MaxDownloadMB</c>'s default, 128 MiB).
    /// LIMITS1: the old 32 MiB value was copied on 2026-09-13 from a cap meant for transfers over the game
    /// connection (since removed). A plain HTTPS download only needs a safety bound.
    /// </summary>
    public const long DefaultMaxBytes = 128L * 1024 * 1024;

    /// <summary>
    /// LIMITS1: the ceiling of the package index itself, kept at the old 32 MiB on its own (the
    /// V Rising index is a few MB; the parser has its own JSON ceiling).
    /// </summary>
    public const long IndexMaxBytes = 32L * 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultBackoff = TimeSpan.FromMilliseconds(200);

    private readonly ICatalogTransport _transport;
    private readonly ICatalogClock _clock;
    private readonly CatalogFetchOptions _options;

    public CatalogFetcher(ICatalogTransport transport, CatalogFetchOptions? options = null, ICatalogClock? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? CatalogFetchOptions.Default;
        _clock = clock ?? new SystemCatalogClock();
    }

    /// <summary>Build a fetcher over BCL <see cref="HttpClient"/> with an injectable handler.</summary>
    public static CatalogFetcher CreateHttp(HttpMessageHandler handler, CatalogFetchOptions? options = null, ICatalogClock? clock = null) =>
        new(new HttpCatalogTransport(handler, options ?? CatalogFetchOptions.Default), options, clock);

    public Task<CatalogFetchResult> FetchIndexAsync(string stagingRoot, CancellationToken cancellationToken) =>
        Task.Run(() => FetchCoreAsync(_options.IndexUrl, stagingRoot, "index.json", cancellationToken, IndexMaxBytes), cancellationToken);

    public Task<CatalogFetchResult> FetchDownloadAsync(string downloadUrl, string stagingRoot, string fileName, CancellationToken cancellationToken) =>
        FetchAsync(downloadUrl, stagingRoot, fileName, cancellationToken);

    public Task<CatalogFetchResult> FetchAsync(string url, string stagingRoot, string fileName, CancellationToken cancellationToken)
    {
        // Off the caller's thread (Unity main thread must not do network, archive or disk-heavy work).
        return Task.Run(() => FetchCoreAsync(url, stagingRoot, fileName, cancellationToken, _options.MaxBytes), cancellationToken);
    }

    /// <summary>
    /// Fetch a small JSON/text body into memory. Same HTTPS, redirect, retry and timeout
    /// rules as <see cref="FetchAsync"/>; the body is capped at <paramref name="maxBytes"/>
    /// (and at <see cref="CatalogFetchOptions.MaxBytes"/>) and is not written to disk.
    /// </summary>
    public Task<CatalogTextFetchResult> FetchTextAsync(string url, long maxBytes, CancellationToken cancellationToken) =>
        FetchTextAsync(url, maxBytes, compressed: false, cancellationToken);

    /// <summary>
    /// TSTRAFFIC1: the same, asking for a compressed body (<see cref="AcceptEncoding"/>) when
    /// <paramref name="compressed"/>. The cap applies to the decoded body.
    /// </summary>
    public Task<CatalogTextFetchResult> FetchTextAsync(string url, long maxBytes, bool compressed, CancellationToken cancellationToken)
    {
        long cap = maxBytes > 0 && maxBytes < _options.MaxBytes ? maxBytes : _options.MaxBytes;
        var headers = compressed ? new Dictionary<string, string> { ["Accept-Encoding"] = AcceptEncoding } : null;
        return Task.Run(() => FetchTextCoreAsync(url, cap, headers, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// TSTRAFFIC1: the package index as a conditional, compressed request. Sends
    /// <c>Accept-Encoding: gzip</c> and, when <paramref name="validators"/> carries them,
    /// <c>If-Modified-Since</c> / <c>If-None-Match</c>. A 304 answers <c>Ok</c> with
    /// <see cref="CatalogTextFetchResult.NotModified"/> and no text: the caller reuses its copy.
    /// <paramref name="maxBytes"/> caps the decoded body (the decompression-bomb guard) as well as the wire.
    /// </summary>
    public Task<CatalogTextFetchResult> FetchIndexTextAsync(string url, long maxBytes, IndexValidators validators, CancellationToken cancellationToken)
    {
        long cap = maxBytes > 0 && maxBytes < _options.MaxBytes ? maxBytes : _options.MaxBytes;
        var headers = new Dictionary<string, string> { ["Accept-Encoding"] = AcceptEncoding };
        if (!string.IsNullOrWhiteSpace(validators.LastModified)) headers["If-Modified-Since"] = validators.LastModified!.Trim();
        if (!string.IsNullOrWhiteSpace(validators.ETag)) headers["If-None-Match"] = validators.ETag!.Trim();
        return Task.Run(() => FetchTextCoreAsync(url, cap, headers, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// TSTRAFFIC1: the one encoding asked for. Not <c>deflate</c> (servers disagree on zlib-wrapped versus raw
    /// streams) and not Brotli.
    /// </summary>
    public const string AcceptEncoding = "gzip";

    /// <summary>
    /// Fetch a small binary body (a listing icon, LIST1) into memory. Same HTTPS, redirect,
    /// retry, timeout and size rules as <see cref="FetchTextAsync"/>; nothing is written to disk.
    /// </summary>
    public Task<CatalogBytesFetchResult> FetchBytesAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        long cap = maxBytes > 0 && maxBytes < _options.MaxBytes ? maxBytes : _options.MaxBytes;
        return Task.Run(() => FetchBytesCoreAsync(url, cap, cancellationToken), cancellationToken);
    }

    private async Task<CatalogFetchResult> FetchCoreAsync(
        string url,
        string stagingRoot,
        string fileName,
        CancellationToken cancellationToken,
        long maxBytes)
    {
        if (maxBytes <= 0) maxBytes = _options.MaxBytes;
        if (!TryCreateSourceUri(url, out var uri, out var reason))
            return Fail(reason, 0, null);

        string stamp = FormatStamp(_clock.UtcNow);
        string tempPath = "";
        int retries = 0;
        int redirects = 0;
        int lastStatus = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();

                CatalogTransportResponse response;
                try
                {
                    response = await _transport.SendAsync(new CatalogTransportRequest(uri), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return Fail(InstallReasons.FetchTimeout, lastStatus, stamp);
                }
                catch (OperationCanceledException)
                {
                    return Fail(InstallReasons.FetchCancelled, lastStatus, stamp);
                }
                catch (Exception e)
                {
                    return Fail(InstallReasons.FetchTransport + ": " + e.Message, lastStatus, stamp);
                }

                using (response)
                {
                    lastStatus = response.StatusCode;

                    if (IsRedirect(response.StatusCode))
                    {
                        redirects++;
                        if (redirects > _options.MaxRedirects)
                            return Fail(InstallReasons.FetchRedirectBound, lastStatus, stamp);
                        if (response.Location == null)
                            return Fail(InstallReasons.FetchRedirectBound, lastStatus, stamp);
                        if (!TryCreateHttpsUri(response.Location.ToString(), out uri, out reason))
                            return Fail(reason == InstallReasons.FetchNotHttps
                                ? InstallReasons.FetchRedirectInsecure
                                : reason, lastStatus, stamp);
                        continue;
                    }

                    if (response.StatusCode == 429)
                    {
                        if (retries >= _options.MaxRetries)
                            return Fail(InstallReasons.FetchTooManyRetries, lastStatus, stamp);
                        retries++;
                        await _clock.Delay(Backoff(retries, response.RetryAfter), timeout.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (response.StatusCode >= 500 && response.StatusCode <= 599)
                    {
                        if (retries >= _options.MaxRetries)
                            return Fail(InstallReasons.FetchTooManyRetries, lastStatus, stamp);
                        retries++;
                        await _clock.Delay(Backoff(retries, response.RetryAfter), timeout.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (response.StatusCode < 200 || response.StatusCode > 299)
                        return Fail(InstallReasons.HttpStatus(response.StatusCode), lastStatus, stamp);

                    if (response.ContentLength is > 0 && response.ContentLength.Value > maxBytes)
                        return Fail(InstallReasons.FetchSizeCeiling, lastStatus, stamp);

                    tempPath = PrepareTempPath(stagingRoot, fileName);
                    long written;
                    string sha;
                    try
                    {
                        (written, sha) = await WriteBoundedAsync(
                                response.Content,
                                tempPath,
                                maxBytes,
                                timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (CatalogFetchSizeException)
                    {
                        TryDelete(tempPath);
                        return Fail(InstallReasons.FetchSizeCeiling, lastStatus, stamp);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        TryDelete(tempPath);
                        return Fail(InstallReasons.FetchTimeout, lastStatus, stamp);
                    }
                    catch (OperationCanceledException)
                    {
                        TryDelete(tempPath);
                        return Fail(InstallReasons.FetchCancelled, lastStatus, stamp);
                    }
                    catch (Exception e)
                    {
                        // REMOTEINST1: a body that ends early (a truncated transfer), a stalled read
                        // or any other I/O fault mid-stream used to escape this method and leave the
                        // .part file behind. It is a failed fetch like any other: nothing stays on disk.
                        TryDelete(tempPath);
                        return Fail(InstallReasons.FetchTransport + ": " + e.Message, lastStatus, stamp);
                    }

                    // REMOTEINST1: a body shorter than the Content-Length the server announced is a
                    // truncated transfer, not a package.
                    if (response.ContentLength is > 0 && written != response.ContentLength.Value)
                    {
                        TryDelete(tempPath);
                        return Fail(InstallReasons.FetchTruncated, lastStatus, stamp);
                    }

                    return new CatalogFetchResult(true, null, tempPath, written, sha, lastStatus, stamp);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(tempPath);
            return Fail(InstallReasons.FetchTimeout, lastStatus, stamp);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            return Fail(InstallReasons.FetchCancelled, lastStatus, stamp);
        }
    }

    private Task<CatalogBytesFetchResult> FetchBytesCoreAsync(
        string url,
        long maxBytes,
        CancellationToken cancellationToken) =>
        FetchBytesCoreAsync(url, maxBytes, null, cancellationToken);

    private async Task<CatalogBytesFetchResult> FetchBytesCoreAsync(
        string url,
        long maxBytes,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        bool conditional = headers != null
            && (headers.ContainsKey("If-Modified-Since") || headers.ContainsKey("If-None-Match"));
        if (!TryCreateSourceUri(url, out var uri, out var reason))
            return BytesFail(reason, 0);

        int retries = 0;
        int redirects = 0;
        int lastStatus = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();

                CatalogTransportResponse response;
                try
                {
                    response = await _transport.SendAsync(new CatalogTransportRequest(uri, "GET", headers), timeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return BytesFail(InstallReasons.FetchTimeout, lastStatus, FetchFailureCause.Timeout);
                }
                catch (OperationCanceledException)
                {
                    return BytesFail(InstallReasons.FetchCancelled, lastStatus, FetchFailureCause.Cancelled);
                }
                catch (Exception e)
                {
                    // INSTALLERR1: the cause from the exception's type (a localized OS message says nothing reliable).
                    return BytesFail(InstallReasons.FetchTransport + ": " + e.Message, lastStatus,
                        RecordingCatalogTransport.Classify(e, cancellationToken.IsCancellationRequested));
                }

                using (response)
                {
                    lastStatus = response.StatusCode;

                    // TSTRAFFIC1: our copy is current. Only as the answer to a conditional request; a 304
                    // nobody asked for is an error like any other non-2xx status.
                    if (response.StatusCode == 304 && conditional)
                        return new CatalogBytesFetchResult(true, null, null, 0, 304)
                        {
                            NotModified = true,
                            LastModified = response.LastModified,
                            ETag = response.ETag,
                        };

                    if (IsRedirect(response.StatusCode))
                    {
                        redirects++;
                        if (redirects > _options.MaxRedirects)
                            return BytesFail(InstallReasons.FetchRedirectBound, lastStatus);
                        if (response.Location == null)
                            return BytesFail(InstallReasons.FetchRedirectBound, lastStatus);
                        if (!TryCreateHttpsUri(response.Location.ToString(), out uri, out reason))
                            return BytesFail(reason == InstallReasons.FetchNotHttps
                                ? InstallReasons.FetchRedirectInsecure
                                : reason, lastStatus);
                        continue;
                    }

                    if (response.StatusCode == 429)
                    {
                        if (retries >= _options.MaxRetries)
                            return BytesFail(InstallReasons.FetchTooManyRetries, lastStatus, FetchFailureCause.Http);
                        retries++;
                        await _clock.Delay(Backoff(retries, response.RetryAfter), timeout.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (response.StatusCode >= 500 && response.StatusCode <= 599)
                    {
                        if (retries >= _options.MaxRetries)
                            return BytesFail(InstallReasons.FetchTooManyRetries, lastStatus, FetchFailureCause.Http);
                        retries++;
                        await _clock.Delay(Backoff(retries, response.RetryAfter), timeout.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (response.StatusCode < 200 || response.StatusCode > 299)
                        return BytesFail(InstallReasons.HttpStatus(response.StatusCode), lastStatus, FetchFailureCause.Http);

                    if (response.ContentLength is > 0 && response.ContentLength.Value > maxBytes)
                        return BytesFail(InstallReasons.FetchSizeCeiling, lastStatus);

                    var wire = new CountingReadStream(response.Content);
                    if (!TryDecode(response.ContentEncoding, wire, out Stream body))
                        return BytesFail(InstallReasons.FetchTransport + ": unsupported Content-Encoding " + response.ContentEncoding, lastStatus);
                    using var decoder = ReferenceEquals(body, wire) ? null : body;
                    try
                    {
                        // TSTRAFFIC1: the cap counts DECODED bytes, so a small gzip body that inflates
                        // past it fails here (the decompression-bomb guard).
                        var (bytes, written) = await ReadBoundedBytesAsync(
                                body,
                                maxBytes,
                                timeout.Token)
                            .ConfigureAwait(false);
                        return new CatalogBytesFetchResult(true, null, bytes, written, lastStatus)
                        {
                            LastModified = response.LastModified,
                            ETag = response.ETag,
                            WireBytes = wire.Count,
                            ContentEncoding = IsIdentity(response.ContentEncoding) ? null : response.ContentEncoding!.Trim().ToLowerInvariant(),
                        };
                    }
                    catch (CatalogFetchSizeException)
                    {
                        return BytesFail(InstallReasons.FetchSizeCeiling, lastStatus);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return BytesFail(InstallReasons.FetchTimeout, lastStatus, FetchFailureCause.Timeout);
                    }
                    catch (OperationCanceledException)
                    {
                        return BytesFail(InstallReasons.FetchCancelled, lastStatus, FetchFailureCause.Cancelled);
                    }
                    catch (Exception e)
                    {
                        return BytesFail(InstallReasons.FetchTransport + ": " + e.Message, lastStatus,
                            RecordingCatalogTransport.Classify(e, cancellationToken.IsCancellationRequested));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return BytesFail(InstallReasons.FetchTimeout, lastStatus, FetchFailureCause.Timeout);
        }
        catch (OperationCanceledException)
        {
            return BytesFail(InstallReasons.FetchCancelled, lastStatus, FetchFailureCause.Cancelled);
        }
    }

    /// <summary>The text path is the bytes path decoded as UTF-8; every rule above is shared.</summary>
    private async Task<CatalogTextFetchResult> FetchTextCoreAsync(
        string url,
        long maxBytes,
        IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var result = await FetchBytesCoreAsync(url, maxBytes, headers, cancellationToken).ConfigureAwait(false);
        if (result.Ok && result.NotModified)
            return new CatalogTextFetchResult(true, null, null, 0, result.StatusCode)
            {
                NotModified = true,
                LastModified = result.LastModified,
                ETag = result.ETag,
            };
        if (!result.Ok || result.Bytes == null)
            return TextFail(result.Error ?? InstallReasons.FetchTransport, result.StatusCode, result.FailureCause);
        return new CatalogTextFetchResult(true, null, Encoding.UTF8.GetString(result.Bytes), result.ByteCount, result.StatusCode)
        {
            LastModified = result.LastModified,
            ETag = result.ETag,
            WireBytes = result.WireBytes,
            ContentEncoding = result.ContentEncoding,
        };
    }

    private static bool IsIdentity(string? encoding) =>
        string.IsNullOrWhiteSpace(encoding) || string.Equals(encoding!.Trim(), "identity", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// TSTRAFFIC1: the decoding stream for one <c>Content-Encoding</c>. Identity passes through;
    /// <c>gzip</c> (and <c>x-gzip</c>) is decoded. Anything else (<c>deflate</c>, <c>br</c>, a chain of
    /// codings) is refused: it was never asked for. The caller disposes a decoding stream it was handed.
    /// </summary>
    internal static bool TryDecode(string? encoding, Stream wire, out Stream body)
    {
        body = wire;
        if (IsIdentity(encoding)) return true;
        string e = encoding!.Trim().ToLowerInvariant();
        if (e == "gzip" || e == "x-gzip")
        {
            body = new GZipStream(wire, CompressionMode.Decompress, leaveOpen: true);
            return true;
        }

        return false;
    }

    /// <summary>TSTRAFFIC1: counts the encoded bytes read off the wire (for the request log line).</summary>
    private sealed class CountingReadStream : Stream
    {
        private readonly Stream _inner;

        public CountingReadStream(Stream inner) => _inner = inner;

        public long Count { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Count; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = _inner.Read(buffer, offset, count);
            if (n > 0) Count += n;
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int n = await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            if (n > 0) Count += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0) Count += n;
            return n;
        }
    }

    internal static async Task<(string Text, long Bytes)> ReadBoundedStringAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var (bytes, total) = await ReadBoundedBytesAsync(source, maxBytes, cancellationToken).ConfigureAwait(false);
        return (Encoding.UTF8.GetString(bytes), total);
    }

    internal static async Task<(byte[] Bytes, long Count)> ReadBoundedBytesAsync(
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream(maxBytes > 0 && maxBytes < int.MaxValue ? (int)Math.Min(maxBytes, 1024 * 1024) : 4096);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) throw new CatalogFetchSizeException();
            output.Write(buffer, 0, read);
        }

        return (output.ToArray(), total);
    }

    private static CatalogTextFetchResult TextFail(string error, int status, FetchFailureCause cause = FetchFailureCause.None) =>
        new(false, error, null, 0, status) { FailureCause = cause };

    private static CatalogBytesFetchResult BytesFail(string error, int status, FetchFailureCause cause = FetchFailureCause.None) =>
        new(false, error, null, 0, status) { FailureCause = cause };

    private TimeSpan Backoff(int retry, TimeSpan? retryAfter)
    {
        if (retryAfter is { } hinted && hinted > TimeSpan.Zero && hinted < TimeSpan.FromMinutes(2))
            return hinted;
        long ticks = _options.Backoff.Ticks * (1L << Math.Min(retry - 1, 4));
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>
    /// HOT1: the first URL of a fetch. HTTPS always; a <c>file://</c> URL only when the fetch
    /// options allow one (<c>[Install] LocalIndexPath</c> served through
    /// <see cref="FileCatalogTransport"/>). Redirect targets keep the HTTPS-only rule.
    /// </summary>
    internal bool TryCreateSourceUri(string? url, out Uri uri, out string error)
    {
        if (_options.AllowFileUrls
            && Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var parsed)
            && parsed != null
            && string.Equals(parsed.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            error = "";
            return true;
        }

        return TryCreateHttpsUri(url, out uri, out error);
    }

    internal static bool TryCreateHttpsUri(string? url, out Uri uri, out string error)
    {
        uri = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(url))
        {
            error = InstallReasons.FetchNoUrl;
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) || parsed == null)
        {
            error = InstallReasons.FetchNotHttps;
            return false;
        }

        // LOCALTS1: plain http only for the active dev Thunderstore on a loopback address.
        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && !ThunderstoreMirror.AllowsHttp(parsed))
        {
            error = InstallReasons.FetchNotHttps;
            return false;
        }

        uri = parsed;
        return true;
    }

    internal static bool IsRedirect(int status) =>
        status == 301 || status == 302 || status == 303 || status == 307 || status == 308;

    internal static string FormatStamp(DateTime utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The folder downloads are staged in (<c>progenitor-staging/index/tmp</c> under the root).</summary>
    internal static string TempDirectory(string stagingRoot) =>
        Path.Combine(
            Path.GetFullPath(stagingRoot),
            ModFolders.StagingFolderName,
            ModManifestIds.NormalizeHostKey(InstallReasons.IndexHostKey),
            "tmp");

    internal static string PrepareTempPath(string stagingRoot, string fileName)
    {
        string leaf = ModManifestIds.SafeFileName(fileName);
        if (string.IsNullOrWhiteSpace(leaf)) leaf = "download.bin";
        string dir = TempDirectory(stagingRoot);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, leaf + "." + Guid.NewGuid().ToString("N") + ".part");
    }

    /// <summary>
    /// Stream <paramref name="source"/> to <paramref name="path"/>, hashing as we go.
    /// The hash is of the received bytes only — not a publisher signature.
    /// </summary>
    internal static async Task<(long Bytes, string Sha256)> WriteBoundedAsync(
        Stream source,
        string path,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) throw new CatalogFetchSizeException();
            sha.TransformBlock(buffer, 0, read, null, 0);
            await output.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (total, ToHex(sha.Hash!));
    }

    internal static string ToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 2);
        for (int i = 0; i < data.Length; i++) sb.Append(data[i].ToString("x2"));
        return sb.ToString();
    }

    internal static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ToHex(sha.ComputeHash(stream));
    }

    private static CatalogFetchResult Fail(string error, int status, string? stamp) =>
        new(false, error, null, 0, null, status, stamp);

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    private sealed class CatalogFetchSizeException : Exception { }
}

internal sealed class CatalogFetchOptions
{
    private static readonly CatalogFetchOptions Thunderstore = new();

    /// <summary>
    /// The defaults: thunderstore.io, or the dev Thunderstore stand-in while one is configured and allowed
    /// (LOCALTS1, <see cref="ThunderstoreMirror"/>).
    /// </summary>
    public static CatalogFetchOptions Default =>
        ThunderstoreMirror.Active ? new CatalogFetchOptions { IndexUrl = ThunderstoreMirror.IndexUrl } : Thunderstore;

    public string IndexUrl { get; init; } = CatalogFetcher.DefaultIndexUrl;
    public int MaxRedirects { get; init; } = CatalogFetcher.DefaultMaxRedirects;
    public int MaxRetries { get; init; } = CatalogFetcher.DefaultMaxRetries;
    public long MaxBytes { get; init; } = CatalogFetcher.DefaultMaxBytes;
    public TimeSpan Timeout { get; init; } = CatalogFetcher.DefaultTimeout;
    public TimeSpan Backoff { get; init; } = CatalogFetcher.DefaultBackoff;
    public string UserAgent { get; init; } = InstallReasons.UserAgent;

    /// <summary>
    /// HOT1: accept a <c>file://</c> source URL (the local dummy index and the artifacts it names).
    /// False everywhere else, so nothing but HTTPS is fetched from the real catalog.
    /// </summary>
    public bool AllowFileUrls { get; init; }
}

/// <summary>An in-memory binary body from <see cref="CatalogFetcher.FetchBytesAsync"/> (LIST1: listing icons).</summary>
internal sealed class CatalogBytesFetchResult
{
    public CatalogBytesFetchResult(bool ok, string? error, byte[]? bytes, long byteCount, int statusCode)
    {
        Ok = ok;
        Error = error;
        Bytes = bytes;
        ByteCount = byteCount;
        StatusCode = statusCode;
    }

    public bool Ok { get; }
    public string? Error { get; }
    public byte[]? Bytes { get; }
    public long ByteCount { get; }
    public int StatusCode { get; }

    /// <summary>TSTRAFFIC1: 304 to a conditional request; no body was read.</summary>
    public bool NotModified { get; init; }

    public string? LastModified { get; init; }
    public string? ETag { get; init; }

    /// <summary>TSTRAFFIC1: encoded body bytes received; <see cref="ByteCount"/> is the decoded size.</summary>
    public long WireBytes { get; init; }

    public string? ContentEncoding { get; init; }

    /// <summary>INSTALLERR1: the transport cause of a failure, from the exception's type (never its localized text).</summary>
    public FetchFailureCause FailureCause { get; init; }
}

/// <summary>BCL <see cref="HttpClient"/> transport. Follows no redirects itself; <see cref="CatalogFetcher"/> bounds them.</summary>
internal sealed class HttpCatalogTransport : ICatalogTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpCatalogTransport(HttpMessageHandler handler, CatalogFetchOptions options)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _client.DefaultRequestHeaders.UserAgent.Clear();
        if (ProductInfoHeaderValue.TryParse(options.UserAgent, out var parsed))
            _client.DefaultRequestHeaders.UserAgent.Add(parsed);
        else
            _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
    }

    public async Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Uri);
        foreach (var kv in request.Headers)
            message.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        var response = await _client.SendAsync(
                message,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter != null)
        {
            if (response.Headers.RetryAfter.Delta is { } delta) retryAfter = delta;
            else if (response.Headers.RetryAfter.Date is { } date)
            {
                var wait = date - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) retryAfter = wait;
            }
        }

        Stream content = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return new CatalogTransportResponse(
            (int)response.StatusCode,
            new HttpResponseStream(response, content),
            response.Headers.Location,
            response.Content.Headers.ContentLength,
            retryAfter)
        {
            // TSTRAFFIC1: the validators as sent (echoed back verbatim on the next conditional request).
            LastModified = HeaderText(response.Content.Headers, "Last-Modified") ?? HeaderText(response.Headers, "Last-Modified"),
            ETag = HeaderText(response.Headers, "ETag"),
            ContentEncoding = response.Content.Headers.ContentEncoding.Count == 0
                ? null
                : string.Join(", ", response.Content.Headers.ContentEncoding),
        };
    }

    private static string? HeaderText(HttpHeaders headers, string name)
    {
        try
        {
            if (!headers.TryGetValues(name, out var values)) return null;
            string joined = string.Join(", ", values).Trim();
            return joined.Length == 0 ? null : joined;
        }
        catch { return null; }
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Keeps the <see cref="HttpResponseMessage"/> alive for the lifetime of the content stream.</summary>
    private sealed class HttpResponseStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;

        public HttpResponseStream(HttpResponseMessage response, Stream inner)
        {
            _response = response;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// TSTRAFFIC1: one <see cref="HttpClient"/> for the Install tab, the join screens and the server's
/// install-window read (each used to build its own client and handler). Each owner takes a
/// <see cref="Lease"/> and disposes it when it used to dispose its own transport (the join screens on
/// teardown, the server's read with its reader); the Mods menu holds one lease for its Core generation
/// and reuses it across catalog rebuilds, as its own client was never disposed. The client and its
/// handler go with the last lease. The server's remote installer keeps its own handler: its connect
/// timeout follows <c>[RemoteInstall] ConnectTimeoutSeconds</c>.
/// </summary>
internal static class SharedCatalogHttp
{
    private static readonly object Gate = new();
    private static HttpCatalogTransport? _transport;
    private static HttpClientHandler? _handler;
    private static int _leases;

    /// <summary>Live leases (tests).</summary>
    internal static int Leases { get { lock (Gate) return _leases; } }

    public static Lease Acquire()
    {
        lock (Gate)
        {
            if (_transport == null)
            {
                _handler = new HttpClientHandler();
                _transport = new HttpCatalogTransport(_handler, CatalogFetchOptions.Default);
            }

            _leases++;
            return new Lease(_transport);
        }
    }

    private static void Release()
    {
        HttpCatalogTransport? transport = null;
        HttpClientHandler? handler = null;
        lock (Gate)
        {
            if (_leases > 0) _leases--;
            if (_leases == 0)
            {
                transport = _transport;
                handler = _handler;
                _transport = null;
                _handler = null;
            }
        }

        try { transport?.Dispose(); }
        catch { /* teardown */ }
        try { handler?.Dispose(); } // HttpCatalogTransport does not own its handler.
        catch { /* teardown */ }
    }

    /// <summary>One owner's use of the shared client. Disposing it twice releases once.</summary>
    internal sealed class Lease : ICatalogTransport, IDisposable
    {
        private HttpCatalogTransport? _transport;

        internal Lease(HttpCatalogTransport transport) => _transport = transport;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            var transport = Volatile.Read(ref _transport) ?? throw new ObjectDisposedException(nameof(SharedCatalogHttp));
            return transport.SendAsync(request, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _transport, null) != null) Release();
        }
    }
}
