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
/// INSTALLSIDES1 (Option A): the Install pane's per-side row — the expand button as the drawer's
/// only toggle, the drawer's 80% sizing and rail, the server picker and the side buttons' routes.
/// Game-free: the pane against a fake catalog and the recording UI runtime.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class InstallSidesPaneTests : IDisposable
{
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-sides-" + Guid.NewGuid().ToString("N"));

    public InstallSidesPaneTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiDialog.ForgetForTests();
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => InstallSearchFilterTests.KnownClientBuild;
        InstallModsPane.ProcessFilterProbe = null;
        InstallModsPane.PlainOfferProbe = null;
        InstallModsPane.RememberedPick = null;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiDialog.ForgetForTests();
        InstallModsPane.RememberedPick = null;
        UiLayoutBatch.ResetForTests();
        CatalogUpdateTags.ResetProvidersForTests();
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.sides");
        _panels.Add(panel);
        return panel;
    }

    private sealed class Rig
    {
        private readonly InstallSidesPaneTests _owner;

        public Rig(InstallSidesPaneTests owner, FakeCatalog catalog, Action<InstallModsPane>? setup = null, LocalServerFolder? server = null)
        {
            _owner = owner;
            _server = server;
            Catalog = catalog;
            Pane = new InstallModsPane(catalog, new NeverClock(), null);
            Pane.SetDetailRebuild(Rebuild);
            Pane.DialogPanelFactory = _ => owner.NewPanel();
            setup?.Invoke(Pane);
            Rebuild();
        }

        public FakeCatalog Catalog { get; }
        public InstallModsPane Pane { get; }
        public int Rebuilds { get; private set; }
        private IUiPanel? _panel;
        private readonly LocalServerFolder? _server;

        public void Rebuild()
        {
            Rebuilds++;
            _panel?.Dispose();
            _panel = _owner.NewPanel();
            Pane.Build(_panel, true, false, _server, () => { }, () => { }, DateTime.UtcNow);
        }

        public bool Click(string label) => UiCellRowHandle.TryInvoke(label, out _);

        public CellSpec Cell(string key, string cell) =>
            Pane.Rows.Single(r => r.Row.Key == key).Row.Cells.Single(c => c.Key == cell);
    }

    private static CatalogEntry Listing(string key, string side = "Client", bool nsfw = false, string? description = null) =>
        Tagged(key, new[] { side, "Oakveil Update" }, nsfw, description);

    private static CatalogEntry Tagged(string key, string[] categories, bool nsfw = false, string? description = null) =>
        new(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "o", key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: key.Substring(2),
            description: description ?? "one-liner of " + key,
            owner: "o",
            packageName: key.Substring(2),
            categories: categories,
            releasedUtc: "2026-09-11T00:00:00Z",
            downloads: 10,
            hasNsfwContent: nsfw);

    // ---- The expand button --------------------------------------------------------------------

    [Fact]
    public void No_click_on_a_row_its_title_or_its_buttons_opens_or_closes_a_drawer()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a"), Listing("o-b") });
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;

        // The row itself and its title: nothing.
        rig.Click("o-a");
        Assert.Null(pane.DrawerKey);
        Assert.DoesNotContain(rig.Cell("o-a", InstallModsPane.CellName).Kind, new[] { CellKind.Button });

        // Open a's drawer, then press b's Install: a's drawer stays open, b's does not open.
        Assert.True(rig.Click("o-a/" + InstallModsPane.CellExpand));
        Assert.Equal("o-a", pane.DrawerKey);
        Assert.True(rig.Click("o-b/" + InstallModsPane.CellClientA));
        Assert.Equal("o-a", pane.DrawerKey);
        Assert.Equal("o-b", pane.FlowKey);
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        pane.PromptNo();
        Assert.Equal("o-a", pane.DrawerKey);

        // The open row's own button does not close it either.
        Assert.True(rig.Click("o-a/" + InstallModsPane.CellClientA));
        Assert.Equal("o-a", pane.DrawerKey);
        pane.PromptNo();

        // The expand button and the drawer's Collapse do.
        Assert.True(rig.Click(InstallModsPane.DrawerKeyPrefix + "o-a/" + InstallModsPane.CellCollapse));
        Assert.Null(pane.DrawerKey);
        Assert.True(rig.Click("o-a/" + InstallModsPane.CellExpand));
        Assert.True(rig.Click("o-a/" + InstallModsPane.CellExpand));
        Assert.Null(pane.DrawerKey);
    }

    /// <summary>
    /// INSTALLSIDES2 (user feedback 2026-10-03): expanding, collapsing and switching drawers keep the
    /// list still. The clicked row is measured before the rebuild and anchored back to that distance
    /// from the viewport's top after it; a rebuild nobody clicked writes nothing.
    /// </summary>
    [Fact]
    public void Toggling_a_drawer_keeps_the_clicked_row_where_it_was_and_nothing_else_moves_the_list()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a"), Listing("o-b") });
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        var calls = new List<(string Key, float? Wanted)>();
        // The row sits 240 px down the viewport before the toggle; after the rebuild it measures
        // elsewhere (the drawer above it closed), and the anchor puts it back.
        pane.AnchorProbe = (key, wanted) =>
        {
            calls.Add((key, wanted));
            return wanted == null ? (240f, 900f) : (60f, 720f);
        };

        // Open a: measured before, anchored after, at the same 240 px.
        rig.Click("o-a/" + InstallModsPane.CellExpand);
        Assert.Equal(("o-a", (float?)null), calls[0]);
        Assert.Equal(("o-a", (float?)240f), calls[1]);
        Assert.True(pane.TryDescribeSidebarRow(pane.DrawerHeadRow!, out var first));
        var at = (Dictionary<string, object?>)first!["anchoredTo"]!;
        Assert.Equal("o-a", at["key"]);
        Assert.Equal(240d, at["wantedY"]);

        // Switching to b (a's drawer above it closes): b is the anchored row.
        calls.Clear();
        rig.Click("o-b/" + InstallModsPane.CellExpand);
        Assert.Equal("o-b", calls[0].Key);
        Assert.Equal(("o-b", (float?)240f), calls[1]);

        // Collapse from the drawer's own button anchors the row too.
        calls.Clear();
        rig.Click(InstallModsPane.DrawerKeyPrefix + "o-b/" + InstallModsPane.CellCollapse);
        Assert.Null(pane.DrawerKey);
        Assert.Equal(("o-b", (float?)240f), calls[1]);

        // A rebuild nobody clicked does not touch the list.
        calls.Clear();
        rig.Rebuild();
        Assert.Empty(calls);
        // An unmeasurable row (no scroll view) is left alone, not guessed.
        pane.AnchorProbe = (key, wanted) => { calls.Add((key, wanted)); return null; };
        rig.Click("o-a/" + InstallModsPane.CellExpand);
        Assert.Single(calls);
    }

    [Fact]
    public void The_anchor_offset_compensates_and_clamps_only_to_the_content()
    {
        // Drawer A (300 px) above row B closed: B measured 300 px higher, so the list scrolls up 300.
        Assert.Equal(600f, MenuPanelLayout.AnchorOffset(900f, rowY: -60f, wantedY: 240f, contentHeight: 5000f, viewportHeight: 700f), 3);
        // Opening below the row: the row did not move, nothing is written.
        Assert.Equal(900f, MenuPanelLayout.AnchorOffset(900f, 240f, 240f, 5000f, 700f), 3);
        // Clamped only to what the content allows.
        Assert.Equal(0f, MenuPanelLayout.AnchorOffset(100f, -400f, 240f, 5000f, 700f), 3);
        Assert.Equal(300f, MenuPanelLayout.AnchorOffset(900f, 240f, 240f, 1000f, 700f), 3);
        Assert.Equal(0f, MenuPanelLayout.AnchorOffset(900f, 240f, 240f, 500f, 700f), 3);
        // The pane no longer reveals: no RevealOpenedDrawer, no row-to-top.
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        Assert.DoesNotContain("RevealOpenedDrawer", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("UiReveal.TryReveal(", pane, StringComparison.Ordinal);
        SourcePin.AssertStatementExactly(SourcePin.MethodBody(pane, "public void Build("), "AnchorClickedRow();");
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertStatementExactly(SourcePin.MethodBody(runtime, "static UnityUiRuntime()"), "UiReveal.Anchor = TryAnchorRow;");
        string anchor = SourcePin.MethodBody(runtime, "internal static bool TryAnchorRow(");
        SourcePin.AssertUncommented(anchor, "rowY = viewRect.MaxY - rowRect.MaxY;");
        SourcePin.AssertUncommented(anchor, "float next = MenuPanelLayout.AnchorOffset(was, rowY, wantedY, ContentHeightOf(content), viewH);");
    }

    [Fact]
    public void Explicit_listing_navigation_reveals_at_top_once_after_layout()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a"), Listing("o-b") });
        var rig = new Rig(this, catalog);
        var frames = new Queue<Action>();
        rig.Pane.FrameScheduler = step => frames.Enqueue(step);
        var writes = new List<float>();
        rig.Pane.AnchorProbe = (key, wanted) =>
        {
            if (wanted.HasValue) writes.Add(wanted.Value);
            return (620f, 620f);
        };
        rig.Pane.SelectListing("o-b");
        rig.Rebuild();
        rig.Pane.RevealListing("o-b");
        Assert.Empty(writes);
        while (frames.Count > 0) frames.Dequeue()();
        Assert.Equal(new[] { 0f }, writes);
        rig.Rebuild();
        while (frames.Count > 0) frames.Dequeue()();
        Assert.Single(writes);
    }

    /// <summary>
    /// Review of PR 133, finding 4: a reveal whose listing the filters hide is dropped, so a later build (the filters
    /// cleared) does not jump the list to it.
    /// </summary>
    [Fact]
    public void A_reveal_of_a_filtered_out_listing_is_dropped()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a", side: "Server"), Listing("o-b") });
        var rig = new Rig(this, catalog);
        var frames = new Queue<Action>();
        rig.Pane.FrameScheduler = step => frames.Enqueue(step);
        var writes = new List<float>();
        rig.Pane.AnchorProbe = (key, wanted) =>
        {
            if (wanted.HasValue) writes.Add(wanted.Value);
            return (620f, 620f);
        };
        rig.Pane.Filters.Side = InstallSideFilter.Server;
        rig.Pane.SelectListing("o-b");
        rig.Rebuild();
        rig.Pane.RevealListing("o-b");
        while (frames.Count > 0) frames.Dequeue()();
        Assert.Empty(writes);

        rig.Pane.Filters.Side = InstallSideFilter.Any;
        rig.Rebuild();
        while (frames.Count > 0) frames.Dequeue()();
        Assert.Empty(writes);
    }

    // ---- The drawer --------------------------------------------------------------------------

    [Fact]
    public void The_drawer_is_joined_to_its_row_and_compact_with_the_next_row_in_view()
    {
        var catalog = new FakeCatalog();
        // A long description so the body is not shortened to its text.
        catalog.Entries.Add(Listing("o-a", description: string.Join(" ", Enumerable.Range(0, 400).Select(i => "word" + i))));
        catalog.Entries.Add(Listing("o-b"));
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        pane.SetListViewport(708f);
        rig.Click("o-a/" + InstallModsPane.CellExpand);

        var head = pane.DrawerHeadRow!;
        var body = pane.DrawerBodyRow!;
        var hosted = _runtime.Rows.Where(r => r.Alive && !r.Text.StartsWith("dialog", StringComparison.Ordinal)).Select(r => r.Text).ToList();
        int at = hosted.IndexOf("o-a");
        Assert.Equal(new[] { "o-a", InstallModsPane.DrawerKeyPrefix + "o-a", InstallModsPane.DrawerBodyKeyPrefix + "o-a", "o-b" },
            hosted.Skip(at).Take(4).ToArray());
        // Both drawer rows continue the row: selected (the shared fill and the rail), joined above.
        Assert.True(head.Selected);
        Assert.True(body.Selected);
        Assert.True(pane.Rows.Single(r => r.Row.Key == "o-a").Row.Selected);
        Assert.Equal(true, head.Describe()["joinAbove"]);
        Assert.Equal(true, body.Describe()["joinAbove"]);
        // Indented under the mod's name: the first cell is the indent.
        float indent = head.Cells[0].Width.Value + CellLayout.Gap;
        Assert.Equal(InstallDrawerLayout.Indent, indent, 1);
        Assert.Equal(InstallModsPane.CellIndent, body.Cells[0].Key);
        Assert.Equal(InstallModsPane.CellBody, body.Cells[1].Key);
        Assert.Equal(CellKind.ScrollText, body.Cells[1].Kind);

        // INSTALLSIDES2 item 4 (option A): a compact, fixed body — no scroll of its own, at most the
        // compact ceiling, the next listing still in view, and the long README not in it.
        Assert.True(body.Cells[1].NoScroll);
        float bodyHeight = body.Describe()["height"] is float bh ? bh : (float)(double)body.Describe()["height"]!;
        Assert.True(bodyHeight <= InstallDrawerLayout.CompactMaxBody, "body " + bodyHeight);
        float block = InstallDrawerLayout.BlockHeight(bodyHeight, CellLayout.TwoLineRowHeight);
        Assert.True(block + PanelLayout.RowGap + InstallDrawerLayout.NextRowPeek <= 708f + .5f);
        var bodyLines = body.Cells[1].Lines;
        Assert.DoesNotContain(bodyLines, l => l.Text.Contains("word399", StringComparison.Ordinal));
        Assert.True(pane.TryDescribeSidebarRow(body, out var extra));
        Assert.NotNull(extra!["drawerBody"]);
    }

    [Fact]
    public void Height_math_takes_the_share_keeps_the_next_row_and_clamps_both_ways()
    {
        const float row = CellLayout.TwoLineRowHeight;
        // 80% of 708 minus the row, the head and two gaps.
        float body = InstallDrawerLayout.BodyHeight(708f, row);
        Assert.Equal((float)Math.Round(708f * 0.8f - row - InstallDrawerLayout.HeadHeight - 2 * PanelLayout.RowGap), body);
        Assert.InRange(InstallDrawerLayout.BlockHeight(body, row) / 708f, 0.79f, 0.81f);
        // Short text: only what it needs (never under the floor).
        Assert.Equal(200f, InstallDrawerLayout.BodyHeight(708f, row, contentHeight: 200f));
        Assert.Equal(InstallDrawerLayout.MinBodyHeight, InstallDrawerLayout.BodyHeight(708f, row, contentHeight: 20f));
        // A cramped viewport: the floor holds.
        Assert.Equal(InstallDrawerLayout.MinBodyHeight, InstallDrawerLayout.BodyHeight(150f, row));
        // A tall viewport still leaves the next row's peek: 80% is under the ceiling there.
        float tall = InstallDrawerLayout.BodyHeight(2000f, row);
        Assert.True(InstallDrawerLayout.BlockHeight(tall, row) + InstallDrawerLayout.NextRowPeek <= 2000f);
        // Nonsense in, the fallback viewport out.
        Assert.Equal(InstallDrawerLayout.BodyHeight(InstallDrawerLayout.FallbackViewport, row), InstallDrawerLayout.BodyHeight(float.NaN, row));
        Assert.Equal(InstallDrawerLayout.BodyHeight(InstallDrawerLayout.FallbackViewport, row), InstallDrawerLayout.BodyHeight(-5f, row));
        // The estimate grows with the text and wraps long lines.
        var shortLines = new List<CellLine> { new(CellLineKind.Body, "one line") };
        var longLines = new List<CellLine> { new(CellLineKind.Body, new string('x', 400)), new(CellLineKind.Fact, "1.0.0", "Latest version") };
        Assert.True(InstallDrawerLayout.EstimateContentHeight(longLines, 900f) > InstallDrawerLayout.EstimateContentHeight(shortLines, 900f));
        Assert.Equal(0f, InstallDrawerLayout.EstimateContentHeight(Array.Empty<CellLine>(), 900f));
    }

    [Fact]
    public void The_drawer_states_the_facts_including_the_nsfw_flag()
    {
        var catalog = new FakeCatalog();
        var flagged = Listing("o-x", nsfw: true);
        catalog.Entries.Add(flagged);
        var rig = new Rig(this, catalog, pane => pane.ShowNsfwProbe = () => true);
        var lines = rig.Pane.DrawerLines(flagged, rig.Pane.ActionFor(flagged), out _);
        // INSTALLSIDES2 item 4: two facts to a line, in the same order.
        string[] labels = lines.Where(l => l.Kind == CellLineKind.Fact)
            .SelectMany(l => l.HasSecond ? new[] { l.Label, l.Label2 } : new[] { l.Label }).ToArray();
        Assert.Equal(new[]
        {
            InstallModsCopy.FactLatest, InstallModsCopy.FactReleased, InstallModsCopy.FactDownloads, InstallModsCopy.FactSide,
            InstallModsCopy.FactInstalledHere, InstallModsCopy.FactRequires, InstallModsCopy.FactNsfw, InstallModsCopy.FactPackage,
            InstallModsCopy.FactUpdateTag,
        }, labels);
        Assert.Contains(lines, l => (l.Label == InstallModsCopy.FactNsfw && l.Text == InstallModsCopy.NsfwFlagged)
            || (l.Label2 == InstallModsCopy.FactNsfw && l.Text2 == InstallModsCopy.NsfwFlagged));
        Assert.Contains(lines, l => l.Kind == CellLineKind.Body && l.Text == "one-liner of o-x");
        var clean = Listing("o-y");
        Assert.DoesNotContain(rig.Pane.DrawerLines(clean, rig.Pane.ActionFor(clean), out _),
            l => l.Label == InstallModsCopy.FactNsfw || l.Label2 == InstallModsCopy.FactNsfw);
    }

    // ---- The server picker ---------------------------------------------------------------------

    private static ServerPickerInputs Inputs(bool connected, string configured, params LocalServerCandidate[] found) => new()
    {
        Connected = connected,
        ConnectedServer = ServerIdentity.Connected("Forms Test", "127.0.0.1", 9976),
        ConfiguredPath = configured,
        ConfiguredName = configured.Length > 0 ? "Local Box" : "",
        Discovered = found,
    };

    [Fact]
    public void The_picker_sits_on_the_toolbar_names_the_server_column_and_points_the_cells()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-s", side: "Server"));
        var inputs = Inputs(connected: false, configured: "");
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerPickerProbe = () => inputs;
            pane.PrivateGamesSharedProbe = () => true;
        });
        var pane = rig.Pane;

        // Nothing connected, no folder: the private games this client hosts.
        Assert.Equal(ServerPickKind.PrivateGames, pane.PickedServer!.Kind);
        var toolbar = pane.ToolbarRow!.Cells;
        Assert.Equal(new[] { InstallModsPane.CellSearch, InstallModsPane.CellCount, InstallModsPane.CellGap, InstallModsPane.CellServerLabel, InstallModsPane.CellServerPick },
            toolbar.Select(c => c.Key).ToArray());
        var pick = toolbar.Single(c => c.Key == InstallModsPane.CellServerPick);
        Assert.Equal(CellKind.Button, pick.Kind);
        Assert.Equal(CellSkin.Dropdown, pick.Skin);
        Assert.Equal(ServerPicker.PrivateGamesCaption, pick.Text);
        Assert.Equal("Server · Private games", pane.HeaderRow!.Cells.Single(c => c.Key == InstallModsPane.CellServer).Text);
        // A server mod installs from the Client cell; the private games load it from there.
        Assert.Equal(SideCells.InstallCaption, rig.Cell("o-s", InstallModsPane.CellClientA).Text);
        Assert.Equal(SideCells.SameAsClient, rig.Cell("o-s", InstallModsPane.CellServer).Text);

        // None: this client only. A server mod is not needed here and has no server.
        Assert.True(pane.PickServer(ServerPicker.IdNone));
        Assert.Equal(ServerTargetKind.None, pane.ServerTarget);
        rig.Rebuild();
        Assert.Equal(SideCells.NoClientSide, rig.Cell("o-s", InstallModsPane.CellClient).Text);
        Assert.Equal(SideCells.NoServerSelected, rig.Cell("o-s", InstallModsPane.CellServer).Text);
        // INSTALLSIDES2 item 2: both are grayed cells in their columns.
        Assert.True(rig.Cell("o-s", InstallModsPane.CellClient).Grayed);
        Assert.True(rig.Cell("o-s", InstallModsPane.CellServer).Grayed);
        Assert.Equal(ServerPicker.ServerWord, pane.HeaderRow!.Cells.Single(c => c.Key == InstallModsPane.CellServer).Text);
        Assert.Equal(ServerRouteKind.None, pane.CurrentRoute.Kind);

        // The readback names the picked entry and the list.
        Assert.True(pane.TryDescribeSidebarRow(pane.ToolbarRow!, out var toolbarExtra));
        var picker = (Dictionary<string, object?>)toolbarExtra!["picker"]!;
        Assert.Equal("none", picker["target"]);
        Assert.Equal(2, ((List<Dictionary<string, object?>>)picker["entries"]!).Count);
    }

    [Fact]
    public void Connected_is_the_default_and_a_found_folder_is_applied_before_it_is_picked()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-s", side: "Server"));
        string found = Path.Combine(_root, "found");
        var inputs = Inputs(connected: true, configured: "", new LocalServerCandidate(found, "Found Box", "Steam"));
        var applied = new List<string>();
        string? refusal = "That folder has no VRisingServer.exe.";
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerPickerProbe = () => inputs;
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
            pane.ApplyServerFolder = path => { applied.Add(path); return refusal; };
        });
        var pane = rig.Pane;
        Assert.Equal(ServerPickKind.Connected, pane.PickedServer!.Kind);
        Assert.Equal("Connected: Forms Test", pane.ToolbarRow!.Cells.Single(c => c.Key == InstallModsPane.CellServerPick).Text);
        Assert.Equal("Server · Forms Test", pane.HeaderRow!.Cells.Single(c => c.Key == InstallModsPane.CellServer).Text);
        Assert.Equal(ServerRouteKind.Remote, pane.CurrentRoute.Kind);

        // A refused folder keeps the pick and says why.
        Assert.False(pane.PickServer(ServerPicker.LocalId(found)));
        Assert.Equal(new[] { found }, applied);
        Assert.Equal(ServerPickKind.Connected, pane.PickedServer!.Kind);
        Assert.Equal(refusal, pane.PickerError);

        // Accepted: the folder becomes the configured one (the menu rebuilds the catalog for it).
        refusal = null;
        Assert.True(pane.PickServer(ServerPicker.LocalId(found)));
        Assert.Null(pane.PickerError);
        Assert.Equal(ServerPicker.LocalId(found), InstallModsPane.RememberedPick);
        // The pick survives a new pane over a rebuilt catalog.
        inputs = Inputs(connected: true, configured: found);
        var next = new InstallModsPane(new FakeCatalog()) { ServerPickerProbe = () => inputs };
        Assert.Equal(ServerPickKind.LocalFolder, next.PickedServer!.Kind);
    }

    [Fact]
    public void The_dropdown_opens_the_games_popup_list_and_a_pick_applies()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-a"));
        var rig = new Rig(this, catalog, pane => pane.ServerPickerProbe = () => Inputs(connected: true, configured: ""));
        var pane = rig.Pane;
        Assert.True(rig.Click(InstallModsPane.ToolbarKey + "/" + InstallModsPane.CellServerPick));
        var dialog = UiDialog.Current;
        Assert.NotNull(dialog);
        Assert.Equal(InstallModsPane.PickerDialogKind, dialog!.Kind);
        Assert.Equal(UiDialogShape.Choose, dialog.Content.Shape);
        Assert.Equal(ServerPicker.DialogTitle, dialog.Content.Title);
        // The current entry is marked; every entry is a button in the game's popup look.
        Assert.StartsWith(InstallModsCopy.PickedMark, dialog.Content.Options[0]);
        Assert.Equal(InstallModsCopy.PickerCancel, dialog.Content.CancelCaption);
        if (dialog.View!.OptionRows.Count > 0)
            Assert.All(dialog.View.OptionRows, r => Assert.Equal(CellSkin.PopupButton, r.Cells.Single(c => c.Kind == CellKind.Button).Skin));
        // The body names the connected server and its address.
        Assert.EndsWith("Connected to Forms Test at 127.0.0.1:9976.", dialog.Content.Body);
        int none = dialog.Content.Options.ToList().FindIndex(o => o.EndsWith(ServerPicker.NoneCaption, StringComparison.Ordinal));
        dialog.PickForBridge(none);
        Assert.Equal(ServerPickKind.None, pane.PickedServer!.Kind);
    }

    /// <summary>
    /// Live review 2026-10-03 item 2: opening and closing a drawer and opening the picker are not
    /// actions; every row's two cells and shortcut read the same before and after them, through
    /// every rebuild they cause. (The live flip was a real Cancel uninstall: a pointer click on the
    /// row's button and then on the Keep dialog, logged by the input pump.)
    /// </summary>
    [Fact]
    public void Drawers_and_the_picker_never_change_a_row_s_side_cells()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a"), Listing("o-s", side: "Server"), Tagged("o-b", new[] { "Client", "Server", "Oakveil Update" }) });
        var inputs = Inputs(connected: true, configured: "");
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerPickerProbe = () => inputs;
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        });
        var pane = rig.Pane;
        string Snapshot() => string.Join("|", catalog.Entries.Select(e =>
        {
            var d = pane.SideCellsFor(e);
            return e.Key + ":" + d.Client.Kind + "/" + d.Client.Text + "/" + d.Server.Kind + "/" + d.Server.Text + "/" + d.Shortcut;
        })) + "#" + pane.ServerHeaderCaption();

        string before = Snapshot();
        int rebuilds = rig.Rebuilds;
        Assert.True(rig.Click("o-a/" + InstallModsPane.CellExpand));
        Assert.Equal(before, Snapshot());
        Assert.True(rig.Click(InstallModsPane.DrawerKeyPrefix + "o-a/" + InstallModsPane.CellCollapse));
        Assert.Equal(before, Snapshot());
        Assert.True(rig.Click(InstallModsPane.ToolbarKey + "/" + InstallModsPane.CellServerPick));
        Assert.Equal(before, Snapshot());
        UiDialog.Current!.Close();
        rig.Rebuild();
        Assert.Equal(before, Snapshot());
        Assert.True(rig.Rebuilds > rebuilds);
    }

    // ---- The side buttons' routes ------------------------------------------------------------

    [Fact]
    public void Side_buttons_open_the_confirmation_for_their_own_side_only()
    {
        byte[] bytes = { 7, 7 };
        string sha = InstalledState.HashBytes(bytes);
        var catalog = new FakeCatalog();
        var entry = Listing("o-a");
        catalog.Entries.Add(entry);
        var marks = new List<(string, bool)>();
        var rig = new Rig(this, catalog, pane =>
        {
            pane.LedgerProbe = _ => true;
            pane.HotLedgerProbe = _ => false;
            pane.UninstallMarker = (key, mark, force) => { marks.Add((key, mark)); return new ServerInstallResult(true, "marked", new[] { "a.dll" }, Array.Empty<string>()); };
        });
        var pane = rig.Pane;
        string reload = Path.Combine(_root, "progenitor-reload", "a.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(reload)!);
        File.WriteAllBytes(reload, bytes);
        pane.SetInstalledQuery(new InstalledStateQuery(
            new[] { new InstalledFile(reload, InstalledState.ReloadKind, sha, InstalledState.ClientRoot) },
            new[] { new InstalledInventoryRow("o-a", "1.0.0", "Local") },
            new[] { new InstalledLedgerRow("o-a", sha, "1.0.0", "a.dll", "package:o-a@1.0.0", "mod", InstalledState.ClientRoot, reload) },
            loadedGeneration: "33"));
        rig.Rebuild();
        Assert.Equal("Installed 1.0.0", rig.Cell("o-a", InstallModsPane.CellClient).Text);
        Assert.Equal(SideCells.UninstallCaption, rig.Cell("o-a", InstallModsPane.CellClientA).Text);

        Assert.True(rig.Click("o-a/" + InstallModsPane.CellClientA));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal("client", pane.PromptSideName);
        // The box names this game only: no server line, the game's own restart.
        Assert.DoesNotContain(pane.PromptFacts(entry, InstallPromptKind.Uninstall), f => f.Label == InstallModsCopy.FactServer);
        Assert.Contains(pane.PromptFacts(entry, InstallPromptKind.Uninstall), f => f.Label == InstallModsCopy.FactTakesEffect && f.Value == InstallModsCopy.EffectGameRestart);
        Assert.True(pane.PromptYes());
        Assert.Equal(new[] { ("o-a", true) }, marks);
        Assert.Equal((InstallModsPane.UninstallHalfMarked, InstallModsPane.UninstallHalfNothing), pane.LastUninstallHalves);
        Assert.Equal("whole", pane.PromptSideName);
    }

    /// <summary>
    /// LOCALDETECT1 review (live 2026-10-10: the box said "on Progenitor Headless Test" and the plan was for the client):
    /// with an auto-detected local server and no configured folder, the box, the target and the plan all name the
    /// detected server.
    /// </summary>
    [Fact]
    public void A_detected_servers_install_box_target_and_plan_agree()
    {
        string folder = Path.Combine(_root, "headless", "server");
        var catalog = new FakeCatalog();
        var entry = Listing("o-r", side: "Server");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
            pane.HostedRouteFolderProbe = () => folder;
            pane.DetectedRouteFolderProbe = () => folder;
            pane.ServerWriteRootProbe = () => Path.Combine(folder, "BepInEx");
            pane.ServerIdentityProbe = remote => remote ? ServerIdentity.Connected("", "") : ServerIdentity.Local("Headless Test", folder);
        });
        var pane = rig.Pane;

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(InstallTarget.LocalServer, pane.Target);
        var (server, both) = pane.PromptServerFor(entry, InstallPromptKind.Install);
        Assert.NotNull(server);
        Assert.False(server!.Remote);
        Assert.Equal(folder, server.Folder);
        Assert.False(both);
        Assert.Contains("local:" + folder.ToLowerInvariant(), pane.BoxTargetSignature(entry));

        pane.PlanSelected(null);
        Assert.Equal(InstallTarget.LocalServer, catalog.Planned.Last());
        Assert.DoesNotContain(InstallTarget.Client, catalog.Planned);
    }

    /// <summary>
    /// LOCALDETECT1 review: a server target this pane cannot write (no route folder, no configured folder) is the client
    /// fallback in the box too, never a box naming a server over a client write.
    /// </summary>
    [Fact]
    public void A_server_target_the_pane_cannot_write_is_the_client_in_the_box_too()
    {
        var catalog = new FakeCatalog();
        var entry = Listing("o-s", side: "Server");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
            pane.HostedRouteFolderProbe = () => null;
            pane.ServerWriteRootProbe = () => null;
        });
        var pane = rig.Pane;

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallTarget.Client, pane.Target);
        Assert.Null(pane.PromptServerFor(entry, InstallPromptKind.Install).Server);
        Assert.StartsWith(InstallTarget.Client + "|", pane.BoxTargetSignature(entry));
    }

    [Fact]
    public void The_both_shortcut_installs_both_halves_through_the_local_folder()
    {
        var catalog = new FakeCatalog();
        var entry = Listing("o-b", side: "Server");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, pane =>
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None), ValidatedFolder());
        var pane = rig.Pane;
        // A Server-tagged mod with a local server: only the server needs it, so no shortcut.
        Assert.False(rig.Cell("o-b", InstallModsPane.CellBoth).Visible);
        // A both-sided listing gets it, drawn over the Mod column on the author line.
        catalog.Entries.Add(Tagged("o-c", new[] { "Client", "Server", "Oakveil Update" }));
        rig.Rebuild();
        Assert.Equal(PackageSide.Both, InstallModsPane.SideOf(catalog.Entries[1]));
        var shortcut = rig.Cell("o-c", InstallModsPane.CellBoth);
        Assert.True(shortcut.Visible);
        Assert.Equal(SideCells.InstallBothCaption, shortcut.Text);
        Assert.Equal(CellSkin.Quiet, shortcut.Skin);
        Assert.Equal(InstallModsPane.CellName, shortcut.OverlayOf);
        Assert.True(rig.Cell("o-c", InstallModsPane.CellName).NoteReserve > 0f);
        Assert.Equal("install", pane.ListingToWidget(catalog.Entries[1])["shortcut"]);

        Assert.True(rig.Click("o-c/" + InstallModsPane.CellBoth));
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(InstallTarget.Both, pane.Target);
        Assert.Null(pane.DrawerKey);
    }

    [Fact]
    public void The_both_shortcut_on_a_connected_server_writes_this_client_then_sends_the_server_half()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-d", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, pane =>
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None));
        var pane = rig.Pane;
        Assert.Equal(SideButton.Install, pane.SideCellsFor(entry).Shortcut);
        Assert.True(pane.OnBothClick(entry.Key));
        Assert.Equal(InstallTarget.Client, pane.Target);
        // The box names the connected server as the second half.
        var (server, alsoHere) = pane.PromptServerFor(entry, InstallPromptKind.Install);
        Assert.NotNull(server);
        Assert.True(server!.Remote);
        Assert.True(alsoHere);
    }

    // ---- LIMITS1: the confirmation offers both sides -----------------------------------------------

    [Fact]
    public void One_sides_install_offers_install_on_both_when_the_shortcut_would()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-c", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog, pane =>
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None), ValidatedFolder());
        var pane = rig.Pane;
        Assert.Equal(SideButton.Install, pane.SideCellsFor(entry).Shortcut);

        // The client's Install: [No] [Yes, client only] [Yes, install on both].
        Assert.True(rig.Click("o-c/" + InstallModsPane.CellClientA));
        var dialog = UiDialog.Current!;
        Assert.Equal(InstallModsCopy.PromptYesClientOnly, dialog.Content.MainCaption);
        var both = Assert.Single(dialog.Content.ExtraButtons);
        Assert.Equal(InstallModsPane.CellPromptBoth, both.Key);
        Assert.Equal(InstallModsCopy.PromptYesInstallBoth, both.Caption);
        // Three popup buttons do not fit one 560 px row at full caption width, so the both button
        // stands in its own full-width row above [No] [Yes, client only]; nothing is ellipsized.
        Assert.True(dialog.Content.ExtrasStacked);
        var captions = dialog.Content.RowButtons().Select(b => b.Caption).ToList();
        Assert.Equal(new[] { InstallModsCopy.PromptNo, InstallModsCopy.PromptYesClientOnly }, captions.ToArray());
        Assert.True(DialogLayout.ButtonsFit(captions, DialogLayout.ContentRowWidth()), string.Join(" | ", captions));
        var stacked = Assert.Single(dialog.View!.ExtraRows);
        Assert.Equal(InstallModsPane.CellPromptBoth, stacked.Key);
        var bothCell = stacked.Row.Cells.Single(c => c.Key == InstallModsPane.CellPromptBoth);
        Assert.Equal(CellSkin.PopupButton, bothCell.Skin);
        Assert.True(DialogLayout.CaptionWidth(bothCell.Text) <= DialogLayout.ListButtonWidth);
        Assert.Equal(InstallTarget.Client, pane.Target);

        // The both button runs the row's both-sides flow, already answered (F39: no second Yes): the box here is still
        // checking the package, so the Yes waits for its plan and is honored then.
        Assert.True(rig.Click(UiDialogs.ExtraRow(InstallModsPane.CellPromptBoth) + "/" + InstallModsPane.CellPromptBoth));
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Equal(InstallTarget.Both, pane.Target);
        Assert.True(pane.PromptYesPending);
        var after = UiDialog.Current!;
        Assert.Empty(after.Content.ExtraButtons);
        Assert.NotEqual(InstallModsCopy.PromptYesClientOnly, after.Content.MainCaption);
        pane.PromptNo();

        // The server's Install reads "server only".
        Assert.True(rig.Click("o-c/" + InstallModsPane.CellServerA));
        Assert.Equal(InstallModsCopy.PromptYesServerOnly, UiDialog.Current!.Content.MainCaption);
        Assert.Single(UiDialog.Current!.Content.ExtraButtons);
        pane.PromptNo();
    }

    [Fact]
    public void A_mod_both_sides_need_says_why_the_other_side_is_not_offered()
    {
        var catalog = new FakeCatalog();
        // Manifest-verified both-sided: both sides want it.
        var entry = new CatalogEntry(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "o", "o-v"),
            CatalogProvenance.ManifestVerified, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z", displayName: "v", description: "d", side: PackageSide.Both,
            owner: "o", packageName: "v", categories: new[] { "Oakveil Update" }, releasedUtc: "2026-09-11T00:00:00Z");
        catalog.Entries.Add(entry);
        var inputs = new ServerPickerInputs { Connected = false };
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerPickerProbe = () => inputs;
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NoServer);
        });
        var pane = rig.Pane;
        Assert.True(pane.PickServer(ServerPicker.IdNone));
        rig.Rebuild();
        Assert.True(InstallModsPane.NeedsBoth(entry));
        Assert.True(rig.Click("o-v/" + InstallModsPane.CellClientA));
        var dialog = UiDialog.Current!;
        // One yes, and the note says why the server is not offered.
        Assert.Empty(dialog.Content.ExtraButtons);
        Assert.Equal(InstallModsCopy.PromptYesInstall, dialog.Content.MainCaption);
        Assert.Contains(InstallModsCopy.BothNoServer, dialog.Content.Note);
        pane.PromptNo();
    }

    // ---- The mod page --------------------------------------------------------------------------

    [Fact]
    public void The_mod_page_band_states_both_sides_in_the_install_rows_words()
    {
        var perSide = SideCells.Decide(new SideCellInputs
        {
            Side = PackageSide.Both,
            Latest = "1.2.0",
            Client = new SideHalf { State = HalfState.Installed, Version = "1.2.0", CanUninstall = true },
            Server = new SideHalf { State = HalfState.Absent },
            Target = ServerTargetKind.Server,
            Route = new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin),
        });
        var facts = new ModFrameworkFacts { PluginId = "o.a", Version = "1.2.0", Sides = ModSides.Both, ActionCaption = "Uninstall" }
            .WithPerSide(perSide, "Server · Forms Test");
        var line = ModFrameworkLine.Compose(facts);
        var row = line.BuildRow(ModPagePane.FrameworkKey, 1236f);
        var client = row.Cells.Single(c => c.Key == ModFrameworkLine.CellClientSide);
        var server = row.Cells.Single(c => c.Key == ModFrameworkLine.CellServerSide);
        Assert.Equal("Client: Installed 1.2.0", client.Text);
        Assert.Equal(CellTone.OkBright, client.Tone);
        // Live (HotDemo, 2026-10-03): the server's name made the cell overflow its cap and cut the state
        // ("Server · Progenitor Forms Test: Installed …"); the band says "Server", the readback keeps the name.
        Assert.Equal("Server: Not installed · Ask a server admin", server.Text);
        Assert.DoesNotContain("…", server.Text, StringComparison.Ordinal);
        Assert.Equal(CellTone.Muted, server.Tone);
        // The side cells replace the server-part sentence; the page's own action stays its one button.
        Assert.DoesNotContain(row.Cells, c => c.Key == ModFrameworkLine.CellServerPart);
        Assert.Single(row.Cells, c => c.Kind == CellKind.Button);
        var readback = line.Readback();
        Assert.Equal(SideCells.KindInstalled, ((Dictionary<string, object?>)readback["sideClient"]!)["kind"]);
        Assert.Equal("Server · Forms Test", readback["serverColumn"]);

        // Without a listing the band is what it always was.
        var plain = ModFrameworkLine.Compose(new ModFrameworkFacts { PluginId = "o.b", Sides = ModSides.Both }).BuildRow(ModPagePane.FrameworkKey, 1236f);
        Assert.DoesNotContain(plain.Cells, c => c.Key == ModFrameworkLine.CellClientSide);
        Assert.Contains(plain.Cells, c => c.Key == ModFrameworkLine.CellServerPart);
    }

    // ---- INSTALLSIDES2 item 6: a side's state is that side's evidence --------------------------

    /// <summary>
    /// Live 2026-10-03: HotDemo's Client cell read <c>Installed 0.1.0 · Managed elsewhere</c> while
    /// this client had no copy (Bootstrap removed it at start; the client ledger no longer names it).
    /// The evidence was the local server's promoted copy — a server-root <c>mod</c> ledger row with
    /// its file in the server's <c>progenitor-reload/</c> — which the listing-wide detection reports as
    /// loaded; the Client half read that as its own. Fixture of that rig: the server root has the copy,
    /// the handshake lists the mod (a <c>Server list</c> inventory row), the client has nothing.
    /// </summary>
    /// <summary>
    /// F34 (live 2026-10-08): while hosting a private game, its launcher stages this client's reload folder into the hosted
    /// child, which loads it (the private server's log: "Progenitor Hot Demo 0.1.0 loaded on Server"). That staged copy has
    /// no server ledger row; the Server cell reads it as installed instead of "Missing: the client has it". Game-free; not
    /// live-verified.
    /// </summary>
    [Fact]
    public void A_copy_staged_into_the_private_game_reads_as_installed_there()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        string clientReload = Path.Combine(_root, "client", "BepInEx", "progenitor-reload", "progenitor.hotdemo.dll");
        string staged = Path.Combine(_root, "VRising_Server", "BepInEx", "progenitor-reload", "progenitor.hotdemo.dll");
        InstalledStateQuery Query(bool withStaged) => new(
            files: withStaged
                ? new[] { new InstalledFile(clientReload, InstalledState.ReloadKind, "f0b8", InstalledState.ClientRoot, 8192), new InstalledFile(staged, InstalledState.ReloadKind, "", InstalledState.ServerRoot, 8192) }
                : new[] { new InstalledFile(clientReload, InstalledState.ReloadKind, "f0b8", InstalledState.ClientRoot, 8192) },
            inventory: new[] { new InstalledInventoryRow("progenitor.hotdemo", "0.1.0", "Local") },
            ledger: new[]
            {
                new InstalledLedgerRow("o-HotDemo", "f0b8", "0.1.0", "progenitor.hotdemo.dll",
                    source: "package:o-HotDemo@0.1.0", category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: clientReload,
                    pluginId: "progenitor.hotdemo"),
            });
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
            pane.LedgerProbe = _ => false;
        }, ValidatedFolder());
        var pane = rig.Pane;

        pane.SetInstalledQuery(Query(withStaged: false));
        Assert.Equal(HalfState.Absent, pane.ServerHalfOf(entry).State);

        pane.SetInstalledQuery(Query(withStaged: true));
        Assert.True(InstalledState.StagedOnServer(entry, Query(true), out string version));
        Assert.Equal("0.1.0", version);
        var half = pane.ServerHalfOf(entry);
        Assert.Equal(HalfState.Installed, half.State);
        Assert.False(half.CanUninstall);
        Assert.NotEqual(SideCells.MissingClientHasIt, pane.SideCellsFor(entry).Server.Text);
    }

    /// <summary>
    /// REQRULE3 (live 2026-10-08): HotDemo installed on both sides from the client; the local server's reloader hot-loaded
    /// its copy, and the uninstall box said "Now on this game; at the next server restart on the server" although the
    /// removal deletes that copy now (it is a trusted hot-loadable image in a watched reload folder). The box now asks the
    /// removal's own question; a copy the server can't drop now still says its next restart. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void The_uninstall_box_says_now_for_a_server_copy_its_reloader_drops_now()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        string clientReload = Path.Combine(_root, "client", "BepInEx", "progenitor-reload", "progenitor.hotdemo.dll");
        string serverReload = Path.Combine(_root, "server", "BepInEx", "progenitor-reload", "progenitor.hotdemo.dll");
        var query = new InstalledStateQuery(
            files: new[]
            {
                new InstalledFile(clientReload, InstalledState.ReloadKind, "f0b8", InstalledState.ClientRoot, 8192),
                new InstalledFile(serverReload, InstalledState.ReloadKind, "f0b8", InstalledState.ServerRoot, 8192),
            },
            inventory: new[] { new InstalledInventoryRow("progenitor.hotdemo", "0.1.0", "Local") },
            ledger: new[]
            {
                new InstalledLedgerRow("o-HotDemo", "f0b8", "0.1.0", "progenitor.hotdemo.dll",
                    source: "package:o-HotDemo@0.1.0", category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: clientReload,
                    pluginId: "progenitor.hotdemo"),
                // The cross-process write carries no hot flag: the server process loaded it, not this one.
                new InstalledLedgerRow("o-HotDemo", "f0b8", "0.1.0", "progenitor.hotdemo.dll",
                    source: "package:o-HotDemo@0.1.0", category: ServerInstallLedger.CategoryMod, root: InstalledState.ServerRoot, filePath: serverReload,
                    pluginId: "progenitor.hotdemo"),
            });
        bool serverNow = true;
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
            pane.LedgerProbe = _ => true;
            pane.HotLedgerProbe = _ => true;
            pane.ServerUnloadsNowProbe = _ => serverNow;
        }, ValidatedFolder());
        var pane = rig.Pane;
        pane.SetInstalledQuery(query);
        Assert.Equal((InstalledState.HalfLoaded, InstalledState.HalfLoaded), pane.HalvesOf(entry));

        string Effect() => pane.PromptFacts(entry, InstallPromptKind.Uninstall).First(f => f.Label == InstallModsCopy.FactTakesEffect).Value;
        Assert.Equal(InstallModsCopy.EffectUnloadNow, Effect());
        Assert.Contains(InstallModsCopy.UninstallHotServerNowBody, pane.PromptBodyText(entry, InstallPromptKind.Uninstall), StringComparison.Ordinal);

        // Review L1: the local server folder's copy goes now on the remote route too, as the removal does it.
        pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        Assert.Equal(InstallModsCopy.EffectUnloadNow, Effect());
        pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        string source = File.ReadAllText(Path.Combine(RepoRootForSource(), "src", "Progenitor.Core", "Ui", "InstallModsPane.cs"));
        Assert.Contains("bool now = LocalServerHalfUnloadsNow(entry, log: true);", source);
        Assert.Contains("bool now = !pending && LocalServerHalfUnloadsNow(entry, log: true);", source);

        // A copy the server can't drop now (not running, not watched, not a trusted hot-loadable image) keeps the restart.
        serverNow = false;
        Assert.Equal(InstallModsCopy.EffectUnloadNowServerLater, Effect());
        Assert.Contains(InstallModsCopy.UninstallHotServerLoadedBody, pane.PromptBodyText(entry, InstallPromptKind.Uninstall), StringComparison.Ordinal);
    }

    [Fact]
    public void The_local_server_s_promoted_copy_never_reads_as_this_client_s()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        string serverReload = Path.Combine(_root, "server", "BepInEx", "progenitor-reload", "progenitor.hotdemo.dll");
        var query = new InstalledStateQuery(
            files: new[] { new InstalledFile(serverReload, InstalledState.ReloadKind, "f0b8", InstalledState.ServerRoot, 8192) },
            inventory: new[] { new InstalledInventoryRow("progenitor.hotdemo", "Server 0.1.0", "Server list") },
            ledger: new[]
            {
                new InstalledLedgerRow("o-HotDemo", "f0b8", "0.1.0", "progenitor.hotdemo.dll",
                    source: "package:o-HotDemo@0.1.0", category: ServerInstallLedger.CategoryMod, root: InstalledState.ServerRoot, filePath: serverReload,
                    pluginId: "progenitor.hotdemo"),
            });
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
            pane.LedgerProbe = _ => false;
        }, ValidatedFolder());
        var pane = rig.Pane;
        // A build re-scans the disk; the fixture replaces that scan (no rebuild follows).
        pane.SetInstalledQuery(query);
        Assert.Equal((InstalledState.HalfNone, InstalledState.HalfLoaded), InstalledState.HalvesOf(entry, query, false));

        // The listing-wide answer still sees the server's copy...
        Assert.NotEqual(InstalledKind.NotInstalled, InstalledState.Detect(entry, query).State);
        // ...but this client's evidence is empty, and the Client cell says so.
        Assert.Equal(InstalledKind.NotInstalled, pane.DetectClient(entry).State);
        var cells = pane.SideCellsFor(entry);
        Assert.Equal(HalfState.Absent, pane.ClientHalfOf(entry).State);
        Assert.Equal(SideCells.NotInstalled, cells.Client.Text);
        Assert.DoesNotContain("Managed", cells.Client.Text, StringComparison.Ordinal);
        // The server half is the server's copy.
        Assert.Equal((InstalledState.HalfNone, InstalledState.HalfLoaded), pane.HalvesOf(entry));
        Assert.Equal(ServerRouteKind.Local, pane.CurrentRoute.Kind);
        Assert.Equal(HalfState.Installed, pane.ServerHalfOf(entry).State);

        // A copy this client really loads (the inventory says Local) is still read as installed.
        var loadedHere = new InstalledStateQuery(inventory: new[] { new InstalledInventoryRow("progenitor.hotdemo", "0.1.0", "Local") });
        pane.SetInstalledQuery(loadedHere);
        Assert.Equal(HalfState.Installed, pane.ClientHalfOf(entry).State);
    }

    /// <summary>
    /// LOCALONLY1 (live 2026-10-07): a both-sides listing only this PC's local test source carries, on the remote route. The
    /// Server cell says the server can't fetch it (not "Not on Thunderstore"), the install targets this client only, and no
    /// server install is sent; on the local route nothing changes.
    /// </summary>
    [Fact]
    public void A_local_test_listing_installs_on_this_client_only_on_the_remote_route()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var remote = new RemoteInstallClient();
        var sent = new List<byte[]>();
        remote.Send = b => { sent.Add(b); return true; };
        remote.Accept(new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true, Array.Empty<RemoteServerRow>()));
        var route = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => route;
            pane.RemoteServer = remote;
            pane.LocalListingProbe = e => e.Key == "o-HotDemo";
        });
        var pane = rig.Pane;
        pane.SetInstalledQuery(new InstalledStateQuery());
        Assert.True(pane.LocalOnlyOnRemote(entry));
        Assert.Equal(InstallModsCopy.LocalOnlyServerWord, pane.SideCellsFor(entry).Server.Text);
        Assert.False(pane.SideCellsFor(entry).Server.Has(SideButton.Install));
        var resolved = pane.ResolvedTargetFor(entry);
        Assert.Equal(InstallTarget.Client, resolved.Target);
        Assert.False(resolved.ToConnectedServer);
        Assert.Contains(pane.DrawerLines(entry, pane.ActionFor(entry), out _), l => l.Text == InstallModsCopy.LocalOnlyServerSentence);
        Assert.Empty(sent.FindAll(b => RemoteInstallWire.TryDecodeRequest(b, out var r) && r.Op == RemoteInstallOp.Install));

        route = new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        Assert.False(pane.LocalOnlyOnRemote(entry));
    }

    /// <summary>
    /// F16 (live 2026-10-07): when the connected server's own local test index offers the listing (its status row
    /// <c>local:&lt;key&gt;</c>), the Server cell offers Install, names that source, and the request carries the version that
    /// index has. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_listing_the_servers_own_local_index_offers_installs_there_remotely()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var remote = new RemoteInstallClient();
        var sent = new List<byte[]>();
        remote.Send = b => { sent.Add(b); return true; };
        remote.Accept(new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true,
            new[] { new RemoteServerRow(RemoteLocalOffer.Of("o-HotDemo"), "0.2.0", RemoteRowState.None, "") }));
        var route = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => route;
            pane.RemoteServer = remote;
            pane.LocalListingProbe = e => e.Key == "o-HotDemo";
        });
        var pane = rig.Pane;
        pane.SetInstalledQuery(new InstalledStateQuery());
        Assert.False(pane.LocalOnlyOnRemote(entry));
        Assert.Equal("0.2.0", pane.ServerLocalOffer(entry)!.Version);
        Assert.NotEqual(InstallModsCopy.LocalOnlyServerWord, pane.SideCellsFor(entry).Server.Text);
        Assert.True(pane.SideCellsFor(entry).Server.Has(SideButton.Install));
        Assert.Contains(pane.DrawerLines(entry, pane.ActionFor(entry), out _), l => l.Text == InstallModsCopy.ServerLocalSourceSentence("0.2.0"));
    }

    /// <summary>F16/F5 (live 2026-10-07): a client-only local listing's Server cell reads no server side, not "local test source only".</summary>
    [Fact]
    public void A_client_only_local_listing_has_no_server_side_on_the_remote_route()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-FilmExample", new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var remote = new RemoteInstallClient();
        remote.Accept(new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true, Array.Empty<RemoteServerRow>()));
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
            pane.RemoteServer = remote;
            pane.LocalListingProbe = _ => true;
        });
        rig.Pane.SetInstalledQuery(new InstalledStateQuery());
        var server = rig.Pane.SideCellsFor(entry).Server;
        Assert.NotEqual(InstallModsCopy.LocalOnlyServerWord, server.Text);
        Assert.False(server.Has(SideButton.Install));
    }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07): HotDemo's client copy was a DLL in progenitor-reload that no
    /// ledger listed. The Mods page offered its Uninstall (FWALLOW1) while this tab said "Managed
    /// elsewhere" with no button. The Client cell now reads where it is, offers Uninstall, and the
    /// press goes to the same reload-folder removal (through the plain-action seam).
    /// </summary>
    [Fact]
    public void A_reload_folder_copy_no_ledger_lists_is_installed_there_with_Uninstall()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var pressed = new List<(string Id, bool Cancel)>();
        InstallModsPane.PlainOfferProbe = id => string.Equals(id, "progenitor.hotdemo", StringComparison.OrdinalIgnoreCase)
            ? new InstallModsPane.PlainOffer { CanUninstall = true, Reload = true }
            : null;
        InstallModsPane.PlainActionHandler = (id, _, cancel) => pressed.Add((id, cancel));
        try
        {
            var rig = new Rig(this, catalog, pane => pane.LedgerProbe = _ => false);
            var pane = rig.Pane;
            pane.SetInstalledQuery(new InstalledStateQuery(inventory: new[] { new InstalledInventoryRow("progenitor.hotdemo", "1.0.0", "Local") }));
            var half = pane.ClientHalfOf(entry);
            Assert.Equal(HalfState.Installed, half.State);
            Assert.Equal(InstallModsCopy.InReloadFolder, half.Location);
            var cells = pane.SideCellsFor(entry);
            Assert.Equal("Installed 1.0.0" + SideCells.Dot + InstallModsCopy.InReloadFolder, cells.Client.Text);
            Assert.DoesNotContain("Managed", cells.Client.Text, StringComparison.Ordinal);
            Assert.True(cells.Client.Has(SideButton.Uninstall));
            Assert.True(pane.OnSideClick(entry.Key, server: false, SideButton.Uninstall));
            Assert.Equal(("progenitor.hotdemo", false), Assert.Single(pressed));
            pane.PromptNo();

            // Review (HIGH 1): the uninstall box draws its facts, and they name this copy exactly as Yes
            // treats it — removed in its own words, or left in place when nothing here can remove it.
            DialogFact ClientFact() => Assert.Single(pane.PromptFacts(entry, InstallPromptKind.Uninstall),
                f => f.Label == InstallModsCopy.FactThisClient);
            Assert.Equal(InstallModsCopy.ThisClientCopyStaysValue, ClientFact().Value);
            InstallModsPane.PlainUninstallSentence = id => id == "progenitor.hotdemo" ? "Deletes Progenitor.HotDemo.dll now." : "";
            Assert.Equal("Deletes Progenitor.HotDemo.dll now.", ClientFact().Value);

            // Yes acts on the copy the open box showed; if that changed, nothing is done and the drawer says so.
            var removed = new List<string>();
            InstallModsPane.PlainUninstallNow = (id, _) => { removed.Add(id); return "Removed"; };
            // The whole-row box (Simple's both-sides Uninstall) opened directly: this rig has no server copy.
            var openWhole = typeof(InstallModsPane).GetMethod("OpenWholePrompt",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            Assert.True((bool)openWhole.Invoke(pane, new object[] { entry, InstallPromptKind.Uninstall })!);
            Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
            // UNINSTREQ1: the connected server requires it: the box says what happens.
            Networking.RequiredHereFact.Probe = id => id == "progenitor.hotdemo" ? ("Night Garden", true, false) : null;
            Networking.RequiredHereFact.RechecksProbe = () => true;
            var required = Assert.Single(pane.PromptFacts(entry, InstallPromptKind.Uninstall), f => f.Label == Networking.RequiredHereFact.Label);
            Assert.StartsWith("Night Garden: it checks your mods while you play", required.Value, StringComparison.Ordinal);
            Assert.Equal(DialogFactTone.Warn, required.Tone);
            Networking.RequiredHereFact.Probe = null;
            Networking.RequiredHereFact.RechecksProbe = null;
            Assert.Equal("Deletes Progenitor.HotDemo.dll now.", ClientFact().Value);
            InstallModsPane.PlainUninstallSentence = id => "Something else now.";
            Assert.False(pane.PromptYes());
            Assert.Empty(removed);
            Assert.Contains(pane.DrawerLines(entry, pane.ActionFor(entry), out _), l => l.Text == ModUninstallPlan.ChangedNote);

            // Live 2026-10-07 (C): after the copy is deleted and unloaded the row reads a fresh scan, with the
            // inventory read again, not the build's (which still listed the loaded plugin).
            InstallModsPane.PlainOfferProbe = _ => null;
            pane.FreshInstalledQueryProbe = () => new InstalledStateQuery();
            pane.NoteClientCopyRemoved();
            Assert.Equal(HalfState.Absent, pane.ClientHalfOf(entry).State);
            Assert.Equal(SideCells.NotInstalled, pane.SideCellsFor(entry).Client.Text);
        }
        finally
        {
            InstallModsPane.PlainOfferProbe = null;
            InstallModsPane.PlainActionHandler = null;
            InstallModsPane.PlainUninstallSentence = null;
            InstallModsPane.PlainUninstallNow = null;
            Networking.RequiredHereFact.Probe = null;
            Networking.RequiredHereFact.RechecksProbe = null;
        }
    }

    /// <summary>
    /// Live 2026-10-03: on a fresh Core generation HotDemo's mod page band read <c>Client: Not installed ·
    /// Server: Not installed</c> while it ran on both sides, and read right only after one visit to the
    /// Install tab — the only place that handed the pane its installed-state scan. A page opened first
    /// now takes that scan itself; with no scan to be had it draws no side cells, never "Not installed".
    /// </summary>
    [Fact]
    public void A_mod_page_opened_before_the_install_tab_scans_before_it_says_not_installed()
    {
        var catalog = new FakeCatalog();
        var entry = Tagged("o-HotDemo", new[] { "Client", "Server", "Oakveil Update" });
        catalog.Entries.Add(entry);
        var pane = new InstallModsPane(catalog);
        Assert.False(pane.HasInstalledQuery);

        // No scan to be had (none, or one that throws): no side cells at all.
        Assert.Null(ModsMenu.PageSideCells(pane, entry, () => null));
        Assert.Null(ModsMenu.PageSideCells(pane, entry, () => throw new IOException("ledger locked")));
        Assert.False(pane.HasInstalledQuery);

        // The page's own scan: this client loads it, so the Client cell says Installed.
        var cells = ModsMenu.PageSideCells(pane, entry, () =>
            new InstalledStateQuery(inventory: new[] { new InstalledInventoryRow("progenitor.hotdemo", "0.1.0", "Local") }));
        Assert.NotNull(cells);
        Assert.True(pane.HasInstalledQuery);
        Assert.Equal(HalfState.Installed, pane.ClientHalfOf(entry).State);
        Assert.NotEqual(SideCells.NotInstalled, cells!.Client.Text);

        // A later page keeps the scan the pane has; the Install tab's own builds refresh it.
        int scans = 0;
        Assert.NotNull(ModsMenu.PageSideCells(pane, entry, () => { scans++; return null; }));
        Assert.Equal(0, scans);
    }

    // ---- INSTALLSIDES2: column tint, grayed sides, glyphs, the admin lock -------------------------

    [Fact]
    public void Every_side_cell_and_button_sits_in_its_column_and_unused_sides_are_grayed()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-c", side: "Client"), Listing("o-s", side: "Server") });
        var rig = new Rig(this, catalog, pane =>
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None), ValidatedFolder());
        foreach (string key in new[] { "o-c", "o-s" })
        {
            foreach (string cell in new[] { InstallModsPane.CellClient, InstallModsPane.CellClientA, InstallModsPane.CellClientB })
                Assert.Equal(CellColumn.Client, rig.Cell(key, cell).Column);
            foreach (string cell in new[] { InstallModsPane.CellServer, InstallModsPane.CellServerA, InstallModsPane.CellServerB })
                Assert.Equal(CellColumn.Server, rig.Cell(key, cell).Column);
            Assert.Equal(CellColumn.None, rig.Cell(key, InstallModsPane.CellName).Column);
            // No Side column any more.
            Assert.DoesNotContain(rig.Pane.Rows.Single(r => r.Row.Key == key).Row.Cells, c => c.Key == InstallModsPane.CellSide);
        }

        // A client mod: the Server cell is grayed and says so; a server mod: the Client cell.
        Assert.True(rig.Cell("o-c", InstallModsPane.CellServer).Grayed);
        Assert.Equal(SideCells.NoServerSide, rig.Cell("o-c", InstallModsPane.CellServer).Text);
        Assert.False(rig.Cell("o-c", InstallModsPane.CellClient).Grayed);
        Assert.True(rig.Cell("o-s", InstallModsPane.CellClient).Grayed);
        Assert.False(rig.Cell("o-s", InstallModsPane.CellServer).Grayed);
        // The side buttons are the game's popup button, tinted by their column.
        Assert.Equal(CellSkin.PopupButton, rig.Cell("o-c", InstallModsPane.CellClientA).Skin);
        // The side filter still filters by side without a Side column.
        rig.Pane.Filters.Side = InstallSideFilter.Server;
        Assert.Equal(new[] { "o-s" }, rig.Pane.VisibleEntries().Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Not_an_admin_on_the_list_gets_Authenticate_as_admin_which_runs_the_existing_flow()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-s", side: "Server"));
        var offer = AdminOffer.Authenticate;
        int runs = 0;
        var rig = new Rig(this, catalog, pane =>
        {
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin);
            pane.AdminOfferProbe = () => offer;
            pane.AuthenticateAction = () => { runs++; return true; };
        });
        var pane = rig.Pane;
        Assert.Equal(ServerTargetKind.Server, pane.ServerTarget);
        var button = rig.Cell("o-s", InstallModsPane.CellServerA);
        Assert.Equal(AdminAuthFlow.ButtonCaption, button.Text);
        Assert.Equal(CellKind.Button, button.Kind);
        Assert.NotEqual(CellTone.Disabled, button.Tone);
        Assert.False(rig.Cell("o-s", InstallModsPane.CellServer).Grayed);
        // The drawer says why (the route's admin-list sentence).
        Assert.True(rig.Click("o-s/" + InstallModsPane.CellServerA));
        Assert.Equal(1, runs);

        // While it runs the button says so and cannot be pressed again.
        offer = AdminOffer.Authenticating;
        rig.Rebuild();
        button = rig.Cell("o-s", InstallModsPane.CellServerA);
        Assert.Equal(SideCells.AuthenticatingCaption, button.Text);
        Assert.Equal(CellTone.Disabled, button.Tone);
        Assert.False(pane.OnSideClick("o-s", server: true, SideButton.Authenticate));
        Assert.Equal(1, runs);

        // Not on the list: the dim lock, with its glyph asked for.
        offer = AdminOffer.AdminOnly;
        rig.Rebuild();
        button = rig.Cell("o-s", InstallModsPane.CellServerA);
        Assert.Equal(SideCells.AdminOnlyCaption, button.Text);
        Assert.Equal(CellTone.Disabled, button.Tone);
        Assert.Equal(SideCells.LockGlyph, button.Glyph);
    }

    [Fact]
    public void The_state_glyph_rides_on_the_side_text_cell()
    {
        var catalog = new FakeCatalog();
        var entry = Listing("o-c", side: "Client");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        // Progenitor installed it on this client (a client-root ledger row and its reload copy) and
        // this process loads it (a Local inventory row) at the listing's version.
        string reload = Path.Combine(_root, "client", "BepInEx", "progenitor-reload", "o.c.dll");
        rig.Pane.SetInstalledQuery(new InstalledStateQuery(
            files: new[] { new InstalledFile(reload, InstalledState.ReloadKind, "abcd", InstalledState.ClientRoot, 10) },
            inventory: new[] { new InstalledInventoryRow("o.c", "1.0.0", "Local") },
            ledger: new[]
            {
                new InstalledLedgerRow("o-c", "abcd", "1.0.0", "o.c.dll", source: "package:o-c@1.0.0",
                    category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: reload),
            }));
        var cell = rig.Pane.RowCells(entry).Single(c => c.Key == InstallModsPane.CellClient);
        Assert.Equal(HalfState.Installed, rig.Pane.ClientHalfOf(entry).State);
        Assert.Equal(SideCells.InstalledGlyph, cell.Glyph);
        Assert.Equal(CellTone.OkBright, cell.Tone);
        Assert.Equal(CellColumn.Client, cell.Column);
    }

    [Fact]
    public void The_runtime_draws_bands_tints_the_vanilla_button_and_checks_glyphs_against_the_font()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string place = SourcePin.MethodBody(runtime, "public void PlaceCells(float rowWidth)");
        SourcePin.AssertUncommented(place, "PlaceBands(placements, rowHeight);");
        string popup = SourcePin.MethodBody(runtime, "private static bool PaintPopupButton(Cell cell)");
        // The game's sprite stays; only its color is multiplied by the column's hue.
        SourcePin.AssertUncommented(popup, "cell.Fill.sprite = sprite;");
        SourcePin.AssertUncommented(popup, "var tint = column == CellColumn.None ? Color.white : ToColor(MenuPalette.ColumnButtonTint(column));");
        // Live review 3: the hue is a solid plate over the sprite (never the sprite again, whose
        // mostly transparent face thinned it to nothing), inset so the sprite's ends and rim show,
        // under the caption, active and with the column's color.
        SourcePin.AssertUncommented(popup, "PaintColumnOverlay(cell, sprite, type, ppu);");
        string overlay = SourcePin.MethodBody(runtime, "private static void PaintColumnOverlay(");
        SourcePin.AssertUncommented(overlay, "SetPlate(cell, color);");
        // VANILLA1: the plate is a helper shared with the filter chips.
        string plate = SourcePin.MethodBody(runtime, "private static void SetPlate(");
        SourcePin.AssertUncommented(plate, "cell.Tint.sprite = null;");
        SourcePin.AssertUncommented(plate, "cell.Tint.type = Image.Type.Simple;");
        SourcePin.AssertUncommented(plate, "InsetStretch(go, InstallColumns.ButtonTintInsetX, InstallColumns.ButtonTintInsetY);");
        SourcePin.AssertUncommented(plate, "try { go.transform.SetSiblingIndex(0); } catch { /* */ }");
        SourcePin.AssertUncommented(plate, "cell.Tint.gameObject.SetActive(true);");
        SourcePin.AssertUncommented(plate, "cell.Tint.color = color.Value;");
        Assert.DoesNotContain("cell.Tint.sprite = sprite;", overlay + plate, StringComparison.Ordinal);
        // The readback names what a column button actually draws (live check: widget_read images).
        string images = SourcePin.MethodBody(runtime, "public IReadOnlyList<Dictionary<string, object?>> CellImages(string key)");
        SourcePin.AssertUncommented(images, "Add(cell.Go.GetComponent<Image>(), -1);");
        string handles = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiHandles.cs");
        Assert.Contains("described[\"images\"] = images;", handles, StringComparison.Ordinal);
        string glyph = SourcePin.MethodBody(runtime, "private static string GlyphPrefix(TextMeshProUGUI? tmp, string? glyph)");
        SourcePin.AssertUncommented(glyph, "try { one = font.HasCharacter(c, true, true); }");
        SourcePin.AssertUncommented(glyph, "if (char.IsSurrogate(c)) { has = false; break; }");
        // A NoScroll block registers no inner scroll, so the wheel always scrolls the list.
        string scroll = SourcePin.MethodBody(runtime, "private void BuildScrollText(Cell cell)");
        int early = scroll.IndexOf("if (cell.Spec.NoScroll)", StringComparison.Ordinal);
        int register = scroll.IndexOf("InnerScrolls.Add(scroll);", StringComparison.Ordinal);
        Assert.True(early >= 0 && register > early, "NoScroll must return before the block is registered");
    }

    // ---- INSTALLSIDES2 live review: color, grayed cells, facts grid ------------------------------

    [Fact]
    public void Bands_are_clearly_blue_and_red_keep_text_contrast_and_grayed_cells_recede()
    {
        var client = MenuPalette.ColumnClientCellFill;
        var server = MenuPalette.ColumnServerCellFill;
        // Blue is the client band's strongest channel, red the server's, by a clear margin.
        Assert.True(client.B > client.R + 0.15f && client.B > client.G + 0.05f, client.ToString());
        Assert.True(server.R > server.G + 0.15f && server.R > server.B + 0.15f, server.ToString());
        // The headers share their band's hue.
        Assert.True(MenuPalette.ColumnClientHeaderFill.B > MenuPalette.ColumnClientHeaderFill.R + 0.15f);
        Assert.True(MenuPalette.ColumnServerHeaderFill.R > MenuPalette.ColumnServerHeaderFill.B + 0.15f);
        // Every word color a side cell uses stays readable on its band: WCAG 2 contrast (sRGB
        // linearized) of at least 4.5:1.
        foreach (var band in new[] { client, server })
        {
            foreach (var word in new[] { MenuPalette.Muted, MenuPalette.Text, MenuPalette.OkText, MenuPalette.InfoText, MenuPalette.PendText })
                Assert.True(Wcag(word, band) >= 4.49f, word + " on " + band + ": " + Wcag(word, band));
        }
        // The header captions use the column's light text on the header fill.
        Assert.True(Wcag(MenuPalette.ColumnClientButtonText, MenuPalette.ColumnClientHeaderFill) >= 4.5f);
        Assert.True(Wcag(MenuPalette.ColumnServerButtonText, MenuPalette.ColumnServerHeaderFill) >= 4.5f);
        // A grayed band is darker than either active band, has no rim, and its words are dimmer than muted.
        var (grayFill, grayRim) = MenuPalette.ColumnBand(CellColumn.Client, header: false, grayed: true);
        Assert.True(grayFill.Luminance < client.Luminance && grayFill.Luminance < server.Luminance);
        Assert.Equal(0f, grayRim.A);
        Assert.True(MenuPalette.GrayedText.Luminance < MenuPalette.Muted.Luminance);
        Assert.Equal(MenuPalette.GrayedText, MenuPalette.TextColor(CellTone.Faint));
        // The button overlay is the column's hue, strong enough to show over the dark sprite.
        Assert.True(MenuPalette.ColumnButtonOverlay(CellColumn.Client).B > 0.7f && MenuPalette.ColumnButtonOverlay(CellColumn.Client).A >= 0.5f);
        Assert.True(MenuPalette.ColumnButtonOverlay(CellColumn.Server).R > 0.7f && MenuPalette.ColumnButtonOverlay(CellColumn.Server).A >= 0.5f);
    }

    private static float Wcag(PaletteColor a, PaletteColor b)
    {
        static double Lin(float c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        static double L(PaletteColor c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        double la = L(a), lb = L(b);
        return (float)((Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05));
    }

    [Fact]
    public void Side_buttons_carry_a_hue_brighter_than_their_band_and_a_missing_half_is_not_an_update()
    {
        // Live review 3 measured the button faces as drawn: client #14203a, server #1a1418 — the band
        // seen through SmallerButton_Normal's mostly transparent face. The plate (a solid color, not
        // the sprite, so the sprite's alpha cannot thin it) over those faces must read as a lighter
        // blue / red than the band under the button.
        static PaletteColor Over(PaletteColor overlay, PaletteColor under) => new(
            overlay.R * overlay.A + under.R * (1f - overlay.A),
            overlay.G * overlay.A + under.G * (1f - overlay.A),
            overlay.B * overlay.A + under.B * (1f - overlay.A), 1f);
        var client = Over(MenuPalette.ColumnButtonOverlay(CellColumn.Client), PaletteColor.Hex(0x14203a));
        var server = Over(MenuPalette.ColumnButtonOverlay(CellColumn.Server), PaletteColor.Hex(0x1a1418));
        Assert.True(client.Luminance > MenuPalette.ColumnClientCellFill.Luminance * 1.5f && client.B > client.R + 0.2f, client.ToString());
        Assert.True(server.Luminance > MenuPalette.ColumnServerCellFill.Luminance * 1.5f && server.R > server.B + 0.2f, server.ToString());
        Assert.True(MenuPalette.ColumnButtonOverlay(CellColumn.Client).A >= 0.5f);
        // The multiply keeps the sprite light, so its hover / pressed swaps still show.
        Assert.True(MenuPalette.ColumnButtonTint(CellColumn.Client).Luminance > 0.85f);
        Assert.True(MenuPalette.ColumnButtonTint(CellColumn.Server).Luminance > 0.85f);

        // ↑ is an update only; a missing half is "!" in the same amber.
        var missing = SideCells.Decide(new SideCellInputs
        {
            Side = PackageSide.Both, NeedsBoth = true, Latest = "0.1.0",
            Client = SideHalf.None,
            Server = new SideHalf { State = HalfState.Installed, Version = "0.1.0", CanUninstall = true },
            Target = ServerTargetKind.Server,
            Route = new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None),
        }).Client;
        Assert.Equal(SideCells.KindMissing, missing.Kind);
        Assert.Equal(InstallModsPane.MissingGlyph, InstallModsPane.SideGlyph(missing));
        Assert.Equal(CellTone.Amber, InstallModsPane.SideTone(missing.Tone));
        var update = SideCells.Decide(new SideCellInputs
        {
            Side = PackageSide.Client, Latest = "2.0.0",
            Client = new SideHalf { State = HalfState.Installed, Version = "1.0.0", CanUninstall = true },
            Target = ServerTargetKind.None,
        }).Client;
        Assert.Equal(SideCells.UpdateGlyph, InstallModsPane.SideGlyph(update));
    }

    [Fact]
    public void Grayed_side_words_are_faint()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-c", side: "Client"));
        var rig = new Rig(this, catalog, pane =>
            pane.ServerRouteProbe = () => new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None), ValidatedFolder());
        Assert.Equal(CellTone.Faint, rig.Cell("o-c", InstallModsPane.CellServer).Tone);
        Assert.NotEqual(CellTone.Faint, rig.Cell("o-c", InstallModsPane.CellClient).Tone);
    }

    [Fact]
    public void Facts_fill_a_two_column_grid_in_order_and_long_facts_span_after_it()
    {
        var catalog = new FakeCatalog();
        var entry = Listing("o-x");
        catalog.Entries.Add(entry);
        var rig = new Rig(this, catalog);
        rig.Pane.ColumnRowWidthProbe = () => 1624f;
        var lines = rig.Pane.DrawerLines(entry, rig.Pane.ActionFor(entry), out _).Where(l => l.Kind == CellLineKind.Fact).ToList();
        float half = rig.Pane.FactHalfWidth();
        // Every paired line holds two facts that fit half; at most one short fact is alone (the last
        // of an odd count), and every line after the first single one is a long, spanning fact.
        bool spanning = false;
        int singlesShort = 0;
        foreach (var line in lines)
        {
            if (line.HasSecond)
            {
                Assert.False(spanning, "a pair after a spanning fact: " + line);
                Assert.True(InstallModsPane.FitsHalf((line.Label, line.Text), half), line.ToString());
                Assert.True(InstallModsPane.FitsHalf((line.Label2, line.Text2), half), line.ToString());
                continue;
            }

            if (InstallModsPane.FitsHalf((line.Label, line.Text), half))
            {
                Assert.False(spanning, "a short fact after a spanning one: " + line);
                singlesShort++;
            }
            else spanning = true;
        }
        Assert.True(singlesShort <= 1);
        // The grid keeps the facts' order.
        var order = lines.SelectMany(l => l.HasSecond ? new[] { l.Label, l.Label2 } : new[] { l.Label }).ToList();
        Assert.True(order.IndexOf(InstallModsCopy.FactLatest) < order.IndexOf(InstallModsCopy.FactReleased));
        // The live row's Side fact ("Client (this game) (from its Thunderstore tags)") fits half the 1624 px row.
        Assert.True(InstallModsPane.FitsHalf((InstallModsCopy.FactSide, InstallModsCopy.SideFact(PackageSide.Client, fromTags: true)), half));
    }

    // ---- INSTALLFILTERS1: the Show filter ---------------------------------------------------------

    private static SideCellsDecision Sides(
        SideHalf client, SideHalf? server = null, ServerTargetKind target = ServerTargetKind.Server,
        PackageSide side = PackageSide.Both, bool needsBoth = false, string latest = "1.0.0", bool shared = true) =>
        SideCells.Decide(new SideCellInputs
        {
            Side = side,
            NeedsBoth = needsBoth,
            Latest = latest,
            Client = client,
            Server = server ?? new SideHalf { State = HalfState.Absent },
            Target = target,
            PrivateShared = shared,
            Route = new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None),
        });

    private static SideHalf Have(string v) => new() { State = HalfState.Installed, Version = v, CanUninstall = true, ProgenitorManaged = true };
    private static SideHalf None => new() { State = HalfState.Absent };

    [Fact]
    public void Show_predicates_follow_the_side_cells_table()
    {
        bool Is(SideCellsDecision d, InstallShowFilter f) => InstallModsPane.ShowPredicate(d, f);

        // Missing a side: a manifest-verified both-sided mod the server has and this client lacks.
        var missing = Sides(None, Have("1.0.0"), needsBoth: true);
        Assert.Equal(SideCells.KindMissing, missing.Client.Kind);
        Assert.True(Is(missing, InstallShowFilter.MissingSide));
        Assert.True(Is(missing, InstallShowFilter.Installed));
        Assert.False(Is(missing, InstallShowFilter.NeedsUpdate));
        // ...and the other way round.
        Assert.True(Is(Sides(Have("1.0.0"), None, needsBoth: true), InstallShowFilter.MissingSide));
        // Thunderstore's tags alone never make a missing side.
        Assert.False(Is(Sides(None, Have("1.0.0"), needsBoth: false), InstallShowFilter.MissingSide));
        // A side the mod does not use is never missing.
        Assert.False(Is(Sides(Have("1.0.0"), None, side: PackageSide.Client, needsBoth: true), InstallShowFilter.MissingSide));
        // Picker None: no server, nothing missing, whatever the client holds.
        var none = Sides(Have("1.0.0"), Have("1.0.0"), target: ServerTargetKind.None, needsBoth: true);
        Assert.Equal(SideCells.KindNoServer, none.Server.Kind);
        Assert.False(Is(none, InstallShowFilter.MissingSide));
        Assert.True(Is(none, InstallShowFilter.Installed));
        // Private games: they load the client's copy (shared, or a separate folder): never missing.
        Assert.False(Is(Sides(Have("1.0.0"), None, target: ServerTargetKind.PrivateGames, needsBoth: true), InstallShowFilter.MissingSide));
        Assert.False(Is(Sides(Have("1.0.0"), None, target: ServerTargetKind.PrivateGames, needsBoth: true, shared: false), InstallShowFilter.MissingSide));
        Assert.False(Is(Sides(None, None, target: ServerTargetKind.PrivateGames, needsBoth: true), InstallShowFilter.MissingSide));

        // Needs update: behind the listing on either side...
        var behind = Sides(Have("0.9.0"), Have("1.0.0"));
        Assert.Equal(SideCells.KindUpdate, behind.Client.Kind);
        Assert.True(Is(behind, InstallShowFilter.NeedsUpdate));
        Assert.True(Is(Sides(Have("1.0.0"), Have("0.9.0")), InstallShowFilter.NeedsUpdate));
        // ...or older than the other side of a both-sided mod.
        var older = Sides(Have("1.0.0"), Have("1.1.0"), needsBoth: true, latest: "1.0.0");
        Assert.Equal(SideCells.KindOlder, older.Client.Kind);
        Assert.True(Is(older, InstallShowFilter.NeedsUpdate));
        Assert.False(Is(Sides(Have("1.0.0"), Have("1.0.0")), InstallShowFilter.NeedsUpdate));

        // Installed: a copy on either side, pending and leaving included; nothing on neither.
        Assert.True(Is(Sides(new SideHalf { State = HalfState.Pending, Version = "1.0.0", CanCancelInstall = true }), InstallShowFilter.Installed));
        Assert.True(Is(Sides(new SideHalf { State = HalfState.RemovalPending, Version = "1.0.0", CanCancelUninstall = true }), InstallShowFilter.Installed));
        Assert.False(Is(Sides(None, None), InstallShowFilter.Installed));
        Assert.True(Is(Sides(None, None), InstallShowFilter.All));
    }

    /// <summary>A client-root Progenitor install of <paramref name="key"/> at <paramref name="version"/>, loaded now.</summary>
    private InstalledStateQuery ClientInstalls(params (string Key, string Plugin, string Version)[] mods)
    {
        var files = new List<InstalledFile>();
        var ledger = new List<InstalledLedgerRow>();
        var inventory = new List<InstalledInventoryRow>();
        foreach (var (key, plugin, version) in mods)
        {
            string reload = Path.Combine(_root, "client", "BepInEx", "progenitor-reload", plugin + ".dll");
            files.Add(new InstalledFile(reload, InstalledState.ReloadKind, "sha-" + key, InstalledState.ClientRoot, 10));
            ledger.Add(new InstalledLedgerRow(key, "sha-" + key, version, plugin + ".dll", source: "package:" + key + "@" + version,
                category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: reload));
            inventory.Add(new InstalledInventoryRow(plugin, version, "Local"));
        }
        return new InstalledStateQuery(files, inventory, ledger);
    }

    [Fact]
    public void Show_counts_combine_with_side_only_and_search_and_the_choice_is_single()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[]
        {
            Listing("o-a", side: "Client"), Listing("o-b", side: "Client"), Listing("o-c", side: "Client"), Listing("o-s", side: "Server"),
        });
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        // o-a installed and current; o-b installed and behind the listing (1.0.0); o-c and o-s absent.
        pane.SetInstalledQuery(ClientInstalls(("o-a", "o.a", "1.0.0"), ("o-b", "o.b", "0.9.0")));

        var counts = pane.ShowCounts();
        Assert.Equal(4, counts[InstallShowFilter.All]);
        Assert.Equal(2, counts[InstallShowFilter.Installed]);
        Assert.Equal(1, counts[InstallShowFilter.NeedsUpdate]);
        Assert.Equal(0, counts[InstallShowFilter.MissingSide]);

        // Single choice: picking one replaces the other.
        pane.SetShowFilter(InstallShowFilter.Installed);
        Assert.Equal(new[] { "o-a", "o-b" }, pane.VisibleEntries().Select(e => e.Key).OrderBy(k => k).ToArray());
        pane.SetShowFilter(InstallShowFilter.NeedsUpdate);
        Assert.Equal(InstallShowFilter.NeedsUpdate, pane.Filters.Show);
        Assert.Equal(new[] { "o-b" }, pane.VisibleEntries().Select(e => e.Key).ToArray());

        // The counts follow the Side group and the search (the Show choice itself aside).
        pane.SetSideFilter(InstallSideFilter.Server);
        Assert.Empty(pane.VisibleEntries());
        counts = pane.ShowCounts();
        Assert.Equal(1, counts[InstallShowFilter.All]);
        Assert.Equal(0, counts[InstallShowFilter.Installed]);
        pane.SetSideFilter(InstallSideFilter.Any);
        pane.Search = "o-b";
        counts = pane.ShowCounts();
        Assert.Equal(1, counts[InstallShowFilter.All]);
        Assert.Equal(1, counts[InstallShowFilter.Installed]);
        Assert.Equal(1, counts[InstallShowFilter.NeedsUpdate]);
        pane.Search = "o-c";
        counts = pane.ShowCounts();
        Assert.Equal(1, counts[InstallShowFilter.All]);
        Assert.Equal(0, counts[InstallShowFilter.Installed]);
        Assert.Equal(0, counts[InstallShowFilter.NeedsUpdate]);
        pane.Search = "";

        // Only toggles narrow the counts too (none of these declares no-restart).
        pane.ToggleNoRestartOnly();
        counts = pane.ShowCounts();
        Assert.Equal(0, counts[InstallShowFilter.All]);
        Assert.Equal(0, counts[InstallShowFilter.NeedsUpdate]);
        pane.ToggleNoRestartOnly();

        // Clear filters resets the Show choice too.
        Assert.False(pane.AtDefaults);
        pane.ClearFilters();
        Assert.Equal(InstallShowFilter.All, pane.Filters.Show);
        Assert.True(pane.AtDefaults);
    }

    [Fact]
    public void The_filter_rows_read_back_the_show_choice_counts_summary_and_click_keys()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { Listing("o-a", side: "Client"), Listing("o-b", side: "Client") });
        var rig = new Rig(this, catalog);
        var pane = rig.Pane;
        Assert.Equal(new[] { InstallModsPane.FiltersKey, InstallModsPane.FiltersOnlyKey }, pane.FilterRows.Select(r => r.Key).ToArray());

        // The Show chips are clickable by key on the filters row; the Only chips on filters-only.
        Assert.True(rig.Click(InstallModsPane.FiltersKey + "/" + InstallModsPane.CellShowInstalled));
        Assert.Equal(InstallShowFilter.Installed, pane.Filters.Show);
        Assert.True(rig.Click(InstallModsPane.FiltersKey + "/" + InstallModsPane.CellShowAll));
        Assert.Equal(InstallShowFilter.All, pane.Filters.Show);
        Assert.True(rig.Click(InstallModsPane.FiltersOnlyKey + "/" + InstallModsPane.CellNoRestart));
        Assert.True(pane.Filters.NoRestartOnly);
        Assert.True(rig.Click(InstallModsPane.FiltersOnlyKey + "/" + InstallModsPane.CellClear));
        Assert.False(pane.Filters.NoRestartOnly);

        pane.SetSideFilter(InstallSideFilter.Client);
        pane.SetShowFilter(InstallShowFilter.MissingSide);
        foreach (var row in pane.FilterRows)
        {
            Assert.True(pane.TryDescribeSidebarRow(row, out var extra));
            var filters = (Dictionary<string, object?>)extra!["filters"]!;
            Assert.Equal("missing", filters["show"]);
            Assert.Equal("client", filters["side"]);
            var counts = (Dictionary<string, object?>)filters["counts"]!;
            Assert.Equal(new[] { "all", "installed", "missing", "update" }, counts.Keys.ToArray());
            Assert.Equal(2, counts["all"]);
            Assert.Equal(new List<string> { InstallModsPane.FiltersKey, InstallModsPane.FiltersOnlyKey }, filters["rows"]);
            Assert.StartsWith("Showing 0" + SideCells.Dot + "missing a side" + SideCells.Dot + "client mods", (string)filters["summary"]!);
        }

        // The summary cell and the chips' captions and tones follow in place.
        var only = pane.FilterOnlyRow!.Cells;
        Assert.StartsWith("Showing 0 · missing a side · client mods", only.Single(c => c.Key == InstallModsPane.CellSummary).Text);
        Assert.EndsWith("current game update only", only.Single(c => c.Key == InstallModsPane.CellSummary).Text);
        var showMissing = pane.FilterRow!.Cells.Single(c => c.Key == InstallModsPane.CellShowMissing);
        Assert.Equal("Missing a side 0", showMissing.Text);
        Assert.Equal(CellTone.ChipOn, showMissing.Tone);
        Assert.Equal(CellTone.ChipOff, pane.FilterRow!.Cells.Single(c => c.Key == InstallModsPane.CellShowAll).Tone);
        Assert.Equal(CellTone.ChipOn, pane.FilterRow!.Cells.Single(c => c.Key == InstallModsPane.CellSideClient).Tone);
    }

    [Fact]
    public void A_narrow_row_wraps_the_show_group_onto_its_own_row()
    {
        var catalog = new FakeCatalog();
        catalog.Entries.Add(Listing("o-a"));
        var rig = new Rig(this, catalog, pane => pane.ColumnRowWidthProbe = () => 860f);
        var pane = rig.Pane;
        Assert.Equal(new[] { InstallModsPane.FiltersKey, InstallModsPane.FiltersShowKey, InstallModsPane.FiltersOnlyKey },
            pane.FilterRows.Select(r => r.Key).ToArray());
        Assert.True(rig.Click(InstallModsPane.FiltersShowKey + "/" + InstallModsPane.CellShowUpdate));
        Assert.Equal(InstallShowFilter.NeedsUpdate, pane.Filters.Show);
        Assert.DoesNotContain(pane.FilterRow!.Cells, c => c.Key == InstallModsPane.CellShowAll);
    }

    // ---- Kit pieces ------------------------------------------------------------------------------

    [Fact]
    public void An_overlay_cell_sits_over_the_right_end_of_its_target_and_takes_no_slot()
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, "a").With(width: CellWidth.Px(100f)),
            new CellSpec(CellKind.Text, "name").With(width: CellWidth.Fill(50f)),
            new CellSpec(CellKind.Button, "over").With(text: "x", width: CellWidth.Px(40f)),
            new CellSpec(CellKind.Text, "b").With(width: CellWidth.Px(100f)),
        };
        cells[2].OverlayOf = "name";
        var placed = CellLayout.Resolve(400f, cells);
        var name = placed.Single(p => p.Key == "name");
        var over = placed.Single(p => p.Key == "over");
        // The row is laid out as if the overlay were not there: a, gap, name, gap, b fill 400.
        Assert.Equal(400f - 200f - 2 * CellLayout.Gap, name.Width, 1);
        Assert.True(over.Visible);
        Assert.Equal(40f, over.Width, 1);
        Assert.Equal(name.Right, over.Right, 1);
        // Hidden with its target, and never wider than it.
        cells[2].Visible = false;
        Assert.False(CellLayout.Resolve(400f, cells).Single(p => p.Key == "over").Visible);
        cells[2].Visible = true;
        cells[2].With(width: CellWidth.Px(900f));
        var wide = CellLayout.Resolve(400f, cells);
        Assert.Equal(wide.Single(p => p.Key == "name").Width, wide.Single(p => p.Key == "over").Width, 1);
    }

    [Fact]
    public void An_open_puts_the_row_at_the_top_even_when_its_block_already_fits()
    {
        const float view = 700f, content = 3000f;
        // Already in view: a rebuild leaves the list where it is...
        Assert.Equal(100f, MenuPanelLayout.RevealOffsetY(300f, 200f, 100f, view, content), 3);
        // ...an open brings the row to the top...
        Assert.Equal(300f, MenuPanelLayout.RevealOffsetY(300f, 200f, 100f, view, content, toTop: true), 3);
        // ...clamped to the end of the content for the last rows.
        Assert.Equal(content - view, MenuPanelLayout.RevealOffsetY(content - 100f, 200f, 0f, view, content, toTop: true), 3);
    }

    private LocalServerFolder ValidatedFolder()
    {
        string path = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(path, LocalServerFolder.BepInExFolderName));
        File.WriteAllBytes(Path.Combine(path, LocalServerFolder.ServerExecutableName), new byte[] { 0 });
        var folder = new LocalServerFolder();
        var applied = folder.Apply(path);
        Assert.True(applied.Ok, applied.Refusal);
        return folder;
    }

    // ---- Fakes -----------------------------------------------------------------------------------

    private sealed class NeverClock : ICatalogClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    internal sealed class FakeCatalog : IInstallCatalog
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
            new TaskCompletionSource<InspectCallResult>().Task;

        public List<InstallTarget> Planned { get; } = new();

        public PlanCallResult Plan(string key, InstallTarget target)
        {
            Planned.Add(target);
            return new(false, InstallReasons.NotInspected, null);
        }

        public ApproveCallResult Approve(string planHash) => new(true, null, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) => new(true, null, "pending/" + planHash, false);

        // Never lands: a README arriving would rebuild the pane mid-test.
        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            new TaskCompletionSource<ListingDocumentSet>().Task;
    }

    private static string RepoRootForSource()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Progenitor.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }
}
