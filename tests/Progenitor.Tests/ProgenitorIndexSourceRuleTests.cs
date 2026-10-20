using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// INDEX1 follow-ups, game-free: a Disable takedown keeps an installed copy from loading (and lifting it loads it again),
/// the Install tab lists the Index with Thunderstore off, and updates stay with the source a mod was installed from.
/// </summary>
[Collection("PluginLogger")]
public sealed class ProgenitorIndexSourceRuleTests : IDisposable
{
    private readonly string _root;

    public ProgenitorIndexSourceRuleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pindex2-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Progenitor.Plugins.ModEnableState.ResetForTests();
        Progenitor.Plugins.ModEnableState.TestDirectory = Path.Combine(_root, "config");
    }

    public void Dispose()
    {
        Progenitor.Plugins.ModEnableState.TakedownReason = null;
        Progenitor.Plugins.ModEnableState.TestDirectory = null;
        Progenitor.Plugins.ModEnableState.ResetForTests();
        IndexRevocationSource.IndexProbe = null;
        IndexRevocationSource.InstalledProbe = null;
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static IndexRevocation Down(string owner, string name, string version, string reason = "malware") =>
        new() { Owner = owner, Name = name, Version = version, Reason = reason, Mode = IndexRevocationMode.Disable };

    // ---- a disable takedown disables -------------------------------------------------------------------------

    [Fact]
    public void A_disable_takedown_keeps_the_installed_copy_from_loading_and_lifting_it_loads_it_again()
    {
        var installed = new[] { new IndexRevocationSource.Installed("a.mod", "A-Mod", "1.0.0", FromIndex: true) };
        var doc = IndexKit.Doc(new[] { IndexKit.Package("A", "Mod", "1.0.0") }, new[] { Down("A", "Mod", "1.0.0", "Sends data away.") });
        IndexRevocationSource.InstalledProbe = () => installed;
        IndexRevocationSource.IndexProbe = () => doc;
        Progenitor.Plugins.ModEnableState.TakedownReason = IndexRevocationSource.DisableReason;

        // The load gate every Progenitor plugin passes, at a start and at a hot load.
        Assert.Equal("taken down from the Progenitor Index (Sends data away)", Progenitor.Plugins.ModEnableState.OffReason("a.mod", null));
        Assert.Null(Progenitor.Plugins.ModEnableState.OffReason("b.other", null));
        // Nothing was written: the player's own on/off file is untouched.
        Assert.False(Progenitor.Plugins.ModEnableState.IsDisabled("a.mod"));
        Assert.False(File.Exists(Progenitor.Plugins.ModEnableState.FilePath));

        // The mod page says why, in plain words.
        var line = Assert.Single(new IndexRevocationSource().Current());
        Assert.Contains("took this version down: Sends data away.", line.Sentence);
        Assert.Contains("Progenitor does not load it", line.Sentence);
        Assert.Contains("loads again if the takedown is lifted", line.Sentence);

        // Another version, or a warning only, loads.
        Assert.Null(IndexRevocationSource.DisableReasonFor(doc, new[] { new IndexRevocationSource.Installed("a.mod", "A-Mod", "1.1.0") }, "a.mod"));
        var warned = IndexKit.Doc(doc.Packages, new[] { new IndexRevocation { Owner = "A", Name = "Mod", Version = "1.0.0", Mode = IndexRevocationMode.Warning } });
        Assert.Null(IndexRevocationSource.DisableReasonFor(warned, installed, "a.mod"));

        // The takedown is lifted (a newer list without it): the copy loads again.
        doc = IndexKit.Doc(doc.Packages);
        Assert.Null(Progenitor.Plugins.ModEnableState.OffReason("a.mod", null));
    }

    [Fact]
    public void A_plugin_needing_a_taken_down_mod_is_skipped_with_it()
    {
        IndexRevocationSource.InstalledProbe = () => new[] { new IndexRevocationSource.Installed("a.lib", "A-Lib", "1.0.0") };
        IndexRevocationSource.IndexProbe = () => IndexKit.Doc(Array.Empty<IndexPackage>(), new[] { Down("A", "Lib", "*") });
        Progenitor.Plugins.ModEnableState.TakedownReason = IndexRevocationSource.DisableReason;
        Assert.NotNull(Progenitor.Plugins.ModEnableState.OffReason("a.lib", null));
        // A dependant is skipped through the load order's own "skipped now" answer, as for a mod the player turned off.
        Assert.NotNull(Progenitor.Plugins.ModEnableState.OffReason("b.user", new[] { "a.lib" }, id => id == "a.lib"));
    }

    // ---- the Install tab with Thunderstore off ---------------------------------------------------------------

    private static string Listing(string owner, string name, string version) =>
        "[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + owner + "/" + name + "/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "-" + version + "\",\"description\":\"d\",\"version_number\":\"" + version + "\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/" + owner + "/" + name + "/" + version + "/\",\"file_size\":10,\"is_active\":true}]}]";

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    [Fact]
    public void With_Thunderstore_off_the_catalog_lists_the_Index_alone_and_on_again_both()
    {
        bool thunderstore = true;
        var catalog = new InstallCatalogService(_root, new NoTransport(), registerForBridge: false)
        {
            IndexSource = IndexKit.Source(IndexKit.Package("I", "Mod", "1.0.0")),
            ThunderstoreEnabled = () => thunderstore,
        };
        Assert.True(catalog.LoadIndexFromJson(Listing("T", "Other", "1.0.0"), "s").Ok);
        Assert.Equal(2, catalog.Search(null).Count);

        thunderstore = false;
        var only = catalog.LoadIndexOnly();
        Assert.True(only.Ok);
        Assert.Equal(1, only.Count);
        Assert.True(Assert.Single(catalog.Search(null)).FromIndex);

        thunderstore = true;
        catalog.ReloadLocalIndex();
        Assert.Equal(2, catalog.Search(null).Count);
    }

    [Fact]
    public async Task The_refresher_skips_the_Thunderstore_read_while_it_is_off_and_still_serves_the_Index()
    {
        int fetches = 0, indexChecks = 0;
        var refresher = new InstallIndexRefresher(_root, (json, stamp) => new CatalogLoadResult(true, null, 9, stamp),
            ct => { fetches++; return Task.FromResult(new CatalogTextFetchResult(true, null, "[]", 2, 200)); })
        {
            BeforeLoad = (ct, force) => { indexChecks++; return Task.CompletedTask; },
            SkipThunderstore = () => true,
            LoadWithoutThunderstore = () => new CatalogLoadResult(true, null, 1, "index"),
        };
        Assert.True(refresher.TryServeCache(out var served));
        Assert.Equal(1, served.Count);
        var refreshed = await refresher.RefreshAsync(CancellationToken.None);
        Assert.True(refreshed.Ok);
        Assert.Equal(1, refreshed.Count);
        Assert.Equal(0, fetches);
        Assert.Equal(1, indexChecks);
    }

    // ---- updates stay with their source ----------------------------------------------------------------------

    private static UpdateReport Detect(ManagedPackage m, IReadOnlyList<CatalogEntry> rows) =>
        UpdateDetection.Detect(new[] { m }, ClientUpdateInputs.ListingsOf(rows, null), null, null, showNsfw: false, DateTime.UtcNow);

    private static IReadOnlyList<CatalogEntry> Merged(IEnumerable<IndexPackage> index, string? thunderstoreJson)
    {
        IReadOnlyList<CatalogEntry> ts = Array.Empty<CatalogEntry>();
        if (thunderstoreJson != null)
        {
            Assert.True(ThunderstoreAdapter.TryLoadIndex(thunderstoreJson, "s", out var t, out string error), error);
            ts = t;
        }

        return IndexCatalog.Merge(null, IndexCatalog.ToEntries(IndexKit.Doc(index), "s"), ts, out _, out _);
    }

    [Fact]
    public void An_Index_install_updates_only_from_the_Index_and_never_from_a_Thunderstore_listing_of_the_same_name()
    {
        var fromIndex = new ManagedPackage { Key = "A-Mod", Version = "1.0.0", FromIndex = true };
        // The Index lists a newer version: offered.
        var report = Detect(fromIndex, Merged(new[] { IndexKit.Package("A", "Mod", "1.2.0") }, Listing("A", "Mod", "2.0.0")));
        Assert.Equal("1.2.0", Assert.Single(report.Items).Latest);
        // The mod left the Index; Thunderstore lists the name: no update.
        report = Detect(fromIndex, Merged(Array.Empty<IndexPackage>(), Listing("A", "Mod", "2.0.0")));
        Assert.Empty(report.Items);
        Assert.Equal(UpdateDetection.SkipOtherSource, Assert.Single(report.Skipped).Reason);
    }

    [Fact]
    public void A_Thunderstore_install_updates_only_from_Thunderstore_even_when_the_Index_lists_the_mod()
    {
        var fromTs = new ManagedPackage { Key = "A-Mod", Version = "1.0.0" };
        // Both list it: the Thunderstore version is the update, not the Index's.
        var report = Detect(fromTs, Merged(new[] { IndexKit.Package("A", "Mod", "3.0.0") }, Listing("A", "Mod", "1.1.0")));
        Assert.Equal("1.1.0", Assert.Single(report.Items).Latest);
        // Only the Index lists it now: no update (the player may reinstall it from the Index by hand).
        report = Detect(fromTs, Merged(new[] { IndexKit.Package("A", "Mod", "3.0.0") }, null));
        Assert.Empty(report.Items);
        Assert.Equal(UpdateDetection.SkipOtherSource, Assert.Single(report.Skipped).Reason);
        // Thunderstore only, as before.
        Assert.Equal("1.1.0", Assert.Single(Detect(fromTs, Merged(Array.Empty<IndexPackage>(), Listing("A", "Mod", "1.1.0"))).Items).Latest);
    }

    [Fact]
    public void The_ledger_origin_feeds_detection_and_an_update_download_takes_the_installed_copy_s_own_source()
    {
        string game = Path.GetDirectoryName(_root)!;
        string file = Path.Combine(_root, "progenitor-reload", "a.mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");
        Assert.True(ServerInstall.TryAppendInstalledFile(_root, file, "", 1, "package:A-Mod@1.0.0", ServerInstallLedger.CategoryMod, pluginId: "a.mod"));
        Assert.False(Assert.Single(ClientUpdateInputs.ReadManaged(game)).FromIndex);

        var catalog = new InstallCatalogService(_root, new NoTransport(), registerForBridge: false)
        {
            IndexSource = IndexKit.Source(IndexKit.Package("A", "Mod", "3.0.0")),
        };
        Assert.True(catalog.LoadIndexFromJson(Listing("A", "Mod", "1.1.0"), "s").Ok);
        Assert.True(catalog.TryGetEntry("A-Mod", out var row) && row!.FromIndex);
        Assert.Equal(false, catalog.InstalledFromIndex("A-Mod"));

        // A Thunderstore install: the update's row becomes Thunderstore's.
        Assert.Null(catalog.UseInstalledSource("A-Mod"));
        Assert.True(catalog.TryGetEntry("A-Mod", out row) && !row!.FromIndex);
        Assert.Equal("1.1.0", row.Manifest.Version);

        // The same copy recorded as an Index install, and the Index no longer lists it: refused, not switched.
        Assert.True(ServerInstall.TryAppendInstalledFile(_root, file, "", 1, "package:A-Mod@1.0.0", ServerInstallLedger.CategoryMod,
            pluginId: "a.mod", origin: ProgenitorIndex.LedgerOrigin));
        Assert.True(Assert.Single(ClientUpdateInputs.ReadManaged(game)).FromIndex);
        catalog.IndexSource = IndexKit.Source();
        Assert.Equal(true, catalog.InstalledFromIndex("A-Mod"));
        Assert.Equal(InstallReasons.UpdateIndexGone, catalog.UseInstalledSource("A-Mod"));
    }

    [Fact]
    public void A_Thunderstore_install_the_Index_now_lists_says_how_to_move_it_on_its_page()
    {
        IndexRevocationSource.IndexProbe = () => IndexKit.Doc(new[] { IndexKit.Package("A", "Mod", "2.0.0") });
        IndexRevocationSource.InstalledProbe = () => new[]
        {
            new IndexRevocationSource.Installed("a.mod", "A-Mod", "1.0.0"),
            new IndexRevocationSource.Installed("a.indexed", "A-Mod", "1.0.0", FromIndex: true),
        };
        var line = Assert.Single(new IndexRevocationSource().Current());
        Assert.Equal("a.mod", line.ModId);
        Assert.Equal(ModProblemSeverity.Info, line.Severity);
        Assert.Contains("keeps updating from Thunderstore", line.Sentence);
        Assert.Contains("uninstall it and install it again", line.WhatToDo);
    }
}
