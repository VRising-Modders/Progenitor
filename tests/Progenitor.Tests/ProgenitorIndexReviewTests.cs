using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// INDEX1 review fixes, game-free: imported entries are ignored, join versions fall through to Thunderstore, takedowns reach
/// Thunderstore rows only when they mean them, future-dated lists, pinned closures, takedown wording, the dev URL and key, an
/// unreadable range, Thunderstore off in updates, list change by hash.
/// </summary>
[Collection("ProgenitorIndex")]
public sealed class ProgenitorIndexReviewTests : IDisposable
{
    private readonly string _root;

    public ProgenitorIndexReviewTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pindex3-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(_root);
        IndexCatalog.RunningProgenitorVersion = () => "2026.10.162";
    }

    public void Dispose()
    {
        IndexCatalog.RunningProgenitorVersion = () => null;
        ProgenitorIndexConfig.TestOverride = null;
        IndexRevocationSource.IndexProbe = null;
        IndexRevocationSource.InstalledProbe = null;
        IndexRevocationSource.StateProbe = null;
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static IndexPackage Imported(string owner, string name, string version) =>
        Clone(IndexKit.Package(owner, name, version), imported: true);

    private static IndexPackage Clone(IndexPackage p, bool imported) => new()
    {
        Id = p.Id, Owner = p.Owner, Name = p.Name, Version = p.Version, Description = p.Description, Repo = p.Repo,
        Source = p.Source, Package = p.Package, Sha256 = p.Sha256, Size = p.Size, Dependencies = p.Dependencies,
        IconUrl = p.IconUrl, Progenitor = p.Progenitor, Readme = p.Readme, Imported = imported,
    };

    private static string Listing(params (string Owner, string Name, string[] Versions)[] packages)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < packages.Length; i++)
        {
            var (o, n, vs) = packages[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"name\":\"" + n + "\",\"full_name\":\"" + o + "-" + n + "\",\"owner\":\"" + o + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + o + "/" + n + "/\",\"is_deprecated\":false,\"versions\":[");
            for (int j = 0; j < vs.Length; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append("{\"name\":\"" + n + "\",\"full_name\":\"" + o + "-" + n + "-" + vs[j] + "\",\"description\":\"d\",\"version_number\":\"" + vs[j]
                    + "\",\"dependencies\":[],\"download_url\":\"https://thunderstore.io/package/download/" + o + "/" + n + "/" + vs[j] + "/\",\"file_size\":10,\"is_active\":true}");
            }

            sb.Append("]}");
        }

        return sb.Append(']').ToString();
    }

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private InstallCatalogService Catalog(IndexDocument doc, string thunderstore)
    {
        var catalog = new InstallCatalogService(_root, new NoTransport(), registerForBridge: false) { IndexSource = ProgenitorIndexSource.Fixed(doc) };
        Assert.True(catalog.LoadIndexFromJson(thunderstore, "s").Ok);
        return catalog;
    }

    private static IndexRevocation Takedown(string owner, string name, string version, IndexRevocationMode mode, string source = "", string repo = "") =>
        new() { Owner = owner, Name = name, Version = version, Reason = "bad", Mode = mode, Source = source, Repo = repo };

    // ---- imported entries are not an install source ----------------------------------------------------------

    [Fact]
    public void Imported_entries_are_never_listed_never_shadow_Thunderstore_and_pin_nothing()
    {
        var doc = IndexKit.Doc(new[] { Imported("T", "Mod", "9.0.0"), IndexKit.Package("N", "Native", "1.0.0") });
        Assert.Equal(new[] { "N-Native" }, IndexCatalog.ToEntries(doc, "s").Select(r => r.Key));
        Assert.Null(doc.Find("T-Mod-9.0.0"));
        Assert.Null(doc.NativeForKey("T-Mod"));

        var catalog = Catalog(doc, Listing(("T", "Mod", new[] { "1.0.0" })));
        Assert.True(catalog.TryGetEntry("T-Mod", out var mod) && !mod!.FromIndex);
        Assert.Equal("1.0.0", mod.Manifest.Version);
        Assert.Equal(0, catalog.IndexShadowed);

        // No information line on a Thunderstore install of an imported mod.
        IndexRevocationSource.IndexProbe = () => doc;
        IndexRevocationSource.InstalledProbe = () => new[] { new IndexRevocationSource.Installed("t.mod", "T-Mod", "1.0.0") };
        Assert.Empty(new IndexRevocationSource().Current());

        // The join screens do not take it from the Index.
        var into = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        PackageVersions.AddFromIndex(IndexCatalog.ToEntries(doc, "s", allVersions: true),
            new Dictionary<string, string> { ["T-Mod"] = "9.0.0" }, into, null);
        Assert.Empty(into);
    }

    // ---- H1: the join takes the Index only when it has the version ---------------------------------------------

    [Fact]
    public void A_version_only_Thunderstore_still_serves_falls_through_to_Thunderstore()
    {
        var rows = IndexCatalog.ToEntries(IndexKit.Doc(new[] { IndexKit.Package("A", "Mod", "2.0.0") }), "s", allVersions: true);
        var json = Listing(("A", "Mod", new[] { "2.0.0", "1.0.0" }));

        // The server pins 1.0.0, which only Thunderstore keeps.
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["A-Mod"] = "1.0.0" };
        var into = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        PackageVersions.AddFromIndex(rows, wanted, into, null);
        Assert.Empty(into);
        Assert.True(ThunderstoreVersions.TryLoad(json, "s", wanted, into, local: false, out _));
        Assert.False(into["A-Mod"].FromIndex);
        Assert.Equal("1.0.0", into["A-Mod"].Matched);
        Assert.Equal(InstallReasons.SourceThunderstore, into["A-Mod"].Entry!.SourceId);
        Assert.Equal("", IndexCatalog.LedgerOriginOf(into["A-Mod"].Entry));

        // 2.0.0, or "any version": the Index answers.
        foreach (string v in new[] { "2.0.0", "" })
        {
            var again = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
            PackageVersions.AddFromIndex(rows, new Dictionary<string, string> { ["A-Mod"] = v }, again, null);
            Assert.True(again["A-Mod"].FromIndex);
            Assert.Equal("2.0.0", again["A-Mod"].Matched);
        }

        // Same minor: 2.0.1 asked, the Index's 2.0.0 is accepted under Minor, refused under Exact.
        var minor = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        PackageVersions.AddFromIndex(rows, new Dictionary<string, string> { ["A-Mod"] = "2.0.1" }, minor,
            new Dictionary<string, Progenitor.Plugins.NetworkVersionRule> { ["A-Mod"] = Progenitor.Plugins.NetworkVersionRule.Minor });
        Assert.True(minor["A-Mod"].FromIndex);
        var exact = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        PackageVersions.AddFromIndex(rows, new Dictionary<string, string> { ["A-Mod"] = "2.0.1" }, exact, null);
        Assert.Empty(exact);
    }

    [Fact]
    public async Task The_join_index_and_the_server_availability_read_fall_through_the_same_way()
    {
        var rows = IndexCatalog.ToEntries(IndexKit.Doc(new[] { IndexKit.Package("A", "Mod", "2.0.0") }), "s", allVersions: true);
        string json = Listing(("A", "Mod", new[] { "2.0.0", "1.0.0" }));
        var index = new JoinPackageIndex(ct => Task.FromResult(new CatalogTextFetchResult(true, null, json, json.Length, 200)))
        {
            IndexRows = ct => Task.FromResult(rows),
        };
        var old = await index.LoadAsync(new Dictionary<string, string> { ["A-Mod"] = "1.0.0" }, CancellationToken.None);
        Assert.False(old.Versions["A-Mod"].FromIndex);
        var current = await index.LoadAsync(new Dictionary<string, string> { ["A-Mod"] = "2.0.0" }, CancellationToken.None);
        Assert.True(current.Versions["A-Mod"].FromIndex);
    }

    // ---- M1 / L7: takedowns reach Thunderstore rows only when they mean them -----------------------------------

    [Fact]
    public void A_takedown_of_a_Thunderstore_mod_removes_its_rows_and_versions_from_the_tab_and_the_join()
    {
        var doc = IndexKit.Doc(Array.Empty<IndexPackage>(), new[]
        {
            Takedown("T", "Gone", "*", IndexRevocationMode.Disable),
            Takedown("T", "Half", "1.0.0", IndexRevocationMode.Hidden),
            Takedown("T", "Warned", "*", IndexRevocationMode.Warning),
        });
        string json = Listing(("T", "Gone", new[] { "1.0.0" }), ("T", "Half", new[] { "1.0.0" }), ("T", "Warned", new[] { "1.0.0" }), ("T", "Fine", new[] { "1.0.0" }));
        var catalog = Catalog(doc, json);
        Assert.False(catalog.TryGetEntry("T-Gone", out _));
        Assert.False(catalog.TryGetEntry("T-Half", out _));
        Assert.True(catalog.TryGetEntry("T-Warned", out _));
        Assert.True(catalog.TryGetEntry("T-Fine", out _));

        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["T-Gone"] = "1.0.0", ["T-Half"] = "1.0.0", ["T-Fine"] = "1.0.0" };
        var join = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        Assert.True(ThunderstoreVersions.TryLoad(Listing(("T", "Gone", new[] { "1.0.0" }), ("T", "Half", new[] { "2.0.0", "1.0.0" }), ("T", "Fine", new[] { "1.0.0" })),
            "s", wanted, join, local: false, out _));
        PackageVersions.RemoveTakedowns(join, doc);
        Assert.False(join.ContainsKey("T-Gone"));
        Assert.Equal(new[] { "2.0.0" }, join["T-Half"].Active);
        Assert.Null(join["T-Half"].Matched);
        Assert.Equal("1.0.0", join["T-Fine"].Matched);
    }

    [Fact]
    public void A_takedown_of_a_native_entry_does_not_reach_another_author_s_Thunderstore_package_unless_it_names_Thunderstore()
    {
        var native = IndexKit.Package("Same", "Name", "1.0.0");
        var doc = IndexKit.Doc(new[] { native }, new[] { Takedown("Same", "Name", "*", IndexRevocationMode.Disable) });
        // The Index entry is down; the Thunderstore package of the same name is not.
        Assert.Empty(IndexCatalog.ToEntries(doc, "s"));
        var catalog = Catalog(doc, Listing(("Same", "Name", new[] { "1.0.0" })));
        Assert.True(catalog.TryGetEntry("Same-Name", out var row) && !row!.FromIndex);
        Assert.NotNull(IndexCatalog.RevocationForInstalled(doc, "Same-Name", "1.0.0", fromIndex: true));
        Assert.Null(IndexCatalog.RevocationForInstalled(doc, "Same-Name", "1.0.0", fromIndex: false));

        // Named explicitly: the Thunderstore package goes, and the Index entry stays.
        var ts = IndexKit.Doc(new[] { native }, new[] { Takedown("Same", "Name", "*", IndexRevocationMode.Disable, source: "thunderstore") });
        Assert.Single(IndexCatalog.ToEntries(ts, "s"));
        Assert.NotNull(IndexCatalog.RevocationForInstalled(ts, "Same-Name", "1.0.0", fromIndex: false));
        Assert.Null(IndexCatalog.RevocationForInstalled(ts, "Same-Name", "1.0.0", fromIndex: true));

        // A takedown with a repo covers the entry of that repository only.
        var other = IndexKit.Doc(new[] { native }, new[] { Takedown("Same", "Name", "*", IndexRevocationMode.Disable, repo: "https://github.com/Else/Name") });
        Assert.Single(IndexCatalog.ToEntries(other, "s"));
        var mine = IndexKit.Doc(new[] { native }, new[] { Takedown("Same", "Name", "*", IndexRevocationMode.Disable, repo: native.Repo) });
        Assert.Empty(IndexCatalog.ToEntries(mine, "s"));
    }

    // ---- M2: future-dated lists ------------------------------------------------------------------------------

    private sealed class FixedClock : ICatalogClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_list_dated_more_than_a_day_ahead_is_never_cached_or_adopted_and_is_no_replay_floor()
    {
        var clock = new FixedClock();
        string json = "", sig = "";
        var fetch = new ProgenitorIndexFetch((url, v, max, ct) => Task.FromResult(url.EndsWith(".sig", StringComparison.Ordinal)
                ? new CatalogTextFetchResult(true, null, sig, sig.Length, 200)
                : new CatalogTextFetchResult(true, null, json, json.Length, 200)),
            new IndexDiskCache(_root, ProgenitorIndex.PrimaryUrl), () => new[] { IndexKit.Key.SpkiBase64 }, clock);
        var source = new ProgenitorIndexSource(fetch, clock: clock);

        json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "9.0.0") }, "2026-10-25T00:00:00Z");
        sig = IndexKit.Sign(json, IndexKit.Key);
        var doc = await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Empty(doc.Packages);
        Assert.Contains("more than a day ahead", source.LastError);
        Assert.False(File.Exists(fetch.Cache.IndexPath!));

        // Within a day ahead: fine.
        json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") }, "2026-10-21T06:00:00Z");
        sig = IndexKit.Sign(json, IndexKit.Key);
        Assert.Single((await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None)).Packages);

        // A future-dated copy already on disk (written by an older build) is not adopted and is no floor.
        string future = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "7.0.0") }, "2027-01-01T00:00:00Z");
        Assert.True(fetch.Cache.Write(SelfFeed.Envelope(IndexKit.Sign(future, IndexKit.Key).Trim(), future), IndexValidators.None, clock.UtcNow));
        var fresh = new ProgenitorIndexSource(fetch, clock: clock);
        fresh.EnsureCache();
        Assert.Empty(fresh.Current.Packages);
        json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.1.0") }, "2026-10-20T11:00:00Z");
        sig = IndexKit.Sign(json, IndexKit.Key);
        Assert.Equal("1.1.0", Assert.Single((await fresh.RefreshAsync(TimeSpan.Zero, CancellationToken.None)).Packages).Version);
    }

    // ---- M3: an Index root's closure is pinned ---------------------------------------------------------------

    [Fact]
    public void Every_member_of_an_Index_root_s_closure_must_be_pinned()
    {
        var lib = IndexKit.Package("B", "Lib", "1.0.0");
        var tsPinned = new IndexDependency { Id = "C-Ts-1.1.0", From = "thunderstore", Package = "https://thunderstore.io/package/download/C/Ts/1.1.0/", Sha256 = IndexKit.Sha("ts") };
        var other = IndexKit.Package("O", "Other", "1.0.0", deps: new[] { tsPinned });
        var root = IndexKit.Package("R", "Root", "1.0.0");
        var doc = IndexKit.Doc(new[] { lib, other, root });
        var catalog = Catalog(doc, Listing(("C", "Ts", new[] { "2.0.0", "1.1.0" }), ("U", "Unpinned", new[] { "1.0.0" })));
        Assert.True(catalog.TryGetEntry("R-Root", out var rootRow) && rootRow!.FromIndex);

        // An Index entry of the member: pinned.
        Assert.Null(catalog.PinDependencyOf(rootRow, "B-Lib", "1.0.0"));
        Assert.True(catalog.TryGetEntry("B-Lib", out var libRow) && libRow!.FromIndex);
        // A Thunderstore member whose version another native entry pins: that exact file, not Thunderstore's newest.
        Assert.Null(catalog.PinDependencyOf(rootRow, "C-Ts", "1.1.0"));
        Assert.True(catalog.TryGetEntry("C-Ts", out var tsRow));
        Assert.Equal("1.1.0", tsRow!.Manifest.Version);
        Assert.Equal(tsPinned.Sha256, tsRow.Pin!.Sha256);
        // Another version of it, or a package the Index pins nowhere: refused.
        Assert.Equal(InstallReasons.UnpinnedDependency("C-Ts 2.0.0"), catalog.PinDependencyOf(rootRow, "C-Ts", "2.0.0"));
        Assert.Equal(InstallReasons.UnpinnedDependency("U-Unpinned 1.0.0"), catalog.PinDependencyOf(rootRow, "U-Unpinned", "1.0.0"));
        // A Thunderstore root is not asked.
        Assert.True(catalog.TryGetEntry("U-Unpinned", out var tsRoot));
        Assert.Null(catalog.PinDependencyOf(tsRoot!, "C-Ts", "2.0.0"));
    }

    // ---- M5: the takedown's words per case -------------------------------------------------------------------

    [Fact]
    public void A_disable_takedown_says_what_it_does_for_each_kind_of_plugin()
    {
        var doc = IndexKit.Doc(new[] { IndexKit.Package("A", "Mod", "1.0.0") }, new[] { Takedown("A", "Mod", "*", IndexRevocationMode.Disable) });
        IndexRevocationSource.IndexProbe = () => doc;
        IndexRevocationSource.InstalledProbe = () => new[] { new IndexRevocationSource.Installed("a.mod", "A-Mod", "1.0.0", FromIndex: true) };
        foreach (var (state, words) in new[]
        {
            (IndexRevocationSource.TakedownLoadState.Running, "It stops loading at the next start"),
            (IndexRevocationSource.TakedownLoadState.NotLoaded, "Progenitor does not load it"),
            (IndexRevocationSource.TakedownLoadState.Plain, "plain BepInEx plugin, which Progenitor cannot stop from loading"),
        })
        {
            IndexRevocationSource.StateProbe = _ => state;
            var line = Assert.Single(new IndexRevocationSource().Current());
            Assert.Contains(words, line.Sentence);
            Assert.Equal("Uninstall it.", line.WhatToDo);
        }
    }

    // ---- L1 / L2: the dev URL and key ------------------------------------------------------------------------

    [Fact]
    public void The_dev_URL_is_loopback_only_and_the_dev_key_never_verifies_the_published_list()
    {
        var key = IndexKit.Other.SpkiBase64;
        ProgenitorIndexConfig.TestOverride = (true, "https://index.example.org/index.json", key, true);
        Assert.Null(ProgenitorIndexConfig.DevUrl);
        ProgenitorIndexConfig.TestOverride = (true, "http://127.0.0.1:27460/index.json", key, false);
        Assert.Null(ProgenitorIndexConfig.DevUrl);
        ProgenitorIndexConfig.TestOverride = (true, "http://127.0.0.1:27460/index.json", key, true);
        Assert.Equal("http://127.0.0.1:27460/index.json", ProgenitorIndexConfig.DevUrl!.AbsoluteUri);

        Assert.DoesNotContain(key, ProgenitorIndexConfig.KeysFor(ProgenitorIndex.PrimaryUrl)());
        Assert.Contains(key, ProgenitorIndexConfig.KeysFor("http://127.0.0.1:27460/index.json")());
        Assert.DoesNotContain(key, ProgenitorIndexConfig.KeysFor("http://127.0.0.1:9999/other.json")());
    }

    // ---- L3: an unreadable range -----------------------------------------------------------------------------

    [Fact]
    public void An_unreadable_Progenitor_range_is_refused_with_neutral_words()
    {
        var entry = IndexCatalog.EntryOf(IndexKit.Package("A", "Odd", "1.0.0", progenitor: "^2026.10"), null, "s");
        Assert.True(IndexCatalog.ProgenitorRangeUnreadable(entry));
        Assert.False(IndexCatalog.ProgenitorTooOld(entry, out _));
        Assert.Equal(IndexCatalog.RangeUnreadable, InstallCatalogService.PinRefusal(entry));
        Assert.False(IndexCatalog.ProgenitorRangeUnreadable(IndexCatalog.EntryOf(IndexKit.Package("A", "Ok", "1.0.0", progenitor: ">=1.0"), null, "s")));
    }

    // ---- L4: Thunderstore off in updates ---------------------------------------------------------------------

    [Fact]
    public void A_Thunderstore_install_s_update_says_Thunderstore_is_off()
    {
        var m = new ManagedPackage { Key = "T-Mod", Version = "1.0.0" };
        var report = UpdateDetection.Detect(new[] { m }, new Dictionary<string, IndexListing>(), null, null, false, DateTime.UtcNow, "", thunderstoreOff: true);
        Assert.Equal(UpdateDetection.SkipThunderstoreOff, Assert.Single(report.Skipped).Reason);
        var fromIndex = new ManagedPackage { Key = "I-Mod", Version = "1.0.0", FromIndex = true };
        report = UpdateDetection.Detect(new[] { fromIndex }, new Dictionary<string, IndexListing>(), null, null, false, DateTime.UtcNow, "", thunderstoreOff: true);
        Assert.Equal(UpdateDetection.SkipNotListed, Assert.Single(report.Skipped).Reason);

        string file = Path.Combine(_root, "progenitor-reload", "t.mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        Assert.True(ServerInstall.TryAppendInstalledFile(_root, file, "", 1, "package:T-Mod@1.0.0", ServerInstallLedger.CategoryMod, pluginId: "t.mod"));
        var catalog = new InstallCatalogService(_root, new NoTransport(), registerForBridge: false) { ThunderstoreEnabled = () => false };
        Assert.Equal(InstallReasons.UpdateThunderstoreOff, catalog.UseInstalledSource("T-Mod"));
    }

    // ---- L5: a changed list is a changed hash ----------------------------------------------------------------

    [Fact]
    public async Task A_list_with_the_same_time_and_counts_but_other_bytes_is_a_new_list()
    {
        string json = IndexKit.Json(new[] { IndexKit.Package("A", "Mod", "1.0.0") });
        var served = json;
        var fetch = new ProgenitorIndexFetch((url, v, max, ct) => Task.FromResult(url.EndsWith(".sig", StringComparison.Ordinal)
                ? new CatalogTextFetchResult(true, null, IndexKit.Sign(served, IndexKit.Key), 129, 200)
                : new CatalogTextFetchResult(true, null, served, served.Length, 200)),
            new IndexDiskCache(_root, ProgenitorIndex.PrimaryUrl), () => new[] { IndexKit.Key.SpkiBase64 });
        var source = new ProgenitorIndexSource(fetch);
        await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        int v1 = source.Version;
        await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(v1, source.Version);
        served = json.Replace("Mod from the Index", "Mod from the Index, edited");
        await source.RefreshAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.NotEqual(v1, source.Version);
    }
}
