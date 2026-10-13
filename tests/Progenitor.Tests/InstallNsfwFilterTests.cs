using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>INSTALLSIDES1: Thunderstore's <c>has_nsfw_content</c> flag and the Sources switch that shows flagged listings.</summary>
public sealed class InstallNsfwFilterTests
{
    private static string Package(string name, string nsfw) =>
        "{\"name\":\"" + name + "\",\"full_name\":\"o-" + name + "\",\"owner\":\"o\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/o/" + name + "/\","
        + "\"is_deprecated\":false" + nsfw + ",\"categories\":[],"
        + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"o-" + name + "-1.0.0\",\"description\":\"d\","
        + "\"version_number\":\"1.0.0\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/o/" + name + "/1.0.0/\","
        + "\"downloads\":5,\"date_created\":\"2026-08-21T09:14:34Z\",\"is_active\":true,\"file_size\":10}]}";

    [Theory]
    [InlineData(",\"has_nsfw_content\":true", true)]
    [InlineData(",\"has_nsfw_content\":false", false)]
    [InlineData("", false)]
    [InlineData(",\"has_nsfw_content\":\"true\"", false)]
    public void The_adapter_reads_the_package_level_flag(string field, bool expected)
    {
        Assert.True(ThunderstoreAdapter.TryAdaptOne(Package("A", field), "stamp", out var entry, out string error), error);
        Assert.Equal(expected, entry.HasNsfwContent);
    }

    [Fact]
    public void An_inspection_keeps_the_flag()
    {
        Assert.True(ThunderstoreAdapter.TryAdaptOne(Package("A", ",\"has_nsfw_content\":true"), "stamp", out var entry, out _));
        var inspected = entry.WithInspection(CatalogProvenance.AdaptedListing, entry.Manifest, PackageSide.Client);
        Assert.True(inspected.HasNsfwContent);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, false)]
    public void The_rule(bool flagged, bool show, bool installed, bool hidden) =>
        Assert.Equal(hidden, InstallModsPane.NsfwHidden(flagged, show, installed));

    private static CatalogEntry Listing(string key, bool nsfw) =>
        new(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "o", key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: key.Substring(2),
            owner: "o",
            packageName: key.Substring(2),
            releasedUtc: "2026-09-11T00:00:00Z",
            hasNsfwContent: nsfw);

    [Fact]
    public void Flagged_listings_are_hidden_by_default_and_shown_by_the_switch()
    {
        var catalog = new FakeCatalog();
        var clean = Listing("o-Clean", nsfw: false);
        var flagged = Listing("o-Spicy", nsfw: true);
        catalog.Entries.AddRange(new[] { clean, flagged });
        var pane = new InstallModsPane(catalog);

        Assert.False(pane.ShowNsfwContent);
        Assert.Equal(new[] { "o-Clean" }, pane.AllEntries().Select(e => e.Key).ToArray());
        Assert.Equal(new[] { "o-Clean" }, pane.VisibleEntries().Select(e => e.Key).ToArray());

        bool show = true;
        pane.ShowNsfwProbe = () => show;
        Assert.Equal(new[] { "o-Clean", "o-Spicy" }, pane.AllEntries().Select(e => e.Key).OrderBy(k => k).ToArray());

        show = false;
        Assert.DoesNotContain(pane.AllEntries(), e => e.Key == "o-Spicy");
    }

    [Fact]
    public void An_installed_flagged_mod_stays_listed_so_it_can_be_removed()
    {
        var catalog = new FakeCatalog();
        var flagged = Listing("o-Spicy", nsfw: true);
        catalog.Entries.Add(flagged);
        var pane = new InstallModsPane(catalog);
        pane.SetInstalledQuery(new InstalledStateQuery(
            inventory: new[] { new InstalledInventoryRow("o-Spicy", "1.0.0", "Local") }));

        Assert.True(pane.InstalledAnywhere(flagged));
        Assert.Contains(pane.AllEntries(), e => e.Key == "o-Spicy");
    }

    [Fact]
    public void A_throwing_probe_reads_off()
    {
        var pane = new InstallModsPane(new FakeCatalog()) { ShowNsfwProbe = () => throw new InvalidOperationException() };
        Assert.False(pane.ShowNsfwContent);
    }

    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries.ToArray();

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            entry = Entries.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
            return entry != null;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) => new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(true, null, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) => new(true, null, "pending/" + planHash, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.FetchCancelled));
    }
}
