using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using System.Linq;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Install-tab identities, search rank, in-place refilter, targets, surfaces and the footer on the LIST2 list view. Each fact names the
/// guard whose removal must fail the test.
/// </summary>
public sealed class InstallModsPaneTests : IDisposable
{
    private readonly FakeCatalog _catalog = new();
    private readonly InstallModsPane _pane;
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();

    public InstallModsPaneTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        // The automatic README run parks after its first key: nothing lands from a background thread mid-test.
        _pane = new InstallModsPane(_catalog, new NeverClock(), null);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        foreach (var panel in _panels) panel.Dispose();
        UiApi.ResetForTests();
        UiLayoutBatch.ResetForTests();
        LocalServerFolder.ReparseLookup = null;
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ShareLookup = null;
    }

    [Fact]
    public void Catalog_row_cannot_open_as_a_loaded_plugin()
    {
        var entry = Listing("dyno-ExampleCommandFramework", "Example Command Framework");
        _catalog.Entries.Add(entry);
        _pane.SelectListing(entry.Key);
        var identity = _pane.SelectedIdentity;
        Assert.NotNull(identity);
        Assert.Equal(ModsIdentityKind.CatalogListing, identity!.Value.Kind);
        Assert.False(identity.Value.CanOpenAsLoadedPlugin);
        Assert.NotEqual(ModsWorkspaceIdentity.Installed(entry.Key), identity.Value);
        Assert.False(ModsTabState.CatalogRowOpensAsLoadedPlugin(identity.Value));
        // Revert: SelectListing storing Installed(...) or CanOpenAsLoadedPlugin true fails this.
    }

    [Fact]
    public void Search_matches_display_name_and_id_not_a_loaded_plugin_open()
    {
        var named = Listing("owner-pack", "Blood Craft", id: "");
        var described = Listing("owner-other", "Other", id: "example.blood", description: "adds extra blood rituals");
        Assert.True(InstallModsPane.MatchesSearch(named, "blood"));
        Assert.True(InstallModsPane.MatchesSearch(described, "rituals"));
        Assert.False(InstallModsPane.MatchesSearch(named, "zzz"));
        Assert.False(ModsWorkspaceIdentity.Catalog(named.Key).CanOpenAsLoadedPlugin);
        // Revert: dropping display-name match leaves "blood" unmatched.
    }

    [Fact]
    public void Search_is_description_first_and_ignores_raw_api_metadata()
    {
        const string uuid = "f21c391c-0bc5-431d-a233-95323b95e01b";
        const string url = "https://thunderstore.io/package/download/x/uuid-pack/1.0.0/";
        var described = Listing(
            "owner-bags",
            "BagPlus",
            description: "Adds extra inventory slots for vampire bags",
            owner: "dyno",
            listingUrl: url,
            downloadUrl: url,
            foreignDependencies: new[] { "other-dep-" + uuid });
        var namedOnly = Listing("owner-blood", "Blood Craft", description: "unrelated text", owner: "other");
        var metaOnly = Listing(
            "owner-" + uuid,
            "Meta Pack",
            id: uuid,
            description: "plain listing",
            listingUrl: "https://example/" + uuid,
            downloadUrl: url);

        Assert.True(InstallModsPane.MatchesSearch(described, "inventory"));
        Assert.True(InstallModsPane.MatchesSearch(described, "vampire bags"));
        Assert.False(InstallModsPane.MatchesSearch(described, "inventory zzz"));
        Assert.False(InstallModsPane.MatchesSearch(described, uuid));
        Assert.False(InstallModsPane.MatchesSearch(described, "thunderstore.io"));
        Assert.False(InstallModsPane.MatchesSearch(metaOnly, uuid));
        Assert.False(InstallModsPane.MatchesSearch(metaOnly, url));
        Assert.Equal(2, InstallModsPane.SearchRank(described, "inventory"));
        Assert.Equal(1, InstallModsPane.SearchRank(namedOnly, "blood"));
        Assert.Equal(0, InstallModsPane.SearchRank(described, uuid));

        var nameHit = Listing("owner-hud", "Inventory HUD", description: "unrelated", owner: "x");
        _catalog.Entries.Add(namedOnly);
        _catalog.Entries.Add(nameHit);
        _catalog.Entries.Add(described);
        _pane.Search = "inventory";
        var ranked = _pane.VisibleEntries();
        Assert.Equal(2, ranked.Count);
        // LIST2: the list keeps the selected sort (downloads, then index order), not the rank.
        Assert.Same(nameHit, ranked[0]);
        Assert.Same(described, ranked[1]);
        _pane.Search = "blood craft";
        ranked = _pane.VisibleEntries();
        Assert.Same(namedOnly, ranked[0]);
        // INSTALLSIDES1: the name cell is the name over the author; the one-liner opens the drawer.
        string caption = InstallModsPane.NameCellText(described);
        Assert.StartsWith("BagPlus", caption, StringComparison.Ordinal);
        Assert.Contains("\n" + InstallModsCopy.ByPrefix, caption, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory", caption, StringComparison.OrdinalIgnoreCase);
        // Revert: searching Key/url/uuid, dropping AND, or a one-line name cell fails this.
    }

    [Fact]
    public void Search_and_chips_refilter_the_built_rows_in_place_without_rebuilding()
    {
        _catalog.Entries.Add(Listing("owner-blood", "Blood Craft", description: "rituals"));
        _catalog.Entries.Add(Listing("owner-other", "Other Pack", description: "bags"));
        var panel = NewPanel();
        _pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        int hosted = _runtime.Rows.Count;
        int live = UiCellRowHandle.LiveCountForTests;
        // LIST3: the search is an input cell of the toolbar row (no separate kit input row).
        var toolbar = _pane.ToolbarRow;
        var field = _pane.SearchCell;
        Assert.NotNull(toolbar);
        Assert.NotNull(field);
        Assert.Equal(CellKind.Input, field!.Kind);
        Assert.Equal(InstallModsCopy.SearchPlaceholder, field.Placeholder);
        Assert.Equal("", field.Text);
        Assert.Equal(2, _pane.Rows.Count);
        Assert.All(_pane.Rows, r => Assert.True(r.Row.Enabled));
        Assert.Equal("2 listings", _pane.CountText());

        _pane.Search = "blood";
        Assert.Equal(hosted, _runtime.Rows.Count);
        Assert.Equal(live, UiCellRowHandle.LiveCountForTests);
        Assert.Same(toolbar, _pane.ToolbarRow);
        Assert.Same(field, _pane.SearchCell);
        Assert.Equal("blood", field.Text);
        Assert.Equal("blood", _pane.Search);
        Assert.True(_pane.Rows.Single(r => r.Row.Key == "owner-blood").Row.Visible);
        Assert.False(_pane.Rows.Single(r => r.Row.Key == "owner-other").Row.Visible);
        Assert.Equal("1 listing filtered · 1 shown", _pane.CountText());
        Assert.Equal((1, 1, 2), _pane.MatchCount);
        Assert.Equal("blood", field.Text);
        // A hidden row leaves the bridge's visible-row registry; the shown one stays addressable.
        Assert.Null(UiCellRowHandle.DescribeVisible("owner-other"));
        Assert.NotNull(UiCellRowHandle.DescribeVisible("owner-blood"));

        _pane.SetSideFilter(InstallSideFilter.Client);
        Assert.Equal(hosted, _runtime.Rows.Count);
        Assert.False(_pane.Rows.Single(r => r.Row.Key == "owner-blood").Row.Visible);
        Assert.Equal("2 listings filtered · 0 shown", _pane.CountText());
        Assert.True(_pane.EmptyLabelHandle!.Visible);

        _pane.ClearFilters();
        Assert.Equal(hosted, _runtime.Rows.Count);
        Assert.Equal(live, UiCellRowHandle.LiveCountForTests);
        Assert.Equal("", field.Text);
        Assert.True(_pane.AtDefaults);
        Assert.True(_pane.Rows.Single(r => r.Row.Key == "owner-other").Row.Visible);
        Assert.False(_pane.EmptyLabelHandle!.Visible);
        Assert.Equal("2 listings", _pane.CountText());
        string body = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs"),
            "public void RefilterListings");
        SourcePin.AssertUncommented(body, "UiLayoutBatch.Begin()");
        SourcePin.AssertUncommented(body, "UiLayoutBatch.End()");
        SourcePin.AssertNoStatementContaining(body, "panel.CellRow(");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string relayout = SourcePin.MethodBody(runtime, "public void Relayout()");
        SourcePin.AssertUncommented(relayout, "UiLayoutBatch.ShouldRunLayout()");
        // Revert: rebuilding the panel (new Button/CellRow calls) on a search or chip change fails this.
    }

    [Fact]
    public void Footer_is_refresh_index_and_close_and_the_pane_can_hot_load()
    {
        // HOT2: the hot-load lane is reachable from this pane now (admission snapshot, executor and
        // pre-write recheck all exist). The footer claim below is unrelated and unchanged.
        Assert.True(InstallModsPane.CanHotLoadFromThisPane);
        Assert.False(InstallModsPane.FooterHasOverviewRefresh);
        // LIST3 item 9: the Install tab pins no footer rows (no Refresh index, no Close Mods).
        Assert.Empty(InstallModsPane.SidebarFooterCaptions);
        Assert.Equal(0, InstallModsPane.FooterRows);
        Assert.DoesNotContain(InstallModsCopy.OverviewRefresh, InstallModsPane.SidebarFooterCaptions);
        _catalog.Entries.Add(Listing("owner-blood", "Blood Craft"));
        var panel = new RecordingInstallPanel();
        int refreshes = 0, closes = 0;
        _pane.Build(panel, true, false, null, () => refreshes++, () => closes++, DateTime.UtcNow);
        // LIST3 item 9: a ready list builds no kit buttons at all (no footer; the pending bar only when something is pending).
        Assert.Empty(panel.Buttons);
        Assert.Null(panel.ButtonNamed(InstallModsCopy.RefreshIndex));
        Assert.Null(panel.ButtonNamed(InstallModsCopy.CloseMods));
        Assert.Equal(0, refreshes);
        Assert.Equal(0, closes);
        // The surfaces: an error shows the message and a Retry that refreshes the index (the one manual refresh left).
        _catalog.StatusOverride = new InstallProgress(InstallStatusKind.Failed, "boom");
        _pane.NoteLoadResult(new CatalogLoadResult(false, "boom", 0, null));
        var errored = new RecordingInstallPanel();
        _pane.Build(errored, true, false, null, () => refreshes++, () => closes++, DateTime.UtcNow);
        Assert.Contains(errored.Labels, l => l.Text == "boom");
        Assert.Single(errored.Buttons);
        errored.ButtonNamed(InstallModsCopy.ErrorRetry)!.Click();
        Assert.Equal(1, refreshes);
        Assert.Equal(0, closes);
        Assert.Empty(_pane.Rows);
        // Revert: adding Overview / refresh to the Install footer, or enabling hot-load, fails this.
    }

    [Fact]
    public void Target_control_is_client_only_without_a_validated_server_and_Both_is_never_implied()
    {
        var none = InstallModsPane.TargetChoices(null);
        Assert.Single(none);
        Assert.Equal(InstallTarget.Client, none[0].Target);
        Assert.Equal(InstallModsCopy.TargetClient, none[0].Caption);

        var empty = new LocalServerFolder();
        var without = InstallModsPane.TargetChoices(empty);
        Assert.Single(without);
        Assert.False(InstallModsPane.BothImpliedByMetadata(PackageSide.Both));
        Assert.Equal(InstallTarget.Client, InstallModsPane.DefaultTarget);
        Assert.Equal(InstallTarget.Client, InstallModsPane.ResolveTarget(null, PackageSide.Both, empty));
        Assert.False(_pane.TrySetTarget(InstallTarget.Both, empty, out var refusal));
        Assert.Equal(InstallReasons.NoConfiguredLocalServer, refusal);
        Assert.Equal(InstallTarget.Client, _pane.Target);
        // Revert: adding Both without a server, or implying Both from PackageSide.Both, fails this.
    }

    [Fact]
    public void Validated_server_offers_all_three_targets_and_Both_is_explicit()
    {
        var server = ValidatedFolder();
        var choices = InstallModsPane.TargetChoices(server);
        Assert.Equal(3, choices.Count);
        Assert.Equal(InstallTarget.Client, choices[0].Target);
        Assert.Equal(InstallTarget.LocalServer, choices[1].Target);
        Assert.Equal(InstallTarget.Both, choices[2].Target);
        Assert.True(choices[1].Available);
        Assert.True(choices[2].Available);
        Assert.True(_pane.TrySetTarget(InstallTarget.Both, server, out _));
        Assert.Equal(InstallTarget.Both, _pane.Target);
        Assert.False(InstallModsPane.BothImpliedByMetadata(PackageSide.Both));
        // Revert: hiding Both after validation, or auto-selecting it, fails this.
    }

    [Fact]
    public void Later_invalid_server_keeps_targets_visible_but_unavailable_and_blocks_them()
    {
        var server = ValidatedFolder();
        server.Apply(server.Current.CanonicalPath);
        LocalServerFolder.WriteAccessOverride = _ => false;
        var now = server.Revalidate();
        Assert.False(now.Available);
        Assert.True(server.IsUnavailable);
        var choices = InstallModsPane.TargetChoices(server);
        Assert.Equal(3, choices.Count);
        Assert.True(choices[0].Available);
        Assert.False(choices[1].Available);
        Assert.False(choices[2].Available);
        Assert.True(InstallModsPane.TargetOperationBlocked(InstallTarget.LocalServer, server));
        Assert.True(InstallModsPane.TargetOperationBlocked(InstallTarget.Both, server));
        Assert.False(InstallModsPane.TargetOperationBlocked(InstallTarget.Client, server));
        // Revert: falling back to client-only (dropping the two choices) or leaving them available fails this.
    }

    [Fact]
    public void Empty_loading_and_error_states()
    {
        Assert.Equal(InstallPaneSurface.NoSourceEnabled, _pane.Surface(false, false));
        Assert.Equal(InstallModsCopy.NoSourceEnabled, InstallModsPane.SurfaceMessage(InstallPaneSurface.NoSourceEnabled, null));

        _catalog.StatusOverride = new InstallProgress(InstallStatusKind.LoadingIndex, "loading");
        Assert.Equal(InstallPaneSurface.Loading, _pane.Surface(true, false));
        Assert.Equal(InstallModsCopy.Loading, InstallModsPane.SurfaceMessage(InstallPaneSurface.Loading, null));

        _catalog.StatusOverride = new InstallProgress(InstallStatusKind.Failed, "boom");
        _pane.NoteLoadResult(new CatalogLoadResult(false, "boom", 0, null));
        Assert.Equal(InstallPaneSurface.Error, _pane.Surface(true, false));
        Assert.Equal("boom", InstallModsPane.SurfaceMessage(InstallPaneSurface.Error, _pane.LastError));
        Assert.Equal(InstallModsCopy.ErrorRetry, InstallModsCopy.ErrorRetry);

        _catalog.StatusOverride = InstallProgress.Idle;
        var unloaded = new InstallModsPane(new FakeCatalog());
        Assert.Equal(InstallPaneSurface.IndexNotLoaded, unloaded.Surface(true, false));
        Assert.Equal(InstallModsCopy.IndexNotLoaded, InstallModsPane.SurfaceMessage(InstallPaneSurface.IndexNotLoaded, null));
        // Revert: collapsing error into ready, or dropping NoSourceEnabled, fails the matching assert.
    }

    [Fact]
    public void Listing_readme_cap_is_applied_across_blocks_with_the_note_last()
    {
        string longPara = new string('x', 80);
        var parts = new List<string>();
        int n = (MarkdownPlain.DefaultMaxChars / longPara.Length) + 4;
        for (int i = 0; i < n; i++)
            parts.Add("p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + longPara);
        string markdown = string.Join("\n\n", parts);
        var blocks = InstallModsPane.FormatReadmeBlocks(markdown, "https://thunderstore.io/c/v-rising/p/dyno/ExampleCommandFramework/");
        Assert.True(blocks.Count > 1);
        Assert.Equal(InstallModsCopy.ReadMoreOnThunderstore, blocks[blocks.Count - 1].Text);
        int content = 0;
        for (int i = 0; i < blocks.Count - 1; i++)
        {
            Assert.NotEqual(InstallModsCopy.ReadMoreOnThunderstore, blocks[i].Text);
            content += blocks[i].Text.Length;
            Assert.DoesNotContain("\n\n", blocks[i].Text, StringComparison.Ordinal);
            Assert.True(blocks[i].Text.Length <= MarkdownPlain.DefaultMaxChars, blocks[i].Text.Length.ToString());
        }

        Assert.True(content <= MarkdownPlain.DefaultMaxChars + MarkdownPlain.Ellipsis.Length);
        Assert.True(MarkdownPlain.ContentLength(MarkdownPlain.RenderBlocks(markdown, int.MaxValue)) > MarkdownPlain.DefaultMaxChars);
        string noted = InstallModsPane.FormatReadme(markdown, "https://thunderstore.io/c/v-rising/p/dyno/ExampleCommandFramework/");
        Assert.EndsWith(InstallModsCopy.ReadMoreOnThunderstore, noted, StringComparison.Ordinal);
        // Revert: capping only the joined string into one label, or dropping the Thunderstore note, fails this.
    }

    [Fact]
    public void Execute_goes_to_pending_and_the_catalog_has_no_reload_write()
    {
        _catalog.Entries.Add(Verified("example.mod", "Mod"));
        _pane.SelectListing("example.mod");
        _catalog.Plans["h"] = new InstallPlan(
            "h",
            _catalog.Entries[0],
            InstallTarget.Client,
            InstallDecision.RestartClientRequired,
            InstallReasons.RestartClient,
            Array.Empty<ArtifactHash>(),
            Array.Empty<PlanDependency>(),
            "example.mod",
            "example.mod.dll");
        var executed = _pane.ExecuteToPending("h");
        Assert.True(executed.Ok);
        Assert.True(_catalog.ExecutedPending);
        Assert.False(_catalog.WroteReload);
        // HOT2: the lane exists (CanHotLoadFromThisPane is true), and that is exactly why this test
        // still matters — a RestartClientRequired plan must go to pending, never to the reload folder.
        // The decision decides, not the availability of the lane.
        Assert.True(InstallModsPane.CanHotLoadFromThisPane);
        // Revert: a reload-folder write (WroteReload) for a restart plan fails this.
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.pane1");
        _panels.Add(panel);
        return panel;
    }

    /// <summary>A clock whose delay never elapses (it only cancels): the automatic README run parks after its first key.</summary>
    private sealed class NeverClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);

        public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
            return tcs.Task;
        }
    }

    private static CatalogEntry Listing(
        string key,
        string display,
        string id = "",
        string description = "",
        string? owner = null,
        string? listingUrl = null,
        string? downloadUrl = null,
        IReadOnlyList<string>? foreignDependencies = null,
        string? releasedUtc = "2026-09-11T00:00:00Z",
        string? dateUpdated = null)
    {
        var dash = key.IndexOf('-');
        string ns = dash > 0 ? key.Substring(0, dash) : (owner ?? "n");
        return new CatalogEntry(
            new ManifestMod(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, ns, key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: display,
            author: owner,
            description: description,
            listingUrl: listingUrl,
            foreignDependencies: foreignDependencies,
            downloadUrl: downloadUrl,
            owner: owner,
            packageName: key,
            dateUpdated: dateUpdated,
            releasedUtc: releasedUtc);
    }

    private static CatalogEntry Verified(string id, string display) =>
        new(
            new ManifestMod(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true, "n", id, "aa", 1),
            CatalogProvenance.ManifestVerified,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: display,
            side: PackageSide.Client);

    private static LocalServerFolder ValidatedFolder()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "progenitor-tabs2-gone-" + Guid.NewGuid().ToString("N"));
        var folder = new LocalServerFolder();
        var valid = LocalServerFolderResult.Valid(path, path, "gone", path);
        var field = typeof(LocalServerFolder).GetField("_current", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var had = typeof(LocalServerFolder).GetField("_hadValid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field!.SetValue(folder, valid);
        had!.SetValue(folder, true);
        return folder;
    }

    private sealed class FakeCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();
        public Dictionary<string, InstallPlan> Plans { get; } = new(StringComparer.OrdinalIgnoreCase);
        public InstallProgress? StatusOverride { get; set; }
        public bool ExecutedPending { get; private set; }
        public bool WroteReload { get; private set; }

        public InstallProgress Status => StatusOverride ?? InstallProgress.Idle;

        public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogLoadResult(true, null, Entries.Count, "stamp"));

        public IReadOnlyList<CatalogEntry> Search(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Entries;
            var hits = new List<CatalogEntry>();
            for (int i = 0; i < Entries.Count; i++)
                if (InstallModsPane.MatchesSearch(Entries[i], query)) hits.Add(Entries[i]);
            return hits;
        }

        public bool TryGetEntry(string key, out CatalogEntry? entry)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (!string.Equals(Entries[i].Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Entries[i].Manifest.Id, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                entry = Entries[i];
                return true;
            }

            entry = null;
            return false;
        }

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new InspectCallResult(false, InstallReasons.FetchNoUrl, null, null));

        public PlanCallResult Plan(string key, InstallTarget target)
        {
            if (Plans.TryGetValue(key, out var plan) || Plans.TryGetValue("h", out plan!))
                return new PlanCallResult(true, null, plan);
            return new PlanCallResult(false, InstallReasons.NotInspected, null);
        }

        public ApproveCallResult Approve(string planHash) =>
            new(true, null, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash)
        {
            ExecutedPending = true;
            WroteReload = false;
            return new ExecuteCallResult(true, null, "pending/" + planHash, false);
        }

        public Dictionary<string, ListingDocumentSet> Documents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int DocumentLoadCalls { get; private set; }

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken)
        {
            DocumentLoadCalls++;
            if (Documents.TryGetValue(key ?? "", out var set))
                return Task.FromResult(set);
            return Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
        }
    }

    private sealed class RecordingInstallPanel : IUiPanel
    {
        public int ButtonCount { get; private set; }
        public int RelayoutCount { get; set; }
        public RecordingInstallInput? Search { get; private set; }
        public List<RecordingInstallButton> Buttons { get; } = new();
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
        public List<RecordingInstallLabel> Labels { get; } = new();

        internal void NoteRelayout()
        {
            if (UiLayoutBatch.ShouldRunLayout()) RelayoutCount++;
        }

        public RecordingInstallButton? Listing(string caption)
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                string text = Buttons[i].Text;
                if (string.Equals(text, caption, StringComparison.Ordinal)
                    || text.StartsWith(caption + "  ·  ", StringComparison.Ordinal)
                    || text.StartsWith(caption + "\n", StringComparison.Ordinal)
                    || text.StartsWith(caption + " ", StringComparison.Ordinal))
                    return Buttons[i];
            }

            return null;
        }

        public RecordingInstallButton? ButtonNamed(string caption)
        {
            for (int i = 0; i < Buttons.Count; i++)
            {
                if (string.Equals(Buttons[i].Text, caption, StringComparison.Ordinal))
                    return Buttons[i];
            }

            return null;
        }

        public IUiButton Button(string text)
        {
            ButtonCount++;
            var button = new RecordingInstallButton(this) { Text = text ?? "", Align = ContentAlignment };
            Buttons.Add(button);
            return button;
        }

        public IUiInput Input(string text, string initial = "")
        {
            Search = new RecordingInstallInput { Text = text ?? "", Value = initial ?? "" };
            return Search;
        }

        public IUiLabel Label(string text) => AddLabel(text, "label");

        public IUiLabel Heading(string text) => AddLabel(text, "heading");

        public IUiLabel Note(string text) => AddLabel(text, "note");

        private RecordingInstallLabel AddLabel(string text, string kind)
        {
            var label = new RecordingInstallLabel(this)
            {
                Text = text ?? "",
                Align = ContentAlignment,
                Kind = kind,
            };
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

    private sealed class RecordingInstallButton : IUiButton
    {
        private readonly RecordingInstallPanel _owner;
        private bool _visible = true;

        public RecordingInstallButton(RecordingInstallPanel owner) => _owner = owner;

        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "";
        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                _owner.NoteRelayout();
            }
        }
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        private Action? _click;
        public IDisposable OnClick(Action handler)
        {
            _click = handler;
            return this;
        }

        public void Click() => _click?.Invoke();

        public void Dispose() => IsDisposed = true;
    }

    private sealed class RecordingInstallInput : IUiInput
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

    private sealed class RecordingInstallLabel : IUiLabel
    {
        private readonly RecordingInstallPanel _owner;
        private bool _visible = true;
        private string _text = "";

        public RecordingInstallLabel(RecordingInstallPanel owner) => _owner = owner;

        public string Align { get; set; } = SettingsAlign.Centre;
        public string Kind { get; set; } = "label";
        public string Text
        {
            get => _text;
            set
            {
                string next = value ?? "";
                if (string.Equals(_text, next, StringComparison.Ordinal)) return;
                _text = next;
                _owner.NoteRelayout();
            }
        }
        public bool Visible
        {
            get => _visible;
            set
            {
                if (_visible == value) return;
                _visible = value;
                _owner.NoteRelayout();
            }
        }
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }
}
