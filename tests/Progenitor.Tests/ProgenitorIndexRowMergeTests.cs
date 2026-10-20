using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-20 (headless client, dev Index on 127.0.0.1:27460): the Index's one native entry was merged into the Install
/// catalog (310 rows for 309 Thunderstore listings) but never shown, because the default "Current update only" filters
/// (Advanced and Simple) judged it by Thunderstore update categories, which an Index row has none of. Reproduced here end
/// to end from a signed dev-URL list, with the adoption and merge log lines the next live test reads.
/// </summary>
[Collection("ProgenitorIndex")]
public sealed class ProgenitorIndexRowMergeTests : IDisposable
{
    private const string DevUrl = "http://127.0.0.1:27460/index.json";
    private readonly string _root;
    private readonly List<string> _log = new();

    public ProgenitorIndexRowMergeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pindex4-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(_root);
        IndexCatalog.RunningProgenitorVersion = () => "2026.10.172";
        InstallLog.TestSink = (line, _) => { lock (_log) _log.Add(line); };
    }

    public void Dispose()
    {
        InstallLog.TestSink = null;
        IndexCatalog.RunningProgenitorVersion = () => null;
        ProgenitorIndexConfig.TestOverride = null;
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private const string Tag = "Oakveil Update";

    private static string Thunderstore() =>
        "[{\"name\":\"Tagged\",\"full_name\":\"T-Tagged\",\"owner\":\"T\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/T/Tagged/\",\"is_deprecated\":false,"
        + "\"categories\":[\"Mods\",\"" + Tag + "\"],\"versions\":[{\"name\":\"Tagged\",\"full_name\":\"T-Tagged-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\","
        + "\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/T/Tagged/1.0.0/\",\"file_size\":10,\"is_active\":true}]},"
        + "{\"name\":\"Old\",\"full_name\":\"T-Old\",\"owner\":\"T\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/T/Old/\",\"is_deprecated\":false,"
        + "\"categories\":[\"Mods\"],\"versions\":[{\"name\":\"Old\",\"full_name\":\"T-Old-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\","
        + "\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/T/Old/1.0.0/\",\"file_size\":10,\"is_active\":true}]}]";

    [Fact]
    public async Task A_dev_source_native_entry_is_merged_into_the_Install_catalog_and_passes_the_current_update_filters()
    {
        // The live setup: a loopback dev URL in a developer tree, its own throwaway key, the package on the dev origin.
        ProgenitorIndexConfig.TestOverride = (true, DevUrl, IndexKit.Other.SpkiBase64, true);
        var package = IndexKit.Package("ProgenitorTest", "HotDemo", "0.1.0", url: "http://127.0.0.1:27460/HotDemo-0.1.0.zip",
            hot: true, sides: "both", progenitor: ">=2026.10.172");
        string json = IndexKit.Json(new[] { package }, "2026-10-20T17:07:00Z");
        string sig = IndexKit.Sign(json, IndexKit.Other);
        var fetch = new ProgenitorIndexFetch((url, v, max, ct) => Task.FromResult(url.EndsWith(".sig", StringComparison.Ordinal)
                ? new CatalogTextFetchResult(true, null, sig, sig.Length, 200)
                : new CatalogTextFetchResult(true, null, json, json.Length, 200)),
            new IndexDiskCache(_root, DevUrl), ProgenitorIndexConfig.KeysFor(DevUrl));
        var source = new ProgenitorIndexSource(fetch, () => true, pagesRoot: ProgenitorIndexConfig.PagesRoot);

        var catalog = new InstallCatalogService(_root, new NoTransport(), registerForBridge: false)
        {
            IndexSource = source,
            IndexRefreshAge = ProgenitorIndexSource.InstallTabAge,
        };
        // The Install tab's order: the Index check (BeforeLoad), then the Thunderstore body.
        await catalog.RefreshIndexAsync(CancellationToken.None);
        Assert.True(catalog.LoadIndexFromJson(Thunderstore(), "s").Ok);

        Assert.Equal(3, catalog.Search(null).Count);
        Assert.True(catalog.TryGetEntry("ProgenitorTest-HotDemo", out var row) && row!.FromIndex);
        Assert.Null(InstallCatalogService.PinRefusal(row));
        Assert.Equal("http://127.0.0.1:27460/icons/ProgenitorTest-HotDemo-0.1.0.png", row.Pin!.IconUrl);

        // The defect: the default filters (Current update only on) must show the Index row; the untagged Thunderstore row
        // is still filtered out and the tagged one stays.
        var defaults = new InstallSearchFilters();
        Assert.True(defaults.CurrentUpdateOnly);
        Assert.True(defaults.Matches(row, Tag));
        Assert.True(CatalogUpdateTags.IsTagged(row, Tag));
        Assert.Null(CatalogUpdateTags.StaleWarning(row, Tag));
        Assert.True(catalog.TryGetEntry("T-Old", out var old) && !defaults.Matches(old!, Tag));
        Assert.True(catalog.TryGetEntry("T-Tagged", out var tagged) && defaults.Matches(tagged!, Tag));

        // The evidence lines.
        lock (_log)
        {
            Assert.Contains(_log, l => l.StartsWith("Progenitor Index: 1 listed (native), 0 imported ignored, 0 taken down", StringComparison.Ordinal)
                && l.Contains(DevUrl, StringComparison.Ordinal));
            Assert.Contains(_log, l => l.StartsWith("Install: merged 1 Progenitor Index row(s) into the catalog", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_adoption_line_names_what_is_left_out_and_why()
    {
        const string sha = "0000000000000000000000000000000000000000000000000000000000000000";
        string json = "{\"format\":1,\"generated\":\"2026-10-20T00:00:00Z\",\"packages\":["
            + "{\"owner\":\"A\",\"name\":\"NoSha\",\"version\":\"1.0.0\",\"source\":\"github-release\",\"package\":\"https://github.com/A/NoSha/releases/download/1/a.zip\",\"size\":1},"
            + "{\"owner\":\"A\",\"name\":\"Ok\",\"version\":\"1.0.0\",\"source\":\"github-release\",\"package\":\"https://github.com/A/Ok/releases/download/1/a.zip\",\"size\":1,\"sha256\":\"" + sha + "\",\"imported\":false},"
            + "{\"owner\":\"A\",\"name\":\"Copied\",\"version\":\"1.0.0\",\"source\":\"thunderstore\",\"package\":\"https://thunderstore.io/package/download/A/Copied/1.0.0/\",\"size\":1,\"sha256\":\"" + sha + "\",\"imported\":true},"
            + "{\"owner\":\"A\",\"name\":\"Down\",\"version\":\"1.0.0\",\"source\":\"github-release\",\"package\":\"https://github.com/A/Down/releases/download/1/a.zip\",\"size\":1,\"sha256\":\"" + sha + "\",\"imported\":false}],"
            + "\"revoked\":[{\"owner\":\"A\",\"name\":\"Down\",\"version\":\"*\",\"reason\":\"r\",\"severity\":\"disable\",\"date\":\"2026-10-20\"}]}";
        var doc = ProgenitorIndex.Parse(json, out string error);
        Assert.Equal("", error);
        string line = doc.Summary();
        Assert.StartsWith("Progenitor Index: 1 listed (native), 1 imported ignored, 1 taken down; left out: ", line);
        Assert.Contains("A-NoSha-1.0.0: no sha256", line);
        Assert.Contains("A-Down-1.0.0: taken down (disable)", line);
    }
}
