using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// FIX-NONMODS (live 2026-10-03): <c>ebkr-r2modman</c> — a mod manager application, 363 MB, not tagged
/// <c>Tools</c> — reached the install confirmation and downloaded up to the 32 MiB ceiling before
/// failing. Listings over the download limit and mod manager apps are never installable: the row says
/// why with no button, the drawer explains, the filters treat them like tools, and the catalog
/// refuses them before any request.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class InstallNonModTests : IDisposable
{
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-nonmods-" + Guid.NewGuid().ToString("N"));

    public InstallNonModTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiDialog.ForgetForTests();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.KnownClientBuild;
        InstallModsPane.ProcessFilterProbe = null;
        InstallModsPane.RememberedPick = null;
        InstallModsPane.ResetDownloadLimit();
        NonModPackages.TestMaxDownloadMb = null;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiDialog.ForgetForTests();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        InstallModsPane.ResetDownloadLimit();
        NonModPackages.TestMaxDownloadMb = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static CatalogEntry Entry(string owner, string name, long? size, string[] categories, string description = "d") =>
        new(
            new ManifestMod("", "3.2.20", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, owner, owner + "-" + name),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: name,
            description: description,
            downloadUrl: "https://thunderstore.io/package/download/" + owner + "/" + name + "/3.2.20/",
            listingFileSize: size,
            owner: owner,
            packageName: name,
            categories: categories,
            releasedUtc: "2026-09-11T00:00:00Z",
            downloads: 10);

    /// <summary>The live listing, from the client's cached index.</summary>
    private static CatalogEntry R2modman() =>
        Entry("ebkr", "r2modman", 363105843, new[] { "Gloomrot Update", "1.0 Update", "Oakveil Update" },
            "A simple and easy to use mod manager for many games using Thunderstore");

    private InstallModsPane Pane(params CatalogEntry[] entries)
    {
        var catalog = new InstallSidesPaneTests.FakeCatalog();
        catalog.Entries.AddRange(entries);
        var pane = new InstallModsPane(catalog, null, null);
        var panel = UiApi.Panel("Install mods", _owner, "example.nonmods");
        _panels.Add(panel);
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        return pane;
    }

    [Fact]
    public void Size_and_manager_rules_read_the_listing_metadata_only()
    {
        // LIMITS1: [Install] MaxDownloadMB, default 128 (was 32 MiB, the game-connection transfer cap).
        Assert.Equal(128L * 1024 * 1024, NonModPackages.DownloadLimit);
        Assert.True(NonModPackages.TooLarge(363105843, NonModPackages.DownloadLimit));
        Assert.False(NonModPackages.TooLarge(123779786, NonModPackages.DownloadLimit));
        Assert.False(NonModPackages.TooLarge(NonModPackages.DownloadLimit, NonModPackages.DownloadLimit));
        Assert.False(NonModPackages.TooLarge(null, NonModPackages.DownloadLimit));
        Assert.False(NonModPackages.TooLarge(0, NonModPackages.DownloadLimit));
        Assert.Equal("Too large to install (346 MB; limit 128 MB)", NonModPackages.TooLargeText(363105843, NonModPackages.DownloadLimit));
        // The setting is clamped to 8-1024 MB.
        NonModPackages.TestMaxDownloadMb = 2;
        Assert.Equal(8L * 1024 * 1024, NonModPackages.DownloadLimit);
        NonModPackages.TestMaxDownloadMb = 5000;
        Assert.Equal(1024L * 1024 * 1024, NonModPackages.DownloadLimit);
        NonModPackages.TestMaxDownloadMb = 64;
        Assert.Equal(64L * 1024 * 1024, NonModPackages.DownloadLimit);
        Assert.Equal(64L * 1024 * 1024, InstallModsPane.DownloadLimit);
        NonModPackages.TestMaxDownloadMb = null;
        // The index keeps its own bound; the game-connection transfer cap is untouched (32 MiB).
        Assert.Equal(32L * 1024 * 1024, CatalogFetcher.IndexMaxBytes);
        Assert.Equal(128L * 1024 * 1024, CatalogFetcher.DefaultMaxBytes);

        Assert.True(NonModPackages.IsModManager("ebkr", "r2modman", null));
        Assert.True(NonModPackages.IsModManager(null, null, "ebkr-r2modman"));
        Assert.True(NonModPackages.IsModManager("Kesomannen", "GaleModManager", null));
        // A display name, or a mod with "manager" in its name, is never enough.
        Assert.False(NonModPackages.IsModManager("Varek", "BepInExConfigEditors", "Varek-BepInExConfigEditors"));
        Assert.False(NonModPackages.IsModManager("someone", "r2modman", null));
    }

    [Fact]
    public void A_listing_over_the_download_limit_shows_why_on_both_sides_and_offers_nothing()
    {
        var big = Entry("Someone", "HugePack", 200L * 1024 * 1024, new[] { "Client", "Oakveil Update" });
        var pane = Pane(big);
        Assert.True(InstallModsPane.IsTooLarge(big));
        Assert.True(InstallModsPane.IsNotInstallable(big));
        var sides = pane.SideCellsFor(big);
        Assert.Equal("Too large to install (200 MB; limit 128 MB)", sides.Client.Text);
        Assert.Equal(sides.Client.Text, sides.Server.Text);
        Assert.Empty(sides.Client.Buttons);
        Assert.Empty(sides.Server.Buttons);
        Assert.Equal(SideButton.None, sides.Shortcut);
        Assert.Equal(SideCellTone.Muted, sides.Client.Tone);
        var action = pane.ActionFor(big);
        Assert.Equal(InstallListingState.Tool, action.State);
        Assert.Contains("download limit", action.Reason);
        // The drawer says why (its warning line is the action's reason).
        var lines = pane.DrawerLines(big, action, out _);
        Assert.Contains(lines, l => l.Kind == CellLineKind.Warn && l.Text.Contains("over the 128 MB download limit", StringComparison.Ordinal));
        // No side press can start an install.
        Assert.False(pane.OnSideClick(big.Key, server: false, SideButton.Install));
        Assert.False(pane.OnSideClick(big.Key, server: true, SideButton.Install));
    }

    [Fact]
    public void A_mod_manager_app_is_not_a_game_mod()
    {
        var r2 = R2modman();
        var pane = Pane(r2);
        Assert.True(InstallModsPane.IsModManager(r2));
        Assert.False(InstallModsPane.IsToolCategory(r2));
        // r2modman is also over the limit; the size words and reason win (the more specific refusal).
        Assert.StartsWith("Too large to install", pane.SideCellsFor(r2).Client.Text);

        var small = Entry("Kesomannen", "GaleModManager", 5_000_000, new[] { "Oakveil Update" });
        var pane2 = Pane(small);
        var sides = pane2.SideCellsFor(small);
        Assert.Equal(NonModPackages.ModManagerText, sides.Client.Text);
        Assert.Equal(SideCells.Dash, sides.Server.Text);
        Assert.Empty(sides.Client.Buttons);
        Assert.Equal(NonModPackages.ModManagerReason, pane2.ActionFor(small).Reason);
        Assert.Equal(NonModPackages.ModManagerText, InstallModsPane.RowStatusText(pane2.ActionFor(small)));
    }

    [Fact]
    public void Filters_counts_and_pending_treat_them_like_tools()
    {
        var r2 = R2modman();
        var mod = Entry("o", "Real", 1000, new[] { "Client", "Oakveil Update" });
        var pane = Pane(r2, mod);
        var counts = pane.ShowCounts();
        Assert.Equal(2, counts[InstallShowFilter.All]);
        Assert.Equal(0, counts[InstallShowFilter.Installed]);
        Assert.Equal(0, counts[InstallShowFilter.MissingSide]);
        Assert.Equal(0, counts[InstallShowFilter.NeedsUpdate]);
        var sides = pane.SideCellsFor(r2);
        foreach (var show in new[] { InstallShowFilter.Installed, InstallShowFilter.MissingSide, InstallShowFilter.NeedsUpdate })
            Assert.False(InstallModsPane.ShowPredicate(sides, show));
        Assert.Equal(SideCells.KindNotInstallable, sides.Client.Kind);
        Assert.Equal(0, pane.PendingCount());
    }

    [Fact]
    public async Task The_catalog_refuses_an_oversized_or_manager_listing_before_any_request()
    {
        int calls = 0;
        var transport = new CountingTransport(() => calls++);
        var catalog = new InstallCatalogService(_root, transport, new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) });
        string json = "[" + Listing("ebkr", "r2modman", 363105843) + "," + Listing("Kesomannen", "GaleModManager", 1000) + "," + Listing("o", "Small", 1000) + "]";
        Assert.True(catalog.LoadIndexFromJson(json, "stamp").Ok);

        var big = await catalog.DownloadAndInspectAsync("ebkr-r2modman", CancellationToken.None);
        Assert.False(big.Ok);
        Assert.StartsWith(InstallReasons.FetchSizeCeiling, big.Error);
        Assert.Contains("346 MB", big.Error);
        var manager = await catalog.DownloadAndInspectAsync("Kesomannen-GaleModManager", CancellationToken.None);
        Assert.False(manager.Ok);
        Assert.Equal(NonModPackages.ModManagerText, manager.Error);
        Assert.Equal(0, calls);

        // An ordinary listing still goes to the network (the transport answers 404 here).
        var small = await catalog.DownloadAndInspectAsync("o-Small", CancellationToken.None);
        Assert.True(calls > 0);
        Assert.False(small.Ok);
    }

    private static string Listing(string owner, string name, long size)
    {
        string url = "https://thunderstore.io/package/download/" + owner + "/" + name + "/1.0.0/";
        return "{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner
            + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + owner + "/" + name
            + "/\",\"is_deprecated\":false,\"categories\":[\"Oakveil Update\"],\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\""
            + owner + "-" + name + "-1.0.0\",\"description\":\"d\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\""
            + url + "\",\"file_size\":" + size + ",\"is_active\":true}]}";
    }

    private sealed class CountingTransport : ICatalogTransport
    {
        private readonly Action _onCall;
        public CountingTransport(Action onCall) => _onCall = onCall;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            _onCall();
            return Task.FromResult(new CatalogTransportResponse(404, new MemoryStream(Array.Empty<byte>())));
        }
    }
}
