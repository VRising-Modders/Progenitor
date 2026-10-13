using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TSTRAFFIC1 (decided 2026-10-08): cut Progenitor's Thunderstore traffic. Measured that day: the
/// V Rising index is 1,080,986 bytes, 140,911 with gzip, and a conditional request with the response's
/// Last-Modified answers 304 with no body. These tests pin the conditional, compressed index check and
/// its disk copy, the decompressed-size cap, the failure backoff, the README-only document load and
/// the remembered 404s. All game-free: a fake transport, a fake clock, a temp folder.
/// </summary>
public sealed class ThunderstoreTrafficTests : IDisposable
{
    private const string IndexUrl = CatalogFetcher.DefaultIndexUrl;
    private const string LastModifiedA = "Wed, 08 Oct 2026 12:00:00 GMT";
    private const string LastModifiedB = "Wed, 08 Oct 2026 12:05:00 GMT";

    private const string OneListing =
        "[{\"name\":\"n\",\"full_name\":\"o-n\",\"owner\":\"o\",\"is_deprecated\":false,\"package_url\":\"https://thunderstore.io/c/v-rising/p/o/n/\",\"versions\":[{\"name\":\"n\",\"full_name\":\"o-n-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/o/n/1.0.0/\",\"file_size\":1,\"is_active\":true}]}]";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-tstraffic-" + Guid.NewGuid().ToString("N"));

    public ThunderstoreTrafficTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    // ---- The fetcher: compression, the decoded cap, conditional headers ----------------------

    [Fact]
    public async Task Index_check_asks_for_gzip_and_decodes_it_within_the_cap()
    {
        string body = OneListing + new string(' ', 50_000);
        var transport = new ScriptTransport(_ => Gzip(200, body, LastModifiedA));
        var fetcher = new CatalogFetcher(transport, clock: new TestClock());

        var result = await fetcher.FetchIndexTextAsync(IndexUrl, CatalogFetcher.IndexMaxBytes, IndexValidators.None, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(body, result.Text);
        Assert.Equal("gzip", result.ContentEncoding);
        Assert.True(result.WireBytes > 0 && result.WireBytes < body.Length / 10, "wire " + result.WireBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), result.ByteCount);
        Assert.Equal(LastModifiedA, result.LastModified);
        var sent = transport.Requests[0];
        Assert.Equal("gzip", sent.Header("Accept-Encoding"));
        Assert.Null(sent.Header("If-Modified-Since"));
        Assert.Null(sent.Header("If-None-Match"));
    }

    [Fact]
    public async Task A_small_gzip_body_that_inflates_past_the_cap_is_refused()
    {
        // 4 MiB of zeros gzips to a few KB: the wire is tiny, the decoded body is not.
        string bomb = new string('0', 4 * 1024 * 1024);
        var transport = new ScriptTransport(_ => Gzip(200, bomb, null));
        var fetcher = new CatalogFetcher(transport, clock: new TestClock());

        var result = await fetcher.FetchIndexTextAsync(IndexUrl, 64 * 1024, IndexValidators.None, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(InstallReasons.FetchSizeCeiling, result.Error);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task Only_gzip_is_decoded_and_an_unasked_304_is_an_error()
    {
        // deflate is never asked for (servers disagree on zlib-wrapped versus raw), so a deflate body is refused.
        var deflated = new ScriptTransport(_ => Encoded(200, Zlib("[\"deflated\"]"), "deflate", null));
        var no = await new CatalogFetcher(deflated, clock: new TestClock())
            .FetchIndexTextAsync(IndexUrl, 1024, IndexValidators.None, CancellationToken.None);
        Assert.False(no.Ok);
        Assert.Contains("Content-Encoding", no.Error, StringComparison.Ordinal);

        var brotli = new ScriptTransport(_ => Encoded(200, new byte[] { 1, 2, 3 }, "br", null));
        var refused = await new CatalogFetcher(brotli, clock: new TestClock())
            .FetchIndexTextAsync(IndexUrl, 1024, IndexValidators.None, CancellationToken.None);
        Assert.False(refused.Ok);
        Assert.Contains("Content-Encoding", refused.Error, StringComparison.Ordinal);

        // A 304 to a request that carried no validators is not "not modified".
        var stray = new ScriptTransport(_ => new CatalogTransportResponse(304, new MemoryStream()));
        var plain = await new CatalogFetcher(stray, clock: new TestClock())
            .FetchIndexTextAsync(IndexUrl, 1024, IndexValidators.None, CancellationToken.None);
        Assert.False(plain.Ok);
        Assert.False(plain.NotModified);
        Assert.Equal(304, plain.StatusCode);

        // Plain README fetches (no compression asked) send no Accept-Encoding.
        var docs = new ScriptTransport(_ => Plain(200, "{\"markdown\":\"x\"}", null));
        await new CatalogFetcher(docs, clock: new TestClock()).FetchTextAsync("https://thunderstore.io/x", 1024, CancellationToken.None);
        Assert.Null(docs.Requests[0].Header("Accept-Encoding"));
    }

    // ---- The conditional check and its disk copy ---------------------------------------------

    [Fact]
    public async Task A_304_reuses_the_cached_body_and_refreshes_its_stamp_and_a_changed_index_replaces_it()
    {
        var clock = new TestClock();
        string first = OneListing;
        string second = OneListing.Replace("\"d\"", "\"changed\"", StringComparison.Ordinal);
        int step = 0;
        var transport = new ScriptTransport(request =>
        {
            step++;
            return step switch
            {
                1 => Gzip(200, first, LastModifiedA),
                2 => request.Header("If-Modified-Since") == LastModifiedA
                    ? new CatalogTransportResponse(304, new MemoryStream()) { LastModified = LastModifiedA }
                    : Gzip(200, first, LastModifiedA),
                3 => Gzip(200, second, LastModifiedB),
                _ => request.Header("If-Modified-Since") == LastModifiedB
                    ? new CatalogTransportResponse(304, new MemoryStream()) { LastModified = LastModifiedB }
                    : Gzip(200, second, LastModifiedB),
            };
        });
        var cache = new IndexDiskCache(_root);
        var check = ConditionalIndexFetch.Over(new CatalogFetcher(transport, clock: clock), IndexUrl, cache, "test", clock);

        // 1: nothing cached, an unconditional request; the body, its validators and the stamp land on disk.
        var r1 = await check.FetchAsync(CancellationToken.None);
        Assert.True(r1.Ok, r1.Error);
        Assert.False(r1.NotModified);
        Assert.Equal(first, File.ReadAllText(cache.IndexPath!));
        Assert.Contains("last-modified: " + LastModifiedA, File.ReadAllText(cache.ValidatorsPath!), StringComparison.Ordinal);
        Assert.True(cache.TryReadStamp(out var stamp1));
        Assert.Equal(clock.UtcNow, stamp1);

        // 2: 30 minutes later the check is conditional, Thunderstore answers 304: the cached body, a fresh stamp.
        clock.Advance(TimeSpan.FromMinutes(30));
        var r2 = await check.FetchAsync(CancellationToken.None);
        Assert.Equal(LastModifiedA, transport.Requests[1].Header("If-Modified-Since"));
        Assert.True(r2.Ok);
        Assert.True(r2.NotModified);
        Assert.Equal(304, r2.StatusCode);
        Assert.Equal(first, r2.Text);
        Assert.Equal(0, r2.WireBytes);
        Assert.True(cache.TryReadStamp(out var stamp2));
        Assert.Equal(clock.UtcNow, stamp2);

        // 3: the index changed (a 200 with a new Last-Modified): the copy and its validators are replaced.
        var r3 = await check.FetchAsync(CancellationToken.None);
        Assert.True(r3.Ok);
        Assert.False(r3.NotModified);
        Assert.Equal(second, r3.Text);
        Assert.Equal(second, File.ReadAllText(cache.IndexPath!));

        // 4: the next check sends the NEW Last-Modified.
        var r4 = await check.FetchAsync(CancellationToken.None);
        Assert.Equal(LastModifiedB, transport.Requests[3].Header("If-Modified-Since"));
        Assert.True(r4.NotModified);
        Assert.Equal(second, r4.Text);
        Assert.Equal(4, check.Requests);
    }

    [Fact]
    public async Task Validators_that_do_not_belong_to_the_body_on_disk_are_not_sent()
    {
        var clock = new TestClock();
        var transport = new ScriptTransport(_ => Gzip(200, OneListing, LastModifiedA));
        var cache = new IndexDiskCache(_root);
        var check = ConditionalIndexFetch.Over(new CatalogFetcher(transport, clock: clock), IndexUrl, cache, "test", clock);
        await check.FetchAsync(CancellationToken.None);

        // Someone else replaced index.json (an older Core generation, a hand edit): its sha no longer matches.
        File.WriteAllText(cache.IndexPath!, "[]");
        await check.FetchAsync(CancellationToken.None);
        Assert.Null(transport.Requests[1].Header("If-Modified-Since"));

        // An ETag (Thunderstore sends none today) is echoed as If-None-Match.
        var tagged = new ScriptTransport(_ => new CatalogTransportResponse(200, new MemoryStream(Encoding.UTF8.GetBytes(OneListing))) { ETag = "W/\"abc\"" });
        var root2 = Path.Combine(_root, "tagged");
        var check2 = ConditionalIndexFetch.Over(new CatalogFetcher(tagged, clock: clock), IndexUrl, new IndexDiskCache(root2), "test", clock);
        await check2.FetchAsync(CancellationToken.None);
        await check2.FetchAsync(CancellationToken.None);
        Assert.Equal("W/\"abc\"", tagged.Requests[1].Header("If-None-Match"));
        Assert.Null(tagged.Requests[1].Header("If-Modified-Since"));

        // A body that is not an index is not cached (it would otherwise be revalidated forever).
        var junk = new ScriptTransport(_ => Plain(200, "<html>maintenance</html>", LastModifiedA));
        var root3 = Path.Combine(_root, "junk");
        var check3 = ConditionalIndexFetch.Over(new CatalogFetcher(junk, clock: clock), IndexUrl, new IndexDiskCache(root3), "test", clock);
        var r = await check3.FetchAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.False(File.Exists(new IndexDiskCache(root3).IndexPath!));
    }

    /// <summary>
    /// Review M1: a server's remote installer may read a mirror (<c>[RemoteInstall] ThunderstoreBaseUrl</c>) while its
    /// availability read uses thunderstore.io. Each URL keeps its own copy, and validators written for one URL are
    /// never sent (or a 304 adopted) for another.
    /// </summary>
    [Fact]
    public async Task Each_index_url_keeps_its_own_copy_and_validators()
    {
        var clock = new TestClock();
        const string mirror = "https://mirror.example/c/v-rising/api/v1/package/";
        string mirrorBody = OneListing.Replace("\"d\"", "\"mirror\"", StringComparison.Ordinal);
        var transport = new ScriptTransport(request => request.Uri.AbsoluteUri == mirror
            ? Gzip(200, mirrorBody, LastModifiedB)
            : Gzip(200, OneListing, LastModifiedA));
        var fetcher = new CatalogFetcher(transport, clock: clock);
        var main = ConditionalIndexFetch.Over(fetcher, IndexUrl, new IndexDiskCache(_root, IndexUrl), "server availability", clock);
        var other = ConditionalIndexFetch.Over(fetcher, mirror, new IndexDiskCache(_root, mirror), "remote install", clock);

        await main.FetchAsync(CancellationToken.None);
        await other.FetchAsync(CancellationToken.None);
        Assert.Equal(Path.Combine(_root, "progenitor-cache", "index.json"), main.Cache.IndexPath);
        Assert.NotEqual(main.Cache.IndexPath, other.Cache.IndexPath);
        Assert.Equal(OneListing, File.ReadAllText(main.Cache.IndexPath!));
        Assert.Equal(mirrorBody, File.ReadAllText(other.Cache.IndexPath!));
        Assert.StartsWith("url: " + mirror + "\n", File.ReadAllText(other.Cache.ValidatorsPath!), StringComparison.Ordinal);

        await main.FetchAsync(CancellationToken.None);
        await other.FetchAsync(CancellationToken.None);
        Assert.Equal(LastModifiedA, transport.Requests[2].Header("If-Modified-Since"));
        Assert.Equal(LastModifiedB, transport.Requests[3].Header("If-Modified-Since"));

        // Validators naming another URL (a copied file) are not used.
        File.Copy(main.Cache.ValidatorsPath!, other.Cache.ValidatorsPath!, overwrite: true);
        File.Copy(main.Cache.IndexPath!, other.Cache.IndexPath!, overwrite: true);
        await other.FetchAsync(CancellationToken.None);
        Assert.Null(transport.Requests[4].Header("If-Modified-Since"));

        // A cache built for one URL cannot be wired to another.
        Assert.Throws<ArgumentException>(() => ConditionalIndexFetch.Over(fetcher, mirror, new IndexDiskCache(_root), "x", clock));
    }

    /// <summary>Review L4: the body is written as UTF-8 without a BOM and hashed as written; a BOM added later only costs an unconditional check.</summary>
    [Fact]
    public async Task The_body_is_hashed_as_written_and_a_bom_is_tolerated()
    {
        var clock = new TestClock();
        var transport = new ScriptTransport(request => request.Header("If-Modified-Since") == LastModifiedA
            ? new CatalogTransportResponse(304, new MemoryStream())
            : Gzip(200, OneListing, LastModifiedA));
        var cache = new IndexDiskCache(_root);
        var check = ConditionalIndexFetch.Over(new CatalogFetcher(transport, clock: clock), IndexUrl, cache, "test", clock);
        await check.FetchAsync(CancellationToken.None);
        byte[] onDisk = File.ReadAllBytes(cache.IndexPath!);
        Assert.False(onDisk.Length >= 3 && onDisk[0] == 0xEF);
        Assert.Contains("sha256: " + IndexDiskCache.Sha256(onDisk), File.ReadAllText(cache.ValidatorsPath!), StringComparison.Ordinal);
        Assert.True((await check.FetchAsync(CancellationToken.None)).NotModified);

        // Someone re-saved the file with a BOM: the body still reads (BOM stripped), the request goes out unconditional.
        File.WriteAllText(cache.IndexPath!, OneListing, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.True(cache.TryRead(0, out string body, out var validators));
        Assert.Equal(OneListing, body);
        Assert.True(validators.IsEmpty);
    }

    [Fact]
    public async Task Repeated_failures_pause_automatic_checks_a_forced_check_still_goes_out_and_success_clears_it()
    {
        var clock = new TestClock();
        bool fail = true;
        var transport = new ScriptTransport(_ => fail ? Plain(404, "", null) : Plain(200, OneListing, LastModifiedA));
        var check = ConditionalIndexFetch.Over(new CatalogFetcher(transport, clock: clock), IndexUrl, new IndexDiskCache(_root), "test", clock);

        for (int i = 0; i < ConditionalIndexFetch.FailuresBeforeBackoff; i++)
            Assert.False((await check.FetchAsync(CancellationToken.None)).Ok);
        Assert.Equal(3, transport.Requests.Count);
        Assert.Equal(clock.UtcNow + ConditionalIndexFetch.FirstBackoff, check.PausedUntilUtc);

        // Paused: an automatic check does not reach the network.
        var skipped = await check.FetchAsync(CancellationToken.None);
        Assert.False(skipped.Ok);
        Assert.Contains("paused", skipped.Error, StringComparison.Ordinal);
        Assert.Equal(3, transport.Requests.Count);

        // The player's Retry (force) still asks; a fourth failure doubles the pause.
        Assert.False((await check.FetchAsync(CancellationToken.None, force: true)).Ok);
        Assert.Equal(4, transport.Requests.Count);
        Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(10), check.PausedUntilUtc);

        // Past the pause it asks again; a success clears the count.
        clock.Advance(TimeSpan.FromMinutes(11));
        fail = false;
        Assert.True((await check.FetchAsync(CancellationToken.None)).Ok);
        Assert.Equal(0, check.ConsecutiveFailures);
        Assert.Null(check.PausedUntilUtc);

        // The pause never exceeds the cap.
        fail = true;
        for (int i = 0; i < 12; i++) await check.FetchAsync(CancellationToken.None, force: true);
        Assert.Equal(clock.UtcNow + ConditionalIndexFetch.MaxBackoff, check.PausedUntilUtc);
    }

    [Fact]
    public async Task The_install_tab_refresher_and_the_catalog_load_use_the_conditional_check()
    {
        var clock = new TestClock();
        var transport = new ScriptTransport(request => request.Header("If-Modified-Since") == LastModifiedA
            ? new CatalogTransportResponse(304, new MemoryStream())
            : Gzip(200, OneListing, LastModifiedA));
        var check = ConditionalIndexFetch.Over(new CatalogFetcher(transport, clock: clock), IndexUrl, new IndexDiskCache(_root), "install tab", clock);
        var loads = new List<string>();
        var refresher = new InstallIndexRefresher(_root, (json, stamp) => { loads.Add(json); return new CatalogLoadResult(true, null, 1, stamp); }, check, clock);

        Assert.True((await refresher.RefreshAsync(CancellationToken.None)).Ok);
        clock.Advance(InstallIndexRefresher.RefreshInterval + TimeSpan.FromMinutes(1));
        Assert.True(refresher.ShouldFetch(clock.UtcNow));
        Assert.True((await refresher.RefreshAsync(CancellationToken.None)).Ok);
        Assert.Equal(new[] { OneListing, OneListing }, loads);
        Assert.Equal(304, (await check.FetchAsync(CancellationToken.None)).StatusCode);
        Assert.Equal(LastModifiedA, transport.Requests[1].Header("If-Modified-Since"));
        Assert.Equal(clock.UtcNow, refresher.CacheStampUtc);
        // The refresher's own file names are the cache's (the join and server readers find the same copy).
        Assert.Equal(new IndexDiskCache(_root).IndexPath, refresher.IndexPath);
        Assert.Equal(new IndexDiskCache(_root).StampPath, refresher.StampPath);

        // The catalog's own load (the server's remote installs) checks conditionally against its root's copy.
        var catalogRoot = Path.Combine(_root, "server");
        var service = new InstallCatalogService(catalogRoot, transport, clock: clock, localIndexPath: Path.Combine(_root, "nolocal"), registerForBridge: false);
        int before = transport.Requests.Count;
        Assert.True((await service.LoadIndexAsync(CancellationToken.None)).Ok);
        Assert.Null(transport.Requests[before].Header("If-Modified-Since"));
        var again = await service.LoadIndexAsync(CancellationToken.None);
        Assert.True(again.Ok, again.Error);
        Assert.Equal(1, again.Count);
        Assert.Equal(LastModifiedA, transport.Requests[before + 1].Header("If-Modified-Since"));
        Assert.True(File.Exists(Path.Combine(catalogRoot, "progenitor-cache", "index.json")));
    }

    [Fact]
    public void Server_and_join_index_ages_and_the_remote_install_force()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), InstallIndexRefresher.RefreshInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), JoinPackageIndex.MaxAge);
        Assert.Equal(TimeSpan.FromMinutes(30), JoinPackageIndex.ServerMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(30), ServerPackageAvailability.MaxAge);
        Assert.Equal(TimeSpan.FromMinutes(30), RemoteInstallService.DefaultIndexMaxAge);
        Assert.Equal(TimeSpan.FromMinutes(30), new JoinPackageIndex(_ => Task.FromResult(new CatalogTextFetchResult(false, "x", null, 0, 0)), maxAge: JoinPackageIndex.ServerMaxAge).Age);
        Assert.Equal(JoinPackageIndex.MaxAge, new JoinPackageIndex(_ => Task.FromResult(new CatalogTextFetchResult(false, "x", null, 0, 0))).Age);

        string remote = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "RemoteInstallService.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(remote, "private async Task RunInstallAsync("), "EnsureIndexAsync(settings, ct, force: true)");
        string live = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinFlowLive.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(live, "internal static ServerPackageAvailability CreateServerPackages()"), "maxAge: JoinPackageIndex.ServerMaxAge");
        SourcePin.AssertUncommented(SourcePin.MethodBody(live, "internal static ServerPackageAvailability CreateServerPackages()"), "ConditionalIndexFetch.Over(");
        SourcePin.AssertUncommented(SourcePin.MethodBody(live, "internal static void Install()"), "ConditionalIndexFetch.Over(");
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(menu, "ConditionalIndexFetch.Over(fetcher, options.IndexUrl, new IndexDiskCache(root, options.IndexUrl), \"install tab\")");
        SourcePin.AssertUncommented(menu, "var transport = _httpLease ??= SharedCatalogHttp.Acquire()");
        // The check runs off the caller's thread (the cache read hashes about 1 MB).
        string cache = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "IndexCache.cs");
        SourcePin.AssertUncommented(cache, "Task.Run(() => FetchCoreAsync(cancellationToken, force), cancellationToken)");
    }

    [Fact]
    public async Task Shared_http_client_leases_count_once_each()
    {
        int start = SharedCatalogHttp.Leases;
        var a = SharedCatalogHttp.Acquire();
        var b = SharedCatalogHttp.Acquire();
        Assert.Equal(start + 2, SharedCatalogHttp.Leases);
        a.Dispose();
        a.Dispose();
        Assert.Equal(start + 1, SharedCatalogHttp.Leases);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => a.SendAsync(new CatalogTransportRequest(new Uri(IndexUrl)), CancellationToken.None));
        b.Dispose();
        Assert.Equal(start, SharedCatalogHttp.Leases);
    }

    // ---- READMEs: no changelog request, disk copies and remembered 404s ----------------------

    /// <summary>
    /// TSTRAFFIC1 follow-up: nothing displays the changelog, so a drawer open (or the on-screen prefetch, which
    /// calls the same load) asks for the README only. The README persists on disk by release stamp.
    /// </summary>
    [Fact]
    public async Task A_document_load_asks_for_the_readme_only_and_reads_it_from_disk_next_session()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var transport = new ScriptTransport(_ => Plain(200, "{\"markdown\":\"# Readme\"}", null));
        var fetcher = new CatalogFetcher(transport, clock: new TestClock());
        var disk = new ListingDiskCache(_root);

        var session1 = new ThunderstoreListingDocuments(disk, () => now);
        var set = await session1.LoadAsync("o", "n", "1.0.0", "2026-10-01T00:00:00Z", fetcher, CancellationToken.None);
        Assert.True(set.ReadmeOk);
        Assert.Equal("# Readme", set.ReadmeMarkdown);
        Assert.False(set.ChangelogOk);
        Assert.Equal(InstallReasons.ChangelogNotFetched, set.ChangelogError);
        var only = Assert.Single(transport.Requests);
        Assert.DoesNotContain("/changelog", only.Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("gzip", only.Header("Accept-Encoding"));
        Assert.True(disk.TryReadReadmeMarkdown("o", "n", "1.0.0", "2026-10-01T00:00:00Z", out string md));
        Assert.Equal("# Readme", md);

        // The next game start (a new instance, empty memory) reads the README from disk: no request.
        var session2 = new ThunderstoreListingDocuments(disk, () => now);
        var again = await session2.LoadAsync("o", "n", "1.0.0", "2026-10-01T00:00:00Z", fetcher, CancellationToken.None);
        Assert.Equal("# Readme", again.ReadmeMarkdown);
        Assert.Single(transport.Requests);
        Assert.Equal(0, session2.Requests);

        // A new release stamp misses and refetches.
        await new ThunderstoreListingDocuments(disk, () => now).LoadAsync("o", "n", "1.0.0", "2026-10-07T00:00:00Z", fetcher, CancellationToken.None);
        Assert.Equal(2, transport.Requests.Count);

        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "ThunderstoreListingDocuments.cs");
        Assert.DoesNotContain("TryBuildChangelogUrl", src, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_404_is_remembered_on_disk_for_a_day_or_until_a_new_release()
    {
        var now = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        var transport = new ScriptTransport(_ => Plain(404, "", null));
        var fetcher = new CatalogFetcher(transport, clock: new TestClock());
        var disk = new ListingDiskCache(_root);
        const string stamp = "2026-10-01T00:00:00Z";

        var first = await new ThunderstoreListingDocuments(disk, () => now).LoadAsync("o", "n", "1.0.0", stamp, fetcher, CancellationToken.None);
        Assert.False(first.ReadmeOk);
        Assert.Single(transport.Requests);
        Assert.True(disk.IsMarkedMissing("o", "n", "1.0.0", ThunderstoreAdapter.ReadmeDocument, stamp, now));
        Assert.True(File.Exists(disk.MissingPath("o", "n", "1.0.0", ThunderstoreAdapter.ReadmeDocument)));

        // Next session, same day: the 404 answers from disk. Nothing is asked.
        var second = await new ThunderstoreListingDocuments(disk, () => now.AddHours(23)).LoadAsync("o", "n", "1.0.0", stamp, fetcher, CancellationToken.None);
        Assert.False(second.ReadmeOk);
        Assert.Contains("404", second.ReadmeError, StringComparison.Ordinal);
        Assert.Single(transport.Requests);

        // After 24 hours it is asked again.
        await new ThunderstoreListingDocuments(disk, () => now.AddHours(25)).LoadAsync("o", "n", "1.0.0", stamp, fetcher, CancellationToken.None);
        Assert.Equal(2, transport.Requests.Count);

        // A new release is asked at once, whatever the age of the memory.
        await new ThunderstoreListingDocuments(disk, () => now.AddHours(25)).LoadAsync("o", "n", "1.0.0", "2026-10-08T00:00:00Z", fetcher, CancellationToken.None);
        Assert.Equal(3, transport.Requests.Count);

        // A transient failure (a 500) is not remembered.
        var flaky = new ScriptTransport(_ => Plain(500, "", null));
        var flakyFetcher = new CatalogFetcher(flaky, new CatalogFetchOptions { MaxRetries = 0 }, new TestClock());
        await new ThunderstoreListingDocuments(disk, () => now).LoadAsync("o", "m", "1.0.0", stamp, flakyFetcher, CancellationToken.None);
        Assert.False(disk.IsMarkedMissing("o", "m", "1.0.0", ThunderstoreAdapter.ReadmeDocument, stamp, now));
    }

    // ---- Helpers ------------------------------------------------------------------------------

    private static CatalogTransportResponse Plain(int status, string body, string? lastModified) =>
        new(status, new MemoryStream(Encoding.UTF8.GetBytes(body)), null, Encoding.UTF8.GetByteCount(body)) { LastModified = lastModified };

    private static CatalogTransportResponse Gzip(int status, string body, string? lastModified)
    {
        using var buffer = new MemoryStream();
        using (var gz = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] raw = Encoding.UTF8.GetBytes(body);
            gz.Write(raw, 0, raw.Length);
        }

        return Encoded(status, buffer.ToArray(), "gzip", lastModified);
    }

    private static byte[] Zlib(string body)
    {
        using var buffer = new MemoryStream();
        using (var z = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] raw = Encoding.UTF8.GetBytes(body);
            z.Write(raw, 0, raw.Length);
        }

        return buffer.ToArray();
    }

    private static CatalogTransportResponse Encoded(int status, byte[] wire, string encoding, string? lastModified) =>
        new(status, new MemoryStream(wire), null, wire.Length) { ContentEncoding = encoding, LastModified = lastModified };

    private sealed class ScriptTransport : ICatalogTransport
    {
        private readonly Func<CatalogTransportRequest, CatalogTransportResponse> _answer;
        private readonly object _gate = new();
        private readonly List<CatalogTransportRequest> _requests = new();

        public ScriptTransport(Func<CatalogTransportRequest, CatalogTransportResponse> answer) => _answer = answer;

        public List<CatalogTransportRequest> Requests
        {
            get { lock (_gate) return new List<CatalogTransportRequest>(_requests); }
        }

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            lock (_gate) _requests.Add(request);
            return Task.FromResult(_answer(request));
        }
    }

    private sealed class TestClock : ICatalogClock
    {
        public DateTime UtcNow { get; private set; } = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => UtcNow += by;
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
