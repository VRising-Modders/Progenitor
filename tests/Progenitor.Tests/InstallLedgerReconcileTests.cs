using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// INSTORD2: after Bootstrap promotes an Install-tab package the install ledger still names the
/// pending path, and nothing in this session knows the promoted DLL's sha. These facts cover the
/// reconcile that rewrites those rows, detection that reads the ledger instead of a session sha,
/// the pane captions and readback that follow, and Remove over a promoted copy. Each fact names
/// the guard whose removal must fail it.
/// </summary>
public sealed class InstallLedgerReconcileTests : IDisposable
{
    private readonly string _game;

    public InstallLedgerReconcileTests()
    {
        _game = Path.Combine(Path.GetTempPath(), "progenitor-instord2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx"));
    }

    public void Dispose()
    {
        // INSTALLUX2 item 1: the confirmation is a modal dialog on its own panel now. Ui.ResetForTests
        // only FORGETS the open one, so without this its rows outlive the test and the next test's
        // click on prompt:<key> resolves to the dead one.
        Progenitor.Ui.UiDialog.Current?.Close();
        try { Directory.Delete(_game, true); } catch { }
    }

    private string Bep => Path.Combine(_game, "BepInEx");
    private const string Source = "package:ExampleTeam-PhialStore@1.0.0";

    [Fact]
    public void Reconcile_rewrites_a_promoted_row_and_drops_one_with_no_reload_copy()
    {
        byte[] promoted = { 1, 2, 3, 4 };
        string promotedSha = InstalledState.HashBytes(promoted);
        string reload = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), promoted);
        string stillPending = Write(Path.Combine(Bep, InstalledState.PendingFolder, "index", "Other.dll"), new byte[] { 9 });
        string framework = Write(Path.Combine(Bep, "plugins", "Progenitor.Bootstrap.dll"), new byte[] { 7 });

        var ledger = new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", promotedSha, 4, ServerInstallLedger.CategoryPending, Source),
            Row("BepInEx/progenitor-pending/index/Lost.dll", InstalledState.HashBytes(new byte[] { 42 }), 1, ServerInstallLedger.CategoryPending, "package:Some-Lost@1.0.0"),
            Row("BepInEx/progenitor-pending/index/Other.dll", InstalledState.HashBytes(new byte[] { 9 }), 1, ServerInstallLedger.CategoryPending, "package:Some-Other@1.0.0"),
            Row("BepInEx/plugins/Progenitor.Bootstrap.dll", InstalledState.HashBytes(new byte[] { 7 }), 1, ServerInstallLedger.CategoryFramework, "client-image:b"),
        });
        ledger.Write(_game);

        var result = ServerInstall.ReconcilePromoted(Bep);
        Assert.Contains("BepInEx/progenitor-reload/PhialStore.dll", result.Rewritten);
        Assert.Contains("BepInEx/progenitor-pending/index/Lost.dll", result.Dropped);
        Assert.True(result.Changed);

        var after = ServerInstallLedger.TryRead(_game);
        Assert.NotNull(after);
        var rows = new Dictionary<string, ServerInstallLedgerEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in after!.Files) rows[row.Path] = row;
        Assert.False(rows.ContainsKey("BepInEx/progenitor-pending/index/PhialStore.dll"));
        Assert.False(rows.ContainsKey("BepInEx/progenitor-pending/index/Lost.dll"));
        var moved = rows["BepInEx/progenitor-reload/PhialStore.dll"];
        Assert.Equal(ServerInstallLedger.CategoryMod, moved.Category);
        Assert.Equal(Source, moved.Source);
        Assert.Equal(promotedSha, moved.Sha256);
        Assert.Equal(4, moved.Size);
        // A pending row whose file is still there, and the framework rows, are untouched.
        Assert.Equal(ServerInstallLedger.CategoryPending, rows["BepInEx/progenitor-pending/index/Other.dll"].Category);
        Assert.Equal(ServerInstallLedger.CategoryFramework, rows["BepInEx/plugins/Progenitor.Bootstrap.dll"].Category);
        Assert.True(File.Exists(reload) && File.Exists(stillPending) && File.Exists(framework));

        // A second pass has nothing left to do.
        Assert.False(ServerInstall.ReconcilePromoted(Bep).Changed);
        // Revert: keeping the pending path, category pending, or dropping a row whose file exists fails this.
    }

    [Fact]
    public void Detect_reads_the_install_ledger_for_client_loaded_promoted_and_server_copies()
    {
        byte[] bytes = { 5, 5, 5 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, 3, ServerInstallLedger.CategoryMod, Source),
        }).Write(_game);

        var entry = Listing("1.0.0");
        var scanned = InstalledState.Scan(Bep, null, null, "44");

        // Client root, inventory does not name it yet: promoted, loads at the next start.
        var promoted = InstalledState.Detect(entry, scanned);
        Assert.Equal(InstalledKind.Pending, promoted.State);
        Assert.Equal(InstalledState.ByProgenitor, promoted.By);
        Assert.Equal(InstalledState.PromotedReason, promoted.Reason);
        Assert.Equal("1.0.0", promoted.LoadedVersion);
        Assert.Single(promoted.Files);

        // Same root, the inventory names the plugin: loaded.
        var withInventory = InstalledState.Scan(
            Bep,
            null,
            new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") },
            "44");
        var loaded = InstalledState.Detect(entry, withInventory);
        Assert.Equal(InstalledKind.Loaded, loaded.State);
        Assert.Equal(InstalledState.LoadedReason("44"), loaded.Reason);

        // POLISH1 defect C (replaces the INSTORD2 expectation two lines down): a reconciled `mod` row
        // naming a reload copy on the local-server root means that server has already started with it,
        // so it is installed here, not pending. A `pending` row whose file moved is covered below.
        string serverGame = Path.Combine(_game, "server");
        string serverBep = Path.Combine(serverGame, "BepInEx");
        Directory.CreateDirectory(serverBep);
        Write(Path.Combine(serverBep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, 3, ServerInstallLedger.CategoryMod, Source),
        }).Write(serverGame);
        var serverOnly = InstalledState.Detect(entry, InstalledState.Scan(null, serverBep, null, "44"));
        Assert.Equal(InstalledKind.Loaded, serverOnly.State);
        Assert.Equal(InstalledState.PromotedServerLoadedReason, serverOnly.Reason);
        // ... and it is never *hot*: this process does not load that root.
        Assert.False(serverOnly.HotLoaded);
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfLoaded),
            InstalledState.HalvesOf(entry, InstalledState.Scan(null, serverBep, null, "44"), loadedHere: false));

        // POLISH1 defect C: before anything reconciles that root the row still says `pending` and
        // names the file the promote moved, so the half is pending and the bar still counts it. This
        // is the state the live client was stuck in for a whole session.
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", sha, 3, ServerInstallLedger.CategoryPending, Source),
        }).Write(serverGame);
        var serverUnreconciled = InstalledState.Scan(null, serverBep, null, "44");
        Assert.Equal(InstalledKind.Pending, InstalledState.Detect(entry, serverUnreconciled).State);
        Assert.Equal(InstalledState.PromotedServerReason, InstalledState.Detect(entry, serverUnreconciled).Reason);
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfPending),
            InstalledState.HalvesOf(entry, serverUnreconciled, loadedHere: false));
        // Restore the reconciled ledger for the rest of the case.
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, 3, ServerInstallLedger.CategoryMod, Source),
        }).Write(serverGame);

        // A newer listing than the ledger row is an update, on the same files.
        var update = InstalledState.Detect(Listing("2.0.0"), withInventory);
        Assert.Equal(InstalledKind.UpdateAvailable, update.State);
        Assert.Equal("1.0.0", update.LoadedVersion);

        // A ledger row whose file is gone does not claim the listing.
        File.Delete(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"));
        Assert.Equal(InstalledKind.NotInstalled, InstalledState.Detect(entry, InstalledState.Scan(Bep, null)).State);
        // Revert: dropping the progenitor-install.json read from Scan, or matching only by the
        // listing's sha, turns the loaded row back into managed-elsewhere and the server row into none.
    }

    [Fact]
    public void Pane_captions_and_readback_follow_the_ledger_view()
    {
        byte[] bytes = { 6, 6 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, 2, ServerInstallLedger.CategoryMod, Source),
        }).Write(_game);

        var entry = Listing("1.0.0");
        var catalog = new LedgerCatalog();
        catalog.Entries.Add(entry);
        var pane = new InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;

        pane.SetInstalledQuery(InstalledState.Scan(
            Bep,
            null,
            new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") },
            "44"));
        // INSTALL2 item 2: a loaded, ledgered copy is green and says Installed; what removing it
        // costs is the confirmation's sentence, and the row keeps carrying the restart fact.
        var loadedAction = pane.ActionFor(entry);
        Assert.Equal(InstallModsCopy.Installed, loadedAction.Label);
        Assert.Equal(CellTone.Ok, loadedAction.Tone);
        Assert.True(loadedAction.Enabled);
        Assert.True(loadedAction.Restart);

        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        var promotedAction = pane.ActionFor(entry);
        Assert.Equal(InstallModsCopy.InstalledPending, promotedAction.Label);
        Assert.Equal("pending", InstallModsPane.StateName(promotedAction.State));

        var listing = pane.ListingToWidget(entry);
        var ledgerView = Assert.IsType<Dictionary<string, object?>>(listing["ledger"]);
        Assert.Equal(true, ledgerView["client"]);
        Assert.Equal(false, ledgerView["server"]);
        Assert.Equal("1.0.0", ledgerView["version"]);
        var files = Assert.IsType<List<Dictionary<string, object?>>>(listing["files"]);
        Assert.Single(files);
        Assert.Equal(InstalledState.ReloadKind, files[0]["kind"]);
        Assert.Equal(InstalledState.ClientRoot, files[0]["root"]);

        // Without the ledger probe the row is another tool's and never offers Remove.
        pane.LedgerProbe = null;
        Assert.Equal(InstallModsCopy.InstalledPending, pane.ActionFor(entry).Label);
        // Revert: gating Remove on a session sha, or dropping listing.ledger/files, fails this.
    }

    [Fact]
    public void Remove_after_a_promote_deletes_the_reload_copy_and_the_row()
    {
        byte[] bytes = { 8, 8, 8 };
        string sha = InstalledState.HashBytes(bytes);
        string reload = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", sha, 3, ServerInstallLedger.CategoryPending, Source),
            Row("BepInEx/plugins/Progenitor.Bootstrap.dll", "aa", 1, ServerInstallLedger.CategoryFramework, "client-image:b"),
        }).Write(_game);

        Assert.True(ServerInstall.ReconcilePromoted(Bep).Changed);
        // The listing has moved on to 1.0.1: Remove still has to reach the 1.0.0 row the pane
        // offered Remove for (LedgerHas matches any version).
        var result = ServerInstall.RemovePackage(Bep, "package:ExampleTeam-PhialStore@1.0.1");
        Assert.True(result.Ok);
        Assert.False(result.NoOp);
        Assert.Empty(result.Skipped);
        Assert.Contains("BepInEx/progenitor-reload/PhialStore.dll", result.Removed);
        Assert.False(File.Exists(reload));

        var after = ServerInstallLedger.TryRead(_game);
        Assert.NotNull(after);
        Assert.Single(after!.Files);
        Assert.Equal(ServerInstallLedger.CategoryFramework, after.Files[0].Category);

        var entry = Listing("1.0.1");
        Assert.Equal(InstalledKind.NotInstalled, InstalledState.Detect(entry, InstalledState.Scan(Bep, null)).State);
        // Revert: matching the row source with the version attached, or leaving the reload copy,
        // fails this — the row would keep reading Remove with nothing left to remove.
    }

    /// <summary>
    /// Job CONF2, as INSTALL2 item 3 replaced it: removal is two presses, not one. The row's own
    /// action button opens the y/n box for the state it is in — a written-but-unloaded copy asks
    /// <c>Cancel installing …?</c> — and only Yes reaches the remover. What this would catch: a
    /// button that removes on the press (the behavior the approved mock replaced), a caption falling
    /// through to the install path's no-op, or a Yes that reaches the remover twice.
    /// </summary>
    [Fact]
    public void Remove_from_the_row_is_one_click()
    {
        byte[] bytes = { 7, 7 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, 2, ServerInstallLedger.CategoryMod, Source),
        }).Write(_game);

        var entry = Listing("1.0.0");
        var catalog = new LedgerCatalog();
        catalog.Entries.Add(entry);
        var pane = new InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        Assert.Equal(InstallModsCopy.InstalledPending, pane.ActionFor(entry).Label);
        Assert.Equal(CellTone.Pending, pane.ActionFor(entry).Tone);

        int calls = 0;
        pane.Remover = _ =>
        {
            calls++;
            return new ServerInstallResult(true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, Array.Empty<string>());
        };

        // The click the drawer and the row both raise opens the box; nothing is removed yet.
        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(0, calls);
        Assert.Equal(InstallPromptKind.CancelInstall, pane.Prompt);
        Assert.Equal(entry.Key, pane.PromptKey);
        Assert.Equal(InstallModsCopy.CancelInstallBody, pane.PromptBodyText(entry, InstallPromptKind.CancelInstall));
        Assert.Equal(InstallModsCopy.PromptYesCancel, pane.PromptYesText(InstallPromptKind.CancelInstall));
        // No leaves it alone.
        Assert.True(pane.PromptNo());
        Assert.Equal(0, calls);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        // Yes is the removal, once.
        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.True(pane.PromptYes());
        Assert.Equal(1, calls);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.False(pane.TargetChooserOpen);
        Assert.Equal(InstallActionPhase.Idle, pane.Phase);
        Assert.Null(pane.ActiveDownload);
        Assert.NotNull(pane.LastRemoveResult);
        Assert.True(pane.LastRemoveResult!.Ok);
    }

    [Fact]
    public void Resting_identity_answers_the_drawer_fact_and_the_action_readback()
    {
        var cache = new OrdinaryIdentityCache(Bep);
        Assert.True(cache.Write(new OrdinaryIdentityRecord(
            "ExampleTeam", "PhialStore", "1.0.0", "example.phialstore", "1.0.0", "abcd", "the server")));
        var round = cache.TryRead("ExampleTeam", "PhialStore", "1.0.0");
        Assert.NotNull(round);
        Assert.Equal("the server", round!.LoadsIn);

        var entry = Listing("1.0.0");
        var catalog = new LedgerCatalog();
        catalog.Entries.Add(entry);
        var pane = new InstallModsPane(catalog);
        pane.IdentityProbe = e => cache.TryRead(e.Owner ?? "", e.PackageName ?? "", e.Manifest.Version ?? "");

        string fact = pane.IdentityFact(entry);
        Assert.Contains("example.phialstore", fact, StringComparison.Ordinal);
        Assert.Contains("the server", fact, StringComparison.Ordinal);

        var action = pane.ActionToWidget(entry);
        var identity = Assert.IsType<Dictionary<string, object?>>(action["identity"]);
        Assert.Equal("example.phialstore", identity["pluginGuid"]);
        Assert.Equal("the server", identity["loadsIn"]);

        // An uncached package stays silent rather than guessing a GUID from the name.
        pane.IdentityProbe = _ => null;
        Assert.Equal("", pane.IdentityFact(entry));
        Assert.Null(pane.ActionToWidget(entry)["identity"]);
        // Revert: dropping loadsIn from the identity cache, or filling the drawer from the package
        // name, fails this.
    }

    // ---- HOT3: row and inventory state around a hot-loaded plugin ----------------------------

    /// <summary>
    /// HOT3 defect 1. A ledgered <c>progenitor-reload/</c> copy the reloader reports loaded is
    /// <c>installed</c>, and its Remove costs no restart. What this would catch: detection that only
    /// believes the Mods-tab inventory snapshot the pane was built with — the live defect, where the
    /// row read <c>pending</c> for a seconds-old hot-loaded plugin — or a caption that still promises
    /// a restart for an assembly the reloader can drop on its own.
    /// </summary>
    [Fact]
    public void A_reload_copy_the_reloader_reports_loaded_is_installed_and_removes_without_a_restart()
    {
        byte[] bytes = { 3, 1, 4 };
        Ledgered(bytes);
        var entry = Listing("1.0.0");
        var scanned = InstalledState.Scan(Bep, null, null, "44");

        // No reloader report and no inventory row: the promoted copy still reads pending (HOT1).
        Assert.Equal(InstalledKind.Pending, InstalledState.Detect(entry, scanned).State);
        Assert.False(InstalledState.Detect(entry, scanned).HotLoaded);

        // The reloader names it by its BepInEx plugin name, which is how the live log names it.
        var loaded = InstalledState.Detect(entry, scanned, new[] { "Phial Store" });
        Assert.Equal(InstalledKind.Loaded, loaded.State);
        Assert.Equal("loaded", loaded.StateName);
        Assert.True(loaded.HotLoaded);

        var pane = Pane(entry);
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        pane.SetInstalledQuery(scanned);

        // INSTALL2 defect A (live, deploy #69): the reloader's report is what makes the row
        // `installed` rather than `pending` — that is this fact — but it is NOT what makes it hot. The
        // reloader loads every DLL in its folder, so without the executor's own `hot` ledger flag this
        // is a promoted ordinary mod: green, no `new` tag, and a removal that owes a restart.
        var promoted = pane.ActionFor(entry);
        Assert.Equal("installed", InstallModsPane.StateName(promoted.State));
        Assert.Equal(InstallModsCopy.Installed, promoted.Label);
        Assert.True(promoted.Restart);

        // With the flag — a copy THIS process wrote into progenitor-reload/ — it is hot and owes none.
        pane.HotLedgerProbe = _ => true;
        var action = pane.ActionFor(entry);
        Assert.Equal(InstallModsCopy.Installed, action.Label); // INSTALLUX2 item 2: no `new` tag any more
        Assert.False(action.Restart);
        Assert.Equal("installed", InstallModsPane.StateName(action.State));
        Assert.True(action.Enabled);

        var listing = pane.ListingToWidget(entry);
        Assert.Equal("installed", listing["state"]);
        Assert.Equal(true, listing["hotLoaded"]);
        Assert.Equal(false, listing["restart"]);

        // A copy Bootstrap promoted and BepInEx loaded at start: the inventory knows it, the reloader
        // does not own it, and nothing in this process can unload it.
        var atStart = Pane(entry);
        atStart.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        var promotedAction = atStart.ActionFor(entry);
        Assert.Equal(InstallModsCopy.Installed, promotedAction.Label);
        Assert.True(promotedAction.Restart);
        Assert.Equal("installed", InstallModsPane.StateName(promotedAction.State));
        Assert.Equal(false, atStart.ListingToWidget(entry)["hotLoaded"]);
        // Revert: reading Loaded from the inventory alone, or dropping InstalledStateResult.HotLoaded,
        // fails this — the row goes back to pending / Remove-restart-to-unload while the plugin runs.
    }

    /// <summary>
    /// HOT3 defect 2. A Remove the reloader answered with an unload leaves nothing loaded, so the row
    /// is <c>none / Install</c> at once; a restart-route removal keeps the <c>removed</c> state and its
    /// sentence. What this would catch: adding every removal to SessionRemoved, which is the live
    /// defect ("Removed — still loaded until this process restarts" after a clean hot unload).
    /// </summary>
    [Fact]
    public void A_hot_unload_returns_the_row_to_none_and_a_restart_route_remove_still_reads_removed()
    {
        byte[] bytes = { 2, 7 };
        string reload = Ledgered(bytes);
        var entry = Listing("1.0.0");

        var pane = Pane(entry);
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        // INSTALL2 defect A: a copy this process hot loaded is the executor's `hot` ledger flag.
        pane.HotLedgerProbe = _ => true;
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        Assert.Equal(InstallModsCopy.Installed, pane.ActionFor(entry).Label); // INSTALLUX2 item 2: no `new` tag any more

        pane.Remover = _ =>
        {
            File.Delete(reload);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" },
                Array.Empty<string>(), noOp: false, hotUnloaded: true);
        };
        Assert.True(pane.OnRemoveClick(entry.Key));
        Assert.True(pane.LastRemoveResult!.HotUnloaded);

        // The reloader dropped the assembly with the file, so the next read is simply not installed.
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        var after = pane.ActionFor(entry);
        Assert.Equal("none", InstallModsPane.StateName(after.State));
        Assert.Equal(InstallModsCopy.Install, after.Label);
        Assert.True(after.Enabled);
        Assert.Equal("none", pane.ListingToWidget(entry)["state"]);

        // The restart route: a promoted copy this process cannot unload keeps the removed state.
        string second = Ledgered(bytes);
        var restart = Pane(entry);
        restart.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        Assert.Equal(InstallModsCopy.Installed, restart.ActionFor(entry).Label);
        restart.Remover = _ =>
        {
            File.Delete(second);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, Array.Empty<string>());
        };
        Assert.True(restart.OnRemoveClick(entry.Key));
        var removedAction = restart.ActionFor(entry);
        Assert.Equal("removed", InstallModsPane.StateName(removedAction.State));
        Assert.Equal(InstallModsCopy.RemovedNeedsRestart, removedAction.Label);
        // Revert: dropping the HotUnloaded check in OnRemoveClick fails the first half; dropping
        // SessionRemoved altogether fails the second.
    }

    /// <summary>
    /// HOT3: the reloader answers plugin names, not package keys, so the match is over letters and
    /// digits and only equality or a name that ends with the listing's own identity counts. What this
    /// would catch: a bare substring match, where a listing called "Pool" would claim another
    /// plugin's loaded row, or a case/space-sensitive compare that never matches at all.
    /// </summary>
    [Fact]
    public void The_reloader_report_is_matched_on_letters_and_digits_and_never_on_a_bare_substring()
    {
        var entry = Listing("1.0.0");
        Assert.True(InstalledState.ReloaderReportsLoaded(entry, new[] { "phialstore" }));
        Assert.True(InstalledState.ReloaderReportsLoaded(entry, new[] { "Progenitor Phial-Store" }));
        Assert.False(InstalledState.ReloaderReportsLoaded(entry, new[] { "Store" }));
        Assert.False(InstalledState.ReloaderReportsLoaded(entry, new[] { "Phial Store Extras" }));
        Assert.False(InstalledState.ReloaderReportsLoaded(entry, Array.Empty<string>()));
        Assert.False(InstalledState.ReloaderReportsLoaded(entry, null));

        // The plugin GUID a prior inspection cached is an identity too.
        Assert.True(InstalledState.ReloaderReportsLoaded(
            entry, new[] { "Progenitor example.phialstore.demo" }, "example.phialstore.demo"));
    }

    /// <summary>
    /// HOT3 defect 3. A hot install or hot unload asks for a fresh inventory snapshot, and
    /// <see cref="ModMenuInventory.Snapshot"/> is that retake. What this would catch: the live defect
    /// where the Mods tab, selected seconds after the hot load in the same workspace, did not list the
    /// plugin at all; and a restart-route remove that needlessly rebuilt the list.
    /// </summary>
    [Fact]
    public void A_hot_change_asks_for_a_fresh_inventory_snapshot_and_a_restart_route_one_does_not()
    {
        byte[] bytes = { 5, 9 };
        string reload = Ledgered(bytes);
        var entry = Listing("1.0.0");
        var pane = Pane(entry);
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));

        var asked = new List<string>();
        pane.InventoryRefresh = asked.Add;
        pane.Remover = _ =>
        {
            File.Delete(reload);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" },
                Array.Empty<string>(), noOp: false, hotUnloaded: true);
        };
        Assert.True(pane.OnRemoveClick(entry.Key));
        Assert.Equal(new[] { InstallModsPane.InventoryReasonHotUnloaded }, asked);

        // A restart-route removal changes nothing this process has loaded.
        string second = Ledgered(bytes);
        var restart = Pane(entry);
        restart.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        var quiet = new List<string>();
        restart.InventoryRefresh = quiet.Add;
        restart.Remover = _ =>
        {
            File.Delete(second);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, Array.Empty<string>());
        };
        Assert.True(restart.OnRemoveClick(entry.Key));
        Assert.Empty(quiet);

        // The owed retake, and the snapshot that answers it. Parallelization is disabled
        // assembly-wide (AssemblyCollectionBehavior.cs), so this process-wide flag is ours.
        ModMenuInventory.ResetForTests();
        Assert.False(ModMenuInventory.RefreshOwed);
        ModMenuInventory.Invalidate(ModMenuInventory.RefreshReasonHotLoaded);
        Assert.True(ModMenuInventory.RefreshOwed);
        int before = ModMenuInventory.SnapshotsTaken;
        _ = ModMenuInventory.Snapshot();
        Assert.False(ModMenuInventory.RefreshOwed);
        Assert.Equal(before + 1, ModMenuInventory.SnapshotsTaken);

        // A tab switch is also a retake: ModsMenu marks it owed and the tab's build clears it.
        ModMenuInventory.Invalidate(ModMenuInventory.RefreshReasonTabSelect);
        Assert.True(ModMenuInventory.RefreshOwed);
        _ = ModMenuInventory.Snapshot();
        Assert.False(ModMenuInventory.RefreshOwed);
        ModMenuInventory.ResetForTests();
        // Revert: dropping the pane's InventoryRefresh hook, or having Snapshot leave the flag set,
        // fails this.
    }

    /// <summary>
    /// HOT3 live defect (deploy #65, client gen 2). The reloader unloads about a second after the file
    /// goes and the Mods inventory snapshot the pane was built with still names the plugin, so the
    /// rebuild that followed the Remove read that stale evidence back and the row stood at
    /// <c>installed / Installed</c> for the rest of the session. A hot-unloaded listing with nothing of
    /// it on disk reads <c>none / Install</c> at once, and one re-check is scheduled through the pane's
    /// delayed marshaler for any other stale read. What this would catch: dropping either half — the
    /// session record (the row goes back to Installed the moment a rebuild runs before the unload) or
    /// the re-check (a stale read at rebuild time is never corrected).
    /// </summary>
    [Fact]
    public void After_a_hot_unload_a_stale_probe_cannot_make_the_row_installed_again_and_one_recheck_is_scheduled()
    {
        byte[] bytes = { 4, 4 };
        string reload = Ledgered(bytes);
        var entry = Listing("1.0.0");
        var pane = Pane(entry);

        // The stale evidence the live client had at the rebuild: the reloader still reports the plugin
        // and the inventory snapshot still names it, while the file and its ledger row are gone.
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        // INSTALL2 defect A: hot is the executor's `hot` ledger flag, so this copy carries it.
        pane.HotLedgerProbe = _ => true;
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        Assert.Equal(InstallModsCopy.Installed, pane.ActionFor(entry).Label); // INSTALLUX2 item 2: no `new` tag any more

        var scheduled = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => scheduled.Add((seconds, action));
        int rebuilds = 0;
        pane.SetDetailRebuild(() => rebuilds++);
        pane.Remover = _ =>
        {
            File.Delete(reload);
            new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, Array.Empty<ServerInstallLedgerEntry>()).Write(_game);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" },
                Array.Empty<string>(), noOp: false, hotUnloaded: true);
        };
        Assert.True(pane.OnRemoveClick(entry.Key));

        // The pane was rebuilt with the pre-unload probe and a snapshot that still lists the plugin.
        pane.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        var after = pane.ActionFor(entry);
        Assert.Equal("none", InstallModsPane.StateName(after.State));
        Assert.Equal(InstallModsCopy.Install, after.Label);
        Assert.True(after.Enabled);
        Assert.Equal(false, pane.ListingToWidget(entry)["hotLoaded"]);
        Assert.Equal("none", pane.ListingToWidget(entry)["state"]);

        // Exactly one re-check, a reloader scan plus slack out, and it rebuilds the detail.
        var one = Assert.Single(scheduled);
        Assert.Equal(InstallModsPane.HotUnloadRecheckSeconds, one.Seconds);
        int before = rebuilds;
        one.Action();
        Assert.Equal(before + 1, rebuilds);
        Assert.Equal("none", InstallModsPane.StateName(pane.ActionFor(entry).State));

        // A re-check that lands after the pane moved on (a released pane or another page bumps the
        // page token) rebuilds nothing.
        var late = new List<(double Seconds, Action Action)>();
        pane.PostDelayedHook = (seconds, action) => late.Add((seconds, action));
        string again = Ledgered(bytes);
        pane.Remover = _ =>
        {
            File.Delete(again);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" },
                Array.Empty<string>(), noOp: false, hotUnloaded: true);
        };
        Assert.True(pane.OnRemoveClick(entry.Key));
        pane.CancelDownload();
        int stable = rebuilds;
        Assert.Single(late).Action();
        Assert.Equal(stable, rebuilds);
        // Revert: keeping the probe's answer for a hot-unloaded key, or scheduling no re-check,
        // fails this; so does firing the re-check after the pane was released.
    }

    /// <summary>
    /// HOT3 live: a re-install of a listing this session hot unloaded takes the row back — the
    /// hot-unloaded record is not a permanent verdict. What this would catch: a row stuck on
    /// <c>Install</c> after its package was installed again.
    /// </summary>
    [Fact]
    public void A_re_install_after_a_hot_unload_takes_the_row_back()
    {
        byte[] bytes = { 6, 1 };
        string reload = Ledgered(bytes);
        var entry = Listing("1.0.0");
        var pane = Pane(entry);
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        pane.Remover = _ =>
        {
            File.Delete(reload);
            new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, Array.Empty<ServerInstallLedgerEntry>()).Write(_game);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" },
                Array.Empty<string>(), noOp: false, hotUnloaded: true);
        };
        Assert.True(pane.OnRemoveClick(entry.Key));
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        Assert.Equal(InstallModsCopy.Install, pane.ActionFor(entry).Label);

        // The package is on disk and ledgered again, and this process wrote it (the executor's `hot`
        // flag): installed, green, and carrying this session's "new" tag (INSTALL2 item 2).
        Ledgered(bytes);
        pane.HotLedgerProbe = _ => true;
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        var back = pane.ActionFor(entry);
        Assert.Equal("installed", InstallModsPane.StateName(back.State));
        Assert.Equal(InstallModsCopy.Installed, back.Label); // INSTALLUX2 item 2: no `new` tag any more
        Assert.False(back.Restart);
        // Revert: suppressing a hot-unloaded key regardless of what is on disk fails this.
    }

    /// <summary>One ledgered promoted copy of the listing on the client root.</summary>
    private string Ledgered(byte[] bytes)
    {
        string sha = InstalledState.HashBytes(bytes);
        string path = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod, Source),
        }).Write(_game);
        return path;
    }

    /// <summary>A pane over a one-listing catalog with the probes ModsMenu wires, reloader silent.</summary>
    private static InstallModsPane Pane(CatalogEntry entry)
    {
        var catalog = new LedgerCatalog();
        catalog.Entries.Add(entry);
        var pane = new InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        return pane;
    }

    private static ServerInstallLedgerEntry Row(string path, string sha, long size, string category, string source) =>
        new(path, sha, size, category, source, new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc));

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>The live listing: Thunderstore package <c>ExampleTeam-PhialStore</c>, plugin <c>PhialStore</c>.</summary>
    private static CatalogEntry Listing(string version) =>
        new(
            new ManifestMod(
                "PhialStore", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None,
                true, "ExampleTeam", "ExampleTeam-PhialStore"),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: "Phial Store",
            owner: "ExampleTeam",
            packageName: "PhialStore",
            side: PackageSide.Client);

    private sealed class LedgerCatalog : IInstallCatalog
    {
        public List<CatalogEntry> Entries { get; } = new();

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
            new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(false, InstallReasons.ApprovalMissing, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) =>
            new(false, InstallReasons.ApprovalMissing, null, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }
}
