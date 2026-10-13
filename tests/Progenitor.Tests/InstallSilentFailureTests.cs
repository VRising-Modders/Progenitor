using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// INSTALLFAIL1 (live 2026-10-03, Steam client): the Client cell's Install of <c>Progenitor-HotDemo
/// 0.1.0</c> was confirmed and the row still read <c>Missing: the server has it</c> with Install,
/// with no log line after the dialog closed, while the bar said <c>1 mod installed — restart the
/// game to apply</c> and <c>staging/index/tmp</c> collected a finished <c>.part</c> zip per click.
///
/// <para>Replayed game-free with the real package (<c>Fixtures/Progenitor-HotDemo-0.1.0.zip</c>, the
/// bytes the live client downloaded) from a local index exactly like the live one, the client's
/// empty install ledger, and the test server's ledgered copy of the plugin. No admission pipeline:
/// the plan is the restart route the live write took (the pending file was on disk).</para>
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class InstallSilentFailureTests : IDisposable
{
    private const string Key = "Progenitor-HotDemo";
    private const string PluginId = "progenitor.hotdemo";
    private const string PluginFile = PluginId + ".dll";
    private const string DllSha = "c883832d8b028186fb37ec70e09c14f2b1e227df11f9b83dbe75daff51eaf1bb";

    private readonly string _base = Path.Combine(Path.GetTempPath(), "progenitor-installfail-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Line, bool Warn)> _log = new();

    public InstallSilentFailureTests()
    {
        UiApi.ResetForTests();
        UiDialog.ForgetForTests();
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(ClientBep);
        Directory.CreateDirectory(ServerBep);
        File.Copy(FixtureZip, Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip"));
        File.WriteAllText(Path.Combine(Local, LocalIndexSource.IndexFileName), IndexJson());
        InstallLog.TestSink = (line, warn) => { lock (_log) _log.Add((line, warn)); };
    }

    public void Dispose()
    {
        InstallLog.TestSink = null;
        UiDialog.Current?.Close();
        UiDialog.ForgetForTests();
        UiApi.ResetForTests();
        try { Directory.Delete(_base, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FixtureZip => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Progenitor-HotDemo-0.1.0.zip");
    private string Local => Path.Combine(_base, "dummy");
    private string ClientGame => Path.Combine(_base, "VRising");
    private string ClientBep => Path.Combine(ClientGame, "BepInEx");
    private string ServerGame => Path.Combine(_base, "server");
    private string ServerBep => Path.Combine(ServerGame, "BepInEx");
    private string PendingFile => Path.Combine(ClientBep, ModManifestPending.PendingFolderName, "index", PluginFile);
    private string TempDir => CatalogFetcher.TempDirectory(ClientBep);

    /// <summary>The live <c>_testserver/dummy/index.json</c>, pointing at this test's copy of the zip.</summary>
    private string IndexJson()
    {
        string url = new Uri(Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip")).AbsoluteUri;
        return "[{\"name\":\"HotDemo\",\"full_name\":\"Progenitor-HotDemo\",\"owner\":\"Progenitor\",\"package_url\":\"\","
            + "\"date_created\":\"2026-09-13T02:14:35Z\",\"date_updated\":\"2026-09-13T02:14:35Z\",\"is_deprecated\":false,"
            + "\"categories\":[\"Client\",\"Server\",\"Oakveil Update\",\"Mods\"],"
            + "\"versions\":[{\"name\":\"HotDemo\",\"full_name\":\"Progenitor-HotDemo-0.1.0\","
            + "\"description\":\"Dummy Progenitor package for the Install tab hot-load lane.\",\"version_number\":\"0.1.0\","
            + "\"dependencies\":[],\"download_url\":\"" + url + "\",\"downloads\":0,\"date_created\":\"2026-09-13T02:14:35Z\","
            + "\"is_active\":true,\"file_size\":4607}]}]";
    }

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    /// <summary>The live disk: the client ledger exists and is empty; the test server has the plugin ledgered and promoted.</summary>
    private void LiveDisk()
    {
        new ServerInstallLedger("", "", "", new DateTime(2026, 9, 12, 22, 49, 52, DateTimeKind.Utc)).Write(ClientGame);
        string serverCopy = Path.Combine(ServerBep, ModFolders.ReloadFolderName, PluginFile);
        Directory.CreateDirectory(Path.GetDirectoryName(serverCopy)!);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(FixtureZip))
        using (var dll = zip.GetEntry("Progenitor.HotDemo.dll")!.Open())
        using (var file = File.Create(serverCopy))
            dll.CopyTo(file);
        new ServerInstallLedger("", "", "", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/" + PluginFile, DllSha, 8192, ServerInstallLedger.CategoryMod,
                "package:" + Key + "@0.1.0", new DateTime(2026, 9, 29, 7, 18, 50, DateTimeKind.Utc), pluginId: PluginId),
        }).Write(ServerGame);
    }

    private InstallCatalogService Catalog(bool withServer = true, Progenitor.Install.Admission.IAdmissionEvaluator? admission = null)
    {
        var catalog = new InstallCatalogService(
            ClientBep, new NoTransport(), null, null, null, withServer, admission, withServer ? ServerBep : null, Local, false);
        Assert.True(catalog.LoadIndexFromJson("[]", "stamp").Ok);
        Assert.True(catalog.TryGetEntry(Key, out _), "the local index did not merge HotDemo");
        return catalog;
    }

    private InstalledStateQuery Query(InstallCatalogService catalog) =>
        InstalledState.Scan(ClientBep, ServerBep, null, null, catalog.IdentityProbe);

    private string[] Parts() => Directory.Exists(TempDir) ? Directory.GetFiles(TempDir, "*.part") : Array.Empty<string>();

    private static IReadOnlyList<ServerInstallLedgerEntry> Rows(string game) =>
        ServerInstallLedger.TryRead(game)?.Files ?? Array.Empty<ServerInstallLedgerEntry>();

    // ---- 1. the write is ledgered, so the row and the bar agree --------------------------------

    /// <summary>
    /// Root cause. <c>InstallStaging.AppendServerLedger</c> returned early for a progenitor.json
    /// package on the <c>Client</c> target, so the restart write landed in
    /// <c>progenitor-pending/index/</c> with no ledger row. The per-side Client cell reads its half from
    /// the ledger; its unledgered fallback only applies when the server half is also empty, and here
    /// the test server holds HotDemo — so the cell read <c>Missing: the server has it</c> while
    /// <c>PendingCount</c> (which scans the pending file) counted it for the bar.
    /// What this would catch: dropping the client ledger row again, for either an existing or an
    /// absent client ledger.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_client_restart_install_is_ledgered_and_the_client_cell_reads_pending(bool clientLedgerExists)
    {
        LiveDisk();
        if (!clientLedgerExists) File.Delete(ServerInstallLedger.PathFor(ClientGame));
        var catalog = Catalog();

        var inspected = await catalog.DownloadAndInspectAsync(Key, CancellationToken.None);
        Assert.True(inspected.Ok, inspected.Error);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);
        Assert.Equal(InstallDecision.RestartClientRequired, set.Root!.Decision);

        var written = catalog.ExecuteWithDependencies(set);
        Assert.True(written.Ok, written.Error);
        Assert.True(File.Exists(PendingFile));
        Assert.Equal(DllSha, CatalogFetcher.HashFile(PendingFile));

        var row = Assert.Single(Rows(ClientGame));
        Assert.Equal("BepInEx/progenitor-pending/index/" + PluginFile, row.Path);
        Assert.Equal(ServerInstallLedger.CategoryPending, row.Category);
        Assert.Equal("package:" + Key + "@0.1.0", row.Source);
        Assert.Equal(PluginId, row.PluginId);

        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
        var halves = InstalledState.HalvesOf(entry!, Query(catalog), false);
        Assert.Equal(InstalledState.HalfPending, halves.Client);

        // The pane the player looks at: the Client cell and the bar now say the same thing.
        // The whole listing reads Loaded (the local server promoted its copy); this client's own copy is pending.
        Assert.Equal(InstalledKind.Loaded, InstalledState.Detect(entry!, Query(catalog), Array.Empty<string>()).State);
        Assert.Equal(InstalledKind.Pending,
            InstalledState.Detect(entry!, Query(catalog).ForRoot(InstalledState.ClientRoot), Array.Empty<string>()).State);
        var pane = new InstallModsPane(catalog);
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.SetInstalledQuery(Query(catalog));
        Assert.Equal(HalfState.Pending, pane.ClientHalfOf(entry!).State);
        Assert.Equal(SideCells.KindPending, pane.SideCellsFor(entry!).Client.Kind);
        Assert.Equal(1, pane.PendingCount());

        Assert.Contains(_log, l => !l.Warn && l.Line.Contains("Install: wrote " + Key + " 0.1.0") && l.Line.Contains("next restart"));
        // Revert: restoring `if (plan.Target == InstallTarget.Client) return;` leaves the ledger empty
        // and the Client cell `missing` beside a bar that counts one pending install.
    }

    /// <summary>
    /// The live client already holds an unledgered pending copy from the failed sessions. Pressing
    /// Install again finds the same bytes in <c>progenitor-pending</c> (the idempotent branch) and
    /// now ledgers them, so the row recovers without a restart or a manual delete.
    /// </summary>
    [Fact]
    public async Task Installing_again_over_an_unledgered_pending_copy_adopts_it()
    {
        LiveDisk();
        Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(FixtureZip))
        using (var dll = zip.GetEntry("Progenitor.HotDemo.dll")!.Open())
        using (var file = File.Create(PendingFile))
            dll.CopyTo(file);
        var catalog = Catalog();

        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        var written = catalog.ExecuteWithDependencies(set);
        Assert.True(written.Ok, written.Error);
        Assert.Equal(ServerInstallLedger.CategoryPending, Assert.Single(Rows(ClientGame)).Category);
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
        Assert.Equal(InstalledState.HalfPending, InstalledState.HalvesOf(entry!, Query(catalog), false).Client);
    }

    // ---- 2. no finished download is left behind ------------------------------------------------

    /// <summary>
    /// Every click downloaded a fresh <c>HotDemo-0.1.0.zip.&lt;guid&gt;.part</c> and nothing ever
    /// deleted it (43 live). A written set releases the zips it used; a second download of the same
    /// listing replaces the first. (Downloads an ended process left are the StagingTempSweep job.)
    /// </summary>
    [Fact]
    public async Task Downloads_are_released_after_the_write_and_replaced_on_a_second_click()
    {
        LiveDisk();
        var catalog = Catalog();

        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        Assert.Single(Parts());
        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        Assert.Single(Parts());

        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.True(catalog.ExecuteWithDependencies(set).Ok);
        Assert.Empty(Parts());
        Assert.Null(catalog.ZipPathOf(Key));
    }

    // ---- 2b. the verified side survives a Core swap ----------------------------------------------

    /// <summary>
    /// Live (2026-10-03): on Core generation 7 HotDemo's Client cell read <c>Missing: the server has
    /// it</c> (manifest-verified <c>side: both</c>); after the hot swap to generation 8 the same row read
    /// plain <c>Not installed</c>, because the inspection that proved <c>side: both</c> lived only in the
    /// old catalog's memory. A new catalog over the same root (what a swap or a restart builds)
    /// rediscovers it from <c>progenitor-cache/verified</c> with no download.
    /// </summary>
    [Fact]
    public async Task A_new_catalog_rediscovers_the_verified_side_without_a_download()
    {
        LiveDisk();
        var first = Catalog();
        Assert.True((await first.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        Assert.True(first.TryGetEntry(Key, out var inspected) && inspected != null);
        Assert.True(InstallModsPane.NeedsBoth(inspected!));
        Assert.True(File.Exists(new VerifiedManifestCache(ClientBep).PathFor("Progenitor", "HotDemo", "0.1.0")));

        // The swap: a fresh catalog on the same disk, nothing inspected, the zip never fetched again.
        File.Delete(Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip"));
        var second = Catalog();
        Assert.True(second.TryGetEntry(Key, out var rested) && rested != null);
        Assert.Equal(CatalogProvenance.ManifestVerified, rested!.Provenance);
        Assert.Equal(PackageSide.Both, rested.Side);
        Assert.Equal(PluginId, rested.Manifest.Id);
        Assert.True(InstallModsPane.NeedsBoth(rested));
        Assert.Null(second.ZipPathOf(Key));

        // A different version of the same package is not vouched for by this record.
        var other = new VerifiedManifestCache(ClientBep).ReadAll();
        Assert.False(other.ContainsKey(VerifiedManifestCache.Key("Progenitor", "HotDemo", "0.2.0")));
        // Revert: without the cache the second catalog's row is AdaptedListing and NeedsBoth is false
        // (the live `Not installed` after the swap).
    }

    // ---- 3. a confirmed install that fails says so, on the row and in the log -------------------

    /// <summary>
    /// A Yes whose write is refused (here the downloaded zip vanished between the plan and the
    /// write, <see cref="InstallReasons.ChangedArtifact"/>) used to leave a both-sided row exactly as
    /// it was: the per-side cells only showed planner refusals, and the row's side sentence replaced
    /// the failure reason. Now the Client cell reads <c>Install failed</c> with Install to retry, the
    /// row carries the reason, and the log names it.
    /// </summary>
    [Fact]
    public async Task A_failed_confirmed_install_surfaces_on_the_row_and_in_the_log()
    {
        LiveDisk();
        var catalog = Catalog();
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
        var pane = new InstallModsPane(catalog);
        pane.SetInstalledQuery(Query(catalog));

        Assert.Contains(SideButton.Install, pane.SideCellsFor(entry!).Client.Buttons);
        Assert.True(pane.OnSideClick(Key, server: false, SideButton.Install));
        await pane.ActiveDownload!;
        await pane.ActiveSetPlan!;
        Assert.Equal(InstallPromptKind.Install, pane.Prompt);
        Assert.Null(pane.PromptRefusal);

        string zip = catalog.ZipPathOf(Key)!;
        File.Delete(zip);
        Assert.False(pane.PromptYes());

        var result = pane.LastExecuteSet!;
        Assert.False(result.Ok);
        Assert.Contains(InstallReasons.ChangedArtifact, result.Error);
        Assert.False(File.Exists(PendingFile));
        Assert.True(catalog.TryGetEntry(Key, out entry) && entry != null);

        var client = pane.ClientHalfOf(entry!);
        Assert.Equal(InstallStatusLabels.InstallFailed, client.Refused);
        Assert.True(client.RefusedRetry);
        var cell = pane.SideCellsFor(entry!).Client;
        Assert.Equal(SideCells.KindRefused, cell.Kind);
        Assert.Contains(SideButton.Install, cell.Buttons);
        Assert.Contains(InstallReasons.ChangedArtifact, pane.ActionError);
        // The drawer under the row carries the sentence behind the cell's two words.
        var drawer = pane.DrawerLines(entry!, pane.ActionFor(entry!), out _);
        Assert.Contains(drawer, l => l.Kind == CellLineKind.Warn && l.Text.Contains(InstallReasons.ChangedArtifact));

        Assert.Contains(_log, l => l.Warn && l.Line.Contains(Key + " was not installed") && l.Line.Contains(InstallReasons.ChangedArtifact));
        Assert.Empty(Parts());
        // Revert: without the Failed phase in the side cells the Client cell reads `missing` with
        // Install again, exactly the live screenshot, and nothing in the log says why.
    }

    // ---- 4. HOTDEMO1: the hot path, and why it was not taken -----------------------------------

    private sealed class FixedSnapshots : Progenitor.Install.Admission.IAdmissionSnapshotSource
    {
        public Progenitor.Install.Admission.RuntimeGenerationSnapshot Snapshot { get; set; } =
            Progenitor.Install.Admission.RuntimeGenerationSnapshot.Unavailable();

        public Progenitor.Install.Admission.RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) => Snapshot;
    }

    private static IReadOnlyList<Progenitor.Install.Admission.CompatibilityIdentity> Ids(string core) => new[]
    {
        new Progenitor.Install.Admission.CompatibilityIdentity(Progenitor.Install.Admission.CompatibilityKeys.GameBuild, "client-247473480"),
        new Progenitor.Install.Admission.CompatibilityIdentity(Progenitor.Install.Admission.CompatibilityKeys.BootstrapContract, "protocol/4+host/4"),
        new Progenitor.Install.Admission.CompatibilityIdentity(Progenitor.Install.Admission.CompatibilityKeys.CoreContract, core),
    };

    /// <summary>The live client's record, verbatim but for the build key: reviewed against <paramref name="core"/>.</summary>
    private static string HotDemoRecord(string core) =>
        "{\"schema\":1,\"records\":[{\"id\":\"progenitor.hotdemo/initialinstall/client\",\"pluginId\":\"" + PluginId
        + "\",\"artifactSha256\":\"" + DllSha + "\",\"artifactSize\":8192,\"dependencyClosure\":{},"
        + "\"gameBuild\":\"client-247473480\",\"bootstrapContract\":\"protocol/4+host/4\",\"coreContract\":\"" + core + "\","
        + "\"side\":\"both\",\"operation\":\"InitialInstall\",\"target\":\"Client\",\"qualifiedUtc\":\"2026-09-13T02:16:36Z\","
        + "\"evidence\":\"HOT2 acceptance 2026-09-12\"}]}";

    private Progenitor.Install.Admission.AdmissionPipeline Admission(string recordCore, string runningCore)
    {
        var ids = Ids(runningCore);
        var snapshots = new FixedSnapshots
        {
            Snapshot = new Progenitor.Install.Admission.RuntimeGenerationSnapshot(
                true, null, Progenitor.Install.Admission.AdmissionSide.Client, 1, false, true, false, null, ids),
        };
        return new Progenitor.Install.Admission.AdmissionPipeline(
            snapshots, Progenitor.Install.Admission.QualificationPolicy.Parse(HotDemoRecord(recordCore)), ids);
    }

    /// <summary>
    /// HOTDEMO1 root cause (live 2026-10-03, Core 2026.10.28.7): the client's only HotDemo record
    /// was reviewed on 2026-09-13 against <c>core/0.1.0+admission/1</c>. <c>coreContract</c> is Core's
    /// build version, so every Core build since stopped matching it, the admission was
    /// <c>QualificationMismatch</c>, and the plan fell back to the restart route. The user's policy
    /// (2026-10-03, "trust the hotLoadable flag") makes that exact live state the hot path: the
    /// expired record is still named, but HotDemo installs into the reload folder now.
    /// </summary>
    [Fact]
    public async Task With_only_an_expired_record_HotDemo_installs_hot_on_its_flag()
    {
        LiveDisk();
        var catalog = Catalog(admission: Admission("core/0.1.0+admission/1", "core/2026.10.28.7+admission/1"));
        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.True(set.Ok, set.Error);
        Assert.Equal(InstallDecision.ApplyNow, set.Root!.Decision);
        Assert.True(set.Root.ExecutorAvailable);
        Assert.Equal(Progenitor.Install.Admission.AdmissionReasonCode.TrustedHotLoadable, set.Root.Admission!.Reasons[0].Code);
        Assert.True(set.Root.Admission.Has(Progenitor.Install.Admission.AdmissionReasonCode.QualificationMismatch));

        var written = catalog.ExecuteWithDependencies(set);
        Assert.True(written.Ok, written.Error);
        Assert.True(File.Exists(Path.Combine(ClientBep, ModFolders.ReloadFolderName, PluginFile)));
        Assert.Contains(_log, l => l.Line.Contains("Install: wrote " + Key + " 0.1.0") && l.Line.Contains("loaded now"));
    }

    private void RewriteProgenitorJson(string json)
    {
        string zip = Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip");
        File.Delete(zip);
        using var src = System.IO.Compression.ZipFile.OpenRead(FixtureZip);
        using var dst = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var e in src.Entries)
        {
            var copy = dst.CreateEntry(e.FullName);
            using var o = copy.Open();
            if (e.FullName == "progenitor.json")
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                o.Write(bytes, 0, bytes.Length);
            }
            else
            {
                using var i = e.Open();
                i.CopyTo(o);
            }
        }
    }

    /// <summary>A package that opts out (<c>hotLoadable: false</c>) keeps the restart route, record or not.</summary>
    [Fact]
    public async Task A_package_that_opts_out_of_hot_load_keeps_the_restart_route()
    {
        RewriteProgenitorJson("{\"id\":\"" + PluginId + "\",\"version\":\"0.1.0\",\"side\":\"both\",\"hotLoadable\":false,\"sha256\":\"" + DllSha + "\"}");
        LiveDisk();
        string core = "core/2026.10.28.7+admission/1";
        var catalog = Catalog(admission: Admission(core, core));
        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.Equal(InstallDecision.RestartClientRequired, set.Root!.Decision);
        Assert.True(catalog.ExecuteWithDependencies(set).Ok);
        Assert.True(File.Exists(PendingFile));
        Assert.False(File.Exists(Path.Combine(ClientBep, ModFolders.ReloadFolderName, PluginFile)));
    }

    /// <summary>
    /// HOTDEMO1 server route: a running Progenitor server in the local folder (probe faked) takes its
    /// half into its own <c>progenitor-reload/</c>, ledgered as a <c>mod</c> without the client's
    /// <c>hot</c> flag; its later Uninstall unloads it (file and stale marker gone) instead of marking.
    /// A server that is not running keeps the pending file and the marker.
    /// </summary>
    [Fact]
    public async Task A_watched_local_server_takes_its_half_hot_and_gives_it_back_hot()
    {
        string core = "core/2026.10.28.7+admission/1";
        HotLoadTrust.LocalServerProbe = _ => new HotLoadTrust.LocalServerRuntime(true, true, false);
        try
        {
            var catalog = Catalog(admission: Admission(core, core));
            Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
            var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Both, CancellationToken.None);
            Assert.Equal(InstallDecision.ApplyNow, set.Root!.Decision);
            var serverHalf = set.Root.HalfFor(InstallTarget.LocalServer)!;
            Assert.Equal(InstallDecision.ApplyNow, serverHalf.Decision);
            Assert.True(serverHalf.ExecutorAvailable, serverHalf.Reason);

            var written = catalog.ExecuteWithDependencies(set);
            Assert.True(written.Ok, written.Error);
            string serverCopy = Path.Combine(ServerBep, ModFolders.ReloadFolderName, PluginFile);
            Assert.True(File.Exists(serverCopy));
            Assert.False(File.Exists(Path.Combine(ServerBep, ModManifestPending.PendingFolderName, "index", PluginFile)));
            var row = Assert.Single(Rows(ServerGame));
            Assert.Equal(ServerInstallLedger.CategoryMod, row.Category);
            Assert.False(row.Hot);
            Assert.Contains(_log, l => l.Line.Contains("into the local server's reload folder"));

            Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
            File.WriteAllText(UninstallMarkers.MarkerFor(serverCopy), "stale");
            Assert.True(catalog.ServerUnloadsNow(entry!, out string why), why);
            var removed = catalog.RemovePackage(Key, false, InstallTarget.LocalServer);
            Assert.True(removed.Ok && !removed.NoOp, removed.Message);
            Assert.True(removed.HotUnloaded);
            Assert.False(File.Exists(serverCopy));
            Assert.False(File.Exists(UninstallMarkers.MarkerFor(serverCopy)), "the spent marker survived");

            // Not running: the server half is the restart route and its copy is not unloadable from here.
            HotLoadTrust.LocalServerProbe = _ => new HotLoadTrust.LocalServerRuntime(false, true, false);
            LiveDisk();
            Assert.False(catalog.ServerUnloadsNow(entry!, out why));
            Assert.Contains("not running", why);
            var again = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.LocalServer, CancellationToken.None);
            Assert.False(again.Root!.ExecutorAvailable);
        }
        finally
        {
            HotLoadTrust.LocalServerProbe = null;
        }
    }

    /// <summary>The drawer note names the hot path on the local-folder route only when that server is watched.</summary>
    [Fact]
    public void The_restart_note_says_loads_now_for_a_watched_local_server()
    {
        string hot = SideCells.RestartNote(PackageSide.Both, ServerTargetKind.Server, ServerRouteKind.Local, true, null, false, localServerHot: true);
        string cold = SideCells.RestartNote(PackageSide.Both, ServerTargetKind.Server, ServerRouteKind.Local, true, null, false, localServerHot: false);
        Assert.Contains(SideCells.ServerLabel + ": " + SideCells.LoadsNow, hot);
        Assert.Contains(SideCells.ClientLabel + ": " + SideCells.LoadsNow, hot);
        Assert.Contains(SideCells.ServerLabel + ": " + SideCells.LoadsAtServerRestart, cold);
    }

    /// <summary>The at-rest unload trust: the verified hotLoadable bytes of a clean ProgenitorPlugin, and nothing else.</summary>
    [Fact]
    public void Unload_trust_needs_the_verified_hotLoadable_bytes_of_a_clean_Progenitor_plugin()
    {
        string dll = Path.Combine(_base, "copy.dll");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(FixtureZip))
        using (var s = zip.GetEntry("Progenitor.HotDemo.dll")!.Open())
        using (var f = File.Create(dll))
            s.CopyTo(f);
        Progenitor.Networking.ManifestMod Manifest(bool hot, string sha) => new(
            PluginId, "0.1.0", Progenitor.Plugins.NetworkCompatibilityLevel.NoNeedForSync, Progenitor.Plugins.NetworkVersionRule.None, hot, sha256: sha);

        Assert.True(HotLoadTrust.CanUnloadHot(new VerifiedManifestCache.Record(Manifest(true, DllSha), PackageSide.Both), dll, out string why), why);
        Assert.False(HotLoadTrust.CanUnloadHot(new VerifiedManifestCache.Record(Manifest(false, DllSha), PackageSide.Both), dll, out _));
        Assert.False(HotLoadTrust.CanUnloadHot(new VerifiedManifestCache.Record(Manifest(true, new string('b', 64)), PackageSide.Both), dll, out _));
        Assert.False(HotLoadTrust.CanUnloadHot(null, dll, out _));

        // Review 2026-10-07: the verdict is kept per file, size and write time; changed bytes are judged again.
        Assert.True(HotLoadTrust.CanUnloadHot(new VerifiedManifestCache.Record(Manifest(true, DllSha), PackageSide.Both), dll, out _));
        File.AppendAllText(dll, "x");
        File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddMinutes(1));
        Assert.False(HotLoadTrust.CanUnloadHot(new VerifiedManifestCache.Record(Manifest(true, DllSha), PackageSide.Both), dll, out string changed));
        Assert.Contains("not the ones progenitor.json declared", changed, StringComparison.Ordinal);
    }

    /// <summary>A restart plan still says why in the plan line (here: no admission evaluator at all, the eligibility stub).</summary>
    [Fact]
    public async Task A_restart_plan_names_its_reason_in_the_log()
    {
        LiveDisk();
        var catalog = Catalog();
        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.Equal(InstallDecision.RestartClientRequired, set.Root!.Decision);
        Assert.Contains(_log, l => l.Line.StartsWith("Install: planned " + Key) && l.Line.Contains("RestartClientRequired (1 package(s)): "));
    }

    /// <summary>
    /// A hash mismatch is still refused before any trust: a progenitor.json whose sha256 is not the
    /// DLL in the zip fails inspection, nothing is planned and nothing is written.
    /// </summary>
    [Fact]
    public async Task A_package_whose_declared_hash_does_not_match_is_refused()
    {
        string bad = Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip");
        File.Delete(bad);
        using (var src = System.IO.Compression.ZipFile.OpenRead(FixtureZip))
        using (var dst = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries)
            {
                var copy = dst.CreateEntry(e.FullName);
                using var o = copy.Open();
                if (e.FullName == "progenitor.json")
                {
                    var json = System.Text.Encoding.UTF8.GetBytes(
                        "{\"id\":\"" + PluginId + "\",\"version\":\"0.1.0\",\"side\":\"both\",\"hotLoadable\":true,\"sha256\":\"" + new string('a', 64) + "\"}");
                    o.Write(json, 0, json.Length);
                }
                else
                {
                    using var i = e.Open();
                    i.CopyTo(o);
                }
            }
        }

        LiveDisk();
        string core = "core/2026.10.28.7+admission/1";
        var catalog = Catalog(admission: Admission(core, core));
        var inspected = await catalog.DownloadAndInspectAsync(Key, CancellationToken.None);
        Assert.False(inspected.Ok);
        Assert.False(File.Exists(Path.Combine(ClientBep, ModFolders.ReloadFolderName, PluginFile)));
        Assert.False(File.Exists(PendingFile));
    }

    /// <summary>
    /// With a record that matches the running identities, the same package on the same disk is the
    /// hot path end to end: ApplyNow, written to <c>progenitor-reload/</c>, ledgered as a hot <c>mod</c>,
    /// the log saying it loads now, and the Client cell reading Installed with no restart.
    /// </summary>
    [Fact]
    public async Task A_matching_record_installs_HotDemo_hot_into_the_reload_folder()
    {
        LiveDisk();
        string core = "core/2026.10.28.7+admission/1";
        var catalog = Catalog(admission: Admission(core, core));
        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.Equal(InstallDecision.ApplyNow, set.Root!.Decision);
        Assert.True(set.Root.ExecutorAvailable, set.Root.ExecutorReason);

        var written = catalog.ExecuteWithDependencies(set);
        Assert.True(written.Ok, written.Error);
        Assert.True(Assert.Single(written.Members).HotLoaded);
        string reload = Path.Combine(ClientBep, ModFolders.ReloadFolderName, PluginFile);
        Assert.True(File.Exists(reload));
        Assert.False(File.Exists(PendingFile));
        var row = Assert.Single(Rows(ClientGame));
        Assert.Equal(ServerInstallLedger.CategoryMod, row.Category);
        Assert.Contains(_log, l => l.Line.Contains("Install: wrote " + Key + " 0.1.0") && l.Line.Contains("loaded now"));
    }

    /// <summary>
    /// HOTDEMO1 (live): Uninstall of a client copy that was only pending — never loaded — read
    /// <c>Removal pending game restart</c>. The file was deleted at once (correctly), but the pane
    /// asked the listing-wide state whether "this process still holds it", and the local server's
    /// promoted copy answered Loaded. The Client cell is now back to absent immediately.
    /// </summary>
    [Fact]
    public async Task Removing_a_client_copy_that_never_loaded_owes_no_restart()
    {
        LiveDisk();
        var catalog = Catalog();
        Assert.True((await catalog.DownloadAndInspectAsync(Key, CancellationToken.None)).Ok);
        var set = await catalog.PlanWithDependenciesAsync(Key, InstallTarget.Client, CancellationToken.None);
        Assert.True(catalog.ExecuteWithDependencies(set).Ok);
        Assert.True(File.Exists(PendingFile));
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);

        var pane = new InstallModsPane(catalog);
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.InstalledQueryRescan = () => Query(catalog);
        pane.SetInstalledQuery(Query(catalog));
        Assert.Equal(HalfState.Pending, pane.ClientHalfOf(entry!).State);

        Assert.True(pane.OnRemoveClick(Key, force: false, InstallTarget.Client));
        pane.SetInstalledQuery(Query(catalog));
        Assert.False(File.Exists(PendingFile));
        Assert.Empty(Rows(ClientGame));
        Assert.Equal(HalfState.Absent, pane.ClientHalfOf(entry!).State);
        Assert.NotEqual(SideCells.KindRemovalPending, pane.SideCellsFor(entry!).Client.Kind);
        // The server's promoted copy is untouched by a client-scoped removal.
        Assert.True(File.Exists(Path.Combine(ServerBep, ModFolders.ReloadFolderName, PluginFile)));
    }
}
