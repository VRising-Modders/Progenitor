using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
/// DL1 on the LIST2 list view: the Install tab's one primary flow, driven through the row's
/// Install button (the same Bootstrap slot the player clicks), never by calling the service
/// directly. <c>Install · restart</c> → <c>Installing…</c> (download and check) → the planner
/// label or the exact blocked reason → pending (<c>Installed · restart to load</c>, path +
/// sha256). Download/check is separate from execution approval; the downloaded package never
/// executes during the check. The real <see cref="InstallCatalogService"/> is used here with
/// a fake transport, so the zip inspection and the pending write are the production paths.
/// </summary>
public sealed class InstallDownloadFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-dl1-" + Guid.NewGuid().ToString("N"));
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();

    public InstallDownloadFlowTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => null;
        Directory.CreateDirectory(_root);
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
        LocalServerFolder.ReparseLookup = null;
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ShareLookup = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Ordinary_bepinex_package_is_blocked_after_inspection_with_the_exact_planner_reason()
    {
        byte[] zipBytes = ZipBytes(("manifest.json", Encoding.UTF8.GetBytes("{\"name\":\"ExampleCommandFramework\"}")), ("ECF.dll", DllBytes("ecf")));
        var transport = new FakeTransport(_ => new CatalogTransportResponse(200, new MemoryStream(zipBytes)));
        var catalog = new InstallCatalogService(_root, transport, new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock());
        Assert.True(catalog.LoadIndexFromJson(SingleListingJson("dyno", "ExampleCommandFramework", "0.11.0"), "stamp").Ok);
        var rig = new Rig(this, catalog);
        const string key = "dyno-ExampleCommandFramework";
        Assert.Equal(SideCells.InstallCaption, rig.Cell(key, InstallModsPane.CellClientA).Text);
        Assert.Equal(InstallListingState.None, rig.Pane.ActionFor(catalog.Search(null)[0]).State);

        rig.ClickCell(key, InstallModsPane.CellClientA);
        Assert.NotNull(rig.Pane.ActiveDownload);
        Assert.Equal(InstallModsCopy.Installing, rig.Cell(key, InstallModsPane.CellClient).Text);
        await rig.Settle();
        Assert.Equal(1, transport.DownloadCalls);
        Assert.Equal(InstallActionPhase.Inspected, rig.Pane.Phase);
        var action = rig.Pane.ActionFor(catalog.Search(null)[0]);
        Assert.Equal(InstallListingState.Blocked, action.State);
        Assert.Equal(InstallModsCopy.CannotInstall, action.Label);
        Assert.False(action.Enabled);
        Assert.Equal(InstallReasons.OrdinaryBepInExBlocked, action.Reason);
        Assert.Equal(SideCells.CannotInstall, rig.Cell(key, InstallModsPane.CellClient).Text);
        rig.ClickRow(key);
        Assert.Contains(InstallReasons.OrdinaryBepInExBlocked, InstallListViewTests.DrawerTexts(rig.Pane));
        var plan = (Dictionary<string, object?>)rig.Action["plan"]!;
        Assert.Equal(InstallReasons.OrdinaryBepInExBlocked, plan["reason"]);
        Assert.Equal("UnknownBlocked", plan["decision"]);
        Assert.NotEqual(InstallReasons.OrdinaryBepInExRestart, action.Reason);
        Assert.DoesNotContain(InstallReasons.EligibilityNotWired, action.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hot", action.Reason, StringComparison.OrdinalIgnoreCase);
        // INSTALLUX1 items 1 and 2: no chooser row exists any more, and the box that opened on the
        // click now says the refusal and offers only Close.
        Assert.False(rig.Pane.TargetChooserOpen);
        Assert.Equal(InstallPromptKind.Install, rig.Pane.Prompt);
        Assert.Equal(InstallReasons.OrdinaryBepInExBlocked, rig.Pane.PromptRefusal);
        Assert.Equal(
            InstallModsCopy.CannotInstallBody(InstallReasons.OrdinaryBepInExBlocked),
            rig.Pane.InstallPromptBody(catalog.Search(null)[0]));
        var refusedRow = (IUiCellRow)rig.Pane.PromptHandles.Last();
        Assert.Equal(
            // INSTALLUX2 item 1: the refusal is a dialog with one button, under promptClose.
            new[] { "dialog-left", InstallModsPane.CellPromptClose, "dialog-right" },
            refusedRow.Cells.Select(c => c.Key).ToArray());
        Assert.Equal(InstallModsCopy.PromptClose, refusedRow.Cells.Single(c => c.Key == InstallModsPane.CellPromptClose).Text);
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-pending")));
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-reload")));
        Assert.False(Directory.Exists(Path.Combine(_root, "plugins")));
        // Revert: mapping UnknownBlocked to "restart required" / the hot-load excuse, or
        // inventing a restart route for an ordinary package, fails the reason asserts.
    }

    [Fact]
    public async Task Progenitor_manifest_package_installs_to_pending_in_one_click_and_reads_back_the_sha256()
    {
        byte[] dll = DllBytes("sample");
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha + "\",\"side\":\"client\",\"hotLoadable\":false}";
        byte[] zipBytes = ZipBytes(("progenitor.json", Encoding.UTF8.GetBytes(json)), ("example.sample.dll", dll));
        var transport = new FakeTransport(_ => new CatalogTransportResponse(200, new MemoryStream(zipBytes)));
        var catalog = new InstallCatalogService(_root, transport, new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock());
        Assert.True(catalog.LoadIndexFromJson(SingleListingJson("team", "Sample", "1.0.0"), "stamp").Ok);
        var rig = new Rig(this, catalog);
        const string key = "team-Sample";

        // One target (no local server): Install runs the download and the check, then asks. INSTALL2
        // item 3: the write is Yes's, and the body is DEPS1's own sentences.
        rig.ClickCell(key, InstallModsPane.CellClientA);
        await rig.Settle();
        Assert.Equal(InstallActionPhase.Inspected, rig.Pane.Phase);
        Assert.Equal(InstallPromptKind.Install, rig.Pane.Prompt);
        var asked = (Dictionary<string, object?>)rig.Action["prompt"]!;
        Assert.Equal("install", asked["kind"]);
        Assert.StartsWith("Install ", (string)asked["heading"]!);
        Assert.EndsWith(" 1.0.0 on this game?", (string)asked["heading"]!);
        Assert.Equal(InstallModsCopy.PromptYesInstall, asked["yes"]);
        Assert.Contains("Loads at the next restart of V Rising.", (string)asked["body"]!);
        Assert.Equal(0, rig.Pane.PlanSet!.ClosureCount);
        rig.ClickCell(InstallModsPane.PromptKeyPrefix + key, InstallModsPane.CellPromptYes);
        Assert.Equal(InstallActionPhase.Done, rig.Pane.Phase);
        Assert.False(rig.Pane.TargetChooserOpen);
        var result = (Dictionary<string, object?>)rig.Action["result"]!;
        string path = (string)result["path"]!;
        Assert.Equal(Path.Combine(_root, "progenitor-pending", "index", "example.sample.dll"), path);
        Assert.True(File.Exists(path));
        Assert.Equal(dllSha, result["sha256"]);
        Assert.Equal(dllSha, CatalogFetcher.HashFile(path));
        Assert.Equal(false, result["alreadyPresent"]);
        Assert.Equal("RestartClientRequired", ((Dictionary<string, object?>)rig.Action["plan"]!)["decision"]);
        // INSTALLUX2 item 2: the status word is its own label beside the row's button.
        Assert.Equal(InstallStatusLabels.PendingGameRestart, rig.Cell(key, InstallModsPane.CellClient).Text);
        Assert.Equal("pending", rig.Action["state"]);
        Assert.Equal(true, rig.Action["restart"]);
        // The install fact is the drawer head's one muted cell; the head repeats nothing of the row.
        rig.ClickRow(key);
        // INSTALLSIDES2 item 5: the note names each side; the client half owes a game restart.
        Assert.StartsWith(SideCells.ClientLabel + ": " + SideCells.LoadsAtGameRestart,
            rig.Pane.DrawerHeadRow!.Cells.Single(c => c.Key == InstallModsPane.CellNote).Text);
        Assert.DoesNotContain(rig.Pane.DrawerHeadRow!.Cells, c => c.Key == InstallModsPane.CellAction);
        var drawer = InstallListViewTests.DrawerTexts(rig.Pane);
        Assert.Contains(InstallModsCopy.ConfigureInMods, drawer);
        // No hash, tier or framework wording reaches the tab; the sha256 is readback only.
        Assert.DoesNotContain(drawer, t => t.Contains(dllSha, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(drawer, t => t.Contains("Progenitor", StringComparison.Ordinal));
        // INSTALL2 item 5: the bar, the Restart now stub and the one collapsed pending row.
        Assert.Equal(2, rig.Pane.PendingBarHandles.Count);
        Assert.Equal(InstallModsCopy.PendingBar(1), rig.Pane.PendingBarText);
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-reload")));
        Assert.False(Directory.Exists(Path.Combine(_root, "plugins")));
        Assert.Equal(1, transport.DownloadCalls);
        // A second click on the pending row does nothing: INSTALLUX2 item 2 leaves a row this click
        // just wrote with its status word and no button at all, so there is no install cell to press.
        Assert.Empty((IReadOnlyList<string>)rig.Pane.ListingToWidget(rig.Pane.AllEntries().Single(e => e.Key == key))["actions"]!);
        Assert.False(UiCellRowHandle.TryInvoke(key + "/" + InstallModsPane.CellClientA, out _));
        Assert.Equal(1, transport.DownloadCalls);
        Assert.Equal(InstallActionPhase.Done, rig.Pane.Phase);
        // Revert: skipping Approve before ExecuteToPending, writing on the click before the check,
        // a chooser with one target, or a Confirm step between the click and the write fails this.
    }

    [Fact]
    public async Task Progenitor_manifest_package_with_a_local_server_installs_to_the_side_the_setting_resolves()
    {
        byte[] dll = DllBytes("sample");
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha + "\",\"side\":\"server\",\"hotLoadable\":false}";
        byte[] zipBytes = ZipBytes(("progenitor.json", Encoding.UTF8.GetBytes(json)), ("example.sample.dll", dll));
        var transport = new FakeTransport(_ => new CatalogTransportResponse(200, new MemoryStream(zipBytes)));
        var catalog = new InstallCatalogService(_root, transport, new CatalogFetchOptions { Timeout = TimeSpan.FromSeconds(5) }, new ImmediateClock(), localServerConfigured: true);
        Assert.True(catalog.LoadIndexFromJson(SingleListingJson("team", "Sample", "1.0.0"), "stamp").Ok);
        var server = ValidatedFolder();
        var rig = new Rig(this, catalog, server);
        const string key = "team-Sample";
        // INSTALLSIDES1: a local server IS configured; the Server cell's Install writes its folder.
        rig.ClickCell(key, InstallModsPane.CellServerA);
        await rig.Settle();
        Assert.Equal(InstallActionPhase.Inspected, rig.Pane.Phase);
        // No chooser: the pressed side is the target, and the box asks nothing about it.
        Assert.False(rig.Pane.TargetChooserOpen);
        Assert.Equal(InstallTarget.LocalServer, rig.Pane.Target);
        Assert.Equal("localServer", rig.Action["target"]);
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-pending")));
        // The row keeps its own caption while the box is open — clicking it again must not restart
        // the download behind the open box.
        Assert.Equal(InstallPromptKind.Install, rig.Pane.Prompt);
        Assert.True(rig.Pane.PromptPlanReady);
        Assert.Equal(SideCells.InstallCaption, rig.Cell(key, InstallModsPane.CellServerA).Text);
        Assert.False(rig.Pane.OnSideClick(key, server: true, SideButton.Install));
        Assert.False(rig.Pane.OnInstallClick(key));
        // The approval is still bound to the plan hash: a target change drops it, and the box's Yes
        // re-plans and re-approves rather than writing a stale approval.
        Assert.True(rig.Pane.BeginReview());
        Assert.NotNull(rig.Pane.ApprovedPlanHash);
        Assert.True(rig.Pane.SelectTarget(InstallTarget.LocalServer));
        Assert.Null(rig.Pane.ApprovedPlanHash);
        Assert.Equal(InstallActionPhase.Inspected, rig.Pane.Phase);
        rig.ClickCell(InstallModsPane.PromptKeyPrefix + key, InstallModsPane.CellPromptYes);
        Assert.Null(rig.Pane.ApprovedPlanHash);
        Assert.Equal(InstallPromptKind.None, rig.Pane.Prompt);
        Assert.False(rig.Pane.TargetChooserOpen);
        // The write went at the local server, which is what the setting resolved: whatever that
        // process's own folder answered, nothing was written into the client's pending folder.
        Assert.False(Directory.Exists(Path.Combine(_root, "progenitor-pending")));
        // Revert: a target chooser inside the box, writing with a stale approval, or keeping an
        // approval across a target change, fails this.
    }

    [Fact]
    public async Task Page_change_or_pane_release_during_a_download_drops_the_result_and_cancels_the_token()
    {
        var catalog = new FlowCatalog();
        var a = Listing("dyno-A", "A");
        var b = Listing("dyno-B", "B");
        catalog.Entries.AddRange(new[] { a, b });
        catalog.Plans[a.Key] = RestartPlan(a, "ha");
        var rig = new Rig(this, catalog);
        rig.ClickCell(a.Key, InstallModsPane.CellClientA);
        var token = catalog.LastToken;
        var flow = rig.Pane.ActiveDownload!;
        Assert.False(token.IsCancellationRequested);
        // INSTALLSIDES1: opening B's drawer is not a page change any more (a drawer and the flow are
        // separate). The open box's No ends A's flow: its token is canceled and its late result dropped.
        rig.ClickRow(b.Key);
        Assert.False(token.IsCancellationRequested);
        rig.Pane.PromptNo();
        flow = rig.Pane.ActiveDownload ?? flow;
        Assert.True(token.IsCancellationRequested);
        Assert.Equal(InstallActionPhase.Idle, rig.Pane.Phase);
        catalog.Complete(new InspectCallResult(true, null, a, null));
        await flow;
        await rig.Settle();
        Assert.Equal(1, rig.Pane.DroppedCompletions);
        Assert.Equal(0, catalog.ExecuteCalls);
        Assert.Equal(SideCells.InstallCaption, rig.Cell(a.Key, InstallModsPane.CellClientA).Text);

        // Releasing the pane (ModsMenu.ReleaseTabWorkspace) cancels too and applies nothing.
        rig.ClickCell(a.Key, InstallModsPane.CellClientA);
        var second = catalog.LastToken;
        var flow2 = rig.Pane.ActiveDownload!;
        rig.Pane.CancelDownload();
        Assert.True(second.IsCancellationRequested);
        catalog.Complete(new InspectCallResult(true, null, a, null));
        await flow2;
        await rig.Settle();
        Assert.Equal(2, rig.Pane.DroppedCompletions);
        Assert.Equal(0, catalog.ExecuteCalls);
        Assert.Equal(InstallActionPhase.Idle, rig.Pane.Phase);
        // Revert: applying a completion for another page, or leaving A's token live, fails this.
    }

    /// <summary>
    /// The CI baseline's two red rows in this class (run 34794896168), made deterministic. The
    /// download finishes on a worker thread — where a real catalog's zip read and pending write
    /// leave it — at the moment the test thread is part-way through building the rows, which on a
    /// loaded runner is simply what "the next build" means.
    ///
    /// <para>What must hold is the pane's own rule: a completion is applied through the
    /// marshaler. The worker leaves the result in the queue and changes nothing; the pane is
    /// still <c>Downloading</c> when it returns, and one drain later there is one row for the
    /// listing and one Install cell behind it.</para>
    ///
    /// Mutation check (measured): passing <c>null</c> for the pane's marshaler — the rig as it
    /// was — fails this test at the phase assert (the worker applied the completion itself) and,
    /// with that assert removed, on the duplicate row: <c>Rows</c> then holds two entries for one
    /// key, which is the <c>Sequence contains more than one matching element</c> the baseline
    /// reported out of <see cref="Rig.Cell"/>.
    /// </summary>
    [Fact]
    public async Task A_completion_that_lands_mid_build_is_applied_on_the_marshaller()
    {
        var catalog = new FlowCatalog();
        var entry = Listing("dyno-A", "A");
        catalog.Entries.Add(entry);
        catalog.Plans[entry.Key] = RestartPlan(entry, "ha");

        var runtime = new BuildHookRuntime();
        UiApi.SetRuntimeForTests(runtime);
        var rig = new Rig(this, catalog);

        int armed = 0;
        Task? worker = null;
        InstallActionPhase phaseWhenTheWorkerReturned = InstallActionPhase.Idle;
        runtime.OnCellRow = () =>
        {
            // Once, on the first row of the build that BeginDownloadAndCheck asks for: the click
            // opens its box (and rebuilds) before the download starts, and there is nothing to
            // complete during those. Nothing here throws — RequestDetailRebuild swallows what a
            // rebuild raises, so this records and the test thread asserts.
            if (armed != 0 || rig.Pane.Phase != InstallActionPhase.Downloading) return;
            if (Interlocked.Exchange(ref armed, 1) != 0) return;
            worker = Task.Run(() => catalog.Complete(new InspectCallResult(true, null, entry, null)));
            worker.Wait(TimeSpan.FromSeconds(30));
            phaseWhenTheWorkerReturned = rig.Pane.Phase;
        };

        rig.ClickCell(entry.Key, InstallModsPane.CellClientA);
        Assert.Equal(1, armed);
        Assert.NotNull(worker);
        Assert.True(worker!.IsCompletedSuccessfully, "the download never completed on the worker");

        // The worker queued its result and touched nothing: the pane was still downloading when it
        // returned, so the build that was running owned the pane alone.
        Assert.Equal(InstallActionPhase.Downloading, phaseWhenTheWorkerReturned);

        await rig.Settle();
        Assert.Equal(InstallActionPhase.Inspected, rig.Pane.Phase);
        Assert.Equal(1, rig.Pane.Rows.Count(r => string.Equals(r.Row.Key, entry.Key, StringComparison.Ordinal)));
        // ...which is what Cell() and the row registry read: one row, one client Install cell.
        Assert.Equal(SideCells.InstallCaption, rig.Cell(entry.Key, InstallModsPane.CellClientA).Text);
        // Revert: giving the pane the inline marshaler (`null`) applies the completion on the
        // worker and leaves two rows for this one listing.
    }

    /// <summary>
    /// A cell runtime that lets a test act at a known point inside a build — the pane calls this
    /// once per row it creates. <see cref="CellRowTests.FakeCellRuntime"/> is sealed and counts
    /// rows for its own suite; this one only carries the hook.
    /// </summary>
    private sealed class BuildHookRuntime : CellRowTests.PlainRuntime
    {
        public Action? OnCellRow;

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
        {
            OnCellRow?.Invoke();
            return new CellRowTests.FakeCellRow(null, spec, header);
        }
    }

    [Fact]
    public void Inspection_never_executes_the_package_and_the_flow_runs_through_the_marshaller()
    {
        string service = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "InstallCatalogService.cs");
        string download = SourcePin.MethodBody(service, "public async Task<InspectCallResult> DownloadAndInspectAsync(");
        SourcePin.AssertUncommented(download, "PackageInspector.InspectFile(");
        SourcePin.AssertNoStatementContaining(download, "ExecuteToPending");
        SourcePin.AssertNoStatementContaining(download, "InstallStaging");
        SourcePin.AssertNoStatementContaining(download, "Assembly.Load");
        SourcePin.AssertNoStatementContaining(download, "ZipFile.ExtractToDirectory");
        string inspector = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "PackageInspector.cs");
        Assert.DoesNotContain("Assembly.Load", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadFrom(", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractToDirectory", inspector, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractToFile", inspector, StringComparison.Ordinal);

        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string flow = SourcePin.MethodBody(pane, "private async Task DownloadAndInspectFlowAsync(");
        SourcePin.AssertUncommented(flow, "_catalog.DownloadAndInspectAsync(key, ct)");
        SourcePin.AssertUncommented(flow, "_post(() => CompleteDownload(key, version, token, result))");
        string confirm = SourcePin.MethodBody(pane, "public ExecuteCallResult ConfirmExecute()");
        // INSTALL2 item 3: the write moved from the chooser chip to the confirmation's Yes, so the
        // pin follows it. The chip only chooses now (the test above drives both halves).
        SourcePin.AssertUncommented(
            SourcePin.MethodBody(pane, "private bool ConfirmInstall()"),
            "ConfirmExecute().Ok");
        SourcePin.AssertUncommented(confirm, "_catalog.ExecuteToPending(_approvedHash)");
        // HOT2: the pane names BepInEx/progenitor-reload/ in its review caption, so the old blanket
        // "the string never appears" pin would now fail for a caption rather than for a write. What
        // still has to hold is the thing that pin was protecting: the PANE does not write the reload
        // file. Confirm goes through the catalog, which routes to HotLoadExecutor only for a plan that
        // is ApplyNowEligible on this process's own root.
        SourcePin.AssertNoStatementContaining(pane, "HotLoadExecutor");
        SourcePin.AssertNoStatementContaining(pane, "InstallStaging");
        SourcePin.AssertNoStatementContaining(pane, "AtomicWrite");
        SourcePin.AssertNoStatementContaining(pane, "File.WriteAllBytes");
        Assert.True(InstallModsPane.CanHotLoadFromThisPane);
        // Revert: an inspection that extracts or loads the package, a reload-folder write from the
        // pane itself, or a completion applied off the marshaler fails these pins.
    }

    // --- rig ---

    /// <summary>
    /// The pane mounted the way ModsMenu mounts it: one list-view panel on the fake cell
    /// runtime, rebuilt inline on a phase change. Clicks dispatch through the row registry, the
    /// way the bridge does.
    ///
    /// <para><b>The rig marshals, because ModsMenu does.</b> The pane takes its marshaler as a
    /// constructor argument — <c>MainThread.Post</c> in the game — and applies every fetch result
    /// through it, so in the game a download or README completion touches the pane on the main
    /// thread and nowhere else. This rig used to pass <c>null</c>, which is the pane's
    /// "run inline" default, so a completion was applied on whatever thread the catalog's I/O
    /// continuation happened to land on. Where that thread reached the pane while the test thread
    /// was inside <see cref="InstallModsPane.Build"/>, the two races: the completion's rebuild
    /// resets the row list and refills it, the build in progress carries on appending to it, and
    /// the pane ends up holding two rows for one listing and two live handles for one Install
    /// cell. That is the CI baseline's
    /// <c>Sequence contains more than one matching element</c> out of <see cref="Cell"/>, and its
    /// <c>TryInvoke</c> that finds an install cell on a row that has already been installed — both
    /// green on a fast machine and red on a loaded two-core runner, from one build to the next.
    /// <see cref="Post"/> queues instead, and <see cref="Drain"/> applies the queue on the test
    /// thread at a point the test names. <see cref="Settle"/> is the usual one: await what is in
    /// flight, then drain what it posted.</para>
    /// </summary>
    private sealed class Rig
    {
        private readonly InstallDownloadFlowTests _owner;
        private readonly LocalServerFolder? _server;
        private readonly object _postGate = new();
        private readonly Queue<Action> _posted = new();

        public Rig(InstallDownloadFlowTests owner, IInstallCatalog catalog, LocalServerFolder? server = null)
        {
            _owner = owner;
            _server = server;
            Catalog = catalog;
            Pane = new InstallModsPane(catalog, new NeverClock(), Post);
            Pane.SetDetailRebuild(Rebuild);
            Rebuild();
        }

        public InstallModsPane Pane { get; }
        public IUiPanel Panel { get; private set; } = null!;
        public int Rebuilds { get; private set; }
        public IInstallCatalog Catalog { get; }

        /// <summary>The pane's marshaler: a completion is queued here, never applied on the thread it arrived on.</summary>
        private void Post(Action action)
        {
            lock (_postGate) _posted.Enqueue(action);
        }

        /// <summary>Apply everything the pane has posted, on this thread. Returns how many ran.</summary>
        public int Drain()
        {
            int applied = 0;
            while (true)
            {
                Action next;
                lock (_postGate)
                {
                    if (_posted.Count == 0) return applied;
                    next = _posted.Dequeue();
                }

                next();
                // A posted action may post (a rebuild asks for the drawer reveal on the next pass).
                // A queue that never empties is a defect, not something to spin on.
                if (++applied > 512) throw new Xunit.Sdk.XunitException("the pane's marshaler never drained");
            }
        }

        /// <summary>
        /// Await the download and the dependency-set plan in flight, then apply what they posted,
        /// until nothing is left running or queued. This is the test-side shape of the game's next
        /// frame: the work finishes on its own thread, the pane changes on the main one.
        /// </summary>
        public async Task Settle()
        {
            for (int pass = 0; pass < 16; pass++)
            {
                await Finish(Pane.ActiveDownload, "download");
                await Finish(Pane.ActiveSetPlan, "dependency-set plan");
                bool quiet = Drain() == 0;
                if (quiet
                    && (Pane.ActiveDownload?.IsCompleted ?? true)
                    && (Pane.ActiveSetPlan?.IsCompleted ?? true))
                    return;
            }

            throw new Xunit.Sdk.XunitException("the pane never settled: a completion kept posting more work");
        }

        /// <summary>
        /// Await one in-flight task, but fail rather than wait forever: every catalog in this class
        /// completes what it starts, so a task that does not is a defect to report, and a test that
        /// blocks on it would take the CI job's own timeout to say so.
        /// </summary>
        private static async Task Finish(Task? task, string what)
        {
            if (task == null) return;
            if (task != await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30))))
                throw new Xunit.Sdk.XunitException("the " + what + " never finished");
            await task;
        }

        public Dictionary<string, object?> Action =>
            Pane.ActionToWidget(Catalog.Search(null).First(e => string.Equals(e.Key, Pane.FlowKey, StringComparison.OrdinalIgnoreCase)));

        public CellSpec Cell(string key, string cell) =>
            Pane.Rows.SingleOrDefault(r => r.Row.Key == key).Row?.Cells.Single(c => c.Key == cell) ?? throw new Xunit.Sdk.XunitException("no row " + key + " among [" + string.Join(", ", Pane.Rows.Select(r => r.Row.Key)) + "] surface=" + Pane.Surface(true, false) + " status=" + Catalog.Status.Kind + " entries=" + Catalog.Search(null).Count);

        public void ClickCell(string rowKey, string cellKey) =>
            Assert.True(UiCellRowHandle.TryInvoke(rowKey + "/" + cellKey, out _), "no visible cell " + rowKey + "/" + cellKey);

        /// <summary>INSTALLSIDES1: open or close a row's drawer through its expand button.</summary>
        public void ClickRow(string key) =>
            Assert.True(UiCellRowHandle.TryInvoke(key + "/" + InstallModsPane.CellExpand, out _), "no expand button on " + key);

        private void Rebuild()
        {
            Rebuilds++;
            Panel?.Dispose();
            Panel = UiApi.Panel("Install mods", _owner._owner, "example.dl1");
            _owner._panels.Add(Panel);
            Pane.Build(Panel, true, false, _server, () => { }, () => { }, DateTime.UtcNow);
        }
    }

    /// <summary>The automatic README run parks after its first key: nothing lands from a background thread mid-test.</summary>
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
    private static InstallPlan RestartPlan(CatalogEntry entry, string hash) =>
        new(
            hash, entry, InstallTarget.Client, InstallDecision.RestartClientRequired,
            InstallReasons.RestartClient, Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(),
            entry.Manifest.Id, entry.Manifest.Id + ".dll");

    private static CatalogEntry Listing(string key, string display, string version = "1.0.0")
    {
        int dash = key.IndexOf('-');
        string ns = dash > 0 ? key.Substring(0, dash) : "n";
        return new CatalogEntry(
            new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, ns, key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: display,
            author: ns,
            owner: ns,
            packageName: key,
            downloadUrl: "https://thunderstore.io/package/download/" + ns + "/" + key + "/" + version + "/");
    }

    private static CatalogEntry Verified(string id, string display) =>
        new(
            new ManifestMod(id, "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "n", id, "aa", 1),
            CatalogProvenance.ManifestVerified,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: display,
            side: PackageSide.Client);

    /// <summary>A real dedicated-server layout under the test root so Revalidate/CanWrite pass.</summary>
    private LocalServerFolder ValidatedFolder()
    {
        string path = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(path, LocalServerFolder.BepInExFolderName));
        File.WriteAllBytes(Path.Combine(path, LocalServerFolder.ServerExecutableName), new byte[] { 0 });
        var folder = new LocalServerFolder();
        var applied = folder.Apply(path);
        Assert.True(applied.Ok, applied.Refusal);
        Assert.True(folder.HasValidatedServer);
        return folder;
    }

    private static byte[] DllBytes(string tag) => Encoding.UTF8.GetBytes("fake-dll:" + tag + ":" + new string('X', 64));

    private static byte[] ZipBytes(params (string Name, byte[] Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var s = entry.Open();
                s.Write(body, 0, body.Length);
            }
        }

        return ms.ToArray();
    }

    private static string SingleListingJson(string owner, string name, string version)
    {
        string url = "https://thunderstore.io/package/download/" + owner + "/" + name + "/" + version + "/";
        return "[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner
            + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + owner + "/" + name
            + "/\",\"is_deprecated\":false,\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\""
            + owner + "-" + name + "-" + version + "\",\"description\":\"d\",\"version_number\":\""
            + version + "\",\"dependencies\":[],\"download_url\":\"" + url + "\",\"file_size\":10,\"is_active\":true}]}]";
    }

    private sealed class FakeTransport : ICatalogTransport
    {
        private readonly Func<CatalogTransportRequest, CatalogTransportResponse> _handler;
        public int Calls;
        /// <summary>Requests for the package zip; the automatic README run also goes through this transport.</summary>
        public int DownloadCalls;

        public FakeTransport(Func<CatalogTransportRequest, CatalogTransportResponse> handler) => _handler = handler;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.Uri.AbsolutePath.Contains("/package/download/", StringComparison.Ordinal)) DownloadCalls++;
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 12, 9, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A catalog whose download completes when the test says so; plans hash per target.</summary>
    private sealed class FlowCatalog : IInstallCatalog
    {
        private TaskCompletionSource<InspectCallResult>? _pending;

        public List<CatalogEntry> Entries { get; } = new();
        public Dictionary<string, InstallPlan> Plans { get; } = new(StringComparer.OrdinalIgnoreCase);
        public InstallProgress? StatusOverride { get; set; }
        public ExecuteCallResult? ExecuteResult { get; set; }
        public int DownloadCalls { get; private set; }
        public int ApproveCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public string? LastDownloadKey { get; private set; }
        public string? LastExecutedHash { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public InstallProgress Status => StatusOverride ?? InstallProgress.Idle;

        public void Complete(InspectCallResult result)
        {
            var tcs = _pending;
            _pending = null;
            Assert.NotNull(tcs);
            tcs!.SetResult(result);
        }

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

        public Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            LastDownloadKey = key;
            LastToken = cancellationToken;
            _pending = new TaskCompletionSource<InspectCallResult>();
            return _pending.Task;
        }

        public PlanCallResult Plan(string key, InstallTarget target)
        {
            if (!Plans.TryGetValue(key, out var plan))
                return new PlanCallResult(false, InstallReasons.NotInspected, null);
            var forTarget = new InstallPlan(
                plan.PlanHash + "-" + InstallModsPane.TargetName(target),
                plan.Entry, target, plan.Decision, plan.Reason, plan.Artifacts, plan.Dependencies, plan.PluginId, plan.PluginFileName);
            return new PlanCallResult(true, null, forTarget);
        }

        public ApproveCallResult Approve(string planHash)
        {
            ApproveCalls++;
            return new ApproveCallResult(true, null, planHash, false);
        }

        public ExecuteCallResult ExecuteToPending(string planHash)
        {
            ExecuteCalls++;
            LastExecutedHash = planHash;
            return ExecuteResult ?? new ExecuteCallResult(true, null, "pending/" + planHash, false);
        }

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }

}
