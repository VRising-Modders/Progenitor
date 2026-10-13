using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Dev.Ops;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// POLISH1: the four Install-tab defects the deploy #70 live check found — a server half left stale
/// after the local server restarted and promoted it (C), a qualified hot mod that owed a restart it
/// did not owe after a restart (D), the readback's non-ASCII contract (F), and a pending bar that
/// asked for the wrong process's restart (H).
///
/// <para>Each fact names the guard whose removal must fail it.</para>
/// </summary>
public sealed class InstallTabPolishTests : IDisposable
{
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();
    private readonly string _game = Path.Combine(Path.GetTempPath(), "progenitor-polish1-" + Guid.NewGuid().ToString("N"));

    public InstallTabPolishTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => null;
        InstallModsPane.ProcessFilterProbe = null;
        ServerInstall.ResetHotSweepForTests();
        QualificationPolicyCache.Invalidate();
        Directory.CreateDirectory(ClientBep);
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
        InstallModsPane.ProcessFilterProbe = null;
        ServerInstall.ResetHotSweepForTests();
        QualificationPolicyCache.Invalidate();
        UiApi.ResetForTests();
        try { Directory.Delete(_game, true); } catch { }
    }

    private string ClientGame => Path.Combine(_game, "client");
    private string ClientBep => Path.Combine(ClientGame, "BepInEx");
    private string ServerGame => Path.Combine(_game, "server");
    private string ServerBep => Path.Combine(ServerGame, "BepInEx");
    private const string Source = "package:ExampleTeam-PhialStore@1.0.0";

    // ---- Defect C: the server half after the local server restarted -----------------------

    /// <summary>
    /// Live (deploy #70): both installs were pending on the local server, the server restarted, its
    /// Bootstrap promoted them and the reconcile rewrote the rows as <c>mod</c> — and the client's
    /// rows still read <c>pending</c> / <c>Installed · restart to load</c>, still counted in the bar,
    /// and still offered <c>Cancel installing …?</c>. A promoted copy on the server root is installed
    /// there: green <c>Installed</c>, out of the bar, and the click offers <c>Uninstall …?</c>.
    /// What this would catch: reading the server half's state from the file's folder alone, or leaving
    /// the promoted row in the pending count.
    /// </summary>
    [Fact]
    public void A_promoted_server_copy_reads_installed_and_offers_uninstall()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);

        // Before anything reconciled that root: the live defect, and still the honest answer — the
        // row says the server has to start with it.
        PromotedServerCopy(ServerInstallLedger.CategoryPending, "BepInEx/progenitor-pending/index/PhialStore.dll");
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        var stale = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Pending, stale.State);
        Assert.Equal(InstallModsCopy.InstalledPending, stale.Label);
        Assert.Equal(CellTone.Pending, stale.Tone);
        Assert.Equal(1, pane.PendingCount());
        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.CancelInstall, pane.Prompt);
        Assert.True(pane.PromptNo());

        // Reconciled: the server started with it.
        PromotedServerCopy(ServerInstallLedger.CategoryMod, "BepInEx/progenitor-reload/PhialStore.dll");
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        var action = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Installed, action.State);
        Assert.Equal(InstallModsCopy.Installed, action.Label);
        Assert.Equal(CellTone.Ok, action.Tone);
        Assert.True(action.Enabled);
        Assert.Equal(0, pane.PendingCount());
        Assert.Equal(0, pane.PendingUninstallCount());
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfLoaded),
            pane.HalvesOf(entry));

        var listing = pane.ListingToWidget(entry);
        Assert.Equal("installed", listing["state"]);
        Assert.Equal(false, listing["newTag"]);
        var halves = (Dictionary<string, object?>)listing["halves"]!;
        Assert.Equal(InstalledState.HalfLoaded, halves["server"]);

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        // REQRULE6: the title names the side the Yes changes: only the local server holds a copy.
        Assert.Equal("Uninstall Phial Store from the local server?", pane.PromptHeadingText(entry, pane.Prompt));
        // Revert: dropping the `mod`-category requirement makes the stale half read installed too;
        // dropping the promoted branch keeps the live defect.
    }

    /// <summary>
    /// Review M1 (REQRULE6): a whole-row Keep installed un-marks every marked half, so its title names the server when the
    /// local server's copy is the marked one, not "this game". Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void Keep_installed_names_the_server_when_the_server_s_copy_is_marked()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        PromotedServerCopy(ServerInstallLedger.CategoryMod, "BepInEx/progenitor-reload/PhialStore.dll");
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        pane.UninstallPendingProbe = _ => true;
        Assert.Equal((InstalledState.HalfNone, InstalledState.HalfLoaded), pane.HalvesOf(entry));
        Assert.Equal("Keep Phial Store installed on the local server?", pane.PromptHeadingText(entry, InstallPromptKind.Reactivate));
    }

    /// <summary>
    /// The re-check the Install tab runs on every open and tab select: a reconcile that changed a
    /// ledger re-takes the pane's scan in the same build, so the promote shows in the rows it was
    /// found for and not one rebuild later (or never).
    /// What this would catch: reconciling after the rows are read, or not re-scanning at all.
    /// </summary>
    [Fact]
    public void Build_rechecks_the_ledgers_and_re_takes_the_scan()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);

        PromotedServerCopy(ServerInstallLedger.CategoryPending, "BepInEx/progenitor-pending/index/PhialStore.dll");
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        Assert.Equal(InstallListingState.Pending, pane.ActionFor(entry).State);

        int reconciles = 0;
        pane.LedgerRecheckProbe = () =>
        {
            reconciles++;
            // What the real pass does to the file: the promoted row, as category mod.
            PromotedServerCopy(ServerInstallLedger.CategoryMod, "BepInEx/progenitor-reload/PhialStore.dll");
            return true;
        };
        pane.InstalledQueryRescan = () => InstalledState.Scan(ClientBep, ServerBep, null, "44");

        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(1, reconciles);
        Assert.Equal(InstallListingState.Installed, pane.ActionFor(entry).State);
        Assert.Empty(pane.PendingBarHandles);

        // A pass that changed nothing does not re-scan: the query the menu handed in stands.
        pane.LedgerRecheckProbe = () => false;
        pane.InstalledQueryRescan = () => throw new InvalidOperationException("must not re-scan");
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(InstallListingState.Installed, pane.ActionFor(entry).State);
        // Revert: calling RecheckLedgers after the rows are built, or re-scanning unconditionally,
        // fails this.
    }

    /// <summary>
    /// The re-check's cost: one stamp per root. A root whose <c>progenitor-install.json</c> has not
    /// moved since the last pass is not reconciled again, and a root whose ledger changed under the
    /// service (the local server restarting is the only writer that does) is.
    /// What this would catch: reconciling every root on every open (a file rewrite per tab select), or
    /// caching the stamp read before the reconcile so the next open redoes the same work.
    /// </summary>
    [Fact]
    public void The_recheck_only_reconciles_a_root_whose_ledger_stamp_moved()
    {
        Directory.CreateDirectory(ServerBep);
        byte[] bytes = { 9, 9, 9 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(ServerBep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryPending, Source),
        }).Write(ServerGame);

        var service = new InstallCatalogService(
            ClientBep, new NoTransport(), null, new ImmediateClock(), serverBepInExRoot: ServerBep);

        // The constructor's own ReconcileLedgers already rewrote the row, so the first re-check finds
        // nothing left to do — and that is the point: it is free.
        Assert.Equal(
            ServerInstallLedger.CategoryMod,
            ServerInstallLedger.TryRead(ServerGame)!.Files.Single().Category);
        Assert.False(service.ReconcileChangedLedgers().Changed);

        // The server restarts, promotes a second install and leaves the row pending: the stamp moved.
        byte[] second = { 4, 4 };
        string secondSha = InstalledState.HashBytes(second);
        Write(Path.Combine(ServerBep, InstalledState.ReloadFolder, "CryptSnooze.dll"), second);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod, Source),
            Row(
                "BepInEx/progenitor-pending/index/CryptSnooze.dll", secondSha, second.Length,
                ServerInstallLedger.CategoryPending, "package:ExampleTeam-CryptSnooze@2.0.4"),
        }).Write(ServerGame);

        var moved = service.ReconcileChangedLedgers();
        Assert.Equal(new[] { "BepInEx/progenitor-reload/CryptSnooze.dll" }, moved.Rewritten.ToArray());
        // Its own rewrite is not a reason to run again.
        Assert.False(service.ReconcileChangedLedgers().Changed);
        // Revert: storing the pre-pass stamp makes the last assertion reconcile a second time;
        // dropping the stamp check makes the first assertion rewrite the file again.
    }

    // ---- Defect D: a qualified hot mod after a restart ------------------------------------

    /// <summary>
    /// Live (deploy #70): <c>Progenitor-HotDemo</c> read <c>Installed</c>, <c>newTag false</c> and
    /// <c>restart true</c> after a restart, and its Uninstall owed a restart. A ledgered <c>mod</c>
    /// copy the reloader has loaded whose exact bytes the local reviewed policy covers is
    /// hot-unloadable: <c>restart false</c>, and the prompt says it unloads without one. The
    /// session-scoped <c>new</c> tag stays off, which is the project rule.
    /// What this would catch: taking the qualification as a reason to show the `new` tag, or taking
    /// the reloader's report alone (which covers promoted third-party mods) as hot.
    /// </summary>
    [Fact]
    public void A_qualified_loaded_image_is_hot_unloadable_without_the_new_tag()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        // A fresh process: the ledger's `hot` flag is cleared, so nothing session-scoped is true.
        LedgeredOnClient(new byte[] { 7, 7, 7 });
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, null, null, "44"));
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };

        pane.QualifiedProbe = _ => false;
        var unreviewed = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Installed, unreviewed.State);
        Assert.True(unreviewed.Restart);
        Assert.False(pane.IsHotLoaded(entry));
        Assert.Equal(
            InstallModsCopy.UninstallRestartBody + " " + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));

        pane.QualifiedProbe = _ => true;
        var reviewed = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Installed, reviewed.State);
        Assert.Equal(InstallModsCopy.Installed, reviewed.Label);
        Assert.Equal(CellTone.Ok, reviewed.Tone);
        Assert.False(reviewed.Restart);
        Assert.True(pane.IsHotLoaded(entry));
        Assert.False(pane.IsNewThisSession(entry));
        Assert.Equal(
            InstallModsCopy.UninstallHotBody + " " + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));

        var listing = pane.ListingToWidget(entry);
        Assert.Equal(false, listing["restart"]);
        Assert.Equal(true, listing["hotLoaded"]);
        Assert.Equal(false, listing["newTag"]);
        // Revert: putting the qualification back into IsNewThisSession makes newTag true; dropping
        // the DetectInstalled().HotLoaded half makes an unloaded reviewed image read hot.
    }

    /// <summary>
    /// The third source is not evidence on its own: the reloader has to report the image loaded, and
    /// the reviewed record has to be for the bytes that are actually on disk.
    /// What this would catch: answering the row from the policy file alone.
    /// </summary>
    [Fact]
    public void A_reviewed_image_the_reloader_does_not_report_is_not_hot()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        LedgeredOnClient(new byte[] { 7, 7, 7 });
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, null, null, "44"));
        pane.QualifiedProbe = _ => true;
        pane.LoadedPluginsProbe = () => Array.Empty<string>();

        Assert.False(pane.IsHotLoaded(entry));
        Assert.True(pane.ActionFor(entry).Restart);
        // Revert: dropping the loaded half fails this.
    }

    /// <summary>
    /// <see cref="HotUnloadQualification.Covers"/>: every key of the reviewed record has to match the
    /// bytes on disk and this process, and anything unknown refuses.
    /// What this would catch: matching on the hash alone (a record for another size), honoring a
    /// record reviewed for the local server on the client, accepting a Remove/Replace record for an
    /// initial install, or reading a record authored against another game build or Core contract.
    /// </summary>
    [Fact]
    public void Covers_matches_every_key_of_the_reviewed_record()
    {
        const string sha = "11aa22bb33cc44dd55ee66ff77008899aabbccddeeff00112233445566778899";
        var ids = Identities("client-247473480", "protocol/3+host/3", "core/1.2.3+admission/1");
        var policy = QualificationPolicy.Parse(PolicyJson(
            pluginId: "example.phialstore", sha: sha, size: 4096, side: "Client", target: "Client",
            operation: "InitialInstall", build: "client-247473480",
            bootstrap: "protocol/3+host/3", core: "core/1.2.3+admission/1"));
        Assert.Empty(policy.Errors);

        Assert.True(HotUnloadQualification.Covers(policy, "example.phialstore", sha, 4096, InstallTarget.Client, ids));
        // Case does not matter for the plugin id or the hash; everything else is exact.
        Assert.True(HotUnloadQualification.Covers(policy, "Example.PhialStore", sha.ToUpperInvariant(), 4096, InstallTarget.Client, ids));

        Assert.False(HotUnloadQualification.Covers(policy, "example.phialstore", sha, 4097, InstallTarget.Client, ids));
        Assert.False(HotUnloadQualification.Covers(policy, "example.phialstore", sha, 0, InstallTarget.Client, ids));
        Assert.False(HotUnloadQualification.Covers(policy, "example.other", sha, 4096, InstallTarget.Client, ids));
        Assert.False(HotUnloadQualification.Covers(policy, "", sha, 4096, InstallTarget.Client, ids));
        Assert.False(HotUnloadQualification.Covers(policy, "example.phialstore", "not-a-hash", 4096, InstallTarget.Client, ids));
        Assert.False(HotUnloadQualification.Covers(policy, "example.phialstore", sha, 4096, InstallTarget.LocalServer, ids));
        Assert.False(HotUnloadQualification.Covers(null, "example.phialstore", sha, 4096, InstallTarget.Client, ids));
        Assert.False(HotUnloadQualification.Covers(
            QualificationPolicy.Empty, "example.phialstore", sha, 4096, InstallTarget.Client, ids));

        // A different game build, Bootstrap pairing or Core contract is a different review.
        Assert.False(HotUnloadQualification.Covers(
            policy, "example.phialstore", sha, 4096, InstallTarget.Client,
            Identities("client-999", "protocol/3+host/3", "core/1.2.3+admission/1")));
        Assert.False(HotUnloadQualification.Covers(
            policy, "example.phialstore", sha, 4096, InstallTarget.Client,
            Identities("client-247473480", "protocol/4+host/4", "core/1.2.3+admission/1")));
        Assert.False(HotUnloadQualification.Covers(
            policy, "example.phialstore", sha, 4096, InstallTarget.Client,
            Identities("client-247473480", "protocol/3+host/3", "core/9.9.9+admission/1")));
        // An unresolved identity on either side never matches, even against itself.
        Assert.False(HotUnloadQualification.Covers(
            policy, "example.phialstore", sha, 4096, InstallTarget.Client, Identities("", "", "")));

        // A record reviewed for Both covers either half on its own (the one widening HOT4 allows);
        // an operation this is not refuses.
        var both = QualificationPolicy.Parse(PolicyJson(
            "example.phialstore", sha, 4096, "Client", "Both", "InitialInstall",
            "client-247473480", "protocol/3+host/3", "core/1.2.3+admission/1"));
        Assert.True(HotUnloadQualification.Covers(both, "example.phialstore", sha, 4096, InstallTarget.Client, ids));
        var remove = QualificationPolicy.Parse(PolicyJson(
            "example.phialstore", sha, 4096, "Client", "Client", "Remove",
            "client-247473480", "protocol/3+host/3", "core/1.2.3+admission/1"));
        Assert.False(HotUnloadQualification.Covers(remove, "example.phialstore", sha, 4096, InstallTarget.Client, ids));
        // Revert: dropping any single comparison fails one of these.
    }

    // ---- Defect H: which process has to restart -------------------------------------------

    /// <summary>
    /// Live (deploy #70): two installs were pending on the local server alone and the bar said
    /// "restart the game to apply" — the one restart that would not have applied them.
    /// What this would catch: one hard-coded sentence, or a sides value that ignores the local server.
    /// </summary>
    [Fact]
    public void The_pending_bar_names_the_processes_that_have_to_restart()
    {
        Assert.Equal(
            "2 mods installed — restart the game to apply. »",
            InstallModsCopy.PendingBar(2, 0, InstallModsCopy.SidesClient));
        Assert.Equal(
            "2 mods installed — restart the local server to apply. »",
            InstallModsCopy.PendingBar(2, 0, InstallModsCopy.SidesServer));
        Assert.Equal(
            "1 mod installed · 1 uninstalled — restart the game and the local server to apply. »",
            InstallModsCopy.PendingBar(1, 1, InstallModsCopy.SidesBoth));
        // An unknown or absent sides value keeps the sentence the bar always had.
        Assert.Equal(InstallModsCopy.PendingBar(2, 0), InstallModsCopy.PendingBar(2, 0, null));
        Assert.Equal(InstallModsCopy.PendingBar(2, 0), InstallModsCopy.PendingBar(2, 0, InstallModsCopy.SidesNone));
        Assert.Equal("", InstallModsCopy.PendingBar(0, 0, InstallModsCopy.SidesServer));
        // Revert: hard-coding "restart the game" fails this.
    }

    /// <summary>
    /// The sides the pane reports, and the bar it draws from them: the live case is a pending install
    /// on the local server only.
    /// What this would catch: reading the sides from the chosen target instead of from where the files
    /// are, or reporting `client` for a server-only pending install.
    /// </summary>
    [Fact]
    public void Pending_sides_come_from_where_the_pending_files_are()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);

        // Nothing pending at all.
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        Assert.Equal(InstallModsCopy.SidesNone, pane.PendingSides());

        // The local server only: a pending file under the server root.
        PendingServerCopy();
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        Assert.Equal(1, pane.PendingCount());
        Assert.Equal(InstallModsCopy.SidesServer, pane.PendingSides());
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(
            "1 mod installed — restart the local server to apply. »",
            pane.PendingBarText);
        Assert.True(pane.TryDescribeSidebarRow(pane.PendingBarHandles[0], out var serverExtra));
        Assert.Equal(
            InstallModsCopy.SidesServer,
            ((Dictionary<string, object?>)serverExtra!["pending"]!)["sides"]);

        // Both halves pending: the sentence names both processes.
        PendingClientCopy();
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        Assert.Equal(InstallModsCopy.SidesBoth, pane.PendingSides());
        pane.Build(NewPanel(), true, false, null, () => { }, () => { }, DateTime.UtcNow);
        Assert.Equal(
            "1 mod installed — restart the game and the local server to apply. »",
            pane.PendingBarText);

        // A pending change whose sides cannot be read at all — flagged for removal with no ledgered
        // file behind it — counts as the client, which is what the bar said before the sides existed.
        var bare = Pane(new FakeCatalogWith(entry));
        bare.UninstallPendingProbe = _ => true;
        Assert.Equal(1, bare.PendingUninstallCount());
        Assert.Equal(InstallModsCopy.SidesClient, bare.PendingSides());
        // Revert: defaulting an unreadable row to `server`, or to `none`, fails this.
    }

    /// <summary>
    /// POLISH2 item 3: the uninstall prompt's body names the restart it actually owes. Live
    /// (2026-09-13, client gen 2) AutoLatchGates was installed on the local server alone and the
    /// prompt read "Removed at the next restart of V Rising" — the one restart that would not have
    /// removed it. The side rule is <see cref="InstallModsCopy.RestartSentence"/>'s, read from the
    /// halves on disk; a restart is still owed, so the row's <c>restart</c> stays true.
    /// What this would catch: hard-coding the client sentence (the live defect), reading the sides
    /// from the chosen install target instead of from the files, or dropping the owed restart from the
    /// row while renaming it in the prompt.
    /// </summary>
    [Fact]
    public void The_uninstall_body_names_the_processes_that_hold_the_file()
    {
        var entry = Listing();
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);

        // The local server alone, promoted: its restart is the one that removes it.
        PromotedServerCopy(ServerInstallLedger.CategoryMod, "BepInEx/progenitor-reload/PhialStore.dll");
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        Assert.Equal(InstallModsCopy.SidesServer, pane.InstalledSidesOf(entry));
        Assert.Equal(
            "Removed at the next restart of the local server; it stays loaded until then. "
            + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));
        Assert.True(pane.ActionFor(entry).Restart || !pane.IsHotLoaded(entry));

        // Both halves on disk: both processes are named, in that order.
        LedgeredOnClient(new byte[] { 7, 7, 7 });
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, "44"));
        Assert.Equal(InstallModsCopy.SidesBoth, pane.InstalledSidesOf(entry));
        Assert.Equal(
            "Removed at the next restart of V Rising and the local server; it stays loaded until then. "
            + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));

        // The client alone keeps the sentence this prompt always had.
        var clientOnly = Pane(new FakeCatalogWith(entry));
        clientOnly.SetInstalledQuery(InstalledState.Scan(ClientBep, null, null, "44"));
        Assert.Equal(InstallModsCopy.SidesClient, clientOnly.InstalledSidesOf(entry));
        Assert.Equal(
            InstallModsCopy.UninstallRestartBody + " " + InstallModsCopy.SettingsKeptBody,
            clientOnly.PromptBodyText(entry, InstallPromptKind.Uninstall));

        // The three sentences differ only in the process they name, and an unreadable side keeps the
        // client's — the process this menu runs in.
        Assert.Equal(InstallModsCopy.UninstallRestartBody, InstallModsCopy.UninstallRestartBodyFor(InstallModsCopy.SidesClient));
        Assert.Equal(InstallModsCopy.UninstallRestartBody, InstallModsCopy.UninstallRestartBodyFor(InstallModsCopy.SidesNone));
        Assert.Equal(InstallModsCopy.UninstallRestartBody, InstallModsCopy.UninstallRestartBodyFor(null));
        Assert.Equal(
            InstallModsCopy.UninstallRestartServerBody,
            InstallModsCopy.UninstallRestartBodyFor(InstallModsCopy.SidesServer));
        Assert.Equal(
            InstallModsCopy.UninstallRestartBothBody,
            InstallModsCopy.UninstallRestartBodyFor(InstallModsCopy.SidesBoth));
        // Revert: hard-coding UninstallRestartBody in PromptBodyText, or counting a `none` half as a
        // side, fails this.
    }

    // ---- Defect F: the readback's non-ASCII contract --------------------------------------

    /// <summary>
    /// Live (deploy #70): <c>page-store-link.glyph</c> read back as <c>Â»</c> and
    /// <c>page-meta.line</c> as <c>1.0.0 Â· …</c> — UTF-8 bytes decoded as Latin-1 — while the screen
    /// drew both correctly. Nothing in this process can produce that: every string the readback puts on
    /// the bridge is escaped to <c>\uXXXX</c> ASCII by <see cref="ModsUiResult.JsonOptions"/>, so a
    /// reader's encoding choice cannot corrupt it. This pins that, for the KIT1 <c>Describe()</c>
    /// dictionaries and for a merged page-row dictionary, which is what regressed would reintroduce the
    /// symptom: an encoder that passes non-ASCII through puts raw UTF-8 bytes on the wire.
    /// What this would catch: switching the readback to <c>UnsafeRelaxedJsonEscaping</c> (or any
    /// encoder that does not escape non-ASCII).
    /// </summary>
    [Fact]
    public void The_readback_serializes_non_ascii_as_ascii_escapes_and_round_trips()
    {
        var link = UiIconLinkHandle.Disabled(null, MenuPanelLayout.IconLinkGlyph, "Open the store page");
        var described = link.Describe();
        Assert.Equal("»", described["glyph"]);

        // A merged page row: the head block's `page-meta` object beside the link's own parts, the
        // shape DescribeWidget hands the bridge.
        var row = new Dictionary<string, object?>
        {
            ["kind"] = "cellrow",
            ["label"] = InstallModsCopy.InstalledNew,
            ["page-meta"] = new Dictionary<string, object?> { ["line"] = "1.0.0 · 2026-09-12 · by Progenitor" },
            ["page-store-link"] = described,
            ["pending"] = new Dictionary<string, object?>
            {
                ["sides"] = InstallModsCopy.SidesServer,
                ["bar"] = InstallModsCopy.PendingBar(1, 0, InstallModsCopy.SidesServer),
            },
        };

        string json = JsonSerializer.Serialize(row, ModsUiResult.JsonOptions);
        for (int i = 0; i < json.Length; i++)
            Assert.True(json[i] < 0x80, "the readback put a non-ASCII character on the wire: U+" + ((int)json[i]).ToString("X4"));
        Assert.Contains("\\u00BB", json);
        Assert.Contains("\\u00B7", json);
        // Nothing is double-encoded: Â (U+00C2) never appears, escaped or otherwise.
        Assert.DoesNotContain("\\u00C2", json);

        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(
            "»",
            parsed.RootElement.GetProperty("page-store-link").GetProperty("glyph").GetString());
        Assert.Equal(
            "1.0.0 · 2026-09-12 · by Progenitor",
            parsed.RootElement.GetProperty("page-meta").GetProperty("line").GetString());
        Assert.Equal(InstallModsCopy.InstalledNew, parsed.RootElement.GetProperty("label").GetString());
        Assert.Equal(
            "1 mod installed — restart the local server to apply. »",
            parsed.RootElement.GetProperty("pending").GetProperty("bar").GetString());
        // Revert: setting Encoder to UnsafeRelaxedJsonEscaping fails the ASCII sweep.
    }

    // ---- Helpers --------------------------------------------------------------------------

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.polish1");
        _panels.Add(panel);
        return panel;
    }

    private InstallModsPane Pane(IInstallCatalog catalog)
    {
        var pane = new InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.UninstallPendingProbe = _ => false;
        pane.HotLedgerProbe = _ => false;
        pane.QualifiedProbe = _ => false;
        return pane;
    }

    /// <summary>The listing's copy on the local-server root, ledgered at <paramref name="category"/>.</summary>
    private void PromotedServerCopy(string category, string rowPath)
    {
        Directory.CreateDirectory(ServerBep);
        byte[] bytes = { 5, 5, 5 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(ServerBep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row(rowPath, sha, bytes.Length, category, Source),
        }).Write(ServerGame);
    }

    /// <summary>The listing waiting in the local server's own pending folder: nothing promoted yet.</summary>
    private void PendingServerCopy()
    {
        Directory.CreateDirectory(ServerBep);
        byte[] bytes = { 6, 6, 6 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(ServerBep, InstalledState.PendingFolder, "index", "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryPending, Source),
        }).Write(ServerGame);
    }

    /// <summary>The same listing waiting in the client's pending folder.</summary>
    private void PendingClientCopy()
    {
        byte[] bytes = { 6, 6, 6 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(ClientBep, InstalledState.PendingFolder, "index", "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryPending, Source),
        }).Write(ClientGame);
    }

    /// <summary>A promoted, ledgered <c>mod</c> copy on the client root with no <c>hot</c> flag.</summary>
    private void LedgeredOnClient(byte[] bytes)
    {
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(ClientBep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod, Source),
        }).Write(ClientGame);
    }

    private static ServerInstallLedgerEntry Row(string path, string sha, long size, string category, string source) =>
        new(path, sha, size, category, source, new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc));

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static IReadOnlyList<CompatibilityIdentity> Identities(string build, string bootstrap, string core) => new[]
    {
        new CompatibilityIdentity(CompatibilityKeys.GameBuild, build),
        new CompatibilityIdentity(CompatibilityKeys.BootstrapContract, bootstrap),
        new CompatibilityIdentity(CompatibilityKeys.CoreContract, core),
    };

    private static string PolicyJson(
        string pluginId, string sha, long size, string side, string target, string operation,
        string build, string bootstrap, string core) =>
        "{\"schema\":1,\"records\":[{"
        + "\"id\":\"r1\",\"pluginId\":\"" + pluginId + "\","
        + "\"artifactSha256\":\"" + sha + "\",\"artifactSize\":" + size + ","
        + "\"dependencyClosure\":{},"
        + "\"" + CompatibilityKeys.GameBuild + "\":\"" + build + "\","
        + "\"" + CompatibilityKeys.BootstrapContract + "\":\"" + bootstrap + "\","
        + "\"" + CompatibilityKeys.CoreContract + "\":\"" + core + "\","
        + "\"side\":\"" + side + "\",\"operation\":\"" + operation + "\",\"target\":\"" + target + "\","
        + "\"qualifiedUtc\":\"2026-09-13T00:00:00Z\",\"evidence\":\"POLISH1 test\"}]}";

    private static CatalogEntry Listing() =>
        new(
            new ManifestMod(
                "PhialStore", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None,
                true, "ExampleTeam", "ExampleTeam-PhialStore"),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: "Phial Store",
            owner: "ExampleTeam",
            packageName: "PhialStore",
            side: PackageSide.Client,
            description: "PhialStore does a thing.");

    private sealed class ImmediateClock : ICatalogClock
    {
        public DateTime UtcNow => new(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc);
        public Task Delay(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in tests");
    }

    private sealed class FakeCatalogWith : FakeCatalog
    {
        public FakeCatalogWith(CatalogEntry entry) => Entries.Add(entry);
    }

    private class FakeCatalog : IInstallCatalog
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

        public PlanCallResult Plan(string key, InstallTarget target) => new(false, InstallReasons.NotInspected, null);

        public ApproveCallResult Approve(string planHash) => new(false, InstallReasons.ApprovalMissing, planHash, false);

        public ExecuteCallResult ExecuteToPending(string planHash) =>
            new(false, InstallReasons.ApprovalMissing, null, false);

        public Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry));
    }
}
