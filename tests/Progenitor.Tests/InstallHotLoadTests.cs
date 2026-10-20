using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job HOT1: the apply-now executor, the local index source that serves a dummy listing from disk,
/// and Remove of a hot-loaded package. Everything is offline: the plugin image is the Roslyn
/// fixture assembly <see cref="AdmissionFixtures.Valid"/> (compiled, never loaded), the zip is
/// built on disk, the transport is a file:// one, and nothing contacts a game process.
/// </summary>
public sealed class InstallHotLoadTests : IClassFixture<AdmissionFixtures>, IDisposable
{
    private readonly AdmissionFixtures _f;
    private readonly string _root;
    private readonly string _serverRoot;
    private readonly string _local;

    public InstallHotLoadTests(AdmissionFixtures f)
    {
        _f = f;
        string baseDir = Path.Combine(Path.GetTempPath(), "progenitor-hot1-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "client", "BepInEx");
        _serverRoot = Path.Combine(baseDir, "server", "BepInEx");
        _local = Path.Combine(baseDir, "local");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_serverRoot);
        Directory.CreateDirectory(_local);
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- plumbing (the same shapes AdmissionPlannerTests uses) ---------------------------------

    private const string Key = "team-Sample";
    private const string PluginFile = "example.sample.dll";

    private sealed class FakeSnapshots : IAdmissionSnapshotSource
    {
        public RuntimeGenerationSnapshot Snapshot { get; set; } = RuntimeGenerationSnapshot.Unavailable();
        public RuntimeGenerationSnapshot Capture(string pluginId, InstallTarget target) => Snapshot;
    }

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private static IReadOnlyList<CompatibilityIdentity> Identities() => new[]
    {
        new CompatibilityIdentity(CompatibilityKeys.GameBuild, "1.1.5"),
        new CompatibilityIdentity(CompatibilityKeys.BootstrapContract, "b/3"),
        new CompatibilityIdentity(CompatibilityKeys.CoreContract, "c/7"),
    };

    private static RuntimeGenerationSnapshot Live(bool server = false) =>
        new(true, null, AdmissionSide.Client, 1, false, true, server, null, Identities());

    private static string RecordJson(byte[] image, string target = "Client") =>
        "{\"schema\":1,\"records\":[{\"id\":\"sample-initial\",\"pluginId\":\"example.sample\",\"artifactSha256\":\""
        + CatalogFetcher.ToHex(SHA256.HashData(image))
        + "\",\"artifactSize\":" + image.Length
        + ",\"dependencyClosure\":{},\"gameBuild\":\"1.1.5\",\"bootstrapContract\":\"b/3\",\"coreContract\":\"c/7\",\"side\":\"client\","
        + "\"operation\":\"InitialInstall\",\"target\":\"" + target
        + "\",\"qualifiedUtc\":\"2026-09-12T00:00:00Z\",\"evidence\":\"research/x.md\"}]}";

    private string Zip(byte[] dll, string? folder = null)
    {
        string dllSha = CatalogFetcher.ToHex(SHA256.HashData(dll));
        string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha
            + "\",\"side\":\"client\",\"hotLoadable\":true}";
        string path = Path.Combine(folder ?? Path.GetDirectoryName(_root)!, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "progenitor.json", Encoding.UTF8.GetBytes(json));
        Write(zip, PluginFile, dll);
        return path;
    }

    private static void Write(ZipArchive zip, string name, byte[] body)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(body, 0, body.Length);
    }

    private static string Listing(string? downloadUrl = null, string owner = "team", string name = "Sample") =>
        "[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner
        + "\",\"package_url\":\"https://thunderstore.io/c/v-rising/p/" + owner + "/" + name + "/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "-1.0.0\",\"description\":\"d\","
        + "\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\""
        + (downloadUrl ?? "https://thunderstore.io/package/download/team/Sample/1.0.0/")
        + "\",\"file_size\":10,\"is_active\":true}]}]";

    private (InstallCatalogService Catalog, FakeSnapshots Snapshots) Service(
        bool localServer = false,
        string? serverRoot = null,
        bool thisProcessIsServer = false,
        string? localIndexPath = null,
        string? policyJson = null,
        string? indexJson = null)
    {
        var snapshots = new FakeSnapshots();
        var pipeline = new AdmissionPipeline(
            snapshots,
            QualificationPolicy.Parse(policyJson ?? RecordJson(_f.Valid)),
            Identities());
        var catalog = new InstallCatalogService(
            _root,
            new NoTransport(),
            null,
            null,
            null,
            localServer,
            pipeline,
            serverRoot,
            localIndexPath,
            thisProcessIsServer);
        // INDEX1: only an Index install hot loads; these listings are listed there too.
        catalog.IndexSource = IndexKit.Mirror(indexJson ?? Listing());
        Assert.True(catalog.LoadIndexFromJson(indexJson ?? Listing(), "stamp").Ok);
        return (catalog, snapshots);
    }

    private InstallPlan Approved(InstallCatalogService catalog, InstallTarget target, string zipPath)
    {
        var inspect = catalog.InspectLocalZip(Key, zipPath);
        Assert.True(inspect.Ok, inspect.Error);
        var planned = catalog.Plan(Key, target);
        Assert.True(planned.Ok, planned.Error);
        var approved = catalog.Approve(planned.Plan!.PlanHash);
        Assert.True(approved.Ok, approved.Error);
        return planned.Plan!;
    }

    private string ReloadFile(string root) => Path.Combine(root, ModFolders.ReloadFolderName, PluginFile);

    private static IReadOnlyList<ServerInstallLedgerEntry> Ledger(string bepInExRoot)
    {
        var ledger = ServerInstallLedger.TryRead(Path.GetDirectoryName(Path.GetFullPath(bepInExRoot))!);
        return ledger?.Files ?? Array.Empty<ServerInstallLedgerEntry>();
    }

    // ---- 1. the executor ----------------------------------------------------------------------

    [Fact]
    public void Admitted_client_install_is_written_into_the_reload_folder_and_ledgered_as_a_mod()
    {
        var (catalog, snapshots) = Service();
        snapshots.Snapshot = Live();
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Client, zip);

        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);
        Assert.True(plan.ExecutorAvailable);
        Assert.Equal(InstallReasons.ExecutorHotLoad, plan.ExecutorReason);
        Assert.True(plan.ExecutorAvailable, "the pane reads ExecutorAvailable for its no-restart caption");

        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.Equal(ReloadFile(_root), executed.PendingPath);
        Assert.True(File.Exists(ReloadFile(_root)), "the reloader's folder has no plugin image");
        Assert.Equal(CatalogFetcher.ToHex(SHA256.HashData(_f.Valid)), executed.Sha256);
        Assert.Equal(CatalogFetcher.HashFile(ReloadFile(_root)), executed.Sha256);

        // No pending copy: this install is live, not waiting for a restart.
        Assert.False(Directory.Exists(Path.Combine(_root, ModManifestPending.PendingFolderName)));

        // One ledger row, category mod, package source — the same source Remove looks for.
        var rows = Ledger(_root);
        Assert.Single(rows);
        Assert.Equal(ServerInstallLedger.CategoryMod, rows[0].Category);
        Assert.Equal("package:" + Key + "@1.0.0", rows[0].Source);
        Assert.Equal(executed.Sha256, rows[0].Sha256);

        // The readback the bridge and the pane show.
        string readback = JsonSerializer.Serialize(executed.ToReadback());
        Assert.Contains("\"hotLoaded\":true", readback);
        Assert.Contains("\"sha256\":\"" + executed.Sha256 + "\"", readback);
        // Revert: writing pending for an admitted plan puts the reloader's copy behind a restart.
    }

    [Fact]
    public void A_second_confirm_of_the_same_image_is_already_present_and_leaves_one_ledger_row()
    {
        var (catalog, snapshots) = Service();
        snapshots.Snapshot = Live();
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Client, zip);
        Assert.True(catalog.ExecuteToPending(plan.PlanHash).Ok);

        var again = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(again.Ok, again.Error);
        Assert.True(again.AlreadyPresent);
        Assert.True(again.HotLoaded);
        Assert.Single(Ledger(_root));
    }

    [Fact]
    public void Bytes_that_changed_after_approval_are_refused_and_nothing_is_written()
    {
        var (catalog, snapshots) = Service();
        snapshots.Snapshot = Live();
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Client, zip);
        File.WriteAllBytes(zip, new byte[] { 1, 2, 3 });

        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.False(executed.Ok);
        Assert.False(File.Exists(ReloadFile(_root)));
        Assert.Empty(Ledger(_root));
    }

    [Fact]
    public void An_unadmitted_plan_never_reaches_the_reload_folder()
    {
        // HOTDEMO1: a clean ProgenitorPlugin is trusted on its hotLoadable flag now, so the
        // record rules are pinned with a plain BepInEx plugin, which still needs a record.
        // No qualification record: admission is RestartInstall, so the plan is a restart plan and
        // the executor refuses the plan outright even when called directly.
        var (catalog, snapshots) = Service(policyJson: "{\"schema\":1,\"records\":[]}");
        snapshots.Snapshot = Live();
        string zip = Zip(_f.PlainBasePlugin);
        var inspect = catalog.InspectLocalZip(Key, zip);
        Assert.True(inspect.Ok, inspect.Error);
        var plan = catalog.Plan(Key, InstallTarget.Client).Plan!;
        Assert.Equal(InstallDecision.RestartClientRequired, plan.Decision);
        Assert.False(plan.ExecutorAvailable);
        Assert.False(HotLoadExecutor.IsHotLoadPlan(plan));

        var direct = HotLoadExecutor.ExecuteToReload(_root, plan, inspect.Inspection!, zip);
        Assert.False(direct.Ok);
        Assert.Equal(InstallReasons.HotLoadNotAdmitted, direct.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, ModFolders.ReloadFolderName)));
        // Revert: a decision-only check would hot load an Unknown or Blocked admission.
    }

    [Fact]
    public void A_local_server_target_on_a_client_process_keeps_the_restart_route_and_says_so()
    {
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "LocalServer"));
        snapshots.Snapshot = Live(server: true);
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.LocalServer, zip);

        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);
        Assert.False(plan.ExecutorAvailable);
        Assert.Equal(InstallReasons.HotLoadNotThisProcess, plan.ExecutorReason);
        Assert.Contains(InstallReasons.HotLoadNotThisProcess, plan.Reason);
        Assert.False(plan.ExecutorAvailable, "the pane must still say restart for this half");

        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.False(executed.HotLoaded);
        Assert.Contains(ModManifestPending.PendingFolderName, executed.PendingPath!);
        Assert.False(Directory.Exists(Path.Combine(_serverRoot, ModFolders.ReloadFolderName)));
        Assert.False(Directory.Exists(Path.Combine(_root, ModFolders.ReloadFolderName)));
        // Revert: hot loading another process's folder promises a load nothing in this process does.
    }

    /// <summary>
    /// LOCALDETECT1 review: while connected to an auto-detected local server, a server-side install writes into that
    /// server's folder (the catalog's override root), never the configured folder's.
    /// </summary>
    [Fact]
    public void A_server_install_lands_in_the_auto_detected_servers_folder_not_the_configured_one()
    {
        string detectedRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!, "detected", "BepInEx");
        Directory.CreateDirectory(detectedRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "LocalServer"));
        snapshots.Snapshot = Live(server: true);
        catalog.ServerRootOverride = () => detectedRoot;
        catalog.ServerRootOverrideIsHosted = () => false;
        Assert.Equal(Path.GetFullPath(detectedRoot), catalog.ServerBepInExRoot);

        var plan = Approved(catalog, InstallTarget.LocalServer, Zip(_f.Valid));
        var executed = catalog.ExecuteToPending(plan.PlanHash);

        Assert.True(executed.Ok, executed.Error);
        string pending = Path.GetFullPath(executed.PendingPath!);
        Assert.StartsWith(Path.GetFullPath(detectedRoot) + Path.DirectorySeparatorChar, pending, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(pending));
        Assert.False(Directory.Exists(Path.Combine(_serverRoot, ModManifestPending.PendingFolderName)));
        Assert.Empty(Ledger(_serverRoot));
    }

    [Fact]
    public void On_a_server_process_the_local_server_target_is_the_hot_one_and_the_client_target_is_not()
    {
        Assert.True(HotLoadExecutor.CanHotLoad(InstallTarget.Client, thisProcessIsServer: false));
        Assert.False(HotLoadExecutor.CanHotLoad(InstallTarget.LocalServer, thisProcessIsServer: false));
        Assert.True(HotLoadExecutor.CanHotLoad(InstallTarget.LocalServer, thisProcessIsServer: true));
        Assert.False(HotLoadExecutor.CanHotLoad(InstallTarget.Client, thisProcessIsServer: true));
    }

    [Fact]
    public void The_both_target_hot_loads_this_process_and_pends_the_other_root()
    {
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "Both"));
        snapshots.Snapshot = Live(server: true);
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Both, zip);
        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);
        Assert.True(plan.ExecutorAvailable);
        Assert.Contains(InstallReasons.HotLoadServerHalfPending, plan.Reason);

        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.True(File.Exists(ReloadFile(_root)));
        Assert.NotNull(executed.ServerPendingPath);
        Assert.True(File.Exists(executed.ServerPendingPath!));
        Assert.Contains(ModManifestPending.PendingFolderName, executed.ServerPendingPath!);
    }

    // ---- 2. Remove -----------------------------------------------------------------------------

    [Fact]
    public void Remove_of_a_hot_loaded_package_deletes_the_reload_copy_and_the_row_reads_none()
    {
        var (catalog, snapshots) = Service();
        snapshots.Snapshot = Live();
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Client, zip);
        Assert.True(catalog.ExecuteToPending(plan.PlanHash).Ok);
        Assert.True(File.Exists(ReloadFile(_root)));

        var query = InstalledState.Scan(_root, null);
        Assert.Equal(InstalledKind.Pending, InstalledState.Detect(plan.Entry, query).State);

        var removed = catalog.RemovePackage(Key);
        Assert.True(removed.Ok, removed.Message);
        Assert.False(removed.NoOp);
        Assert.True(removed.HotUnloaded);
        Assert.Contains(InstallReasons.HotUnloaded, removed.Message);
        Assert.False(File.Exists(ReloadFile(_root)), "the reload copy survived Remove, so the plugin stays loaded");
        Assert.Empty(Ledger(_root));

        // Nothing is left for a restart to finish: the listing is simply not installed again.
        var after = InstalledState.Detect(plan.Entry, InstalledState.Scan(_root, null));
        Assert.Equal(InstalledKind.NotInstalled, after.State);
        Assert.Equal("not-installed", after.StateName);
        // Revert: leaving the row at pending/removed would claim a restart is still owed.
    }

    [Fact]
    public void Remove_of_a_package_that_was_never_installed_is_a_no_op_and_not_a_hot_unload()
    {
        var (catalog, _) = Service();
        var removed = catalog.RemovePackage(Key);
        Assert.True(removed.Ok);
        Assert.True(removed.NoOp);
        Assert.False(removed.HotUnloaded);
    }

    // ---- 3. the local index source --------------------------------------------------------------

    private string WriteLocalIndex(string zipPath, string owner = "Progenitor", string name = "ReloadExample")
    {
        string url = new Uri(Path.GetFullPath(zipPath)).AbsoluteUri;
        string icon = new Uri(Path.Combine(_local, "icon.png")).AbsoluteUri;
        string json = "[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "\",\"owner\":\"" + owner
            + "\",\"package_url\":\"\",\"is_deprecated\":false,\"categories\":[\"Client\",\"Server\",\"Oakveil Update\",\"Mods\"],"
            + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"" + owner + "-" + name + "-1.0.0\",\"description\":\"dummy\","
            + "\"icon\":\"" + icon + "\",\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"" + url
            + "\",\"file_size\":10,\"is_active\":true}]}]";
        File.WriteAllText(Path.Combine(_local, LocalIndexSource.IndexFileName), json);
        return owner + "-" + name;
    }

    [Fact]
    public void The_local_index_is_merged_in_front_of_the_fetched_one()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        var (catalog, _) = Service(localIndexPath: _local);

        var entries = catalog.Search(null);
        Assert.Equal(2, entries.Count);
        Assert.Equal(localKey, entries[0].Key);
        Assert.Equal(Key, entries[1].Key);
        Assert.True(catalog.IsLocalKey(localKey));
        Assert.False(catalog.IsLocalKey(Key));
        Assert.Null(catalog.LocalIndexError);
        Assert.Equal(0, catalog.LocalIndexShadowed);
        Assert.Equal(_local, catalog.LocalIndexPath);
    }

    /// <summary>
    /// LOCALRELOAD1 (live 2026-10-07: [Install] LocalIndexPath set from empty, the cfg reloaded, and install_local reload
    /// still merged nothing until a Core reload): a reload follows the configured key, and a cleared key drops the local
    /// entries. A caller-named folder is not replaced.
    /// </summary>
    [Fact]
    public void A_reload_follows_a_changed_local_index_path()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        string? configured = null;
        LocalIndexConfig.TestOverride = () => configured;
        try
        {
            var (catalog, _) = Service(localIndexPath: null);
            Assert.Null(catalog.LocalIndexPath);
            Assert.Empty(catalog.LocalIndexKeys);

            configured = _local;
            catalog.ReloadLocalIndex();
            Assert.Equal(Path.GetFullPath(_local), catalog.LocalIndexPath);
            Assert.Contains(localKey, catalog.LocalIndexKeys);
            Assert.True(catalog.IsLocalKey(localKey));

            configured = "";
            catalog.ReloadLocalIndex();
            Assert.Null(catalog.LocalIndexPath);
            Assert.Empty(catalog.LocalIndexKeys);
            Assert.False(catalog.IsLocalKey(localKey));

            // A folder the caller named stays.
            var (named, _) = Service(localIndexPath: _local);
            named.ReloadLocalIndex();
            Assert.Contains(localKey, named.LocalIndexKeys);
        }
        finally { LocalIndexConfig.TestOverride = null; }
    }

    /// <summary>
    /// F1 (live 2026-10-07): a fresh Core's lookup from a plugin to its listing does not wait for an index load: the
    /// configured local index is merged on the first lookup, and a plugin is found by the package its ledger names.
    /// </summary>
    [Fact]
    public void A_plugin_lookup_merges_the_local_index_without_an_index_load()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        LocalIndexConfig.TestOverride = () => _local;
        try
        {
            var (catalog, _) = Service(localIndexPath: null);
            catalog.EnsureLookupReady();
            Assert.Contains(localKey, catalog.LocalIndexKeys);
            Assert.True(catalog.TryGetEntry(localKey, out var entry) && entry != null);
            // F23: a server root override (the hosted private game's folder) replaces the configured one while it answers.
            string hostedRoot = Path.Combine(Path.GetTempPath(), "progenitor-hosted-" + Guid.NewGuid().ToString("N"), "BepInEx");
            catalog.ServerRootOverride = () => hostedRoot;
            Assert.Equal(Path.GetFullPath(hostedRoot), catalog.ServerBepInExRoot);
            catalog.ServerRootOverride = () => null;
            Assert.NotEqual(Path.GetFullPath(hostedRoot), catalog.ServerBepInExRoot);
            catalog.ServerRootOverride = null;
            // F1: a plugin id the index cannot map is found by the package its ledger names; never "not ours".
            var seam = new CatalogUninstallSeam(catalog);
            Assert.False(seam.Owns("example.unmapped"));
            seam.LedgerKeysProbe = id => id == "example.unmapped" ? new[] { localKey } : Array.Empty<string>();
            Assert.True(seam.Owns("example.unmapped"));
        }
        finally { LocalIndexConfig.TestOverride = null; }
    }

    [Fact]
    public void A_local_entry_shadows_a_fetched_listing_with_the_same_key()
    {
        string zip = Zip(_f.Valid, _local);
        WriteLocalIndex(zip, "team", "Sample");
        var (catalog, _) = Service(localIndexPath: _local);
        var entries = catalog.Search(null);
        Assert.Single(entries);
        Assert.Equal(1, catalog.LocalIndexShadowed);
        Assert.True(catalog.IsLocalKey(Key));
    }

    [Fact]
    public void A_missing_or_malformed_local_index_is_reported_and_the_fetched_index_still_loads()
    {
        var (missing, _) = Service(localIndexPath: _local);
        Assert.Single(missing.Search(null));
        Assert.Contains(InstallReasons.LocalIndexMissing, missing.LocalIndexError);
        Assert.Empty(missing.LocalIndexKeys);

        File.WriteAllText(Path.Combine(_local, LocalIndexSource.IndexFileName), "{not json");
        var (malformed, _) = Service(localIndexPath: _local);
        Assert.Single(malformed.Search(null));
        Assert.StartsWith(InstallReasons.MalformedJson, malformed.LocalIndexError, StringComparison.Ordinal);

        var unconfigured = new LocalIndexSource(null);
        Assert.False(unconfigured.Configured);
        Assert.Equal(InstallReasons.LocalIndexNotConfigured, unconfigured.Load().Error);
    }

    [Fact]
    public async Task The_file_transport_serves_the_local_zip_and_refuses_anything_else()
    {
        string zip = Zip(_f.Valid, _local);
        var transport = new FileCatalogTransport(_local);

        using (var ok = await transport.SendAsync(new CatalogTransportRequest(new Uri(new Uri(zip).AbsoluteUri)), CancellationToken.None))
        {
            Assert.Equal(200, ok.StatusCode);
            Assert.Equal(new FileInfo(zip).Length, ok.ContentLength);
        }

        string outside = Path.Combine(Path.GetDirectoryName(_local)!, "outside.bin");
        File.WriteAllBytes(outside, new byte[] { 7 });
        using (var refused = await transport.SendAsync(new CatalogTransportRequest(new Uri(new Uri(outside).AbsoluteUri)), CancellationToken.None))
            Assert.Equal(403, refused.StatusCode);

        using (var https = await transport.SendAsync(new CatalogTransportRequest(new Uri("https://thunderstore.io/x")), CancellationToken.None))
            Assert.Equal(403, https.StatusCode);

        using (var gone = await transport.SendAsync(
            new CatalogTransportRequest(new Uri(new Uri(Path.Combine(_local, "nope.zip")).AbsoluteUri)), CancellationToken.None))
            Assert.Equal(404, gone.StatusCode);
    }

    [Fact]
    public async Task A_local_listing_downloads_inspects_plans_and_hot_loads_through_the_file_transport()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        var snapshots = new FakeSnapshots { Snapshot = Live() };
        var pipeline = new AdmissionPipeline(snapshots, QualificationPolicy.Parse(RecordJson(_f.Valid)), Identities());
        var catalog = new InstallCatalogService(
            _root, new NoTransport(), null, null, null, false, pipeline, null, _local, false);
        Assert.True(catalog.LoadIndexFromJson(Listing(), "stamp").Ok);

        var inspect = await catalog.DownloadAndInspectAsync(localKey, CancellationToken.None);
        Assert.True(inspect.Ok, inspect.Error);
        var planned = catalog.Plan(localKey, InstallTarget.Client);
        Assert.Equal(InstallDecision.ApplyNow, planned.Plan!.Decision);
        Assert.True(planned.Plan.ExecutorAvailable);
        Assert.True(catalog.Approve(planned.Plan.PlanHash).Ok);

        var executed = catalog.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.True(File.Exists(ReloadFile(_root)));
        // Revert: the HTTPS-only source rule with no file:// opt-in refuses this download entirely.
    }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07): a copy hot loaded through the install lane, whose ledger <c>hot</c> flag is gone (a
    /// join-window install sets none; a new start or Core generation clears it), is still the trusted hotLoadable image in
    /// this client's progenitor-reload. The Mods page's Uninstall unloads it now, and its box says so, instead of marking
    /// it for the next start while promising "Unloads now". Without the reloader holding it, it is marked.
    /// </summary>
    [Fact]
    public async Task A_hot_loaded_copy_without_its_hot_flag_is_removed_now_and_the_box_says_so()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        var snapshots = new FakeSnapshots { Snapshot = Live() };
        var pipeline = new AdmissionPipeline(snapshots, QualificationPolicy.Parse(RecordJson(_f.Valid)), Identities());
        var catalog = new InstallCatalogService(
            _root, new NoTransport(), null, null, null, false, pipeline, null, _local, false);
        Assert.True(catalog.LoadIndexFromJson(Listing(), "stamp").Ok);
        Assert.True((await catalog.DownloadAndInspectAsync(localKey, CancellationToken.None)).Ok);
        var planned = catalog.Plan(localKey, InstallTarget.Client);
        Assert.True(catalog.Approve(planned.Plan!.PlanHash).Ok);
        Assert.True(catalog.ExecuteToPending(planned.Plan.PlanHash).HotLoaded);
        ServerInstall.ResetHotSweepForTests();
        ServerInstall.ClearHotFlags(_root);
        Assert.Empty(catalog.HotLoadedPackages());

        var seam = new CatalogUninstallSeam(catalog);
        string pluginId = catalog.TryGetEntry(localKey, out var entry) && entry != null ? entry.Manifest.Id ?? "" : "";
        if (pluginId.Length == 0) pluginId = "example.reloadexample";
        Assert.True(seam.Owns(pluginId), "the seam resolves the installed listing");
        Assert.False(seam.UnloadsNow(pluginId, hot: false));
        Assert.True(seam.UnloadsNow(pluginId, hot: true));

        var removed = seam.MarkUninstall(pluginId, hot: true);
        Assert.True(removed.Ok, removed.Message);
        Assert.False(File.Exists(ReloadFile(_root)), "removed now, not marked");
        Assert.False(File.Exists(ReloadFile(_root) + ".uninstall"));
    }

    [Fact]
    public async Task A_local_listings_documents_come_from_the_files_beside_index_json()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        File.WriteAllText(Path.Combine(_local, LocalIndexSource.ReadmeFileName), "# Dummy\n");
        var (catalog, _) = Service(localIndexPath: _local);

        var docs = await catalog.LoadListingDocumentsAsync(localKey, CancellationToken.None);
        Assert.True(docs.ReadmeOk);
        Assert.Contains("Dummy", docs.ReadmeMarkdown);
        Assert.False(docs.ChangelogOk);
        Assert.Equal(InstallReasons.LocalDocumentUnavailable, docs.ChangelogError);
    }

    // ---- F11: a local listing's README is its own package's --------------------------------------

    /// <summary>A local zip carrying a zip-root README.md (and CHANGELOG.md when given), like the dummy-package script's.</summary>
    private string DocZip(string name, string? readme, string? changelog = null)
    {
        string path = Path.Combine(_local, "Progenitor-" + name + "-1.0.0.zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "progenitor.json", Encoding.UTF8.GetBytes("{\"id\":\"example." + name.ToLowerInvariant() + "\",\"version\":\"1.0.0\",\"side\":\"server\"}"));
        Write(zip, PluginFile, _f.Valid);
        if (readme != null) Write(zip, "README.md", Encoding.UTF8.GetBytes(readme));
        if (changelog != null) Write(zip, "CHANGELOG.md", Encoding.UTF8.GetBytes(changelog));
        // A nested README is not the package's.
        Write(zip, "docs/README.md", Encoding.UTF8.GetBytes("# nested " + name + "\n"));
        return path;
    }

    /// <summary>One index.json listing every (name, zip) pair, owner Progenitor, version 1.0.0.</summary>
    private void WriteLocalIndexOf(params (string Name, string Zip)[] packages)
    {
        var rows = new List<string>();
        foreach (var (name, zip) in packages)
        {
            string url = new Uri(Path.GetFullPath(zip)).AbsoluteUri;
            rows.Add("{\"name\":\"" + name + "\",\"full_name\":\"Progenitor-" + name + "\",\"owner\":\"Progenitor\","
                + "\"package_url\":\"\",\"is_deprecated\":false,\"categories\":[\"Server\"],"
                + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"Progenitor-" + name + "-1.0.0\",\"description\":\"dummy\","
                + "\"version_number\":\"1.0.0\",\"dependencies\":[],\"download_url\":\"" + url
                + "\",\"file_size\":10,\"is_active\":true}]}");
        }
        File.WriteAllText(Path.Combine(_local, LocalIndexSource.IndexFileName), "[" + string.Join(",", rows) + "]");
    }

    /// <summary>
    /// F11 (live 2026-10-07): ReloadExample's Mods page showed TimedBuffExample's README. Both came from
    /// one local folder, whose single README.md beside index.json (the last package the script wrote)
    /// served every listing. Each listing's documents are its own zip's now, through the load route and
    /// the cached route the Mods page and the drawer read.
    /// </summary>
    [Fact]
    public async Task Two_local_listings_in_one_folder_each_show_their_own_readme()
    {
        WriteLocalIndexOf(
            ("ReloadExample", DocZip("ReloadExample", "# ReloadExample\nThe reload sample.\n", "# 1.0.0\nReload notes.\n")),
            ("TimedBuffExample", DocZip("TimedBuffExample", "# TimedBuffExample\nThe timed buff sample.\n")));
        // The folder's own README is the last package's, as the dummy-package script leaves it.
        File.WriteAllText(Path.Combine(_local, LocalIndexSource.ReadmeFileName), "# TimedBuffExample\n");
        File.WriteAllText(Path.Combine(_local, LocalIndexSource.ChangelogFileName), "# folder changelog\n");
        var (catalog, _) = Service(localIndexPath: _local);

        var reload = await catalog.LoadListingDocumentsAsync("Progenitor-ReloadExample", CancellationToken.None);
        Assert.True(reload.ReadmeOk);
        Assert.Contains("The reload sample.", reload.ReadmeMarkdown);
        Assert.DoesNotContain("TimedBuff", reload.ReadmeMarkdown);
        Assert.DoesNotContain("nested", reload.ReadmeMarkdown);
        Assert.True(reload.ChangelogOk);
        Assert.Contains("Reload notes.", reload.ChangelogMarkdown);

        var timed = await catalog.LoadListingDocumentsAsync("Progenitor-TimedBuffExample", CancellationToken.None);
        Assert.Contains("The timed buff sample.", timed.ReadmeMarkdown);
        // No changelog in its zip, and the folder's file is not per package with two listings.
        Assert.False(timed.ChangelogOk);
        Assert.Equal(InstallReasons.LocalDocumentUnavailable, timed.ChangelogError);

        // The cached route (ModsMenu.ReadmeFor, the drawer) answers per package too, with no fetch first.
        Assert.True(catalog.TryGetCachedListingDocuments("Progenitor-ReloadExample", out var cached));
        Assert.Contains("The reload sample.", cached!.ReadmeMarkdown);
        Assert.True(catalog.TryGetCachedListingDocuments("Progenitor-TimedBuffExample", out cached));
        Assert.Contains("The timed buff sample.", cached!.ReadmeMarkdown);

        var pane = new Progenitor.Ui.InstallModsPane(catalog);
        Assert.True(catalog.TryGetEntry("Progenitor-ReloadExample", out var entry) && entry != null);
        var blocks = pane.ReadmeBlocksFor(entry!);
        Assert.Contains(blocks, b => b.Text.Contains("The reload sample.", StringComparison.Ordinal));
        Assert.DoesNotContain(blocks, b => b.Text.Contains("TimedBuff", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_local_zip_without_a_readme_is_unavailable_when_the_folder_lists_several()
    {
        WriteLocalIndexOf(
            ("ReloadExample", DocZip("ReloadExample", null)),
            ("TimedBuffExample", DocZip("TimedBuffExample", "# TimedBuffExample\n")));
        File.WriteAllText(Path.Combine(_local, LocalIndexSource.ReadmeFileName), "# TimedBuffExample\n");
        var (catalog, _) = Service(localIndexPath: _local);

        var docs = await catalog.LoadListingDocumentsAsync("Progenitor-ReloadExample", CancellationToken.None);
        Assert.False(docs.ReadmeOk);
        Assert.Equal(InstallReasons.LocalDocumentUnavailable, docs.ReadmeError);
        Assert.Equal(LocalIndexSource.ZipDocuments.None.Readme, LocalIndexSource.DocumentsOfZip(Path.Combine(_local, "missing.zip")).Readme);
    }

    [Fact]
    public async Task A_rebuilt_local_zip_serves_its_new_readme()
    {
        string zip = DocZip("ReloadExample", "# first build\n");
        WriteLocalIndexOf(("ReloadExample", zip), ("TimedBuffExample", DocZip("TimedBuffExample", "# t\n")));
        var (catalog, _) = Service(localIndexPath: _local);
        Assert.Contains("first build", (await catalog.LoadListingDocumentsAsync("Progenitor-ReloadExample", CancellationToken.None)).ReadmeMarkdown);

        DocZip("ReloadExample", "# second build, a longer readme\n");
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
        Assert.Contains("second build", (await catalog.LoadListingDocumentsAsync("Progenitor-ReloadExample", CancellationToken.None)).ReadmeMarkdown);
    }

    // ---- LOCALSIDE1: a local listing's side before inspection ------------------------------------

    /// <summary>A local package zip whose root <c>progenitor.json</c> declares <paramref name="side"/> (null: no side key; "": no progenitor.json).</summary>
    private string SideZip(string? side, string? path = null, string? extra = null)
    {
        path ??= Path.Combine(_local, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        string sha = CatalogFetcher.ToHex(SHA256.HashData(_f.Valid));
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        if (side != "")
        {
            string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + sha + "\""
                + (side == null ? "" : ",\"side\":\"" + side + "\"") + ",\"hotLoadable\":true" + (extra ?? "") + "}";
            Write(zip, "progenitor.json", Encoding.UTF8.GetBytes(json));
        }
        Write(zip, PluginFile, _f.Valid);
        return path;
    }

    private static CatalogEntry EntryOf(InstallCatalogService catalog, string key)
    {
        Assert.True(catalog.TryGetEntry(key, out var entry) && entry != null, key);
        return entry!;
    }

    /// <summary>
    /// F5 (live 2026-10-07): a server-only local package listed both sides and offered "Install on both"
    /// until it was inspected, because the index's Client+Server category tags were the only signal. The
    /// local zip's progenitor.json side is read when the index is, so the row is right from the start.
    /// </summary>
    [Fact]
    public void A_local_listing_takes_its_zips_declared_side_before_any_inspection()
    {
        string serverKey = WriteLocalIndex(SideZip("server"));
        var (catalog, _) = Service(localIndexPath: _local);

        var entry = EntryOf(catalog, serverKey);
        Assert.Equal(PackageSide.Server, entry.Side);
        var derived = PackageSideDerivation.Derive(entry);
        Assert.Equal(PackageSide.Server, derived.Side);
        Assert.Equal(PackageSideSource.Manifest, derived.Source);
        Assert.Equal(CatalogProvenance.AdaptedListing, entry.Provenance);
        Assert.Equal(PackageSide.Server, Progenitor.Ui.InstallModsPane.SideOf(entry));

        string clientKey = WriteLocalIndex(SideZip("client"), "Progenitor", "FilmExample");
        catalog.ReloadLocalIndex();
        Assert.Equal(PackageSide.Client, EntryOf(catalog, clientKey).Side);
    }

    [Fact]
    public void A_local_zip_without_a_readable_side_leaves_the_listings_own_signals()
    {
        // No side key, no progenitor.json, a malformed one: the index's Client+Server tags still say Both.
        foreach (var zip in new[] { SideZip(null), SideZip(""), SideZip("sideways") })
        {
            LocalTestSource.ResetForTests();
            string key = WriteLocalIndex(zip);
            var (catalog, _) = Service(localIndexPath: _local);
            var entry = EntryOf(catalog, key);
            Assert.Equal(PackageSide.Unknown, entry.Side);
            Assert.Equal(PackageSideSource.Categories, PackageSideDerivation.Derive(entry).Source);
            Assert.Equal(PackageSide.Both, PackageSideDerivation.Derive(entry).Side);
        }

        Assert.Equal(PackageSide.Unknown, LocalIndexSource.DeclaredSideOfZip(Path.Combine(_local, "missing.zip")));
        string notZip = Path.Combine(_local, "not-a-zip.zip");
        File.WriteAllText(notZip, "not a zip");
        Assert.Equal(PackageSide.Unknown, LocalIndexSource.DeclaredSideOfZip(notZip));
    }

    [Fact]
    public void A_nested_progenitor_json_is_not_read_as_the_packages_side()
    {
        // The inspector reads only the zip-root progenitor.json; the listing hint follows the same rule.
        string path = Path.Combine(_local, "nested.zip");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            Write(zip, "sub/progenitor.json", Encoding.UTF8.GetBytes("{\"id\":\"example.sample\",\"side\":\"server\"}"));
        Assert.Equal(PackageSide.Unknown, LocalIndexSource.DeclaredSideOfZip(path));
    }

    [Fact]
    public void A_rewritten_local_zip_is_read_again_and_an_inspection_still_wins()
    {
        string zip = SideZip("server");
        string key = WriteLocalIndex(zip);
        var (catalog, _) = Service(localIndexPath: _local);
        Assert.Equal(PackageSide.Server, EntryOf(catalog, key).Side);

        // The cache is keyed by path, size and write time: a rebuilt package is read again.
        SideZip("client", zip, ",\"note\":\"rebuilt\"");
        File.SetLastWriteTimeUtc(zip, DateTime.UtcNow.AddMinutes(1));
        catalog.ReloadLocalIndex();
        Assert.Equal(PackageSide.Client, EntryOf(catalog, key).Side);

        // An inspection of other bytes for the listing replaces the hint, and survives a re-merge.
        string both = SideZip("both", Path.Combine(_local, "inspected.zip"));
        Assert.True(catalog.InspectLocalZip(key, both).Ok);
        Assert.Equal(PackageSide.Both, EntryOf(catalog, key).Side);
        catalog.ReloadLocalIndex();
        Assert.Equal(PackageSide.Both, EntryOf(catalog, key).Side);
    }

    // ---- 4. the install_local op ----------------------------------------------------------------

    [Fact]
    public void The_install_local_op_reports_the_folder_the_entries_and_a_re_merge()
    {
        string zip = Zip(_f.Valid, _local);
        string localKey = WriteLocalIndex(zip);
        var (catalog, _) = Service(localIndexPath: _local);

        var status = InstallLocalIndexOps.Status(reload: false);
        Assert.Equal(true, status["ok"]);
        Assert.Equal("status", status["action"]);
        Assert.Equal(_local, status["localIndexPath"]);
        Assert.Equal(true, status["configured"]);
        Assert.Equal(true, status["catalog"]);
        Assert.Contains(localKey, (IReadOnlyList<string>)status["entries"]!);
        Assert.Null(status["error"]);

        // A listing added to the folder after the index was loaded appears on reload.
        WriteLocalIndex(zip, "Progenitor", "SecondExample");
        var reloaded = InstallLocalIndexOps.Status(reload: true);
        Assert.Equal("reload", reloaded["action"]);
        Assert.Contains("Progenitor-SecondExample", (IReadOnlyList<string>)reloaded["entries"]!);
        Assert.Equal(2, reloaded["merged"]);
        Assert.Equal(2, catalog.Search(null).Count);
    }

    [Fact]
    public void The_bridge_op_rejects_an_unknown_action_and_defaults_to_status()
    {
        var bad = Progenitor.Dev.Ops.InstallOps.Run(JsonDocument.Parse("{\"action\":\"install\"}").RootElement);
        string badJson = JsonSerializer.Serialize(bad);
        Assert.Contains("unknown action", badJson);
        Assert.Contains("install", badJson);

        var defaulted = Progenitor.Dev.Ops.InstallOps.Run(JsonDocument.Parse("{}").RootElement);
        string json = JsonSerializer.Serialize(defaulted);
        Assert.Contains("\"action\":\"status\"", json);
    }

    // ---- 5. the offline dummy package end to end -----------------------------------------------

    [Fact]
    public void The_dummy_package_shape_the_script_builds_installs_end_to_end_offline()
    {
        // The same entries tools/make-dummy-package.ps1 writes into the zip, built here from the
        // fixture assembly: progenitor.json + the plugin DLL + manifest.json + README + icon.
        string zip = Path.Combine(_local, "Progenitor-ReloadExample-0.1.0.zip");
        string sha = CatalogFetcher.ToHex(SHA256.HashData(_f.Valid));
        using (var file = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            Write(archive, "progenitor.json", Encoding.UTF8.GetBytes(
                "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"side\":\"client\",\"hotLoadable\":true,\"sha256\":\"" + sha + "\"}"));
            Write(archive, PluginFile, _f.Valid);
            Write(archive, "manifest.json", Encoding.UTF8.GetBytes(
                "{\"name\":\"ReloadExample\",\"version_number\":\"1.0.0\",\"website_url\":\"\",\"description\":\"d\",\"dependencies\":[]}"));
            Write(archive, "README.md", Encoding.UTF8.GetBytes("# ReloadExample\n"));
            Write(archive, "icon.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        }

        string localKey = WriteLocalIndex(zip);
        var (catalog, snapshots) = Service(localIndexPath: _local);
        snapshots.Snapshot = Live();

        var inspect = catalog.InspectLocalZip(localKey, zip);
        Assert.True(inspect.Ok, inspect.Error);
        Assert.Equal(CatalogProvenance.ManifestVerified, inspect.Inspection!.Provenance);
        Assert.True(inspect.Inspection.HotLoadableDeclared);

        var planned = catalog.Plan(localKey, InstallTarget.Client);
        Assert.Equal(AdmissionDecision.ApplyNowEligible, planned.Plan!.Admission!.Decision);
        Assert.True(planned.Plan.ExecutorAvailable);
        Assert.True(catalog.Approve(planned.Plan.PlanHash).Ok);

        var executed = catalog.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.Equal(sha, executed.Sha256);
        Assert.True(File.Exists(ReloadFile(_root)));

        var removed = catalog.RemovePackage(localKey);
        Assert.True(removed.HotUnloaded);
        Assert.False(File.Exists(ReloadFile(_root)));
    }
    // ---- 6. HOT3: the row and the inventory around the hot-loaded plugin ------------------------

    /// <summary>
    /// HOT3 defects 1-3 over the real executor: the confirm that hot loads leaves the row
    /// <c>installed / Remove</c> — no restart, in the second before the reloader's own scan reports
    /// the plugin — and asks for a fresh Mods inventory; the Remove that hot unloads puts the row back
    /// to <c>none / Install</c> and asks again. What this would catch: the live HOT2 residuals, where
    /// the row read <c>pending</c> while the plugin ran and <c>removed — still loaded</c> after a clean
    /// unload, and the Mods tab listed neither.
    /// </summary>
    [Fact]
    public void A_hot_install_through_the_pane_reads_installed_then_none_after_the_hot_unload()
    {
        var (catalog, snapshots) = Service();
        snapshots.Snapshot = Live();
        string zip = Zip(_f.Valid);
        Assert.True(catalog.InspectLocalZip(Key, zip).Ok);
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);

        var pane = new Progenitor.Ui.InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        // The reloader has not scanned yet: this is the state the live client showed as "pending".
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        var asked = new List<string>();
        pane.InventoryRefresh = asked.Add;
        pane.SelectListing(Key);
        Assert.True(pane.BeginReview());

        var executed = pane.ConfirmExecute();
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.True(File.Exists(ReloadFile(_root)));
        Assert.Equal(new[] { Progenitor.Ui.InstallModsPane.InventoryReasonHotLoaded }, asked);

        pane.SetInstalledQuery(InstalledState.Scan(_root, null));
        var action = pane.ActionFor(entry!);
        Assert.Equal(Progenitor.Ui.InstallModsCopy.Installed, action.Label); // INSTALLUX2 item 2: no `new` tag any more
        Assert.False(action.Restart);
        Assert.Equal("installed", Progenitor.Ui.InstallModsPane.StateName(action.State));
        Assert.Equal("installed", pane.ListingToWidget(entry!)["state"]);
        Assert.Equal(true, pane.ListingToWidget(entry!)["hotLoaded"]);

        // Remove through the service's own ledger-scoped remover: a hot unload.
        Assert.True(pane.OnRemoveClick(Key));
        Assert.True(pane.LastRemoveResult!.HotUnloaded);
        Assert.False(File.Exists(ReloadFile(_root)));
        Assert.Equal(
            new[]
            {
                Progenitor.Ui.InstallModsPane.InventoryReasonHotLoaded,
                Progenitor.Ui.InstallModsPane.InventoryReasonHotUnloaded,
            },
            asked);

        pane.SetInstalledQuery(InstalledState.Scan(_root, null));
        var after = pane.ActionFor(entry!);
        Assert.Equal("none", Progenitor.Ui.InstallModsPane.StateName(after.State));
        Assert.Equal(Progenitor.Ui.InstallModsCopy.Install, after.Label);
        Assert.True(after.Enabled);
        // Revert: adding a hot-unloaded key to SessionRemoved, or dropping SessionHotLoaded, fails this.
    }

    // ---- HOT4: a Both install is admitted, planned, executed and removed per half ---------------

    /// <summary>Seed an (empty) install ledger on a game root, so the pending route has one to append to.</summary>
    private static void SeedLedger(string bepInExRoot) =>
        new ServerInstallLedger("1.0.0", "", "", DateTime.UtcNow)
            .Write(Path.GetDirectoryName(Path.GetFullPath(bepInExRoot))!);

    private static InstallHalf Half(InstallPlan plan, InstallTarget target)
    {
        var half = plan.HalfFor(target);
        Assert.NotNull(half);
        return half!;
    }

    /// <summary>
    /// HOT4 live (deploy #66): an install that chose <c>Both</c> with a qualification record scoped to
    /// <c>Client</c> and got a restart install on both roots, because admission was evaluated once with
    /// the whole request target and a <c>Client</c> record can never match a <c>Both</c> request. Each
    /// half is now admitted for its own process: the client half hot loads, the server half pends.
    /// </summary>
    [Fact]
    public void Both_with_a_client_record_hot_loads_the_client_half_and_pends_the_server_half()
    {
        // HOTDEMO1: a clean ProgenitorPlugin is trusted on its hotLoadable flag now, so the
        // record rules are pinned with a plain BepInEx plugin, which still needs a record.
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.PlainBasePlugin, "Client"));
        snapshots.Snapshot = Live(server: true);
        string zip = Zip(_f.PlainBasePlugin);
        var plan = Approved(catalog, InstallTarget.Both, zip);

        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);
        Assert.True(plan.ExecutorAvailable);
        Assert.True(plan.IsMixed, "the halves agree, so the client-scoped record was applied to the server half too");
        var client = Half(plan, InstallTarget.Client);
        Assert.Equal(InstallDecision.ApplyNow, client.Decision);
        Assert.True(client.ExecutorAvailable);
        var server = Half(plan, InstallTarget.LocalServer);
        Assert.Equal(InstallDecision.RestartServerRequired, server.Decision);
        Assert.False(server.ExecutorAvailable);
        Assert.Contains("target", server.Reason);
        Assert.True(plan.LedgerClient && plan.LedgerServer);

        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded);
        Assert.Equal(ReloadFile(_root), executed.PendingPath);
        Assert.True(File.Exists(ReloadFile(_root)), "the client half did not reach the reloader's folder");
        Assert.NotNull(executed.ServerPendingPath);
        Assert.True(File.Exists(executed.ServerPendingPath!));
        Assert.Contains(ModManifestPending.PendingFolderName, executed.ServerPendingPath!);
        Assert.False(File.Exists(ReloadFile(_serverRoot)), "the server half was hot loaded into a process this is not");
        // Revert: evaluating admission once with the request target puts this plan back on
        // RestartBothRequired and writes two pending files.
    }

    [Fact]
    public void Both_with_a_both_record_keeps_both_halves_apply_now_as_before()
    {
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "Both"));
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.Both, Zip(_f.Valid));

        Assert.Equal(InstallDecision.ApplyNow, plan.Decision);
        Assert.False(plan.IsMixed);
        Assert.Equal(InstallDecision.ApplyNow, Half(plan, InstallTarget.Client).Decision);
        Assert.Equal(InstallDecision.ApplyNow, Half(plan, InstallTarget.LocalServer).Decision);
        // The server half is still not this process, so it has no executor and says so.
        Assert.False(Half(plan, InstallTarget.LocalServer).ExecutorAvailable);
        Assert.Contains(InstallReasons.HotLoadNotThisProcess, Half(plan, InstallTarget.LocalServer).Reason);
        Assert.Contains(InstallReasons.HotLoadServerHalfPending, plan.Reason);
        // Revert: dropping the Both-covers-either-half rule makes a reviewed Both record match neither.
    }

    [Fact]
    public void Both_with_no_record_still_pends_both_halves()
    {
        // HOTDEMO1: a clean ProgenitorPlugin is trusted on its hotLoadable flag now, so the
        // record rules are pinned with a plain BepInEx plugin, which still needs a record.
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: "{\"schema\":1,\"records\":[]}");
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.Both, Zip(_f.PlainBasePlugin));

        Assert.Equal(InstallDecision.RestartBothRequired, plan.Decision);
        Assert.False(plan.ExecutorAvailable);
        Assert.False(plan.IsMixed);
        Assert.Equal(InstallDecision.RestartClientRequired, Half(plan, InstallTarget.Client).Decision);
        Assert.Equal(InstallDecision.RestartServerRequired, Half(plan, InstallTarget.LocalServer).Decision);

        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.False(executed.HotLoaded);
        Assert.False(File.Exists(ReloadFile(_root)), "an unqualified image reached the reloader's folder");
        Assert.NotNull(executed.ServerPendingPath);
    }

    [Fact]
    public void A_client_record_never_covers_a_local_server_only_install()
    {
        // HOTDEMO1: a clean ProgenitorPlugin is trusted on its hotLoadable flag now, so the
        // record rules are pinned with a plain BepInEx plugin, which still needs a record.
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.PlainBasePlugin, "Client"));
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.LocalServer, Zip(_f.PlainBasePlugin));
        Assert.Equal(InstallDecision.RestartServerRequired, plan.Decision);
        Assert.Contains("target", plan.Reason);
        // Revert: widening TargetCovers in the other direction admits a client review for the server.
    }

    /// <summary>HOT4: <c>install_local qualify target:Both</c> writes one record that covers each half.</summary>
    [Fact]
    public void One_record_authored_for_both_covers_the_client_and_the_server_half()
    {
        Assert.True(QualificationRecord.TargetCovers(InstallTarget.Both, InstallTarget.Client));
        Assert.True(QualificationRecord.TargetCovers(InstallTarget.Both, InstallTarget.LocalServer));
        Assert.True(QualificationRecord.TargetCovers(InstallTarget.Both, InstallTarget.Both));
        Assert.False(QualificationRecord.TargetCovers(InstallTarget.Client, InstallTarget.LocalServer));
        Assert.False(QualificationRecord.TargetCovers(InstallTarget.LocalServer, InstallTarget.Client));
        Assert.False(QualificationRecord.TargetCovers(InstallTarget.Client, InstallTarget.Both));

        string zip = Zip(_f.Valid);
        var authored = QualificationAuthoring.Append(
            _root, zip, InstallTarget.Both, "research/hot4.md", Identities(), "2026-09-12T00:00:00Z");
        Assert.True(authored.Ok, authored.Error);
        Assert.Equal(InstallTarget.Both, authored.Record!.Target);
        var reread = QualificationPolicy.LoadLocal(_root);
        Assert.Single(reread.Records);
        Assert.Equal(InstallTarget.Both, reread.Records[0].Target);
        // Revert: one record per half would need two rows and two ids; one Both row covers each here.
    }

    /// <summary>
    /// HOT4: the mixed caption and readback — the row is <c>installed / Remove</c> with no restart for
    /// the client half, and the drawer carries the second line for the server half.
    /// </summary>
    [Fact]
    public void A_mixed_install_reads_installed_with_a_server_half_line()
    {
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "Client"));
        snapshots.Snapshot = Live(server: true);
        string zip = Zip(_f.Valid);
        var plan = Approved(catalog, InstallTarget.Both, zip);
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);

        var pane = new Progenitor.Ui.InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => new[] { "Sample" };
        pane.SetInstalledQuery(InstalledState.Scan(_root, _serverRoot));

        var action = pane.ActionFor(entry!);
        Assert.Equal(Progenitor.Ui.InstallModsCopy.Installed, action.Label); // INSTALLUX2 item 2: no `new` tag any more
        Assert.False(action.Restart);
        Assert.Equal("installed", Progenitor.Ui.InstallModsPane.StateName(action.State));
        Assert.Equal(Progenitor.Ui.InstallModsCopy.NoRestartNeeded, Progenitor.Ui.InstallModsPane.HeadNoteText(action));
        Assert.Equal(Progenitor.Ui.InstallModsCopy.ServerHalfPending, pane.ServerHalfNote(entry!));

        var listing = pane.ListingToWidget(entry!);
        var halves = (Dictionary<string, object?>)listing["halves"]!;
        Assert.Equal(InstalledState.HalfLoaded, halves["client"]);
        Assert.Equal(InstalledState.HalfPending, halves["server"]);
        var ledger = (Dictionary<string, object?>)listing["ledger"]!;
        Assert.Equal(true, ledger["client"]);
        Assert.Equal(true, ledger["server"]);

        // plan.halves [{target, decision, executorAvailable, reason}]
        var rows = Progenitor.Ui.InstallModsPane.HalvesToWidget(plan);
        Assert.Equal(2, rows.Count);
        Assert.Equal("client", rows[0]["target"]);
        Assert.Equal(true, rows[0]["executorAvailable"]);
        Assert.Equal("localServer", rows[1]["target"]);
        Assert.Equal(false, rows[1]["executorAvailable"]);
        // Revert: a single-half plan readback leaves the server's restart invisible on this row.
    }

    /// <summary>
    /// HOT4 readback defect (live deploy #66): both roots held a ledgered pending row for the listing
    /// and both files were on disk, yet the row read <c>ledger {client false, server false}</c> with
    /// <c>files []</c> — the first-class restart route ledgers its row under the plugin id while the
    /// reader only accepted package keys. Remove now takes back both spellings as well.
    /// </summary>
    [Fact]
    public void A_pending_install_on_both_roots_reads_back_its_ledger_rows_and_files()
    {
        // HOTDEMO1: a clean ProgenitorPlugin is trusted on its hotLoadable flag now, so the
        // record rules are pinned with a plain BepInEx plugin, which still needs a record.
        SeedLedger(_root);
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: "{\"schema\":1,\"records\":[]}");
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.Both, Zip(_f.PlainBasePlugin));
        Assert.Equal(InstallDecision.RestartBothRequired, plan.Decision);
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);

        var query = InstalledState.Scan(_root, _serverRoot);
        var view = InstalledState.LedgerViewOf(entry!, query);
        Assert.True(view.Client, "the client ledger row did not read back");
        Assert.True(view.Server, "the server ledger row did not read back");
        var files = InstalledState.LedgerFilesFor(entry!, query);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Root == InstalledState.ClientRoot && f.Kind == InstalledState.PendingKind);
        Assert.Contains(files, f => f.Root == InstalledState.ServerRoot && f.Kind == InstalledState.PendingKind);
        var halves = InstalledState.HalvesOf(entry!, query, loadedHere: false);
        Assert.Equal(InstalledState.HalfPending, halves.Client);
        Assert.Equal(InstalledState.HalfPending, halves.Server);

        // Remove clears both pending rows, whichever spelling of the source they were written under.
        var removed = catalog.RemovePackage(Key);
        Assert.True(removed.Ok, removed.Message);
        Assert.False(removed.NoOp);
        Assert.Equal(2, removed.Removed.Count);
        Assert.Empty(InstalledState.LedgerFilesFor(entry!, InstalledState.Scan(_root, _serverRoot)));
        // Revert: matching ledger rows on the package key alone reads a real install back as none.
    }

    [Fact]
    public void Remove_of_a_mixed_install_hot_unloads_the_client_half_and_drops_the_server_pending_file()
    {
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "Client"));
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.Both, Zip(_f.Valid));
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        string serverPending = executed.ServerPendingPath!;
        Assert.True(File.Exists(ReloadFile(_root)) && File.Exists(serverPending));

        var removed = catalog.RemovePackage(Key);
        Assert.True(removed.Ok, removed.Message);
        Assert.False(removed.NoOp);
        Assert.True(removed.HotUnloaded, "the client half's reload copy was not recognized as a hot unload");
        Assert.False(File.Exists(ReloadFile(_root)), "the client half stayed loaded");
        Assert.False(File.Exists(serverPending), "the local server would still load the removed half at its next start");
        Assert.Empty(Ledger(_root));
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
        Assert.Equal(
            InstalledKind.NotInstalled,
            InstalledState.Detect(entry!, InstalledState.Scan(_root, _serverRoot)).State);
        // Revert: asking the ledger for one spelling of the source leaves the server half installed.
    }

    // ---- POLISH3 item A: one Yes acts on every half the file is on -----------------------------

    /// <summary>
    /// POLISH3 item A (live deploy #74). HotDemo was installed to Both: the client half hot loaded
    /// and the local server's went to <c>progenitor-pending/</c>. Uninstall then Yes unloaded the
    /// client copy and left the server's file, its ledger row and the Mods tab's <c>pendload</c>
    /// entry behind, because the confirmation had ONE client-shaped route and no per-half accounting:
    /// a half it failed to take was indistinguishable from a half that was not there.
    ///
    /// <para>Now the server half is its own decision — a downloaded copy is canceled, a promoted one
    /// is marked — the body says so before the press, and <c>removed.halves</c> names what each half
    /// actually got.</para>
    /// What this would catch: going back to a single whole-catalog removal (the readback would read
    /// <c>server none</c> while a server file was on disk), a Yes that reports success when the
    /// server root took nothing, or the body promising "nothing to restart" over a server copy.
    /// </summary>
    [Fact]
    public void Uninstalling_a_both_install_takes_the_client_half_and_the_servers_pending_copy_in_one_yes()
    {
        SeedLedger(_root);
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "Client"));
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.Both, Zip(_f.Valid));
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(executed.HotLoaded, "this fixture is the live one: the client half hot loads");
        string serverPending = executed.ServerPendingPath!;
        Assert.True(File.Exists(ReloadFile(_root)) && File.Exists(serverPending));
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);

        var asked = new List<string>();
        var pane = new Progenitor.Ui.InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => new[] { "Sample" };
        pane.InventoryRefresh = asked.Add;
        pane.SetInstalledQuery(InstalledState.Scan(_root, _serverRoot));

        // The halves the live row had: client loaded, server a downloaded copy.
        var halves = pane.HalvesOf(entry!);
        Assert.Equal(InstalledState.HalfLoaded, halves.Client);
        Assert.Equal(InstalledState.HalfPending, halves.Server);

        // The body names BOTH halves before the press, not "nothing to restart".
        Assert.True(pane.OnInstallClick(Key));
        Assert.Equal(Progenitor.Ui.InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal(
            Progenitor.Ui.InstallModsCopy.UninstallHotServerPendingBody + " "
                + Progenitor.Ui.InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry!, Progenitor.Ui.InstallPromptKind.Uninstall));

        Assert.True(pane.PromptYes());

        // Both halves are gone off disk and out of both ledgers, from the one Yes.
        Assert.False(File.Exists(ReloadFile(_root)), "the client half stayed loaded");
        Assert.False(File.Exists(serverPending), "the local server would still load it at its next start");
        Assert.Empty(Ledger(_root));
        Assert.Empty(Ledger(_serverRoot));

        // readback removed.halves {client, server}
        var removed = (Dictionary<string, object?>)pane.ActionToWidget(entry!)["removed"]!;
        var reported = (Dictionary<string, object?>)removed["halves"]!;
        Assert.Equal(Progenitor.Ui.InstallModsPane.UninstallHalfUnloaded, reported["client"]);
        Assert.Equal(Progenitor.Ui.InstallModsPane.UninstallHalfCancelled, reported["server"]);
        Assert.Equal(true, removed["ok"]);

        // The Mods tab is told, so its sidebar stops listing the mod without reopening the workspace.
        Assert.Contains(Progenitor.Ui.InstallModsPane.InventoryReasonUninstalled, asked);

        pane.SetInstalledQuery(InstalledState.Scan(_root, _serverRoot));
        var after = pane.ActionFor(entry!);
        Assert.Equal("none", Progenitor.Ui.InstallModsPane.StateName(after.State));
        Assert.Equal(InstalledState.HalfNone, pane.HalvesOf(entry!).Server);
        // Revert: removing the server-half branch from ConfirmUninstall leaves serverPending on disk
        // and reports `server none` here.
    }

    /// <summary>
    /// POLISH3 item A, the other half-shape: a promoted copy the local server has LOADED cannot be
    /// taken from under a running server, so that half is marked for its next start while the
    /// client's hot half still unloads now. The body says both.
    /// What this would catch: deleting a running server's promoted assembly, or marking the client
    /// half that could simply be unloaded.
    /// </summary>
    [Fact]
    public void A_promoted_server_half_is_marked_while_the_client_half_unloads_now()
    {
        SeedLedger(_root);
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: RecordJson(_f.Valid, "Client"));
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.Both, Zip(_f.Valid));
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        string serverPending = executed.ServerPendingPath!;

        // The local server restarted: Bootstrap promoted the pending copy into progenitor-reload/ and
        // the reconcile rewrote the row as `mod` (POLISH1 defect C).
        string promoted = Path.Combine(
            _serverRoot, ModFolders.ReloadFolderName, Path.GetFileName(serverPending));
        Directory.CreateDirectory(Path.GetDirectoryName(promoted)!);
        File.Copy(serverPending, promoted, true);
        File.Delete(serverPending);
        ServerInstall.ReconcilePromoted(_serverRoot);

        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
        var pane = new Progenitor.Ui.InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => new[] { "Sample" };
        pane.SetInstalledQuery(InstalledState.Scan(_root, _serverRoot));
        Assert.Equal(InstalledState.HalfLoaded, pane.HalvesOf(entry!).Server);

        Assert.True(pane.OnInstallClick(Key));
        Assert.Equal(
            Progenitor.Ui.InstallModsCopy.UninstallHotServerLoadedBody + " "
                + Progenitor.Ui.InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry!, Progenitor.Ui.InstallPromptKind.Uninstall));
        Assert.True(pane.PromptYes());

        // POLISH3 item A, the spelling. POLISH4 item 1 changed what is written: the restart route now
        // ledgers the local server's row under the LISTING KEY, like the hot and ordinary routes, and
        // records the plugin id in the row's own `pluginId` field. The mark still reaches the row
        // through LedgerSpellingsOf — which now also accepts the old plugin-id spelling, so a row an
        // earlier build wrote is reached too (POLISH4 item 2).
        var serverRow = Ledger(_serverRoot).Single();
        Assert.Contains(Key, serverRow.Source);
        Assert.DoesNotContain("example.sample", serverRow.Source);
        Assert.Equal("example.sample", serverRow.PluginId);

        Assert.False(File.Exists(ReloadFile(_root)), "the client half stayed loaded");
        Assert.True(File.Exists(promoted), "the running local server's assembly was deleted under it");
        Assert.NotEmpty(UninstallMarkers.PendingSources(_serverRoot));
        var reported = (Dictionary<string, object?>)((Dictionary<string, object?>)
            pane.ActionToWidget(entry!)["removed"]!)["halves"]!;
        Assert.Equal(Progenitor.Ui.InstallModsPane.UninstallHalfUnloaded, reported["client"]);
        Assert.Equal(Progenitor.Ui.InstallModsPane.UninstallHalfMarked, reported["server"]);
        // Revert: routing a loaded server half through the removal deletes `promoted` here.
    }

    /// <summary>
    /// POLISH3 item E: the <c>Install · restart</c> caption never appears on a row whose only half is
    /// the local server's pending copy — that row is <c>pending</c>, so the restart note is carried
    /// by the status word (<c>Installed · restart to load</c>) and the button beside it is Cancel.
    /// POLISH1's <c>pending.sides</c> reads <c>server</c> for it and the bar names the local server.
    /// What this would catch: a state-None caption leaking onto a row that has a pending half (which
    /// is what the live row showed after the uninstall took only the client half), or the pending
    /// status word losing its restart note.
    /// </summary>
    [Fact]
    public void A_row_whose_only_pending_half_is_the_server_says_installed_restart_to_load()
    {
        SeedLedger(_serverRoot);
        var (catalog, snapshots) = Service(
            localServer: true, serverRoot: _serverRoot, policyJson: "{\"schema\":1,\"records\":[]}");
        snapshots.Snapshot = Live(server: true);
        var plan = Approved(catalog, InstallTarget.LocalServer, Zip(_f.Valid));
        var executed = catalog.ExecuteToPending(plan.PlanHash);
        Assert.True(executed.Ok, executed.Error);
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);

        var pane = new Progenitor.Ui.InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.SetInstalledQuery(InstalledState.Scan(_root, _serverRoot));

        var halves = pane.HalvesOf(entry!);
        Assert.Equal(InstalledState.HalfNone, halves.Client);
        Assert.Equal(InstalledState.HalfPending, halves.Server);

        var action = pane.ActionFor(entry!);
        Assert.Equal(Progenitor.Ui.InstallListingState.Pending, action.State);
        // The restart note lives on the status word, and no caption on this row reads Install · restart.
        // REMOTEINST1: a copy only the server holds names the route that wrote it.
        Assert.Equal(
            ServerRouteCopy.PendingLocal,
            Progenitor.Ui.InstallModsPane.RowStatusText(action));
        Assert.Equal(
            Progenitor.Ui.InstallModsCopy.CancelInstallButton,
            Progenitor.Ui.InstallModsPane.RowButtonText(action));
        Assert.NotEqual(
            Progenitor.Ui.InstallModsCopy.InstallRestart,
            Progenitor.Ui.InstallModsPane.RowStatusText(action));
        Assert.NotEqual(
            Progenitor.Ui.InstallModsCopy.InstallRestart,
            Progenitor.Ui.InstallModsPane.RowButtonText(action));
        Assert.Equal(new[] { "cancel" }, Progenitor.Ui.InstallModsPane.RowActionNames(action));

        // POLISH1's side rule: the restart owed is the local server's, and the bar says so.
        Assert.Equal(Progenitor.Ui.InstallModsCopy.SidesServer, pane.PendingSides());
        Assert.Contains(
            Progenitor.Ui.InstallModsCopy.RestartLocalServer,
            Progenitor.Ui.InstallModsCopy.PendingBar(1, 0, pane.PendingSides()));
        // Revert: letting the state-None branch answer a row with a pending half brings
        // `Install · restart` back onto it.
    }
}
