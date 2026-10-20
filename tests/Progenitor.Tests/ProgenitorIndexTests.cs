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
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Test helper: Progenitor Index lists built, signed with a key made for the run, and held by a fixed source.</summary>
internal static class IndexKit
{
    private static readonly Lazy<ModManifestKey> TestKey = new(() => ModManifestCrypto.Generate());
    private static readonly Lazy<ModManifestKey> OtherKey = new(() => ModManifestCrypto.Generate());

    public static ModManifestKey Key => TestKey.Value;
    public static ModManifestKey Other => OtherKey.Value;

    public static string Sha(string seed) => Progenitor.SelfUpdate.SelfFiles.Sha256(Encoding.UTF8.GetBytes(seed));

    public static IndexPackage Package(
        string owner, string name, string version,
        string source = IndexPackage.SourceGitHubRelease,
        string? url = null, string? sha256 = null, long size = 100, bool hot = false,
        IReadOnlyList<IndexDependency>? deps = null, string progenitor = "*", string sides = "")
    {
        string repo = "https://github.com/" + owner + "/" + name;
        return new IndexPackage
        {
            Id = owner + "-" + name + "-" + version,
            Owner = owner,
            Name = name,
            Version = version,
            Description = name + " from the Index",
            Repo = repo,
            Source = source,
            Package = url ?? (source == IndexPackage.SourceThunderstore
                ? "https://thunderstore.io/package/download/" + owner + "/" + name + "/" + version + "/"
                : repo + "/releases/download/" + version + "/" + owner + "-" + name + "-" + version + ".zip"),
            Sha256 = sha256 ?? Sha(owner + name + version),
            Size = size,
            Dependencies = deps ?? Array.Empty<IndexDependency>(),
            IconUrl = "icons/" + owner + "-" + name + "-" + version + ".png",
            HasProgenitorJson = hot,
            DeclaresHotLoadable = hot,
            Progenitor = progenitor,
            Sides = sides,
            Readme = "# " + name,
        };
    }

    public static IndexDocument Doc(IEnumerable<IndexPackage> packages, IEnumerable<IndexRevocation>? revoked = null, DateTime? generated = null) =>
        new()
        {
            Format = 1,
            Generated = generated ?? new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
            Packages = packages.ToList(),
            Revoked = (revoked ?? Array.Empty<IndexRevocation>()).ToList(),
        };

    public static ProgenitorIndexSource Source(params IndexPackage[] packages) => ProgenitorIndexSource.Fixed(Doc(packages));

    /// <summary>
    /// The hot-load tests' listings as Progenitor Index entries (only an Index install may hot load since INDEX1): every
    /// listing of a Thunderstore v1 index body, at its listed version and download, from a Thunderstore source.
    /// </summary>
    public static ProgenitorIndexSource Mirror(string thunderstoreJson)
    {
        Assert.True(ThunderstoreAdapter.TryLoadIndex(thunderstoreJson, "s", out var entries, out string error), error);
        var packages = new List<IndexPackage>();
        foreach (var e in entries)
            packages.Add(Package(e.Owner!, e.PackageName!, e.Manifest.Version!, source: IndexPackage.SourceThunderstore,
                url: e.DownloadUrl, size: e.ListingFileSize ?? 1));
        return ProgenitorIndexSource.Fixed(Doc(packages));
    }

    /// <summary>The list's JSON as build-index.mjs writes it (the fields Core reads, plus one it does not know).</summary>
    public static string Json(IEnumerable<IndexPackage> packages, string generated = "2026-10-20T09:00:00.000Z", IEnumerable<IndexRevocation>? revoked = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("name", "Progenitor Index");
            w.WriteString("generated", generated);
            w.WriteString("added_later", "ignored");
            w.WriteStartArray("packages");
            foreach (var p in packages)
            {
                w.WriteStartObject();
                w.WriteString("id", p.Id);
                w.WriteString("owner", p.Owner);
                w.WriteString("name", p.Name);
                w.WriteString("version", p.Version);
                w.WriteString("description", p.Description);
                w.WriteString("repo", p.Repo);
                w.WriteString("source", p.Source);
                w.WriteString("package", p.Package);
                w.WriteString("sha256", p.Sha256);
                w.WriteNumber("size", p.Size);
                w.WriteBoolean("imported", false);
                w.WriteBoolean("confirmed", true);
                w.WriteStartArray("dependencies");
                foreach (var d in p.Dependencies)
                {
                    w.WriteStartObject();
                    w.WriteString("id", d.Id);
                    w.WriteString("from", d.From);
                    w.WriteString("package", d.Package);
                    w.WriteString("sha256", d.Sha256);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartObject("icon");
                w.WriteString("url", p.IconUrl);
                w.WriteString("sha256", Sha("icon"));
                w.WriteEndObject();
                if (p.Sides.Length > 0) w.WriteString("sides", p.Sides);
                else w.WriteNull("sides");
                w.WriteString("progenitor", p.Progenitor);
                if (p.HasProgenitorJson)
                {
                    w.WriteStartObject("progenitor_json");
                    w.WriteBoolean("hotLoadable", p.DeclaresHotLoadable);
                    w.WriteEndObject();
                }

                w.WriteString("readme", p.Readme);
                w.WriteString("a_new_field", "ignored");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("revoked");
            foreach (var r in revoked ?? Array.Empty<IndexRevocation>())
            {
                w.WriteStartObject();
                w.WriteString("owner", r.Owner);
                w.WriteString("name", r.Name);
                w.WriteString("version", r.Version);
                w.WriteString("reason", r.Reason);
                w.WriteString("severity", r.Mode.ToString().ToLowerInvariant());
                w.WriteString("date", "2026-10-20");
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static string Sign(string json, ModManifestKey key) => key.Sign(Encoding.UTF8.GetBytes(json)) + "\n";
}

/// <summary>
/// INDEX1, game-free: the Progenitor Index's signature (good, bad, a wrong key, key rollover, replay), its parsing (unknown
/// fields ignored), the download rules (hosts, redirects, size and sha256), dependency resolution (the Index first, then
/// Thunderstore), the hot-load gate by source, takedowns, and the Index row winning over a Thunderstore row of the same mod.
/// </summary>
[Collection("ProgenitorIndex")]
public sealed class ProgenitorIndexTests : IDisposable
{
    private readonly string _root;

    public ProgenitorIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pindex-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(_root);
        IndexCatalog.RunningProgenitorVersion = () => "2026.10.162";
    }

    public void Dispose()
    {
        IndexCatalog.RunningProgenitorVersion = () => null;
        IndexRevocationSource.IndexProbe = null;
        IndexRevocationSource.InstalledProbe = null;
        IndexRevocationSource.StateProbe = null;
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static string[] Trust(params ModManifestKey[] keys) => keys.Select(k => k.SpkiBase64).ToArray();

    // ---- signature -------------------------------------------------------------------------------------------

    [Fact]
    public void A_signed_list_verifies_and_a_changed_byte_a_bad_signature_or_a_wrong_key_does_not()
    {
        string json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        string sig = IndexKit.Sign(json, IndexKit.Key);
        Assert.True(ProgenitorIndex.Verify(bytes, sig, Trust(IndexKit.Key), out string error), error);

        var tampered = (byte[])bytes.Clone();
        tampered[tampered.Length / 2] ^= 1;
        Assert.False(ProgenitorIndex.Verify(tampered, sig, Trust(IndexKit.Key), out _));
        Assert.False(ProgenitorIndex.Verify(bytes, new string('0', 128), Trust(IndexKit.Key), out _));
        Assert.False(ProgenitorIndex.Verify(bytes, "not hex", Trust(IndexKit.Key), out error));
        Assert.Contains("128 hex", error);
        Assert.False(ProgenitorIndex.Verify(bytes, "", Trust(IndexKit.Key), out error));
        Assert.Contains("not signed", error);
        Assert.False(ProgenitorIndex.Verify(bytes, sig, Trust(IndexKit.Other), out error));
        Assert.Contains("does not verify", error);
        Assert.False(ProgenitorIndex.Verify(bytes, sig, Array.Empty<string>(), out _));
    }

    [Fact]
    public void Key_rollover_accepts_the_current_or_the_next_key_and_the_built_in_slots_hold_the_published_key()
    {
        string json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") });
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        // Signed with the next key while Core trusts both: accepted. Once only the old key is trusted: refused.
        Assert.True(ProgenitorIndex.Verify(bytes, IndexKit.Sign(json, IndexKit.Other), Trust(IndexKit.Key, IndexKit.Other), out _));
        Assert.True(ProgenitorIndex.Verify(bytes, IndexKit.Sign(json, IndexKit.Key), Trust(IndexKit.Key, IndexKit.Other), out _));
        Assert.False(ProgenitorIndex.Verify(bytes, IndexKit.Sign(json, IndexKit.Other), Trust(IndexKit.Key), out _));

        var trusted = ProgenitorIndexKeys.Trusted();
        Assert.DoesNotContain("", trusted);
        Assert.Single(trusted);
        Assert.True(ModManifestCrypto.TryImportSpki(trusted[0], out _, out string fp, out _));
        Assert.Equal("eee964f144cb8d67", fp);
    }

    [Fact]
    public void A_list_signed_by_the_Index_node_tools_verifies_here()
    {
        // Written by the Index repository's tools/keygen.mjs (a throwaway key in a temp folder) and tools/sign.mjs: the
        // Node signer and the C# verifier agree on the bytes, the signature encoding and the key.
        string dir = Path.Combine(RepoRoot(), "tests", "Progenitor.Tests", "Fixtures", "progenitor-index");
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, "index.json"));
        string sig = File.ReadAllText(Path.Combine(dir, "index.json.sig"));
        string key = File.ReadAllText(Path.Combine(dir, "test-key.spki")).Trim();
        Assert.True(ProgenitorIndex.Verify(bytes, sig, new[] { key }, out string error), error);
        Assert.False(ProgenitorIndex.Verify(bytes, sig, ProgenitorIndexKeys.Trusted(), out _));

        var doc = ProgenitorIndex.Parse(Encoding.UTF8.GetString(bytes), out error);
        Assert.Equal("", error);
        Assert.Equal(new DateTime(2026, 10, 20, 9, 6, 5, DateTimeKind.Utc), doc.Generated);
        Assert.Equal(2, doc.Packages.Count);
        var hot = doc.Find("ExampleTeam-HotDemo-0.2.0")!;
        Assert.True(hot.DeclaresHotLoadable);
        Assert.Equal(PackageSide.Client, hot.Side);
        Assert.Equal(">=2026.10.162", hot.Progenitor);
        Assert.Equal(57068, hot.Size);
        var dep = Assert.Single(hot.Dependencies);
        Assert.Equal("BepInEx-BepInExPack_V_Rising", dep.Key);
        Assert.Equal("1.0.0", dep.Version);
        // Imported: kept in the list (takedowns name it), never found as an entry Core installs from.
        Assert.Null(doc.Find("Someone-Ordinary-1.4.2"));
        var ordinary = doc.Packages.Single(p => p.Id == "Someone-Ordinary-1.4.2");
        Assert.True(ordinary.Imported);
        Assert.Equal(IndexPackage.SourceThunderstore, ordinary.Source);
        Assert.False(ordinary.HasProgenitorJson);
        var revoked = Assert.Single(doc.Revoked);
        Assert.Equal(IndexRevocationMode.Warning, revoked.Mode);

        bytes[bytes.Length / 2] ^= 1;
        Assert.False(ProgenitorIndex.Verify(bytes, sig, new[] { key }, out _));
    }

    // ---- parsing ---------------------------------------------------------------------------------------------

    [Fact]
    public void Parsing_ignores_unknown_fields_and_leaves_out_a_package_without_a_pin()
    {
        var ok = IndexKit.Package("A", "Mod", "1.0.0", hot: true, sides: "both");
        var noSha = IndexKit.Package("A", "NoSha", "1.0.0", sha256: "x");
        var badSource = IndexKit.Package("A", "Elsewhere", "1.0.0", source: "nexus");
        var doc = ProgenitorIndex.Parse(IndexKit.Json(new[] { ok, noSha, badSource }), out string error);
        Assert.Equal("", error);
        var p = Assert.Single(doc.Packages);
        Assert.Equal("A-Mod", p.Key);
        Assert.True(p.DeclaresHotLoadable);
        Assert.Equal(PackageSide.Both, p.Side);
        Assert.Equal("icons/A-Mod-1.0.0.png", p.IconUrl);

        Assert.Same(IndexDocument.Empty, ProgenitorIndex.Parse("{\"format\":2,\"packages\":[]}", out error));
        Assert.Contains("format 2", error);
        ProgenitorIndex.Parse("[]", out error);
        Assert.Contains("not an object", error);
        ProgenitorIndex.Parse("{", out error);
        Assert.Contains("could not be read", error);
    }

    [Fact]
    public void Sides_fall_back_to_the_copied_progenitor_json_side_and_an_unknown_severity_is_the_strictest()
    {
        const string json = "{\"format\":1,\"generated\":\"2026-10-20T00:00:00Z\",\"packages\":[{\"owner\":\"A\",\"name\":\"S\",\"version\":\"1.0.0\","
            + "\"source\":\"github-release\",\"repo\":\"https://github.com/A/S\",\"package\":\"https://github.com/A/S/releases/download/1/S.zip\","
            + "\"sha256\":\"" + "ab" + "\",\"size\":1}],\"revoked\":[{\"owner\":\"A\",\"name\":\"S\",\"version\":\"*\",\"reason\":\"r\",\"severity\":\"quarantine\",\"date\":\"2026-10-20\"}]}";
        string sha = new('a', 64);
        var doc = ProgenitorIndex.Parse(json.Replace("\"ab\"", "\"" + sha + "\"").Replace("\"size\":1}", "\"size\":1,\"sides\":null,\"progenitor_json\":{\"side\":\"server\"}}"), out string error);
        Assert.Equal("", error);
        Assert.Equal(PackageSide.Server, Assert.Single(doc.Packages).Side);
        Assert.Equal(IndexRevocationMode.Disable, Assert.Single(doc.Revoked).Mode);
        Assert.Equal("*", doc.Revoked[0].Version);
    }

    // ---- fetching and the cache ------------------------------------------------------------------------------

    private sealed class Served
    {
        public string Json = "";
        public string Sig = "";
        public int ListStatus = 200;
        public string? ETag = "\"1\"";
        public readonly List<string> Asked = new();
    }

    private ProgenitorIndexFetch Fetch(Served served, params ModManifestKey[] trust) =>
        new((url, validators, max, ct) =>
        {
            served.Asked.Add(url);
            if (url.EndsWith(".sig", StringComparison.Ordinal))
                return Task.FromResult(new CatalogTextFetchResult(true, null, served.Sig, served.Sig.Length, 200));
            if (validators.ETag != null && validators.ETag == served.ETag)
                return Task.FromResult(new CatalogTextFetchResult(true, null, null, 0, 304) { NotModified = true });
            if (served.ListStatus != 200)
                return Task.FromResult(new CatalogTextFetchResult(false, "HTTP " + served.ListStatus, null, 0, served.ListStatus));
            return Task.FromResult(new CatalogTextFetchResult(true, null, served.Json, served.Json.Length, 200) { ETag = served.ETag });
        }, new IndexDiskCache(_root, ProgenitorIndex.PrimaryUrl), () => Trust(trust));

    [Fact]
    public async Task Only_a_verified_list_is_cached_a_failure_keeps_the_last_good_one_and_only_the_Pages_URL_is_asked()
    {
        var served = new Served { Json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") }) };
        served.Sig = IndexKit.Sign(served.Json, IndexKit.Key);
        var source = new ProgenitorIndexSource(Fetch(served, IndexKit.Key));
        var doc = await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal("A-Mod", Assert.Single(doc.Packages).Key);
        Assert.Equal(new[] { ProgenitorIndex.PrimaryUrl, ProgenitorIndex.PrimaryUrl + ".sig" }, served.Asked);
        Assert.All(served.Asked, u => Assert.DoesNotContain("raw.githubusercontent", u));

        // A 304 reuses the verified cache.
        served.Asked.Clear();
        doc = await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Single(doc.Packages);
        Assert.Equal(new[] { ProgenitorIndex.PrimaryUrl }, served.Asked);

        // A newer list with a bad signature is never cached: the last good one stays, on disk too.
        served.ETag = "\"2\"";
        served.Json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "2.0.0") }, "2026-10-21T00:00:00Z");
        served.Sig = IndexKit.Sign(served.Json, IndexKit.Other);
        doc = await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal("1.0.0", Assert.Single(doc.Packages).Version);
        Assert.Contains("does not verify", source.LastError);
        var fresh = new ProgenitorIndexSource(Fetch(served, IndexKit.Key));
        fresh.EnsureCache();
        Assert.Equal("1.0.0", Assert.Single(fresh.Current.Packages).Version);

        // A server error: the same.
        served.ListStatus = 500;
        doc = await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal("1.0.0", Assert.Single(doc.Packages).Version);
    }

    [Fact]
    public async Task A_verified_list_signed_before_the_cached_one_is_refused_as_a_replay()
    {
        var served = new Served { Json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "2.0.0") }, "2026-10-21T00:00:00Z") };
        served.Sig = IndexKit.Sign(served.Json, IndexKit.Key);
        var source = new ProgenitorIndexSource(Fetch(served, IndexKit.Key));
        await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);

        served.ETag = "\"old\"";
        served.Json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") }, "2026-10-20T00:00:00Z");
        served.Sig = IndexKit.Sign(served.Json, IndexKit.Key);
        var doc = await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal("2.0.0", Assert.Single(doc.Packages).Version);
        Assert.Contains("older than the one already cached", source.LastError);
    }

    [Fact]
    public async Task The_cadence_skips_a_check_younger_than_the_age_and_force_checks_now()
    {
        var served = new Served { Json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") }) };
        served.Sig = IndexKit.Sign(served.Json, IndexKit.Key);
        var source = new ProgenitorIndexSource(Fetch(served, IndexKit.Key));
        await source.RefreshAsync(ProgenitorIndexSource.InstallTabAge, CancellationToken.None);
        int asked = served.Asked.Count;
        await source.RefreshAsync(ProgenitorIndexSource.InstallTabAge, CancellationToken.None);
        Assert.Equal(asked, served.Asked.Count);
        await source.RefreshAsync(ProgenitorIndexSource.InstallTabAge, CancellationToken.None, force: true);
        Assert.True(served.Asked.Count > asked);

        var off = new ProgenitorIndexSource(Fetch(served, IndexKit.Key), enabled: () => false);
        Assert.Empty((await off.RefreshAsync(TimeSpan.Zero, CancellationToken.None)).Packages);
    }

    // ---- download rules --------------------------------------------------------------------------------------

    [Fact]
    public void A_package_URL_must_be_a_release_asset_of_its_own_repo_or_a_Thunderstore_download()
    {
        const string repo = "https://github.com/A/Mod";
        Assert.True(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, repo + "/releases/download/1.0.0/Mod.zip"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, "https://github.com/B/Other/releases/download/1.0.0/Mod.zip"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, "https://raw.githubusercontent.com/A/Mod/main/Mod.zip"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, "http://github.com/A/Mod/releases/download/1.0.0/Mod.zip"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, repo + "/releases/download/../../x.zip"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, "https://api.github.com/repos/A/Mod/releases"));
        Assert.True(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceThunderstore, null, "https://thunderstore.io/package/download/A/Mod/1.0.0/"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceThunderstore, null, "https://evil.example/package/download/A/Mod/1.0.0/"));
        Assert.False(ProgenitorIndex.IsAllowedPackageUrl("nexus", repo, repo + "/releases/download/1.0.0/Mod.zip"));
        // The dev Index's own origin, while it is in effect.
        Assert.True(ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceGitHubRelease, repo, "http://127.0.0.1:27460/p/Mod.zip", "http://127.0.0.1:27460"));
    }

    [Fact]
    public void Redirects_go_only_to_that_source_s_download_hosts()
    {
        string gh = IndexPackage.SourceGitHubRelease, ts = IndexPackage.SourceThunderstore;
        Assert.True(ProgenitorIndex.IsAllowedRedirect(gh, new Uri("https://objects.githubusercontent.com/x")));
        Assert.True(ProgenitorIndex.IsAllowedRedirect(gh, new Uri("https://release-assets.githubusercontent.com/x")));
        Assert.False(ProgenitorIndex.IsAllowedRedirect(gh, new Uri("https://raw.githubusercontent.com/x")));
        Assert.False(ProgenitorIndex.IsAllowedRedirect(gh, new Uri("https://evil.example/x")));
        Assert.False(ProgenitorIndex.IsAllowedRedirect(gh, new Uri("http://objects.githubusercontent.com/x")));
        Assert.False(ProgenitorIndex.IsAllowedRedirect(gh, new Uri("https://ccdn.thunderstore.io/x")));
        Assert.True(ProgenitorIndex.IsAllowedRedirect(ts, new Uri("https://ccdn.thunderstore.io/live/repository/packages/x.zip")));
        Assert.False(ProgenitorIndex.IsAllowedRedirect(ts, new Uri("https://objects.githubusercontent.com/x")));
        Assert.False(ProgenitorIndex.IsAllowedRedirect(ts, new Uri("https://thunderstore.io.evil.example/x")));
    }

    [Fact]
    public void A_download_must_have_the_signed_size_and_sha256()
    {
        string sha = IndexKit.Sha("bytes");
        Assert.Equal("", ProgenitorIndex.CheckDownload(10, sha, 10, sha));
        Assert.Equal("", ProgenitorIndex.CheckDownload(0, sha, 77, sha.ToUpperInvariant()));
        Assert.Contains("bytes; the Progenitor Index lists 10", ProgenitorIndex.CheckDownload(10, sha, 11, sha));
        Assert.Contains("sha256 is not the one", ProgenitorIndex.CheckDownload(10, sha, 10, IndexKit.Sha("other")));
        Assert.Contains("lists no sha256", ProgenitorIndex.CheckDownload(10, "", 10, sha));
    }

    private sealed class ZipTransport : ICatalogTransport
    {
        public readonly List<Uri> Asked = new();
        public byte[] Body = Encoding.UTF8.GetBytes("not really a zip");
        public Uri? RedirectTo;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request.Uri);
            if (RedirectTo != null && request.Uri != RedirectTo)
                return Task.FromResult(new CatalogTransportResponse(302, Stream.Null, RedirectTo));
            return Task.FromResult(new CatalogTransportResponse(200, new MemoryStream(Body), contentLength: Body.Length));
        }
    }

    [Fact]
    public async Task An_Index_download_is_refused_on_a_sha256_or_size_mismatch_and_a_foreign_redirect_and_nothing_is_kept()
    {
        var transport = new ZipTransport();
        var package = IndexKit.Package("A", "Mod", "1.0.0", size: transport.Body.Length);
        var catalog = new InstallCatalogService(_root, transport, registerForBridge: false) { IndexSource = IndexKit.Source(package) };
        await catalog.RefreshIndexAsync(CancellationToken.None);
        Assert.True(catalog.TryGetEntry("A-Mod", out var entry) && entry!.FromIndex);

        var r = await catalog.DownloadAndInspectAsync("A-Mod", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("sha256 is not the one the Progenitor Index lists", r.Error);
        Assert.Empty(Directory.Exists(catalog.StagingTempDirectory) ? Directory.GetFiles(catalog.StagingTempDirectory) : Array.Empty<string>());

        transport.RedirectTo = new Uri("https://evil.example/Mod.zip");
        r = await catalog.DownloadAndInspectAsync("A-Mod", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(InstallReasons.FetchRedirectHost, r.Error);
        Assert.DoesNotContain(transport.Asked, u => u.Host == "evil.example");
    }

    [Fact]
    public async Task An_Index_download_of_the_signed_bytes_passes_the_checks_and_reaches_inspection()
    {
        var transport = new ZipTransport();
        transport.RedirectTo = new Uri("https://objects.githubusercontent.com/asset");
        var package = IndexKit.Package("A", "Mod", "1.0.0", size: transport.Body.Length,
            sha256: Progenitor.SelfUpdate.SelfFiles.Sha256(transport.Body));
        var catalog = new InstallCatalogService(_root, transport, registerForBridge: false) { IndexSource = IndexKit.Source(package) };
        await catalog.RefreshIndexAsync(CancellationToken.None);
        var r = await catalog.DownloadAndInspectAsync("A-Mod", CancellationToken.None);
        // The bytes are what the list signed; the inspection then refuses them as not a zip (the checks before it passed).
        Assert.False(r.Ok);
        Assert.DoesNotContain("Progenitor Index", r.Error ?? "");
        Assert.NotNull(r.Inspection);
        Assert.Contains(transport.Asked, u => u.Host == "objects.githubusercontent.com");
    }

    [Fact]
    public void A_pin_whose_URL_is_not_on_its_source_s_host_is_refused_before_any_request()
    {
        var bad = IndexKit.Package("A", "Mod", "1.0.0", url: "https://example.org/Mod.zip");
        var entry = IndexCatalog.EntryOf(bad, null, "s");
        Assert.Contains("not a GitHub release of https://github.com/A/Mod", InstallCatalogService.PinRefusal(entry));
        var ts = IndexCatalog.EntryOf(IndexKit.Package("A", "T", "1.0.0", source: IndexPackage.SourceThunderstore, url: "https://example.org/x"), null, "s");
        Assert.Contains("not a Thunderstore link", InstallCatalogService.PinRefusal(ts));
        Assert.Null(InstallCatalogService.PinRefusal(IndexCatalog.EntryOf(IndexKit.Package("A", "Mod", "1.0.0"), null, "s")));
    }

    // ---- dependencies ----------------------------------------------------------------------------------------

    [Fact]
    public void A_dependency_resolves_from_the_Index_first_then_Thunderstore_each_pinned_by_its_sha256()
    {
        var lib = IndexKit.Package("B", "Lib", "2.0.0");
        var indexDep = new IndexDependency { Id = "B-Lib-2.0.0", From = IndexDependency.FromIndex, Package = lib.Package, Sha256 = lib.Sha256 };
        var tsDep = new IndexDependency { Id = "C-Ts-1.1.0", From = IndexDependency.FromThunderstore, Package = "https://thunderstore.io/package/download/C/Ts/1.1.0/", Sha256 = IndexKit.Sha("ts") };
        var doc = IndexKit.Doc(new[] { lib });

        var fromIndex = IndexCatalog.PinDependency(indexDep, doc, null, "s", out string refusal);
        Assert.NotNull(fromIndex);
        Assert.True(fromIndex!.FromIndex);
        Assert.Equal(lib.Sha256, fromIndex.Pin!.Sha256);

        // Thunderstore: that exact version's download, pinned by the recorded sha256 (not the listing's newest).
        var fromTs = IndexCatalog.PinDependency(tsDep, doc, null, "s", out refusal);
        Assert.NotNull(fromTs);
        Assert.False(fromTs!.FromIndex);
        Assert.Equal(InstallReasons.SourceThunderstore, fromTs.SourceId);
        Assert.Equal("1.1.0", fromTs.Manifest.Version);
        Assert.Equal(tsDep.Package, fromTs.DownloadUrl);
        Assert.Equal(tsDep.Sha256, fromTs.Pin!.Sha256);
        Assert.Null(InstallCatalogService.PinRefusal(fromTs));

        // A dependency the Index lists wins even when the row says Thunderstore.
        var listedAnyway = new IndexDependency { Id = "B-Lib-2.0.0", From = IndexDependency.FromThunderstore, Package = "https://thunderstore.io/package/download/B/Lib/2.0.0/", Sha256 = lib.Sha256 };
        Assert.True(IndexCatalog.PinDependency(listedAnyway, doc, null, "s", out _)!.FromIndex);

        // A row naming no native entry (an imported one) is a direct pin of its own file: a GitHub release asset here, never
        // an Index listing (no hot load, no Index origin).
        var direct = IndexCatalog.PinDependency(new IndexDependency { Id = "B-Lib-9.0.0", From = "index", Package = lib.Package, Sha256 = lib.Sha256 }, doc, null, "s", out refusal);
        Assert.NotNull(direct);
        Assert.False(direct!.FromIndex);
        Assert.Equal(IndexCatalog.PinnedReleaseSourceId, direct.SourceId);
        Assert.Equal("https://github.com/B/Lib", direct.Pin!.Repo);
        Assert.Null(InstallCatalogService.PinRefusal(direct));

        // Refusals: a changed sha256, no sha256, a link that is neither Thunderstore's nor a release, a takedown.
        Assert.Null(IndexCatalog.PinDependency(new IndexDependency { Id = "B-Lib-2.0.0", From = "index", Package = lib.Package, Sha256 = IndexKit.Sha("x") }, doc, null, "s", out refusal));
        Assert.Contains("not the listed file", refusal);
        Assert.Null(IndexCatalog.PinDependency(new IndexDependency { Id = "C-Ts-1.1.0", From = "thunderstore", Package = tsDep.Package, Sha256 = "" }, doc, null, "s", out refusal));
        Assert.Contains("no pinned sha256", refusal);
        Assert.Null(IndexCatalog.PinDependency(new IndexDependency { Id = "C-Ts-1.1.0", From = "thunderstore", Package = "https://example.org/x", Sha256 = tsDep.Sha256 }, doc, null, "s", out refusal));
        Assert.Contains("not a Thunderstore link", refusal);
        var down = IndexKit.Doc(new[] { lib }, new[] { new IndexRevocation { Owner = "B", Name = "Lib", Version = "*", Reason = "bad", Mode = IndexRevocationMode.Disable } });
        Assert.Null(IndexCatalog.PinDependency(indexDep, down, null, "s", out refusal));
        Assert.Contains("taken down", refusal);
    }

    // ---- the hot-load gate -----------------------------------------------------------------------------------

    [Fact]
    public void Only_an_Index_listing_or_the_local_test_source_may_hot_load()
    {
        var index = IndexCatalog.EntryOf(IndexKit.Package("A", "Mod", "1.0.0", hot: true), null, "s");
        Assert.True(IndexCatalog.MayHotLoad(index, localTestListing: false));
        var catalog = new InstallCatalogService(_root, new ZipTransport(), registerForBridge: false);
        Assert.True(catalog.LoadIndexFromJson(ThunderstoreListing("A", "Mod", "1.0.0"), "s").Ok);
        Assert.True(catalog.TryGetEntry("A-Mod", out var thunderstore));
        Assert.False(IndexCatalog.MayHotLoad(thunderstore, localTestListing: false));
        Assert.False(catalog.HotSourceAllowed(thunderstore));
        Assert.True(IndexCatalog.MayHotLoad(thunderstore, localTestListing: true));
        Assert.Equal(ProgenitorIndex.LedgerOrigin, IndexCatalog.LedgerOriginOf(index));
        Assert.Equal("", IndexCatalog.LedgerOriginOf(thunderstore));
    }

    [Fact]
    public void The_ledger_records_the_origin_and_keeps_it_through_a_rewrite()
    {
        string game = Path.GetDirectoryName(_root)!;
        string file = Path.Combine(_root, "progenitor-pending", "a.mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        Assert.True(ServerInstall.TryAppendInstalledFile(_root, file, "sha", 1, "package:A-Mod@1.0.0", ServerInstallLedger.CategoryPending,
            pluginId: "a.mod", origin: ProgenitorIndex.LedgerOrigin));
        var row = Assert.Single(ServerInstallLedger.TryRead(game)!.Files);
        Assert.True(row.FromIndex);
        Assert.Contains("\"origin\": \"index\"", File.ReadAllText(ServerInstallLedger.PathFor(game)));
        Assert.True(row.WithRoute("remote").FromIndex);

        // A Thunderstore row writes no origin, so its bytes are what earlier builds wrote.
        Assert.True(ServerInstall.TryAppendInstalledFile(_root, file + "2", "sha", 1, "package:B-Ts@1.0.0", ServerInstallLedger.CategoryPending));
        Assert.DoesNotContain("\"origin\": \"\"", File.ReadAllText(ServerInstallLedger.PathFor(game)));
        var catalog = new InstallCatalogService(_root, new ZipTransport(), registerForBridge: false);
        Assert.True(catalog.LedgerSaysIndex(file));
        Assert.False(catalog.LedgerSaysIndex(file + "2"));
        Assert.False(catalog.TrustsHotUnload(IndexCatalog.EntryOf(IndexKit.Package("B", "Ts", "1.0.0"), null, "s"), file + "2", out string why));
        Assert.Equal(InstallReasons.HotUnloadIndexOnly, why);
    }

    // ---- takedowns -------------------------------------------------------------------------------------------

    [Fact]
    public void Takedowns_hide_disable_or_warn_and_flag_installed_copies()
    {
        var hidden = IndexKit.Package("A", "Hidden", "1.0.0");
        var disabled = IndexKit.Package("A", "Disabled", "1.0.0");
        var warned = IndexKit.Package("A", "Warned", "1.0.0");
        var older = IndexKit.Package("A", "Partial", "1.0.0");
        var newer = IndexKit.Package("A", "Partial", "2.0.0");
        var doc = IndexKit.Doc(new[] { hidden, disabled, warned, older, newer }, new[]
        {
            new IndexRevocation { Owner = "A", Name = "Hidden", Version = "1.0.0", Reason = "gone", Mode = IndexRevocationMode.Hidden },
            new IndexRevocation { Owner = "A", Name = "Disabled", Version = "*", Reason = "malware", Mode = IndexRevocationMode.Disable },
            new IndexRevocation { Owner = "A", Name = "Warned", Version = "1.0.0", Reason = "crashes on load", Mode = IndexRevocationMode.Warning },
            new IndexRevocation { Owner = "A", Name = "Partial", Version = "2.0.0", Reason = "bad build", Mode = IndexRevocationMode.Disable },
        });

        var rows = IndexCatalog.ToEntries(doc, "s");
        Assert.DoesNotContain(rows, r => r.Key == "A-Hidden");
        Assert.DoesNotContain(rows, r => r.Key == "A-Disabled");
        var w = Assert.Single(rows, r => r.Key == "A-Warned");
        Assert.Equal(IndexRevocationMode.Warning, w.Pin!.Revocation!.Mode);
        Assert.EndsWith("Progenitor Index · " + InstallModsCopy.IndexWarningMark, InstallModsPane.NameCellText(w));
        // The newest version that is not taken down is the row.
        Assert.Equal("1.0.0", Assert.Single(rows, r => r.Key == "A-Partial").Manifest.Version);

        IndexRevocationSource.IndexProbe = () => doc;
        IndexRevocationSource.InstalledProbe = () => new[]
        {
            new IndexRevocationSource.Installed("a.disabled", "A-Disabled", "1.0.0", FromIndex: true),
            new IndexRevocationSource.Installed("a.warned", "A-Warned", "1.0.0", FromIndex: true),
            new IndexRevocationSource.Installed("a.hidden", "A-Hidden", "1.0.0", FromIndex: true),
            new IndexRevocationSource.Installed("a.partial", "A-Partial", "1.0.0", FromIndex: true),
        };
        var problems = new IndexRevocationSource().Current().ToList();
        Assert.Equal(2, problems.Count);
        var down = Assert.Single(problems, p => p.ModId == "a.disabled");
        Assert.Contains("took this version down: malware", down.Sentence);
        Assert.Equal("Uninstall it.", down.WhatToDo);
        Assert.Contains("crashes on load", Assert.Single(problems, p => p.ModId == "a.warned").Sentence);
    }

    // ---- Index over Thunderstore -----------------------------------------------------------------------------

    private static string ThunderstoreListing(string owner, string name, string version) =>
        "[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + owner + "/" + name + "/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "-" + version + "\",\"description\":\"from Thunderstore\",\"version_number\":\"" + version + "\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/" + owner + "/" + name + "/" + version + "/\",\"file_size\":10,\"is_active\":true}]},"
        + "{\"name\":\"Only\",\"full_name\":\"T-Only\",\"owner\":\"T\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/T/Only/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"Only\",\"full_name\":\"T-Only-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/T/Only/1.0.0/\",\"file_size\":10,\"is_active\":true}]}]";

    [Fact]
    public void A_mod_in_the_Index_and_on_Thunderstore_is_its_Index_row_and_the_rest_stay()
    {
        var catalog = new InstallCatalogService(_root, new ZipTransport(), registerForBridge: false)
        {
            IndexSource = IndexKit.Source(IndexKit.Package("A", "Mod", "1.2.0"), IndexKit.Package("I", "OnlyIndex", "0.1.0")),
        };
        Assert.True(catalog.LoadIndexFromJson(ThunderstoreListing("A", "Mod", "1.3.0"), "s").Ok);
        Assert.True(catalog.TryGetEntry("A-Mod", out var mod));
        Assert.True(mod!.FromIndex);
        Assert.Equal("1.2.0", mod.Manifest.Version);
        Assert.Equal(ProgenitorIndex.SourceLabel, mod.SourceLabel);
        Assert.True(catalog.TryGetEntry("T-Only", out var only) && !only!.FromIndex);
        Assert.True(catalog.TryGetEntry("I-OnlyIndex", out var indexOnly) && indexOnly!.FromIndex);
        Assert.Equal(1, catalog.IndexShadowed);
        Assert.Equal(3, catalog.Search(null).Count);
        Assert.EndsWith(" · Progenitor Index", InstallModsPane.NameCellText(mod));
        Assert.DoesNotContain("Progenitor Index", InstallModsPane.NameCellText(only));
        Assert.Equal("https://vrising-modders.github.io/ProgenitorIndex/icons/A-Mod-1.2.0.png",
            ThunderstoreAdapter.TryBuildIconUrl(mod, out string icon) ? icon : "");
        Assert.True(catalog.TryGetCachedListingDocuments("A-Mod", out var docs) && docs!.ReadmeMarkdown == "# Mod");

        // The Index off: Thunderstore only.
        catalog.IndexSource = null;
        catalog.ReloadLocalIndex();
        Assert.True(catalog.TryGetEntry("A-Mod", out mod) && !mod!.FromIndex);
    }

    [Fact]
    public void The_join_screens_resolve_a_package_from_the_Index_before_Thunderstore()
    {
        var rows = IndexCatalog.ToEntries(IndexKit.Doc(new[] { IndexKit.Package("A", "Mod", "1.0.0"), IndexKit.Package("A", "Mod", "1.1.0") }), "s", allVersions: true);
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A-Mod"] = "1.0.0", ["T-Only"] = "1.0.0" };
        var into = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        PackageVersions.AddFromIndex(rows, wanted, into, null);
        Assert.True(ThunderstoreVersions.TryLoad(ThunderstoreListing("A", "Mod", "1.0.0"), "s", wanted, into, local: false, out _));
        Assert.True(into["A-Mod"].FromIndex);
        Assert.Equal("1.0.0", into["A-Mod"].Matched);
        Assert.True(into["A-Mod"].Entry!.FromIndex);
        Assert.Equal(new[] { "1.0.0", "1.1.0" }, into["A-Mod"].Active);
        Assert.False(into["T-Only"].FromIndex);
    }

    [Fact]
    public void A_mod_that_needs_a_newer_Progenitor_is_refused_and_labeled()
    {
        var needs = IndexCatalog.EntryOf(IndexKit.Package("A", "New", "1.0.0", progenitor: ">=2026.11.1"), null, "s");
        Assert.True(IndexCatalog.ProgenitorTooOld(needs, out string words));
        Assert.Equal("Progenitor >=2026.11.1 (this is 2026.10.162)", words);
        Assert.StartsWith("this mod needs Progenitor >=2026.11.1", InstallCatalogService.PinRefusal(needs));
        Assert.EndsWith(InstallModsCopy.NeedsNewerProgenitorMark, InstallModsPane.NameCellText(needs));
        Assert.False(IndexCatalog.ProgenitorTooOld(IndexCatalog.EntryOf(IndexKit.Package("A", "Ok", "1.0.0", progenitor: ">=2026.10.1"), null, "s"), out _));
        Assert.False(IndexCatalog.ProgenitorTooOld(IndexCatalog.EntryOf(IndexKit.Package("A", "Any", "1.0.0"), null, "s"), out _));
        IndexCatalog.RunningProgenitorVersion = () => null;
        Assert.False(IndexCatalog.ProgenitorTooOld(needs, out _));
    }

    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("repository root not found");
    }
}
