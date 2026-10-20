using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.SelfUpdate;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Test helper: a signed release list (<see cref="SelfFeed"/>) for the self-update tests, signed with a key made for the
/// test run and trusted through <see cref="SelfUpdates.KeysProbe"/>.
/// </summary>
internal static class SelfFeedKit
{
    private static readonly Lazy<ModManifestKey> TestKey = new(() => ModManifestCrypto.Generate());
    private static readonly Lazy<ModManifestKey> OtherKey = new(() => ModManifestCrypto.Generate());

    public static ModManifestKey Key => TestKey.Value;
    public static ModManifestKey Other => OtherKey.Value;

    public static string AssetUrl(string version) => SelfFeed.AssetPrefix + "v" + version + "/Progenitor-" + version + ".zip";

    /// <summary>
    /// The list's text. A release without a sha256 or size takes those of <paramref name="package"/> (the package the test
    /// downloads), or a placeholder when there is none.
    /// </summary>
    public static string FeedJson(IEnumerable<SelfRelease> releases, byte[]? package = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", SelfFeed.Schema);
            w.WriteString("package", SelfFeed.Package);
            w.WriteStartArray("releases");
            foreach (var r in releases)
            {
                w.WriteStartObject();
                w.WriteString("version", r.Version);
                w.WriteString("channel", r.Channel);
                w.WriteString("url", r.DownloadUrl);
                w.WriteNumber("size", r.Size > 0 ? r.Size : package?.LongLength ?? 1);
                w.WriteString("sha256", r.Sha256.Length > 0 ? r.Sha256 : package != null ? SelfFiles.Sha256(package) : new string('0', 64));
                if (r.CoreSha.Length > 0) w.WriteString("coreSha256", r.CoreSha);
                if (r.BootstrapSha.Length > 0) w.WriteString("bootstrapSha256", r.BootstrapSha);
                if (r.BootstrapProtocol > 0) w.WriteNumber("bootstrapProtocol", r.BootstrapProtocol);
                if (r.CoreProtocol > 0) w.WriteNumber("coreProtocol", r.CoreProtocol);
                if (r.HandshakeProtocol > 0) w.WriteNumber("handshakeProtocol", r.HandshakeProtocol);
                if (!r.Active) w.WriteBoolean("active", false);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static string Signature(string feed, params ModManifestKey[] keys)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(feed);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", 1);
            w.WriteStartArray("signatures");
            foreach (var k in keys)
            {
                w.WriteStartObject();
                w.WriteString("key", k.Fingerprint);
                w.WriteString("signature", k.Sign(bytes));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Write the cached form under <paramref name="root"/>, point detection at it and trust the test key.</summary>
    public static string Write(string root, IEnumerable<SelfRelease> releases, byte[]? package)
    {
        string feed = FeedJson(releases, package);
        string path = Path.Combine(root, "progenitor-cache", "release-list.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SelfFeed.Envelope(Signature(feed, Key), feed), new UTF8Encoding(false));
        SelfUpdates.KeysProbe = () => new[] { Key.SpkiBase64 };
        SelfUpdates.IndexPathProbe = () => path;
        return path;
    }
}

/// <summary>
/// Progenitor's own release list (decided 2026-10-20: Progenitor ships from GitHub Releases), game-free: parsing, the
/// signature (a bad one, a wrong key, key rollover, a tampered sha256), the release tool's own output, version selection
/// from the list (release channel only, newest, a protocol change named by the list, never a downgrade), the download
/// checks, the fallback URL read only on failure, the cadence, the record a release zip carries and the one-click
/// adoption of a copy without one.
/// </summary>
[Collection("SelfUpdates")]
public sealed class SelfFeedTests : IDisposable
{
    private readonly string _root;

    public SelfFeedTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pfeed-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(_root);
        SelfUpdates.Reset();
    }

    public void Dispose()
    {
        SelfUpdates.Reset();
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static SelfRelease Rel(string version, string channel = "release") =>
        new() { Version = version, DownloadUrl = SelfFeedKit.AssetUrl(version), Channel = channel };

    private static string[] Trust(params ModManifestKey[] keys) => keys.Select(k => k.SpkiBase64).ToArray();

    // ---- parse and verify -------------------------------------------------------------------------------------

    [Fact]
    public void A_signed_list_parses_and_verifies()
    {
        string feed = SelfFeedKit.FeedJson(new[] { Rel("2026.11.3"), Rel("2026.10.162") });
        string sig = SelfFeedKit.Signature(feed, SelfFeedKit.Key);
        Assert.True(SelfFeed.Verify(Encoding.UTF8.GetBytes(feed), sig, Trust(SelfFeedKit.Key), out string error), error);

        var list = SelfFeed.ReadVerified(SelfFeed.Envelope(sig, feed), Trust(SelfFeedKit.Key), out error);
        Assert.Equal("", error);
        Assert.Equal(new[] { "2026.11.3", "2026.10.162" }, list.Select(r => r.Version));
        Assert.All(list, r => Assert.Equal(SelfFeed.Package, r.Package));
        Assert.All(list, r => Assert.True(SelfFeed.IsSha256(r.Sha256)));
    }

    [Fact]
    public void A_bad_signature_a_wrong_key_or_no_key_is_refused()
    {
        string feed = SelfFeedKit.FeedJson(new[] { Rel("2026.11.3") });
        byte[] bytes = Encoding.UTF8.GetBytes(feed);
        string sig = SelfFeedKit.Signature(feed, SelfFeedKit.Key);

        // Signed by a key this Progenitor does not build in.
        Assert.False(SelfFeed.Verify(bytes, sig, Trust(SelfFeedKit.Other), out string wrong));
        Assert.Contains("does not trust", wrong);

        // A signature whose bytes were changed.
        string bad = sig.Replace("\"signature\":\"", "\"signature\":\"00");
        bad = bad.Remove(bad.IndexOf("\"signature\":\"", StringComparison.Ordinal) + 13 + 128, 2);
        Assert.False(SelfFeed.Verify(bytes, bad, Trust(SelfFeedKit.Key), out string badWhy));
        Assert.Contains("does not verify", badWhy);

        // No key built in (a build before the maintainer made one): nothing verifies.
        Assert.False(SelfFeed.Verify(bytes, sig, Array.Empty<string>(), out string none));
        Assert.Contains("no release key", none);

        // Unsigned, and a signature file that is not one.
        Assert.False(SelfFeed.Verify(bytes, "", Trust(SelfFeedKit.Key), out _));
        Assert.False(SelfFeed.Verify(bytes, "{ torn", Trust(SelfFeedKit.Key), out _));

        // The cached form verifies again on every read: one that does not verify lists nothing.
        Assert.Empty(SelfFeed.ReadVerified(SelfFeed.Envelope(sig, feed), Trust(SelfFeedKit.Other), out string cached));
        Assert.NotEqual("", cached);
    }

    [Fact]
    public void A_tampered_sha256_breaks_the_signature()
    {
        string feed = SelfFeedKit.FeedJson(new[] { new SelfRelease { Version = "2026.11.3", DownloadUrl = SelfFeedKit.AssetUrl("2026.11.3"), Size = 10, Sha256 = new string('a', 64) } });
        string sig = SelfFeedKit.Signature(feed, SelfFeedKit.Key);
        string tampered = feed.Replace(new string('a', 64), new string('b', 64));
        Assert.NotEqual(feed, tampered);
        Assert.False(SelfFeed.Verify(Encoding.UTF8.GetBytes(tampered), sig, Trust(SelfFeedKit.Key), out _));
        Assert.Empty(SelfFeed.ReadVerified(SelfFeed.Envelope(sig, tampered), Trust(SelfFeedKit.Key), out _));
    }

    [Fact]
    public void Key_rollover_accepts_the_current_or_the_next_key()
    {
        string feed = SelfFeedKit.FeedJson(new[] { Rel("2026.11.3") });
        byte[] bytes = Encoding.UTF8.GetBytes(feed);
        var current = SelfFeedKit.Key;
        var next = SelfFeedKit.Other;

        // While the key rolls the list is signed with both; a Progenitor that trusts either accepts it.
        string both = SelfFeedKit.Signature(feed, current, next);
        Assert.True(SelfFeed.Verify(bytes, both, Trust(current), out _));
        Assert.True(SelfFeed.Verify(bytes, both, Trust(next), out _));

        // After the roll the list is signed with the new key only: a Progenitor that built in Current + Next accepts it, one
        // that only knows the old key does not.
        string onlyNext = SelfFeedKit.Signature(feed, next);
        Assert.True(SelfFeed.Verify(bytes, onlyNext, Trust(current, next), out _));
        Assert.False(SelfFeed.Verify(bytes, onlyNext, Trust(current), out _));

        // The built-in slots: an empty slot is no key.
        Assert.DoesNotContain("", SelfFeedKeys.Trusted());
    }

    [Fact]
    public void The_release_tool_s_signed_fixture_verifies_here()
    {
        // Written by tools/release-feed/lib.mjs (generateKey, feedBytes, sign) with a throwaway key: the Node signer and the
        // C# verifier agree on the bytes, the signature encoding and the key fingerprint.
        string dir = Path.Combine(RepoRoot(), "tests", "Progenitor.Tests", "Fixtures", "self-feed");
        byte[] feed = File.ReadAllBytes(Path.Combine(dir, "releases.json"));
        string sig = File.ReadAllText(Path.Combine(dir, "releases.json.sig"));
        string key = File.ReadAllText(Path.Combine(dir, "test-key.spki")).Trim();
        Assert.True(SelfFeed.Verify(feed, sig, new[] { key }, out string error), error);
        var list = SelfFeed.Parse(Encoding.UTF8.GetString(feed), out error);
        Assert.Equal("", error);
        var seed = Assert.Single(list, r => r.Version == "2026.10.162");
        Assert.Equal("40bb2aaada1e55643c1e1768129be661e24a2fb2d4deaae6dd2be55ec241b183", seed.Sha256);
        Assert.Equal(5, seed.BootstrapProtocol);
        Assert.Equal(2, seed.HandshakeProtocol);
        Assert.True(seed.ProtocolsKnown);
        // A changed byte breaks it.
        feed[feed.Length / 2] ^= 1;
        Assert.False(SelfFeed.Verify(feed, sig, new[] { key }, out _));
    }

    [Fact]
    public void The_published_seed_list_names_2026_10_162_as_released()
    {
        string path = Path.Combine(RepoRoot(), "docs", "updates", "releases.json");
        var list = SelfFeed.Parse(File.ReadAllText(path), out string error);
        Assert.Equal("", error);
        var seed = Assert.Single(list, r => r.Version == "2026.10.162");
        Assert.Equal("https://github.com/VRising-Modders/Progenitor/releases/download/v2026.10.162/Progenitor-2026.10.162.zip", seed.DownloadUrl);
        Assert.Equal(15304177, seed.Size);
        Assert.Equal("40bb2aaada1e55643c1e1768129be661e24a2fb2d4deaae6dd2be55ec241b183", seed.Sha256);
        Assert.Equal("1469dd7cd4c6c8f771c033d7ddcb5513cfe24ea080e5e4970ce8f648152ca247", seed.CoreSha);
        Assert.True(SelfFeed.IsAssetUrl(seed.DownloadUrl));
    }

    [Fact]
    public void Entries_without_a_sha256_url_or_size_and_another_schema_are_not_used()
    {
        string json = "{\"schema\":1,\"releases\":[{\"version\":\"1\",\"url\":\"u\",\"size\":1,\"sha256\":\"" + new string('a', 64) + "\"},"
            + "{\"version\":\"2\",\"url\":\"u\",\"size\":1},{\"version\":\"3\",\"url\":\"u\",\"sha256\":\"" + new string('a', 64) + "\"},"
            + "{\"version\":\"4\",\"size\":1,\"sha256\":\"" + new string('a', 64) + "\"}]}";
        Assert.Equal(new[] { "1" }, SelfFeed.Parse(json, out _).Select(r => r.Version));
        Assert.Empty(SelfFeed.Parse("{\"schema\":2,\"releases\":[]}", out string schema));
        Assert.Contains("schema 2", schema);
        Assert.Empty(SelfFeed.Parse("[1]", out _));
        Assert.Empty(SelfFeed.Parse("not json", out _));
    }

    // ---- version selection ------------------------------------------------------------------------------------

    private static SelfFacts Managed(string version) => new()
    {
        CoreVersion = version,
        BootstrapVersion = version,
        CoreSha = "c",
        BootstrapSha = "b",
        Record = new SelfInstallRecord { Package = SelfFeed.Package, Version = version, CoreSha = "c", BootstrapSha = "b" },
        Mode = UpdateMode.Automatic,
    };

    [Fact]
    public void Only_the_release_channel_is_offered_and_the_newest_wins()
    {
        var offer = SelfDetection.Detect(Managed("2026.10.162"), new[] { Rel("2026.11.1"), Rel("2026.11.5", "beta"), Rel("2026.11.3") });
        Assert.Equal("2026.11.3", offer.Release!.Version);
        Assert.True(offer.Auto);
    }

    [Fact]
    public void Never_a_downgrade_nor_the_running_release()
    {
        var offer = SelfDetection.Detect(Managed("2026.11.3"), new[] { Rel("2026.10.162"), Rel("2026.11.3") });
        Assert.False(offer.Available);
    }

    [Fact]
    public void A_protocol_change_the_list_names_is_notify_only_before_any_download()
    {
        // Running protocol 5 (Bootstrap) and the current handshake; the list says 2026.11.4 carries Bootstrap protocol 6.
        SelfUpdates.FactsProbe = () => Managed("2026.10.162");
        SelfUpdates.ProtocolProbe = () => 5;
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.RunOff = work => Task.FromResult(work());
        int downloads = 0;
        SelfUpdates.Download = (_, _, _) => { downloads++; return Task.FromResult<(byte[]?, string)>((null, "offline")); };
        SelfUpdates.WindowClosed = () => "";
        var breaking = Rel("2026.11.4");
        breaking.BootstrapProtocol = 6;
        breaking.CoreProtocol = 6;
        breaking.HandshakeProtocol = Handshake.ProtocolVersion;
        var same = Rel("2026.11.3");
        same.BootstrapProtocol = 5;
        same.CoreProtocol = 5;
        same.HandshakeProtocol = Handshake.ProtocolVersion;
        SelfFeedKit.Write(_root, new[] { breaking, same }, null);
        SelfUpdates.Detect("test");

        var offer = SelfUpdates.Offer;
        Assert.Equal("2026.11.4", offer.Release!.Version);
        Assert.True(offer.Breaking);
        Assert.Contains("Bootstrap protocol 5 → 6", offer.ProtocolChange);
        Assert.False(offer.Auto);
        Assert.True(offer.Clickable);
        Assert.Equal("nothing due", SelfUpdates.Tick());
        Assert.Equal(0, downloads);
    }

    [Fact]
    public void A_list_that_does_not_verify_offers_nothing()
    {
        SelfUpdates.FactsProbe = () => Managed("2026.10.162");
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.RunOff = work => Task.FromResult(work());
        SelfFeedKit.Write(_root, new[] { Rel("2026.11.3") }, null);
        SelfUpdates.KeysProbe = () => new[] { SelfFeedKit.Other.SpkiBase64 };
        var logs = new List<string>();
        SelfUpdates.Log = l => { lock (logs) logs.Add(l); };
        SelfUpdates.Detect("test");
        Assert.False(SelfUpdates.Offer.Available);
        Assert.Contains(logs, l => l.Contains("op=feed") && l.Contains("not used"));
    }

    // ---- download checks ------------------------------------------------------------------------------------

    [Fact]
    public void A_download_must_have_the_listed_size_and_sha256()
    {
        byte[] package = Encoding.UTF8.GetBytes("the package");
        var r = new SelfRelease { Version = "1", Size = package.Length, Sha256 = SelfFiles.Sha256(package) };
        Assert.Equal("", SelfFeed.CheckDownload(r, package));
        Assert.Contains("bytes, the release list says", SelfFeed.CheckDownload(r, Encoding.UTF8.GetBytes("the package!")));
        byte[] flipped = (byte[])package.Clone();
        flipped[0] ^= 1;
        Assert.Contains("sha256", SelfFeed.CheckDownload(r, flipped));

        var image = new SelfImage { CoreSha = "aa", BootstrapSha = "bb" };
        Assert.Equal("", SelfFeed.CheckImages(new SelfRelease { CoreSha = "aa", BootstrapSha = "bb" }, image));
        Assert.Contains("Core", SelfFeed.CheckImages(new SelfRelease { CoreSha = "ab" }, image));
        Assert.Contains("Bootstrap", SelfFeed.CheckImages(new SelfRelease { BootstrapSha = "ab" }, image));
    }

    [Fact]
    public void Only_GitHub_release_assets_are_downloaded_and_redirects_stay_on_GitHub_s_hosts()
    {
        Assert.True(SelfFeed.IsAssetUrl("https://github.com/VRising-Modders/Progenitor/releases/download/v2026.11.3/Progenitor-2026.11.3.zip"));
        Assert.False(SelfFeed.IsAssetUrl("https://github.com/Someone/Progenitor/releases/download/v1/Progenitor-1.zip"));
        Assert.False(SelfFeed.IsAssetUrl("http://github.com/VRising-Modders/Progenitor/releases/download/v1/x.zip"));
        Assert.False(SelfFeed.IsAssetUrl("https://raw.githubusercontent.com/VRising-Modders/Progenitor/main/x.zip"));
        Assert.False(SelfFeed.IsAssetUrl("https://api.github.com/repos/VRising-Modders/Progenitor/releases/assets/1"));
        Assert.False(SelfFeed.IsAssetUrl("https://github.com/VRising-Modders/Progenitor/releases/download/../../x"));
        Assert.True(SelfFeed.IsAllowedRedirect(new Uri("https://objects.githubusercontent.com/github-production-release-asset/1")));
        Assert.True(SelfFeed.IsAllowedRedirect(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1")));
        Assert.False(SelfFeed.IsAllowedRedirect(new Uri("https://example.com/x.zip")));
        Assert.False(SelfFeed.IsAllowedRedirect(new Uri("http://objects.githubusercontent.com/x")));
        // The dev stand-in's origin only while it is set.
        Assert.True(SelfFeed.IsAssetUrl("http://127.0.0.1:27450/progenitor/updates/x.zip", "http://127.0.0.1:27450"));
        Assert.False(SelfFeed.IsAssetUrl("http://127.0.0.1:27450/progenitor/updates/x.zip"));
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_refused_for_this_download()
    {
        var transport = new ScriptedTransport(req =>
            req.Uri.Host == "github.com"
                ? new CatalogTransportResponse(302, Stream.Null, location: new Uri("https://evil.example/x.zip"))
                : new CatalogTransportResponse(200, new MemoryStream(new byte[] { 1, 2, 3 })));
        var fetcher = new CatalogFetcher(transport, new CatalogFetchOptions { RedirectAllowed = u => SelfFeed.IsAllowedRedirect(u) });
        var r = await fetcher.FetchBytesAsync(SelfFeedKit.AssetUrl("2026.11.3"), 1024, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(InstallReasons.FetchRedirectHost, r.Error);

        var ok = new ScriptedTransport(req =>
            req.Uri.Host == "github.com"
                ? new CatalogTransportResponse(302, Stream.Null, location: new Uri("https://release-assets.githubusercontent.com/a"))
                : new CatalogTransportResponse(200, new MemoryStream(new byte[] { 1, 2, 3 })));
        var good = await new CatalogFetcher(ok, new CatalogFetchOptions { RedirectAllowed = u => SelfFeed.IsAllowedRedirect(u) })
            .FetchBytesAsync(SelfFeedKit.AssetUrl("2026.11.3"), 1024, CancellationToken.None);
        Assert.True(good.Ok, good.Error);
        Assert.Equal(3, good.Bytes!.Length);
    }

    // ---- the fallback and the cadence ---------------------------------------------------------------------------

    private sealed class FakeFeedServer
    {
        public readonly Dictionary<string, Func<IndexValidators, CatalogTextFetchResult>> Routes = new(StringComparer.Ordinal);
        public readonly List<string> Asked = new();

        public Task<CatalogTextFetchResult> Fetch(string url, IndexValidators v, long max, CancellationToken ct)
        {
            lock (Asked) Asked.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var f) ? f(v) : new CatalogTextFetchResult(false, "HTTP 404", null, 0, 404));
        }

        public void Serve(string url, string feed, string sig, string etag = "\"e1\"")
        {
            Routes[url] = v => v.ETag == etag
                ? new CatalogTextFetchResult(true, null, null, 0, 304) { NotModified = true }
                : new CatalogTextFetchResult(true, null, feed, feed.Length, 200) { ETag = etag };
            Routes[url + SelfFeed.SignatureSuffix] = _ => new CatalogTextFetchResult(true, null, sig, sig.Length, 200);
        }
    }

    private const string Primary = "https://primary.example/updates/releases.json";
    private const string Fallback = "https://fallback.example/gh/updates/releases.json";

    [Fact]
    public async Task The_fallback_is_read_only_when_the_primary_fails()
    {
        string feed = SelfFeedKit.FeedJson(new[] { Rel("2026.11.3") });
        string sig = SelfFeedKit.Signature(feed, SelfFeedKit.Key);
        var server = new FakeFeedServer();
        server.Serve(Primary, feed, sig);
        server.Serve(Fallback, feed, sig);
        var fetch = new SelfFeedFetch(server.Fetch, new IndexDiskCache(_root, Primary), () => Trust(SelfFeedKit.Key), Fallback);

        // The primary answers: the fallback is never asked; the cached form verifies.
        var first = await fetch.FetchAsync(CancellationToken.None);
        Assert.True(first.Ok, first.Error);
        Assert.DoesNotContain(server.Asked, u => u.StartsWith(Fallback, StringComparison.Ordinal));
        Assert.True(fetch.Cache.TryRead(0, out string cached, out var validators));
        Assert.Single(SelfFeed.ReadVerified(cached, Trust(SelfFeedKit.Key), out _));
        Assert.Equal("\"e1\"", validators.ETag);

        // Conditional: the next check is a 304 with no body and no signature request.
        server.Asked.Clear();
        var again = await fetch.FetchAsync(CancellationToken.None);
        Assert.True(again.NotModified);
        Assert.Equal(new[] { Primary }, server.Asked);

        // The primary is down: the fallback is read once, and cached without the fallback's validators.
        server.Asked.Clear();
        server.Routes[Primary] = _ => new CatalogTextFetchResult(false, "HTTP 503", null, 0, 503);
        var viaFallback = await fetch.FetchAsync(CancellationToken.None);
        Assert.True(viaFallback.Ok, viaFallback.Error);
        Assert.Equal(new[] { Primary, Fallback, Fallback + SelfFeed.SignatureSuffix }, server.Asked);
        Assert.True(fetch.Cache.TryRead(0, out _, out var after));
        Assert.True(after.IsEmpty);
    }

    [Fact]
    public async Task A_list_whose_signature_fails_is_never_cached_and_counts_as_a_failure()
    {
        string feed = SelfFeedKit.FeedJson(new[] { Rel("2026.11.3") });
        string wrong = SelfFeedKit.Signature(feed, SelfFeedKit.Other);
        var server = new FakeFeedServer();
        server.Serve(Primary, feed, wrong);
        server.Serve(Fallback, feed, wrong);
        var fetch = new SelfFeedFetch(server.Fetch, new IndexDiskCache(_root, Primary), () => Trust(SelfFeedKit.Key), Fallback);
        var r = await fetch.FetchAsync(CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("does not trust", r.Error);
        Assert.False(fetch.Cache.TryRead(0, out _, out _));
        Assert.Equal(1, fetch.Conditional.ConsecutiveFailures);
        // The fallback was tried because the primary's list did not verify.
        Assert.Contains(Fallback, server.Asked);
    }

    [Fact]
    public void The_cadence_follows_the_moments_the_old_detection_used()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        // Client: at start only when the cached list is older than 6 hours.
        Assert.Equal("start", SelfFeedCadence.Due(false, now, null, null, false, 6));
        Assert.Equal("start", SelfFeedCadence.Due(false, now, null, now.AddHours(-7), false, 6));
        Assert.Equal("", SelfFeedCadence.Due(false, now, null, now.AddHours(-1), false, 6));
        // Then when the mod index was read, at most every 10 minutes; no timer of its own.
        Assert.Equal("", SelfFeedCadence.Due(false, now, now.AddMinutes(-5), now.AddMinutes(-5), true, 6));
        Assert.Equal("index read", SelfFeedCadence.Due(false, now, now.AddMinutes(-11), now.AddMinutes(-11), true, 6));
        Assert.Equal("", SelfFeedCadence.Due(false, now, now.AddDays(-2), now.AddDays(-2), false, 6));
        // Server: its CheckHours cadence.
        Assert.Equal("cadence", SelfFeedCadence.Due(true, now, null, null, false, 6));
        Assert.Equal("", SelfFeedCadence.Due(true, now, now.AddHours(-1), now.AddHours(-1), false, 6));
        Assert.Equal("cadence", SelfFeedCadence.Due(true, now, now.AddHours(-7), now.AddHours(-7), false, 6));
    }

    [Fact]
    public void The_tick_asks_once_at_start_and_again_after_an_index_read()
    {
        int checks = 0;
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        string index = "a";
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.UtcNow = () => now;
        SelfUpdates.IndexReadProbe = () => index;
        SelfUpdates.CheckIndex = _ => { Interlocked.Increment(ref checks); return Task.FromResult("200"); };
        SelfUpdates.RunOff = work => Task.FromResult(work());
        Assert.Equal("start", SelfUpdates.FeedTick());
        Assert.Equal(1, checks);
        Assert.Equal("", SelfUpdates.FeedTick()); // asked already this session, and the index was not read since
        now = now.AddMinutes(20);
        Assert.Equal("", SelfUpdates.FeedTick()); // no index read: no request
        index = "b";
        Assert.Equal("index read", SelfUpdates.FeedTick());
        Assert.Equal(2, checks);
    }

    // ---- the record a release zip carries, and a copy without one ------------------------------------------------

    [Fact]
    public void A_release_zip_s_own_record_makes_its_first_start_managed_and_changed_files_only_notify()
    {
        var core = SelfUpdateTests.CoreImage(5, "2026.11.3.0", "zip");
        var boot = SelfUpdateTests.BootImage(5, "2026.11.3.0", "zip");
        // The record tools/package.ps1 (New-SelfInstallRecord) writes beside the files.
        string json = "{\n  \"schema\": 1,\n  \"package\": \"" + SelfFeed.Package + "\",\n  \"version\": \"2026.11.3\",\n  \"coreSha256\": \""
            + SelfFiles.Sha256(core) + "\",\n  \"bootstrapSha256\": \"" + SelfFiles.Sha256(boot) + "\",\n  \"installedUtc\": \"2026-11-02T10:00:00Z\",\n  \"source\": \"package\"\n}\n";
        var record = SelfInstallRecord.Parse(json)!;
        var facts = new SelfFacts { CoreVersion = "2026.11.3", BootstrapVersion = "2026.11.3", CoreSha = SelfFiles.Sha256(core), BootstrapSha = SelfFiles.Sha256(boot), Record = record, Mode = UpdateMode.Automatic };
        Assert.Equal(SelfInstallKind.Managed, SelfDetection.KindOf(facts));
        var offer = SelfDetection.Detect(facts, new[] { Rel("2026.11.5") });
        Assert.True(offer.Auto);

        // A changed Core: the record no longer names it, notify only (and no click).
        facts.CoreSha = SelfFiles.Sha256(SelfUpdateTests.CoreImage(5, "2026.11.3.0", "edited"));
        var changed = SelfDetection.Detect(facts, new[] { Rel("2026.11.5") });
        Assert.Equal(SelfInstallKind.Changed, changed.Kind);
        Assert.False(changed.Auto);
        Assert.False(changed.Clickable);

        // A mod-manager copy (no record) only notifies.
        facts.Record = null;
        facts.ModManager = true;
        var manager = SelfDetection.Detect(facts, new[] { Rel("2026.11.5") });
        Assert.Equal(SelfInstallKind.ModManager, manager.Kind);
        Assert.False(manager.Clickable);
    }

    [Fact]
    public void A_copy_without_a_record_is_adopted_by_one_click_and_managed_from_then_on()
    {
        // 2026.10.162's zip carried no record: such a copy notifies; the Install tab's Update installs the release through
        // the normal path and its healthy start writes the record, so the next release applies itself.
        var core = SelfUpdateTests.CoreImage(5, "2026.10.162.0", "old");
        var boot = SelfUpdateTests.BootImage(5, "2026.10.162.0", "old");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        Directory.CreateDirectory(Path.Combine(_root, "progenitor-core"));
        File.WriteAllBytes(SelfPaths.CoreDll(_root), core);
        File.WriteAllBytes(Path.Combine(_root, "plugins", SelfPaths.BootstrapFile), boot);
        var facts = new SelfFacts { CoreVersion = "2026.10.162", BootstrapVersion = "2026.10.162", CoreSha = SelfFiles.Sha256(core), BootstrapSha = SelfFiles.Sha256(boot), Mode = UpdateMode.Automatic };
        double now = 1000;
        var swaps = new List<string>();
        var newCore = SelfUpdateTests.CoreImage(5, "2026.11.3.0", "new");
        byte[] package = SelfUpdateTests.Zip(newCore, boot, "2026.11.3");
        SelfUpdates.RootProbe = () => _root;
        SelfUpdates.FactsProbe = () => new SelfFacts { CoreVersion = facts.CoreVersion, BootstrapVersion = facts.BootstrapVersion, CoreSha = facts.CoreSha, BootstrapSha = facts.BootstrapSha, Record = facts.Record, Mode = facts.Mode };
        SelfUpdates.Download = (_, _, _) => Task.FromResult<(byte[]?, string)>((package, ""));
        SelfUpdates.RequestSwap = reason => { swaps.Add(reason); return "scheduled gen=2"; };
        SelfUpdates.ReloadStatus = () => (false, "ok", swaps.LastOrDefault() ?? "");
        SelfUpdates.WindowClosed = () => "";
        SelfUpdates.ProtocolProbe = () => 5;
        SelfUpdates.BootstrapPathProbe = () => Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        SelfUpdates.RunOff = work => Task.FromResult(work());
        SelfUpdates.Clock = () => now;
        SelfFeedKit.Write(_root, new[] { Rel("2026.11.3") }, package);
        SelfUpdates.Detect("test");

        var offer = SelfUpdates.Offer;
        Assert.Equal(SelfInstallKind.Hand, offer.Kind);
        Assert.False(offer.Auto);
        Assert.True(offer.Clickable);
        Assert.Equal(SelfDetection.WhyHand, offer.NotAutoReason);
        Assert.Equal(1, SelfUpdates.WaitingCount);
        Assert.Equal("nothing due", SelfUpdates.Tick());

        // One click: downloaded, checked against the list, swapped.
        Assert.True(SelfUpdates.Begin(offer, "install-tab", byClick: true, out string started), started);
        SpinWait.SpinUntil(() => !SelfUpdates.Busy, 10_000);
        Assert.Equal(new[] { "self-update 2026.11.3" }, swaps);
        File.WriteAllBytes(SelfPaths.CoreDll(_root), File.ReadAllBytes(SelfPaths.CoreNext(_root)));
        File.Delete(SelfPaths.CoreNext(_root));
        facts.CoreSha = SelfFiles.Sha256(newCore);
        facts.CoreVersion = "2026.11.3";
        SelfUpdates.Invalidate();
        SelfUpdates.Tick(); // the swap is confirmed
        now += SelfUpdates.HealthySeconds + 1;
        SelfUpdates.Tick(); // healthy: the record is written

        var written = SelfFiles.ReadRecord(_root)!;
        Assert.Equal("2026.11.3", written.Version);
        Assert.Equal(SelfFeed.RecordSource, written.Source);
        Assert.True(written.Matches(SelfFiles.Sha256(newCore), SelfFiles.Sha256(boot)));

        // Managed now: the next release applies itself (Automatic is the client default).
        facts.Record = written;
        SelfUpdates.Invalidate();
        SelfFeedKit.Write(_root, new[] { Rel("2026.11.3"), Rel("2026.11.5") }, null);
        SelfUpdates.Detect("test");
        Assert.Equal(SelfInstallKind.Managed, SelfUpdates.Offer.Kind);
        Assert.True(SelfUpdates.Offer.Auto);
    }

    [Fact]
    public void A_record_from_an_older_package_id_still_names_the_same_Progenitor()
    {
        Assert.True(SelfFeed.SamePackage(SelfFeed.Package, "Progenitor-Progenitor"));
        Assert.True(SelfFeed.SamePackage("", SelfFeed.Package));
        Assert.False(SelfFeed.SamePackage(SelfFeed.Package, "Someone-ProgenitorExtras"));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    private sealed class ScriptedTransport : ICatalogTransport
    {
        private readonly Func<CatalogTransportRequest, CatalogTransportResponse> _answer;
        public ScriptedTransport(Func<CatalogTransportRequest, CatalogTransportResponse> answer) => _answer = answer;
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) => Task.FromResult(_answer(request));
    }
}
