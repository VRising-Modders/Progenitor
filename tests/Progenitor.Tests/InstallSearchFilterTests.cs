using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Install.VRising;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using System.Linq;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SEARCH1 on the LIST2 list view: the side / no-restart / current-update chips, the
/// stale-update warning on the row and meta line, README-text search over the per-catalog
/// index (the automatic run is covered in InstallListViewTests). Game-free; no network. TAG1: the current update is resolved from
/// the configured tag or the game-build adapter (never from package-edit dates); these tests
/// pin the known client build through <see cref="CatalogUpdateTags.BuildProvider"/>.
/// </summary>
public sealed class InstallSearchFilterTests : IDisposable
{
    /// <summary>The running client build (Resilience baseline / tools/addresslib), an Oakveil row in the adapter table.</summary>
    internal const string KnownClientBuild = "client-247473480-6a147771-ec12531-66fe4b61f7a0d186";
    internal const string UnmappedBuild = "client-000000001-00000000-0000000-0000000000000000";

    private readonly FakeCatalog _catalog = new();
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();

    public InstallSearchFilterTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => KnownClientBuild;
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        foreach (var panel in _panels) panel.Dispose();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        UiApi.ResetForTests();
    }

    [Fact]
    public void Side_derivation_precedence_and_sideSource()
    {
        var manifest = Listing("a-m", "M", side: PackageSide.Client, categories: new[] { "Server" }, description: "server-only");
        var tagged = Listing("a-t", "T", categories: new[] { "Mods", "Server" }, description: "client-only");
        var both = Listing("a-b", "B", categories: new[] { "Client", "Server" });
        var text = Listing("a-d", "D", description: "A server-only QoL mod");
        var textClient = Listing("a-c", "C", description: "Client side HUD tweak");
        var bare = Listing("a-u", "U", description: "talks to the server");
        var none = Listing("a-n", "N");

        var m = PackageSideDerivation.Derive(manifest);
        Assert.Equal(PackageSide.Client, m.Side);
        Assert.Equal("manifest", m.SourceName);
        var t = PackageSideDerivation.Derive(tagged);
        Assert.Equal(PackageSide.Server, t.Side);
        Assert.Equal("categories", t.SourceName);
        var b = PackageSideDerivation.Derive(both);
        Assert.Equal(PackageSide.Both, b.Side);
        Assert.Equal("categories", b.SourceName);
        var d = PackageSideDerivation.Derive(text);
        Assert.Equal(PackageSide.Server, d.Side);
        Assert.Equal("description", d.SourceName);
        Assert.Equal(PackageSide.Client, PackageSideDerivation.Derive(textClient).Side);
        var u = PackageSideDerivation.Derive(bare);
        Assert.Equal(PackageSide.Unknown, u.Side);
        Assert.Equal("unknown", u.SourceName);
        Assert.Equal("unknown", PackageSideDerivation.Derive(none).SourceName);
        Assert.NotEqual(PackageSide.Both, PackageSideDerivation.Derive(none).Side);
        // Revert: letting categories outrank the manifest side, or a bare "server" word count, fails this.
    }

    [Fact]
    public void Current_update_comes_from_the_adapter_never_from_package_edit_dates()
    {
        // The counterexample from the 2026-09-12 handoff (section 4): an author edits a
        // Gloomrot-tagged package after every Oakveil package was last touched. Under the old
        // latest-date_updated heuristic Gloomrot became "current" and the default filter hid
        // every Oakveil listing.
        var gloomrotEditedToday = Listing("a-g", "G", categories: new[] { "Gloomrot Update", "Mods" }, dateUpdated: "2026-12-31T23:59:59Z");
        var oakveil = Listing("a-o", "O", categories: new[] { "Oakveil Update" }, dateUpdated: "2026-09-01T00:00:00Z");
        var one = Listing("a-1", "One", categories: new[] { "1.0 Update" }, dateUpdated: "2026-07-01T00:00:00Z");
        var tools = Listing("a-tools", "Tools", categories: new[] { "Tools" }, dateUpdated: "2027-01-01T00:00:00Z");
        _catalog.Entries.AddRange(new[] { gloomrotEditedToday, oakveil, one, tools });
        var pane = NewPane();
        Build(pane);

        Assert.Equal("Oakveil Update", pane.CurrentUpdateTag);
        Assert.Equal(CatalogUpdateTags.SourceAdapter, pane.CurrentUpdateSource);
        Assert.True(Row(pane, "O").Visible);
        Assert.False(Row(pane, "G").Visible);
        Assert.Equal((1, 0, 4), pane.MatchCount);
        Assert.Equal("1 listing", pane.CountText());
        var chip = Filters(pane);
        Assert.Equal("Oakveil Update", chip["currentUpdateTag"]);
        Assert.Equal("adapter", chip["currentUpdateSource"]);

        // Pure resolution: the entries never take part.
        var resolved = CatalogUpdateTags.Resolve(null, KnownClientBuild, VRisingUpdateTagMap.TagForBuild);
        Assert.Equal("Oakveil Update", resolved.Tag);
        Assert.Equal(CatalogUpdateTags.SourceAdapter, resolved.Source);
        Assert.True(CatalogUpdateTags.IsTagged(oakveil, "oakveil update"));
        Assert.False(CatalogUpdateTags.IsTagged(tools, "Oakveil Update"));
        Assert.False(CatalogUpdateTags.HasAnyUpdateTag(tools));
        Assert.Equal(new[] { "Gloomrot Update" }, CatalogUpdateTags.UpdateTagsOf(gloomrotEditedToday));

        // The heuristic is gone, not demoted to a fallback: no date_updated read anywhere in the
        // resolver, and no patch name in the shared layer.
        string shared = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "CatalogUpdateTags.cs");
        Assert.DoesNotContain("DateUpdated", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseUtc", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Oakveil", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Gloomrot", shared, StringComparison.Ordinal);
        string filters = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "InstallSearchFilters.cs");
        Assert.DoesNotContain("Oakveil", filters, StringComparison.Ordinal);
        string paneSource = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        Assert.DoesNotContain("CatalogUpdateTags.Current(", paneSource, StringComparison.Ordinal);
        SourcePin.AssertUncommented(paneSource, "_currentUpdate ??= CatalogUpdateTags.ResolveCurrent();");
        // Revert: picking the tag with the latest date_updated (or any per-entry date) as current fails this.
    }

    [Fact]
    public void Unknown_game_build_hides_nothing_reads_back_unknown_and_warns_nothing()
    {
        CatalogUpdateTags.BuildProvider = () => UnmappedBuild;
        var current = Listing("a-c", "Current", categories: new[] { "Oakveil Update" }, dateUpdated: "2026-09-01T00:00:00Z");
        var stale = Verified("example.old", "Old Mod", categories: new[] { "Gloomrot Update" }, dateUpdated: "2026-06-01T00:00:00Z");
        var untagged = Listing("a-u", "Untagged", description: "no tags");
        _catalog.Entries.AddRange(new[] { current, stale, untagged });
        var pane = NewPane();
        Build(pane);

        Assert.Null(pane.CurrentUpdateTag);
        Assert.Equal("unknown", pane.CurrentUpdateSource);
        Assert.True(pane.Filters.CurrentUpdateOnly);
        Assert.Equal((3, 0, 3), pane.MatchCount);
        Assert.True(Row(pane, "Old Mod").Visible);
        Assert.True(Row(pane, "Untagged").Visible);
        var chipCell = pane.FilterRows.SelectMany(r => r.Cells).Single(c => c.Key == InstallModsPane.CellCurrent);
        Assert.Equal(InstallModsCopy.CurrentUpdateChip, chipCell.Text);
        Assert.Equal(CellTone.ChipOff, chipCell.Tone);
        var chip = Filters(pane);
        Assert.Null(chip["currentUpdateTag"]);
        Assert.Equal("unknown", chip["currentUpdateSource"]);
        Assert.Equal(true, chip["currentUpdate"]);
        // The chip still flips (readback follows) but hides nothing either way and never lights up.
        pane.ToggleCurrentUpdateOnly();
        Assert.False(pane.Filters.CurrentUpdateOnly);
        Assert.Equal(CellTone.ChipOff, chipCell.Tone);
        Assert.Equal((3, 0, 3), pane.MatchCount);
        Assert.Equal("3 listings", pane.CountText());
        pane.ToggleCurrentUpdateOnly();

        pane.SelectListing(stale.Key);
        Assert.DoesNotContain(InstallModsCopy.StaleSuffix, pane.MetaLine(stale), StringComparison.Ordinal);
        Assert.DoesNotContain("Not marked", pane.ActionFor(stale).Reason, StringComparison.Ordinal);
        Assert.Equal("", pane.RowCells(stale).Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        Assert.Null(CatalogUpdateTags.StaleWarning(stale, null));
        var resolved = CatalogUpdateTags.Resolve(null, UnmappedBuild, VRisingUpdateTagMap.TagForBuild);
        Assert.Null(resolved.Tag);
        Assert.Equal(CatalogUpdateTags.SourceUnknown, resolved.Source);
        Assert.Equal(CatalogUpdateTags.SourceUnknown, CatalogUpdateTags.Resolve(null, null, VRisingUpdateTagMap.TagForBuild).Source);
        Assert.Equal(CatalogUpdateTags.SourceUnknown, CatalogUpdateTags.Resolve(null, KnownClientBuild, null).Source);
        // Revert: hiding untagged rows while the tag is unknown, warning against a null tag, or an
        // "on"/"off" caption while unresolved fails this.
    }

    [Fact]
    public void Configured_tag_wins_over_the_adapter_and_the_literal_unknown_forces_unknown()
    {
        var oakveil = Listing("a-o", "O", categories: new[] { "Oakveil Update" }, dateUpdated: "2026-09-01T00:00:00Z");
        var gloomrot = Listing("a-g", "G", categories: new[] { "Gloomrot Update" }, dateUpdated: "2026-06-01T00:00:00Z");
        _catalog.Entries.AddRange(new[] { oakveil, gloomrot });

        CatalogUpdateTags.ConfiguredTagProvider = () => "  Gloomrot Update ";
        var pane = NewPane();
        Build(pane);
        Assert.Equal("Gloomrot Update", pane.CurrentUpdateTag);
        Assert.Equal("config", pane.CurrentUpdateSource);
        Assert.True(Row(pane, "G").Visible);
        Assert.False(Row(pane, "O").Visible);
        Assert.Equal("Current game update (Gloomrot)", pane.FilterRows.SelectMany(r => r.Cells).Single(c => c.Key == InstallModsPane.CellCurrent).Text);
        Assert.Equal("config", Filters(pane)["currentUpdateSource"]);
        Assert.Equal(CatalogUpdateTags.NotCurrentUpdate("Gloomrot Update"), CatalogUpdateTags.StaleWarning(oakveil, pane.CurrentUpdateTag));

        var cfg = CatalogUpdateTags.Resolve("Gloomrot Update", KnownClientBuild, VRisingUpdateTagMap.TagForBuild);
        Assert.Equal("Gloomrot Update", cfg.Tag);
        Assert.Equal(CatalogUpdateTags.SourceConfig, cfg.Source);
        var forced = CatalogUpdateTags.Resolve("UNKNOWN", KnownClientBuild, VRisingUpdateTagMap.TagForBuild);
        Assert.Null(forced.Tag);
        Assert.Equal(CatalogUpdateTags.SourceUnknown, forced.Source);
        var blank = CatalogUpdateTags.Resolve("   ", KnownClientBuild, VRisingUpdateTagMap.TagForBuild);
        Assert.Equal("Oakveil Update", blank.Tag);
        Assert.Equal(CatalogUpdateTags.SourceAdapter, blank.Source);
        // A provider that throws is unknown, never an exception on the sidebar path.
        CatalogUpdateTags.ConfiguredTagProvider = () => throw new InvalidOperationException("boom");
        CatalogUpdateTags.BuildProvider = () => throw new InvalidOperationException("boom");
        Assert.Equal(CatalogUpdateTags.SourceUnknown, CatalogUpdateTags.ResolveCurrent().Source);

        // The key exists on the shared config: [Install] CurrentUpdateTag, empty by default.
        Assert.Equal("Install", Progenitor.Config.ConfigHelper.InstallSection);
        Assert.Equal("CurrentUpdateTag", Progenitor.Config.ConfigHelper.CurrentUpdateTagKey);
        Assert.Contains("'unknown'", Progenitor.Config.ConfigHelper.CurrentUpdateTagDescription, StringComparison.Ordinal);
        // Revert: letting the adapter outrank a configured tag, or treating "unknown" as a tag, fails this.
    }

    [Fact]
    public void A_package_with_several_update_tags_is_current_when_any_matches()
    {
        var multi = Listing("a-m", "Multi", categories: new[] { "Gloomrot Update", "Mods", "Oakveil Update" }, dateUpdated: "2026-01-01T00:00:00Z");
        var oldOnly = Listing("a-old", "Old", categories: new[] { "Gloomrot Update", "1.0 Update" });
        Assert.Equal(new[] { "Gloomrot Update", "Oakveil Update" }, CatalogUpdateTags.UpdateTagsOf(multi));
        Assert.True(CatalogUpdateTags.IsTagged(multi, "Oakveil Update"));
        Assert.True(CatalogUpdateTags.IsTagged(multi, "Gloomrot Update"));
        Assert.False(CatalogUpdateTags.IsTagged(oldOnly, "Oakveil Update"));
        Assert.Null(CatalogUpdateTags.StaleWarning(multi, "Oakveil Update"));
        Assert.NotNull(CatalogUpdateTags.StaleWarning(oldOnly, "Oakveil Update"));
        var filters = new InstallSearchFilters();
        Assert.True(filters.Matches(multi, "Oakveil Update"));
        Assert.False(filters.Matches(oldOnly, "Oakveil Update"));

        _catalog.Entries.AddRange(new[] { multi, oldOnly });
        var pane = NewPane();
        Build(pane);
        Assert.True(Row(pane, "Multi").Visible);
        Assert.False(Row(pane, "Old").Visible);
        Assert.True(pane.TryDescribeSidebarRow(Row(pane, "Multi"), out var row));
        Assert.Equal(true, ((Dictionary<string, object?>)row!["listing"]!)["currentUpdate"]);
        Assert.Equal("Gloomrot, Oakveil", InstallModsPane.UpdateTagText(multi));
        // Revert: requiring the first (or only) update tag to be the current one fails this.
    }

    [Fact]
    public void Adapter_table_is_versioned_and_each_row_cites_its_source()
    {
        Assert.True(VRisingUpdateTagMap.Version >= 1);
        Assert.NotEmpty(VRisingUpdateTagMap.Rows);
        foreach (var row in VRisingUpdateTagMap.Rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(row.BuildPrefix));
            Assert.True(CatalogUpdateTags.IsUpdateTag(row.Tag), row.Tag);
            Assert.False(string.IsNullOrWhiteSpace(row.Source), row.BuildPrefix + " has no source");
            Assert.Contains("addresslib", row.Source, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(row.Tag, VRisingUpdateTagMap.TagForBuild(row.BuildPrefix + "-tail"));
        }

        Assert.Equal("Oakveil Update", VRisingUpdateTagMap.TagForBuild(KnownClientBuild));
        Assert.Equal("Oakveil Update", VRisingUpdateTagMap.TagForBuild("server-248598344-6a148fd0-ed22e34-3dcd792996235366"));
        Assert.Equal("Oakveil Update", VRisingUpdateTagMap.TagForBuild(KnownClientBuild.ToUpperInvariant()));
        Assert.Null(VRisingUpdateTagMap.TagForBuild(UnmappedBuild));
        Assert.Null(VRisingUpdateTagMap.TagForBuild(""));
        Assert.Null(VRisingUpdateTagMap.TagForBuild(null));
        // The table is the only V Rising-specific file: it lives under Install/VRising and is
        // where the patch names are allowed.
        string map = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "VRising", "VRisingUpdateTagMap.cs");
        SourcePin.AssertUncommented(map, "public const int Version = 1;");
        Assert.Contains("tools/addresslib/client-247473480-6a147771-ec12531-66fe4b61f7a0d186.json", map, StringComparison.Ordinal);
        // Revert: a row without a source note, or a table the test cannot version, fails this.
    }

    [Fact]
    public void Adapter_reads_categories_and_date_updated()
    {
        const string json = "[{\"name\":\"P\",\"full_name\":\"o-P\",\"owner\":\"o\",\"date_updated\":\"2026-09-01T00:00:00Z\","
            + "\"categories\":[\"Mods\",\"Server\",\"Oakveil Update\"],\"versions\":[{\"version_number\":\"1.0.0\","
            + "\"description\":\"d\",\"dependencies\":[\"ExampleTeam-Progenitor-0.1.0\"],\"is_active\":true}]}]";
        Assert.True(ThunderstoreAdapter.TryLoadIndex(json, "stamp", out var entries, out var error), error);
        var entry = Assert.Single(entries);
        Assert.Equal(new[] { "Mods", "Server", "Oakveil Update" }, entry.Categories);
        Assert.Equal("2026-09-01T00:00:00Z", entry.DateUpdated);
        Assert.True(InstallSearchFilters.DeclaresProgenitorDependency(entry));
        Assert.Equal("categories", PackageSideDerivation.Derive(entry).SourceName);
        var inspected = entry.WithInspection(CatalogProvenance.ManifestVerified, entry.Manifest, PackageSide.Both);
        Assert.Equal(entry.Categories, inspected.Categories);
        Assert.Equal(entry.DateUpdated, inspected.DateUpdated);
        // Revert: dropping the categories / date_updated read in TryAdaptOne or WithInspection fails this.
    }

    [Fact]
    public void Progenitor_supported_is_the_declared_dependency_only()
    {
        Assert.True(InstallSearchFilters.IsProgenitorListingDependency("ExampleTeam-Progenitor-0.1.0"));
        Assert.True(InstallSearchFilters.IsProgenitorListingDependency("anyone-progenitor-1.2.3"));
        Assert.False(InstallSearchFilters.IsProgenitorListingDependency("BepInEx-BepInExPack_V_Rising-1.733.2"));
        Assert.False(InstallSearchFilters.IsProgenitorListingDependency("x-ProgenitorTools-1.0.0"));
        Assert.False(InstallSearchFilters.IsProgenitorListingDependency("Progenitor"));
        Assert.Equal("declares the Progenitor dependency — not hot-swap qualification", InstallSearchFilters.ProgenitorSupportedCaption);
        Assert.DoesNotContain("reboot", InstallSearchFilters.ProgenitorSupportedCaption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supported", InstallSearchFilters.ProgenitorSupportedCaption, StringComparison.OrdinalIgnoreCase);
        // LIST2: the declaration is no longer a chip on the tab; the No restart chip reads the manifest's hotLoadable instead.
        Assert.DoesNotContain("Progenitor", string.Join(" ", new InstallModsPane(new FakeCatalog()).FilterCells().Select(c => c.Text)), StringComparison.Ordinal);
        Assert.True(InstallSearchFilters.DeclaresNoRestart(Verified("example.hot", "Hot", hotLoadable: true)));
        Assert.False(InstallSearchFilters.DeclaresNoRestart(Verified("example.cold", "Cold")));
        var plugin = Listing("a-p", "P", pluginDependencies: new[] { new PluginDependency("progenitor", "0.1.0") });
        Assert.True(InstallSearchFilters.DeclaresProgenitorDependency(plugin));
        Assert.False(InstallSearchFilters.DeclaresProgenitorDependency(Listing("a-n", "N")));
        // Revert: a substring match on "progenitor", or a "supported" / "no reboot" caption, fails this.
    }

    [Fact]
    public void Each_filter_alone_and_combined_with_text_and_the_count_text()
    {
        var server = Listing("a-server", "Server Tools", categories: new[] { "Server", "Oakveil Update" }, description: "admin commands", dateUpdated: "2026-09-01T00:00:00Z");
        var client = Listing("a-client", "Client HUD", categories: new[] { "Client", "Oakveil Update" }, description: "hud commands");
        var both = Listing("a-both", "Both Ways", categories: new[] { "Client", "Server", "Oakveil Update" });
        var old = Listing("a-old", "Old Server", categories: new[] { "Server", "Gloomrot Update" }, description: "admin commands", dateUpdated: "2026-06-01T00:00:00Z");
        var untagged = Listing("a-tool", "Launcher", categories: new[] { "Tools" });
        var hot = Verified("example.hot", "Hot Mod", categories: new[] { "Client", "Oakveil Update" }, hotLoadable: true);
        _catalog.Entries.AddRange(new[] { server, client, both, old, untagged, hot });
        var pane = NewPane();
        Build(pane);
        int hosted = _runtime.Rows.Count;

        Assert.Equal("Oakveil Update", pane.CurrentUpdateTag);
        Assert.True(pane.Filters.CurrentUpdateOnly);
        Assert.Equal(InstallSideFilter.Any, pane.Filters.Side);
        Assert.Equal("4 listings", pane.CountText());
        Assert.Equal((4, 0, 6), pane.MatchCount);
        Assert.False(Row(pane, "Old Server").Visible);
        Assert.False(Row(pane, "Launcher").Visible);

        int buttons = UiCellRowHandle.LiveCountForTests;
        pane.SetSideFilter(InstallSideFilter.Server);
        Assert.Equal(buttons, UiCellRowHandle.LiveCountForTests);
        Assert.Equal(hosted, _runtime.Rows.Count);
        Assert.Equal("3 listings filtered · 1 shown", pane.CountText());
        Assert.True(Row(pane, "Server Tools").Visible);
        Assert.False(Row(pane, "Client HUD").Visible);
        Assert.False(Row(pane, "Both Ways").Visible);
        Assert.Equal("server", Filters(pane)["side"]);

        pane.SetSideFilter(InstallSideFilter.Client);
        Assert.Equal(new[] { "Client HUD", "Hot Mod" }, Names(pane.VisibleEntries()));
        pane.SetSideFilter(InstallSideFilter.Both);
        Assert.Equal(new[] { "Both Ways" }, Names(pane.VisibleEntries()));
        pane.SetSideFilter(InstallSideFilter.Any);

        pane.ToggleNoRestartOnly();
        Assert.Equal(new[] { "Hot Mod" }, Names(pane.VisibleEntries()));
        Assert.Equal("3 listings filtered · 1 shown", pane.CountText());
        Assert.Equal(true, Filters(pane)["noRestart"]);
        pane.ToggleNoRestartOnly();

        pane.ToggleCurrentUpdateOnly();
        Assert.False(pane.Filters.CurrentUpdateOnly);
        Assert.Equal("6 listings", pane.CountText());
        Assert.True(Row(pane, "Old Server").Visible);
        Assert.True(Row(pane, "Launcher").Visible);

        pane.Search = "commands";
        // POLISH2 item 1: the pane opens on Recently released, so the three hits come back newest
        // first — Server Tools (2026-09) then Old Server (2026-06), and Client HUD, which carries no
        // date at all, last — rather than in the index's own order.
        Assert.Equal(new[] { "Server Tools", "Old Server", "Client HUD" }, Names(pane.VisibleEntries()));
        pane.SetSideFilter(InstallSideFilter.Server);
        Assert.Equal(new[] { "Server Tools", "Old Server" }, Names(pane.VisibleEntries()));
        pane.ToggleCurrentUpdateOnly();
        Assert.Equal(new[] { "Server Tools" }, Names(pane.VisibleEntries()));
        Assert.Equal("3 listings filtered · 1 shown", pane.CountText());
        Assert.Equal(buttons, UiCellRowHandle.LiveCountForTests);
        Assert.Equal(hosted, _runtime.Rows.Count);

        pane.ClearFilters();
        Assert.True(pane.AtDefaults);
        Assert.Equal("4 listings", pane.CountText());
        Assert.Equal("", pane.Search);

        Assert.True(pane.TryDescribeSidebarRow(Row(pane, "Server Tools"), out var extra));
        var listing = (Dictionary<string, object?>)extra!["listing"]!;
        Assert.Equal("server", listing["side"]);
        Assert.Equal("oneLiner", listing["searchScope"]);
        Assert.Equal(true, listing["currentUpdate"]);
        Assert.True(pane.TryDescribeSidebarRow(Row(pane, "Old Server"), out var stale));
        Assert.Equal(false, ((Dictionary<string, object?>)stale!["listing"]!)["currentUpdate"]);
        Assert.True(pane.TryDescribeSidebarRow(pane.ToolbarRow!, out var toolbar));
        var count = (Dictionary<string, object?>)toolbar!["count"]!;
        Assert.Equal(4, count["shown"]);
        Assert.Equal(0, count["filtered"]);
        Assert.Equal(6, count["total"]);
        Assert.Equal("any", Filters(pane)["side"]);
        // Revert: dropping a filter from IsVisible/VisibleEntries, the count text, or the listing readback fails this.
    }

    [Fact]
    public void Untagged_packages_hide_only_while_current_update_only_is_on()
    {
        var current = Listing("a-c", "Current", categories: new[] { "Oakveil Update" }, dateUpdated: "2026-09-01T00:00:00Z");
        var untagged = Listing("a-u", "Untagged");
        var filters = new InstallSearchFilters();
        Assert.True(filters.CurrentUpdateOnly);
        Assert.True(filters.Matches(current, "Oakveil Update"));
        Assert.False(filters.Matches(untagged, "Oakveil Update"));
        filters.CurrentUpdateOnly = false;
        Assert.True(filters.Matches(untagged, "Oakveil Update"));
        filters.CurrentUpdateOnly = true;
        // An index with no update tags has nothing to judge by: everything passes.
        Assert.True(filters.Matches(untagged, null));
        // Revert: hiding untagged rows regardless of the toggle, or dropping the null-tag pass, fails this.
    }

    [Fact]
    public void Stale_package_is_warned_on_its_row_and_meta_line_but_is_not_blocked()
    {
        var current = Listing("a-c", "Current", categories: new[] { "Oakveil Update" }, dateUpdated: "2026-09-01T00:00:00Z");
        var stale = Verified("example.old", "Old Mod", categories: new[] { "Gloomrot Update" }, dateUpdated: "2026-06-01T00:00:00Z");
        var untagged = Listing("a-u", "Untagged", description: "no tags");
        _catalog.Entries.AddRange(new[] { current, stale, untagged });
        _catalog.Plans[stale.Key] = new InstallPlan(
            "s", stale, InstallTarget.Client, InstallDecision.RestartClientRequired,
            InstallReasons.RestartClient, Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(),
            "example.old", "example.old.dll");
        var pane = NewPane();
        string warning = CatalogUpdateTags.NotCurrentUpdate("Oakveil Update");
        Assert.Equal("Not marked for the current game update (Oakveil Update) — may be incompatible", warning);

        var action = pane.ActionFor(stale);
        Assert.Equal(InstallListingState.None, action.State);
        Assert.Equal(InstallModsCopy.Install, action.Label);
        Assert.True(action.Enabled);
        Assert.EndsWith(InstallModsCopy.StaleWarnMark + "Gloomrot" + InstallModsCopy.StaleSuffix, pane.MetaLine(stale), StringComparison.Ordinal);
        var cells = pane.RowCells(stale);
        Assert.Equal("Built for Gloomrot", cells.Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        Assert.Equal(CellTone.Warn, cells.Single(c => c.Key == InstallModsPane.CellName).NoteLeadTone);
        Assert.Equal(InstallModsCopy.NoUpdateTag, pane.RowCells(untagged).Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        Assert.Equal("", pane.RowCells(current).Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        Assert.DoesNotContain(InstallModsCopy.StaleSuffix, pane.MetaLine(current), StringComparison.Ordinal);
        Assert.Equal(false, pane.ListingToWidget(stale)["currentUpdate"]);
        Assert.Equal(true, pane.ListingToWidget(current)["currentUpdate"]);
        // The stale row hides only behind the update chip, and stays installable once shown.
        Assert.False(pane.IsVisible(stale));
        pane.ToggleCurrentUpdateOnly();
        Assert.True(pane.IsVisible(stale));
        Assert.True(pane.ActionFor(stale).Enabled);

        // An unknown current update cannot judge: no warning anywhere.
        CatalogUpdateTags.BuildProvider = () => UnmappedBuild;
        var plain = new FakeCatalog();
        plain.Entries.Add(Listing("b-x", "X"));
        var plainPane = new InstallModsPane(plain, new NeverClock(), null);
        Assert.DoesNotContain(InstallModsCopy.StaleSuffix, plainPane.MetaLine(plain.Entries[0]), StringComparison.Ordinal);
        Assert.Equal("", plainPane.RowCells(plain.Entries[0]).Single(c => c.Key == InstallModsPane.CellName).NoteLead);
        // Revert: blocking a stale package (Enabled false), or dropping the update note, fails this.
    }

    [Fact]
    public void Readme_text_search_hits_only_entries_whose_readme_is_known()
    {
        // README search over untagged listings: the current update is made unknown so the
        // default update filter (which hides untagged rows once the build is known) stays out
        // of the way.
        CatalogUpdateTags.BuildProvider = () => UnmappedBuild;
        var withReadme = Listing("a-r", "Runes", description: "adds runes");
        var opened = Listing("a-o", "Orbs", description: "adds orbs");
        var plain = Listing("a-p", "Plain", description: "nothing here");
        _catalog.Entries.AddRange(new[] { withReadme, opened, plain });
        _catalog.Documents[opened.Key] = Docs(opened.Key, "# Orbs\n\nSummons a **glimmering** familiar.");
        _catalog.HoldPrefetch = true;
        var pane = NewPane();
        Build(pane);

        pane.Search = "glimmering";
        Assert.Empty(pane.VisibleEntries());
        Assert.Equal("oneLiner", pane.SearchScope(opened));

        // Opening the drawer fetches its README on demand, which then counts for search.
        _catalog.HoldPrefetch = false;
        pane.SelectListing(opened.Key);
        var task = pane.BeginLoadListingDocuments(CancellationToken.None);
        pane.ApplyListingDocuments(task.Result);
        Assert.Equal("withReadme", pane.SearchScope(opened));
        Assert.Equal("oneLiner", pane.SearchScope(plain));
        Assert.Equal(new[] { "Orbs" }, Names(pane.VisibleEntries()));
        Assert.True(Row(pane, "Orbs").Visible);
        Assert.False(Row(pane, "Plain").Visible);
        Assert.Equal(1, InstallModsPane.SearchRank(opened, "glimmering", "glimmering familiar"));
        Assert.Equal(0, InstallModsPane.SearchRank(opened, "glimmering", null));
        Assert.Equal(2, InstallModsPane.SearchRank(opened, "orbs", "glimmering"));
        Assert.Equal(0, InstallModsPane.SearchRank(opened, "orbs zzz", "glimmering"));

        // A README noted directly (the automatic run's path) counts too; an unavailable README records nothing.
        pane.NoteReadmeForSearch(withReadme, Docs(withReadme.Key, "carved runes glimmering"));
        pane.NoteReadmeForSearch(plain, ListingDocumentSet.Unavailable(plain.Key, "404"));
        pane.RefilterListings();
        Assert.Equal(new[] { "Runes", "Orbs" }, Names(pane.VisibleEntries()));
        Assert.Equal("oneLiner", pane.SearchScope(plain));
        Assert.True(pane.TryDescribeSidebarRow(Row(pane, "Runes"), out var extra));
        Assert.Equal("withReadme", ((Dictionary<string, object?>)extra!["listing"]!)["searchScope"]);

        // A rebuilt pane on the same catalog instance rediscovers the catalog's cache and the
        // process index; a pane on another catalog knows nothing.
        _catalog.Cached[opened.Key] = _catalog.Documents[opened.Key];
        var rebuilt = new InstallModsPane(_catalog, new NeverClock(), null);
        Build(rebuilt);
        Assert.Equal("withReadme", rebuilt.SearchScope(opened));
        Assert.Equal("withReadme", rebuilt.SearchScope(withReadme));
        var other = new InstallModsPane(new FakeCatalog(), new NeverClock(), null);
        Assert.Equal("oneLiner", other.SearchScope(withReadme));
        // Revert: searching README text for unknown rows, or dropping NoteReadmeForSearch on open, fails this.
    }

    [Fact]
    public void Filter_state_persists_across_panes_on_the_same_catalog_not_across_catalogs()
    {
        var first = new InstallModsPane(_catalog);
        first.SetSideFilter(InstallSideFilter.Server);
        first.ToggleCurrentUpdateOnly();
        var again = new InstallModsPane(_catalog);
        Assert.Same(first.Filters, again.Filters);
        Assert.Equal(InstallSideFilter.Server, again.Filters.Side);
        Assert.False(again.Filters.CurrentUpdateOnly);
        var other = new InstallModsPane(new FakeCatalog());
        Assert.True(other.Filters.IsDefault);
        // Revert: filters as plain instance state (lost on a pane rebuild) fails this.
    }

    /// <summary>A pane on this fixture's catalog whose automatic README run parks after its first key (nothing lands from a background thread mid-test).</summary>
    private InstallModsPane NewPane() => new(_catalog, new NeverClock(), null);

    /// <summary>Build the list view into a panel on the fake cell runtime (LIST1's), so row visibility and readback are asserted without Unity.</summary>
    private IUiPanel Build(InstallModsPane pane)
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.search1");
        _panels.Add(panel);
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        return panel;
    }

    private static IUiCellRow Row(InstallModsPane pane, string display) =>
        pane.Rows.Single(r => string.Equals(r.Entry.DisplayName, display, StringComparison.Ordinal)).Row;

    /// <summary>The filter row's readback (<c>filters {side, noRestart, currentUpdate, currentUpdateTag, currentUpdateSource, atDefaults}</c>).</summary>
    private static Dictionary<string, object?> Filters(InstallModsPane pane)
    {
        Assert.True(pane.TryDescribeSidebarRow(pane.FilterRow!, out var extra));
        return (Dictionary<string, object?>)extra!["filters"]!;
    }

    private sealed class NeverClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    private static string[] Names(IReadOnlyList<CatalogEntry> entries)
    {
        var names = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++) names[i] = entries[i].DisplayName ?? "";
        return names;
    }

    private static ListingDocumentSet Docs(string key, string readme) =>
        new(key, "1.0.0", true, null, readme, true, null, "");

    private static CatalogEntry Listing(
        string key,
        string display,
        string description = "",
        IReadOnlyList<string>? categories = null,
        string? dateUpdated = null,
        PackageSide side = PackageSide.Unknown,
        IReadOnlyList<string>? foreignDependencies = null,
        IReadOnlyList<PluginDependency>? pluginDependencies = null)
    {
        int dash = key.IndexOf('-');
        string ns = dash > 0 ? key.Substring(0, dash) : "n";
        return new CatalogEntry(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, ns, key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: display,
            author: ns,
            description: description,
            side: side,
            foreignDependencies: foreignDependencies,
            pluginDependencies: pluginDependencies,
            owner: ns,
            packageName: key,
            categories: categories,
            dateUpdated: dateUpdated);
    }

    private static CatalogEntry Verified(
        string id,
        string display,
        IReadOnlyList<string>? categories = null,
        string? dateUpdated = null,
        bool hotLoadable = false)
    {
        return new CatalogEntry(
            new ManifestMod(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, hotLoadable, "example", id, sha256: new string('a', 64)),
            CatalogProvenance.ManifestVerified,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: display,
            author: "example",
            side: PackageSide.Client,
            owner: "example",
            packageName: id,
            categories: categories,
            dateUpdated: dateUpdated);
    }

    /// <summary>
    /// One-shot gate a fake fetch awaits; <see cref="Release"/> lets the current request finish.
    /// The scheduler's continuations run on the thread pool while the test thread spins on a
    /// counter and then releases, so the handle is armed under a lock and the caller publishes
    /// its observable step only after arming (see <see cref="FakeCatalog.LoadListingDocumentsAsync"/>).
    /// </summary>
    private sealed class StepGate
    {
        private readonly object _lock = new();
        private TaskCompletionSource<bool>? _tcs;

        public Task Wait()
        {
            lock (_lock)
            {
                _tcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _tcs.Task;
            }
        }

        public void Release()
        {
            TaskCompletionSource<bool>? tcs;
            lock (_lock)
            {
                tcs = _tcs;
                _tcs = null;
            }

            if (tcs == null) return;
            tcs.SetResult(true);
        }
    }

    private sealed class StepClock : ICatalogClock
    {
        private readonly object _lock = new();
        private readonly List<TimeSpan> _delays = new();
        private TaskCompletionSource<bool>? _tcs;

        /// <summary>Snapshot; <see cref="Delay"/> is called from a thread-pool continuation.</summary>
        public List<TimeSpan> Delays
        {
            get { lock (_lock) return new List<TimeSpan>(_delays); }
        }

        private Task? _pending;

        /// <summary>The most recent delay task, released or not.</summary>
        public Task? Pending
        {
            get { lock (_lock) return _pending; }
        }

        public DateTime UtcNow => new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            lock (_lock)
            {
                // Arm before publishing the step the test spins on, so a Release() issued as
                // soon as Delays.Count changes always finds this handle.
                _tcs = tcs;
                _pending = tcs.Task;
                _delays.Add(duration);
            }

            return tcs.Task;
        }

        public void Release()
        {
            TaskCompletionSource<bool>? tcs;
            lock (_lock)
            {
                tcs = _tcs;
                _tcs = null;
            }

            if (tcs == null) return;
            tcs.TrySetResult(true);
        }
    }

    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();
        public Dictionary<string, InstallPlan> Plans { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ListingDocumentSet> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ListingDocumentSet> Cached { get; } = new(StringComparer.OrdinalIgnoreCase);
        public StepGate? Gate { get; set; }
        /// <summary>While true every document load answers "canceled" without counting: the automatic run records nothing.</summary>
        public bool HoldPrefetch { get; set; }
        private int _documentLoadCalls;
        public int DocumentLoadCalls => Volatile.Read(ref _documentLoadCalls);

        public InstallProgress Status => InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query) => Entries;

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                entry = Entries[i];
                return true;
            }

            entry = null;
            return false;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target) =>
            Plans.TryGetValue(key, out var plan)
                ? new PlanCallResult(true, null, plan)
                : new PlanCallResult(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(true, null, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) =>
            new(true, null, "pending/" + planHash, false);

        public async Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken)
        {
            if (HoldPrefetch)
                return ListingDocumentSet.Unavailable(key ?? "", InstallReasons.FetchCancelled);
            // Arm the gate before publishing the call count: the test releases the gate as soon
            // as it observes the count change, from another thread.
            Task? wait = Gate?.Wait();
            Interlocked.Increment(ref _documentLoadCalls);
            if (wait != null) await wait.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (Documents.TryGetValue(key ?? "", out var set)) return set;
            return ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry);
        }

        public bool TryGetCachedListingDocuments(string key, out ListingDocumentSet? documents) =>
            Cached.TryGetValue(key ?? "", out documents);
    }

    private sealed class RecordingPanel : IUiPanel
    {
        public int ButtonCount { get; private set; }
        public List<RecordingButton> Buttons { get; } = new();
        public List<RecordingLabel> Labels { get; } = new();
        public string Text { get; set; } = "Install mods";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public UiCaptureMode CaptureMode { get; set; }
        public string ContentAlignment { get; set; } = SettingsAlign.Centre;
        public string Align { get; set; } = SettingsAlign.Centre;

        public IUiButton Button(string text)
        {
            ButtonCount++;
            var button = new RecordingButton { Text = text ?? "" };
            Buttons.Add(button);
            return button;
        }

        public IUiInput Input(string text, string initial = "") => new RecordingInput { Text = text ?? "", Value = initial ?? "" };

        public IUiLabel Label(string text) => AddLabel(text, "label");

        public IUiLabel Heading(string text) => AddLabel(text, "heading");

        public IUiLabel Note(string text) => AddLabel(text, "note");

        private RecordingLabel AddLabel(string text, string kind)
        {
            var label = new RecordingLabel { Text = text ?? "", Kind = kind };
            Labels.Add(label);
            return label;
        }

        public IUiLabel Fact(string name, string value) => Label((name ?? "") + ": " + (value ?? ""));

        public IUiToggle Toggle(string text, bool initial = false) => throw new NotSupportedException();

        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();

        public void Show() { }

        public void Hide() { }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingButton : IUiButton
    {
        private Action? _click;
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";

        public IDisposable OnClick(Action handler)
        {
            _click = handler;
            return this;
        }

        public void Click() => _click?.Invoke();

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingInput : IUiInput
    {
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<string> handler) => this;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingLabel : IUiLabel
    {
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Kind { get; set; } = "label";
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }
}
