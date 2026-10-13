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
/// INSTALL2: the Install tab as the approved mock draws it — hidden listings, one caption and tone
/// per row state, a y/n confirmation under the row for every action (with the target chooser inside
/// it), the pending-uninstall marker an ordinary loaded mod is removed through, Bootstrap honoring
/// that marker at the next start, and the pending bar with its expandable list.
///
/// <para>Each fact names the guard whose removal must fail it. The behavior spec is the Install
/// tab's JS in the approved mods-page mock (internal research notes).</para>
/// </summary>
public sealed class InstallConfirmTests : IDisposable
{
    private readonly CellRowTests.FakeCellRuntime _runtime = new();
    private readonly List<IUiPanel> _panels = new();
    private readonly object _owner = new();
    private readonly string _game = Path.Combine(Path.GetTempPath(), "progenitor-install2-" + Guid.NewGuid().ToString("N"));

    public InstallConfirmTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        CatalogUpdateTags.ResetProvidersForTests();
        CatalogUpdateTags.ConfiguredTagProvider = () => null;
        CatalogUpdateTags.BuildProvider = () => null;
        InstallModsPane.ProcessFilterProbe = null;
        ServerInstall.ResetHotSweepForTests();
        Directory.CreateDirectory(Bep);
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
        UninstallMarkers.ClockUtc = null;
        ServerInstall.ResetHotSweepForTests();
        UiApi.ResetForTests();
        try { Directory.Delete(_game, true); } catch { }
    }

    private string Bep => Path.Combine(_game, "BepInEx");
    private const string Source = "package:ExampleTeam-PhialStore@1.0.0";

    // ---- Item 1: hidden listings --------------------------------------------------------

    /// <summary>
    /// The loader pack and Progenitor's own packages are not listed at all — no row, so no filter
    /// can reveal them, the count does not include them and the Install column is not widened by
    /// their captions.
    /// What this would catch: hiding in the filters instead of the row set (a side chip would bring
    /// the loader back), or a name-based rule that swallows somebody else's mod.
    /// </summary>
    [Fact]
    public void The_loader_and_Progenitor_itself_are_not_listed_on_any_filter()
    {
        var catalog = new FakeCatalog();
        var loader = Listing("BepInEx", "BepInExPack_V_Rising", "6.0.0");
        var self = Listing("Progenitor", "Progenitor", "0.1.0");
        var selfGame = Listing("Progenitor", "Progenitor_VRising", "0.1.0");
        var elseWhere = Listing("Progenitor", "ProgenitorExtras", "1.0.0");
        var mod = Listing("ExampleTeam", "PhialStore", "1.0.0");
        catalog.Entries.AddRange(new[] { loader, self, selfGame, elseWhere, mod });

        var pane = new InstallModsPane(catalog);
        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);

        Assert.Equal(
            new[] { mod.Key, elseWhere.Key },
            pane.AllEntries().Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(loader.Key, pane.Rows.Select(r => r.Row.Key));
        Assert.DoesNotContain(self.Key, pane.Rows.Select(r => r.Row.Key));
        Assert.DoesNotContain(selfGame.Key, pane.Rows.Select(r => r.Row.Key));
        Assert.Equal(2, pane.MatchCount.Shown);
        Assert.Equal("2 listings", pane.CountText());
        Assert.True(InstallModsPane.IsHidden(loader));
        Assert.True(InstallModsPane.IsHidden(self));
        Assert.True(InstallModsPane.IsHidden(selfGame));
        Assert.False(InstallModsPane.IsHidden(elseWhere));
        Assert.False(pane.IsVisible(loader));
        Assert.False(pane.VisibleAtDefaults(self));

        // Every side filter, and a search for the loader by name, still cannot reach them.
        foreach (var side in new[] { InstallSideFilter.Any, InstallSideFilter.Server, InstallSideFilter.Client, InstallSideFilter.Both })
        {
            pane.SetSideFilter(side);
            Assert.DoesNotContain(loader.Key, pane.VisibleEntries().Select(e => e.Key));
        }

        pane.SetSideFilter(InstallSideFilter.Any);
        pane.Search = "BepInExPack";
        Assert.Empty(pane.VisibleEntries());
        pane.Search = "Progenitor";
        Assert.Equal(new[] { elseWhere.Key }, pane.VisibleEntries().Select(e => e.Key).ToArray());
        // Revert: filtering hidden listings out in InstallSearchFilters instead of AllEntries, or
        // matching "Progenitor" as a substring, fails this.
    }

    // ---- Item 2: one caption and tone per state -----------------------------------------

    /// <summary>
    /// The Install column's caption and tone for each state the mock names: Install / Install ·
    /// restart in gold, Installed green, Installed · new for a mod this session hot loaded,
    /// Installed · restart to load yellow, and Reactivate orange for an uninstalled-but-pending row,
    /// whose one-liner says so instead of describing the mod.
    /// What this would catch: the old Remove / Remove · restart to unload captions coming back, a
    /// pending row drawn green, or an orange row that still shows its description.
    /// </summary>
    [Fact]
    public void Every_row_state_has_the_mock_s_caption_and_tone()
    {
        byte[] bytes = { 4, 2 };
        string reload = Ledgered(bytes);
        var entry = Listing("ExampleTeam", "PhialStore", "1.0.0");
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);

        // none: gold, and the caption says whether it costs a restart (this listing declares it
        // does not, so it reads Install; a listing that does reads Install · restart).
        var fresh = new InstallModsPane(catalog);
        Assert.Equal(InstallListingState.None, fresh.ActionFor(entry).State);
        Assert.Equal(InstallModsCopy.Install, fresh.ActionFor(entry).Label);
        Assert.Equal(CellTone.Gold, fresh.ActionFor(entry).Tone);

        // installed and promoted: green, enabled, and its confirmation owes a restart.
        var pane = Pane(catalog);
        pane.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        var installed = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Installed, installed.State);
        Assert.Equal(InstallModsCopy.Installed, installed.Label);
        Assert.Equal(CellTone.Ok, installed.Tone);
        Assert.True(installed.Enabled);
        Assert.True(installed.Restart);
        Assert.Equal(false, pane.ListingToWidget(entry)["newTag"]);

        // INSTALL2 defect A (live, deploy #69): the reloader reporting the plugin loaded is NOT hot.
        // The reloader loads every DLL in its folder, so a copy Bootstrap promoted at start reads as
        // loaded — and CryptSnooze therefore said "Installed · new" and deleted a live file on Yes.
        // A promoted, reported-loaded, ledgered copy is an ordinary loaded mod: green, no tag, owing a
        // restart, so its uninstall takes the marker route.
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        var stillOrdinary = pane.ActionFor(entry);
        Assert.Equal(InstallListingState.Installed, stillOrdinary.State);
        Assert.Equal(InstallModsCopy.Installed, stillOrdinary.Label);
        Assert.True(stillOrdinary.Restart);
        Assert.False(pane.IsHotLoaded(entry));
        Assert.Equal(false, pane.ListingToWidget(entry)["hotLoaded"]);
        Assert.Equal(false, pane.ListingToWidget(entry)["newTag"]);
        Assert.Equal(
            InstallModsCopy.UninstallRestartBody + " " + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));

        // Hot loaded by THIS process: the executor's own `hot` ledger flag (or this session's write).
        // INSTALLUX2 item 2 retired the "new" tag — the caption is the plain green word, and what
        // the row offers beside it is Uninstall; only `restart` still tells the two apart.
        pane.HotLedgerProbe = _ => true;
        var hot = pane.ActionFor(entry);
        Assert.Equal(InstallModsCopy.Installed, hot.Label);
        Assert.Equal(CellTone.Ok, hot.Tone);
        Assert.False(hot.Restart);
        Assert.Equal(true, pane.ListingToWidget(entry)["hotLoaded"]);
        // INSTALLUX2 item 2: the key survives, always false.
        Assert.Equal(false, pane.ListingToWidget(entry)["newTag"]);
        Assert.Equal(
            InstallModsCopy.UninstallHotBody + " " + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));
        // The next start clears the flag, and the same row is an ordinary loaded mod again.
        pane.HotLedgerProbe = _ => false;
        Assert.Equal(InstallModsCopy.Installed, pane.ActionFor(entry).Label);
        pane.HotLedgerProbe = null;

        // pending load: yellow.
        var pending = Pane(catalog);
        pending.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        Assert.Equal(InstallListingState.Pending, pending.ActionFor(entry).State);
        Assert.Equal(InstallModsCopy.InstalledPending, pending.ActionFor(entry).Label);
        Assert.Equal(CellTone.Pending, pending.ActionFor(entry).Tone);

        // pending uninstall: orange, Reactivate, and the row's second line says what it is doing.
        var marked = Pane(catalog);
        marked.UninstallPendingProbe = _ => true;
        marked.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        var orange = marked.ActionFor(entry);
        Assert.Equal(InstallListingState.PendingUninstall, orange.State);
        Assert.Equal("penduninst", InstallModsPane.StateName(orange.State));
        Assert.Equal(InstallModsCopy.Reactivate, orange.Label);
        Assert.Equal(CellTone.Warn, orange.Tone);
        Assert.True(orange.Enabled);
        // INSTALLSIDES1: the row's second line is the author; the Client cell says what is pending.
        Assert.Equal("Phial Store\n" + InstallModsCopy.ByPrefix + "ExampleTeam", marked.RowNameText(entry));
        Assert.Equal(SideCells.KindRemovalPending, marked.SideCellsFor(entry).Client.Kind);
        Assert.Equal(InstallStatusLabels.RemovalPendingGameRestart, marked.SideCellsFor(entry).Client.Text);
        var readback = marked.ListingToWidget(entry);
        Assert.Equal("penduninst", readback["state"]);
        Assert.Equal(true, readback["uninstallPending"]);
        var markedPanel = NewPanel();
        marked.Build(markedPanel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var nameCell = marked.Rows.Single(r => r.Row.Key == entry.Key).Row.Cells.Single(c => c.Key == InstallModsPane.CellName);
        Assert.Equal(CellTone.Warn, nameCell.Tone);
        var clientCell = marked.Rows.Single(r => r.Row.Key == entry.Key).Row.Cells.Single(c => c.Key == InstallModsPane.CellClient);
        Assert.Equal(InstallStatusLabels.RemovalPendingGameRestart, clientCell.Text.Replace("\n", " "));
        // INSTALLSIDES2 item 1: waiting on a restart is the pale blue pending tone.
        Assert.Equal(CellTone.Info, clientCell.Tone);
        Assert.True(File.Exists(reload), "nothing was deleted: the marker is the whole removal");
        // Revert: deciding penduninst after the installed-state read, or reusing the description for
        // an orange row, fails this.
    }

    // ---- Items 3 and 6: the confirmations ------------------------------------------------

    /// <summary>
    /// An uninstall asks first. The box names the mod and the version, says what the removal costs
    /// and that settings are kept, No changes nothing, and Yes marks the mod — which leaves the row
    /// orange with the file still on disk.
    /// What this would catch: a button that removes on the press, a box whose Yes deletes a loaded
    /// copy, or a No that closes the box and marks anyway.
    /// </summary>
    /// <summary>
    /// LOCALDETECT1 review: a confirmation is pinned to the server write root it opened against. When the root moves while
    /// it is open (the player joined another local server), its Yes writes nothing and says why; an unmoved root proceeds.
    /// </summary>
    [Fact]
    public void A_confirmation_refuses_when_the_server_folder_changed_while_it_was_open()
    {
        byte[] bytes = { 9, 9 };
        Ledgered(bytes);
        var entry = Listing("ExampleTeam", "PhialStore", "1.0.0");
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        pane.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        int marks = 0;
        pane.UninstallMarker = (_, _, _) =>
        {
            marks++;
            return new ServerInstallResult(true, "marked", new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, Array.Empty<string>());
        };
        string? root = @"D:\Servers\Alpha\BepInEx";
        pane.ServerWriteRootProbe = () => root;

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        root = @"D:\Servers\Bravo\BepInEx";
        Assert.True(pane.ServerRootMovedSincePrompt());
        Assert.False(pane.PromptYes());
        Assert.Equal(0, marks);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.Equal(InstallModsCopy.ServerFolderChanged, pane.ActionError);

        // Opened again against the new folder, nothing moved: the Yes goes ahead.
        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.False(pane.ServerRootMovedSincePrompt());
        Assert.True(pane.PromptYes());
        Assert.Equal(1, marks);
    }

    [Fact]
    public void The_uninstall_confirmation_asks_before_it_marks_and_No_changes_nothing()
    {
        byte[] bytes = { 9, 9 };
        string reload = Ledgered(bytes);
        var entry = Listing("ExampleTeam", "PhialStore", "1.0.0");
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        pane.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        int marks = 0;
        bool pending = false;
        pane.UninstallMarker = (_, mark, _) =>
        {
            marks++;
            pending = mark;
            return new ServerInstallResult(true, "marked", new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, Array.Empty<string>());
        };
        pane.UninstallPendingProbe = _ => pending;

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal(0, marks);
        Assert.Equal("Uninstall Phial Store from this game?", pane.PromptHeadingText(entry, InstallPromptKind.Uninstall));
        Assert.Equal(
            InstallModsCopy.UninstallRestartBody + " " + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));
        Assert.Equal(InstallModsCopy.PromptYesUninstall, pane.PromptYesText(InstallPromptKind.Uninstall));
        Assert.Equal(CellTone.Warn, InstallModsPane.PromptYesTone(InstallPromptKind.Uninstall));

        Assert.True(pane.PromptNo());
        Assert.Equal(0, marks);
        Assert.Equal(InstallPromptKind.None, pane.Prompt);
        Assert.Equal(InstallModsCopy.Installed, pane.ActionFor(entry).Label);

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.True(pane.PromptYes());
        Assert.Equal(1, marks);
        Assert.True(pending);
        Assert.True(File.Exists(reload), "the loaded copy stays until the next start");
        Assert.Equal(InstallListingState.PendingUninstall, pane.ActionFor(entry).State);
        Assert.Equal(1, pane.PendingUninstallCount());
        var removed = (Dictionary<string, object?>)pane.ActionToWidget(entry)["removed"]!;
        Assert.Equal(true, removed["pending"]);

        // Reactivate: its own box, and Yes drops the mark.
        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.Reactivate, pane.Prompt);
        Assert.Equal("Keep Phial Store installed on this game?", pane.PromptHeadingText(entry, InstallPromptKind.Reactivate));
        Assert.Equal(InstallModsCopy.ReactivateBody, pane.PromptBodyText(entry, InstallPromptKind.Reactivate));
        Assert.Equal(InstallModsCopy.PromptYesKeep, pane.PromptYesText(InstallPromptKind.Reactivate));
        Assert.Equal(CellTone.Ok, InstallModsPane.PromptYesTone(InstallPromptKind.Reactivate));
        Assert.True(pane.PromptYes());
        Assert.False(pending);
        Assert.Equal(InstallModsCopy.Installed, pane.ActionFor(entry).Label);
        Assert.Equal(0, pane.PendingUninstallCount());
        // Revert: marking on the press, or a Reactivate that deletes files, fails this.
    }

    /// <summary>
    /// INSTALL2 item 6: when other installed mods list this one as a dependency, the uninstall box
    /// says which, and its Yes reads "Yes, uninstall anyway" and passes force.
    /// What this would catch: asking the remover first and reporting the refusal afterwards, or a
    /// forced removal that never told the player what it breaks.
    /// </summary>
    [Fact]
    public void An_uninstall_that_breaks_other_mods_names_them_and_its_yes_forces()
    {
        byte[] bytes = { 1, 1 };
        Ledgered(bytes);
        var entry = Listing("ExampleTeam", "PhialStore", "1.0.0");
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        pane.SetInstalledQuery(InstalledState.Scan(
            Bep, null, new[] { new InstalledInventoryRow("PhialStore", "1.0.0", "Local", "44") }, "44"));
        pane.DependentsProbe = _ => new[] { "dyno-BeaconPoint", "dyno-Waypoints" };
        bool? forced = null;
        pane.UninstallMarker = (_, _, force) =>
        {
            forced = force;
            return new ServerInstallResult(true, "marked", new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, Array.Empty<string>());
        };

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(new[] { "dyno-BeaconPoint", "dyno-Waypoints" }, pane.PromptDependents.ToArray());
        string body = pane.PromptBodyText(entry, InstallPromptKind.Uninstall);
        Assert.Contains("Other installed mods need it: dyno-BeaconPoint, dyno-Waypoints.", body);
        Assert.Equal(InstallModsCopy.PromptYesUninstallAnyway, pane.PromptYesText(InstallPromptKind.Uninstall));
        var prompt = (Dictionary<string, object?>)pane.ActionToWidget(entry)["prompt"]!;
        Assert.Equal("uninstall", prompt["kind"]);
        Assert.Equal(InstallModsCopy.PromptYesUninstallAnyway, prompt["yes"]);
        Assert.Equal(2, ((IReadOnlyList<string>)prompt["dependents"]!).Count);

        Assert.True(pane.PromptYes());
        Assert.Equal(true, forced);
        Assert.Equal("", InstallModsCopy.DependentsWarning(Array.Empty<string>()));
        // Revert: dropping DependentsProbe from the open, or passing force:false from the prompt,
        // fails this.
    }

    /// <summary>
    /// A hot-loadable Progenitor mod keeps today's verified behavior: its confirmation says it
    /// unloads now, and Yes takes the reload copy back instead of writing a marker.
    /// What this would catch: marking a hot copy (it would stay loaded and be deleted at the next
    /// start, so Remove would appear to do nothing).
    /// </summary>
    [Fact]
    public void A_hot_loaded_mod_is_unloaded_now_and_never_marked()
    {
        byte[] bytes = { 5, 5 };
        string reload = Ledgered(bytes);
        var entry = Listing("ExampleTeam", "PhialStore", "1.0.0");
        var catalog = new FakeCatalog();
        catalog.Entries.Add(entry);
        var pane = Pane(catalog);
        pane.LoadedPluginsProbe = () => new[] { "Phial Store" };
        // INSTALL2 defect A: hot is the executor's own ledger flag, never the reloader's report.
        pane.HotLedgerProbe = _ => true;
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        int marks = 0, removals = 0;
        pane.UninstallMarker = (_, _, _) => { marks++; return ServerInstallResult.Refuse("should not be asked"); };
        pane.Remover = _ =>
        {
            removals++;
            File.Delete(reload);
            return new ServerInstallResult(
                true, "removed", new[] { "BepInEx/progenitor-reload/PhialStore.dll" },
                Array.Empty<string>(), noOp: false, hotUnloaded: true);
        };

        Assert.True(pane.OnInstallClick(entry.Key));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.Equal(
            InstallModsCopy.UninstallHotBody + " " + InstallModsCopy.SettingsKeptBody,
            pane.PromptBodyText(entry, InstallPromptKind.Uninstall));
        Assert.True(pane.PromptYes());
        Assert.Equal(0, marks);
        Assert.Equal(1, removals);
        Assert.False(File.Exists(reload));
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.HotLedgerProbe = _ => false;
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));
        Assert.Equal(InstallModsCopy.Install, pane.ActionFor(entry).Label);
        // Revert: routing a hot copy through the marker, or reading the reloader's report as hot,
        // fails this.
    }

    // ---- Item 4: the marker and the ledger flag -----------------------------------------

    /// <summary>
    /// The mark writes <c>&lt;file&gt;.uninstall</c> beside the reload copy and flags the ledger row;
    /// the file is untouched. The cancel removes both. The flag survives a ledger round-trip and is
    /// absent from an unmarked row's JSON.
    /// What this would catch: deleting the file on the mark, marking a pending row (whose install is
    /// canceled by deleting it), or a flag that does not round-trip.
    /// </summary>
    [Fact]
    public void Marking_writes_the_marker_and_the_ledger_flag_and_cancelling_removes_both()
    {
        byte[] bytes = { 8, 8 };
        string sha = InstalledState.HashBytes(bytes);
        string reload = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        string pendingFile = Write(Path.Combine(Bep, InstalledState.PendingFolder, "index", "Other.dll"), new byte[] { 3 });
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod, Source),
            Row("BepInEx/progenitor-pending/index/Other.dll", InstalledState.HashBytes(new byte[] { 3 }), 1, ServerInstallLedger.CategoryPending, Source),
        }).Write(_game);
        UninstallMarkers.ClockUtc = () => new DateTime(2026, 9, 13, 1, 2, 3, DateTimeKind.Utc);

        string marker = UninstallMarkers.MarkerFor(reload);
        Assert.Equal(reload + ".uninstall", marker);
        Assert.False(UninstallMarkers.IsPending(Bep, Source));

        var marked = UninstallMarkers.Mark(Bep, Source);
        Assert.True(marked.Ok);
        Assert.False(marked.NoOp);
        Assert.Equal(new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, marked.Removed.ToArray());
        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(reload), "the mark is a request, not a removal");
        Assert.True(File.Exists(pendingFile));
        Assert.False(File.Exists(UninstallMarkers.MarkerFor(pendingFile)), "a pending row never loaded, so it is canceled by deleting it");
        Assert.Equal("2026-09-13T01:02:03Z " + sha + "\n", File.ReadAllText(marker));
        Assert.True(UninstallMarkers.IsPending(Bep, Source));

        var after = ServerInstallLedger.TryRead(_game)!;
        var row = after.Files.Single(f => f.Path == "BepInEx/progenitor-reload/PhialStore.dll");
        Assert.True(row.UninstallPending);
        Assert.False(after.Files.Single(f => f.Category == ServerInstallLedger.CategoryPending).UninstallPending);
        Assert.Contains("\"uninstallPending\": true", after.ToJson());
        Assert.Single(after.ToJson().Split("uninstallPending").Skip(1));

        // An unmarked ledger's bytes are exactly what every build before this job wrote.
        Assert.DoesNotContain(
            "uninstallPending",
            new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[] { Row("BepInEx/x.dll", "aa", 1, ServerInstallLedger.CategoryMod, Source) }).ToJson());

        var cancelled = UninstallMarkers.Cancel(Bep, Source);
        Assert.True(cancelled.Ok);
        Assert.False(cancelled.NoOp);
        Assert.False(File.Exists(marker));
        Assert.True(File.Exists(reload));
        Assert.False(UninstallMarkers.IsPending(Bep, Source));
        Assert.False(ServerInstallLedger.TryRead(_game)!.Files.Single(f => f.Category == ServerInstallLedger.CategoryMod).UninstallPending);

        // A package the ledger does not name is a no-op, never a refusal that looks like a failure.
        var nothing = UninstallMarkers.Mark(Bep, "package:Some-Other@1.0.0");
        Assert.True(nothing.Ok);
        Assert.True(nothing.NoOp);
        Assert.Equal(UninstallMarkers.NothingToMark, nothing.Message);
        // Revert: deleting the file on the mark, marking pending rows, or writing the flag always
        // fails this.
    }

    /// <summary>
    /// Bootstrap's start step deletes every marked DLL and its marker, sweeps a marker whose package
    /// is already gone, and leaves everything else — including a DLL with no marker and a marker in a
    /// subfolder the reloaders never scan. The reload glob does not match a marker, which is why no
    /// reloader had to learn about this file.
    /// What this would catch: a step that runs after the promote (the mod would load one more time),
    /// a recursive sweep, or a glob that treats <c>*.dll.uninstall</c> as a plugin.
    /// </summary>
    [Fact]
    public void Bootstrap_removes_marked_reload_copies_before_the_promote()
    {
        string reload = Path.Combine(_game, "reload");
        string keep = Write(Path.Combine(reload, "Keep.dll"), new byte[] { 1 });
        string doomed = Write(Path.Combine(reload, "Doomed.dll"), new byte[] { 2 });
        Write(doomed + ".uninstall", new byte[] { 3 });
        string orphanMarker = Write(Path.Combine(reload, "Gone.dll.uninstall"), new byte[] { 4 });
        string deep = Write(Path.Combine(reload, "deps", "Nested.dll"), new byte[] { 5 });
        Write(deep + ".uninstall", new byte[] { 6 });

        // The reloaders' own glob: a marker is not a plugin.
        Assert.Equal(
            new[] { "Doomed.dll", "Keep.dll" },
            Directory.GetFiles(reload, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var info = new List<string>();
        var warn = new List<string>();
        var deleted = Progenitor.Bootstrap.PendingInstalls.RemoveMarkedAtStart(reload, info.Add, warn.Add);

        Assert.Equal(new[] { doomed }, deleted.ToArray());
        Assert.False(File.Exists(doomed));
        Assert.False(File.Exists(doomed + ".uninstall"));
        Assert.False(File.Exists(orphanMarker));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(deep), "a subfolder is not a place the reloader loads from");
        Assert.True(File.Exists(deep + ".uninstall"));
        Assert.Empty(warn);
        Assert.Contains(Progenitor.Bootstrap.PendingInstalls.MarkedRemovedLine("Doomed.dll"), info);
        Assert.Contains(Progenitor.Bootstrap.PendingInstalls.MarkedOrphanLine("Gone.dll"), info);
        Assert.Contains(Progenitor.Bootstrap.PendingInstalls.MarkedSummaryLine(1, 1, 0), info);

        // Idempotent: a second start has nothing left to do and logs nothing.
        info.Clear();
        Assert.Empty(Progenitor.Bootstrap.PendingInstalls.RemoveMarkedAtStart(reload, info.Add, warn.Add));
        Assert.Empty(info);
        Assert.Empty(Progenitor.Bootstrap.PendingInstalls.RemoveMarkedAtStart(Path.Combine(_game, "absent"), info.Add, warn.Add));

        // The step is wired ahead of the promote, on the start path Bootstrap actually runs.
        string src = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "PendingInstalls.cs");
        string start = SourcePin.MethodBody(src, "public static void PromoteAtStart()");
        SourcePin.AssertUncommented(start, "RemoveMarkedAtStart(");
        Assert.True(
            start.IndexOf("RemoveMarkedAtStart(", StringComparison.Ordinal) < start.IndexOf("PromoteAll(", StringComparison.Ordinal),
            "the removal must run before the promote");
        // Revert: calling the step after PromoteAll, or enumerating the reload root recursively,
        // fails this.
    }

    /// <summary>
    /// The ledger row of an honored removal is dropped by the reconcile the menu runs on every open —
    /// Bootstrap cannot read this ledger, so the row cannot be dropped where the file is deleted. A
    /// flagged row whose file is still there is still only a request and stays.
    /// What this would catch: dropping unflagged mod rows whose file is missing (an unrelated tool
    /// deleting a file would silently un-ledger it), or leaving the honored row forever.
    /// </summary>
    [Fact]
    public void The_reconcile_drops_a_flagged_row_once_its_file_is_gone()
    {
        byte[] bytes = { 6, 6 };
        string sha = InstalledState.HashBytes(bytes);
        string reload = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod, Source,
                new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), uninstallPending: true),
            Row("BepInEx/progenitor-reload/Other.dll", "aa", 1, ServerInstallLedger.CategoryMod, "package:Some-Other@1.0.0"),
        }).Write(_game);

        // The file is still there: the request stands.
        Assert.False(ServerInstall.ReconcilePromoted(Bep).Changed);
        Assert.True(ServerInstallLedger.TryRead(_game)!.Files.Any(f => f.UninstallPending));

        File.Delete(reload);
        var result = ServerInstall.ReconcilePromoted(Bep);
        Assert.Equal(new[] { "BepInEx/progenitor-reload/PhialStore.dll" }, result.Dropped.ToArray());
        var after = ServerInstallLedger.TryRead(_game)!;
        Assert.DoesNotContain("PhialStore", after.ToJson());
        // The unflagged row whose file was never there is not this pass's business.
        Assert.Single(after.Files);
        Assert.Equal("BepInEx/progenitor-reload/Other.dll", after.Files[0].Path);
        // Revert: dropping every mod row whose file is missing fails this.
    }

    /// <summary>
    /// REQRULE4 (live 2026-10-08): a test server's ledger still listed HotDemo's DLL under the plugin id it had before the
    /// rename, the file gone, beside the current row. The reconcile drops an orphan row only when a newer row of the same
    /// package names a file that is there; a package whose only record lost its file keeps it (the pin above), and no file
    /// is ever deleted.
    /// </summary>
    [Fact]
    public void The_reconcile_drops_an_orphan_row_a_newer_row_of_the_same_package_supersedes()
    {
        byte[] bytes = { 4, 2 };
        string sha = InstalledState.HashBytes(bytes);
        string current = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "example.hotdemo.dll"), bytes);
        DateTime old = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), later = old.AddDays(20);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            // The pre-rename row: its file is gone.
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/old.example.hotdemo.dll", "aa", 1, ServerInstallLedger.CategoryMod,
                "package:Example-HotDemo@0.1.0", old),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/example.hotdemo.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod,
                "package:Example-HotDemo@0.1.0", later),
            // Another package's only row whose file is gone, and a framework row: not this rule's business.
            Row("BepInEx/progenitor-reload/Other.dll", "bb", 1, ServerInstallLedger.CategoryMod, "package:Some-Other@1.0.0"),
            Row("BepInEx/plugins/Progenitor.Bootstrap.dll", "cc", 1, ServerInstallLedger.CategoryFramework, "client-image:cc"),
        }).Write(_game);

        var result = ServerInstall.ReconcilePromoted(Bep);
        Assert.Equal(new[] { "BepInEx/progenitor-reload/old.example.hotdemo.dll" }, result.Dropped.ToArray());
        var after = ServerInstallLedger.TryRead(_game)!.Files.Select(f => f.Path).ToArray();
        Assert.Equal(new[] { "BepInEx/progenitor-reload/example.hotdemo.dll", "BepInEx/progenitor-reload/Other.dll", "BepInEx/plugins/Progenitor.Bootstrap.dll" }, after);
        Assert.True(File.Exists(current));
        // Idempotent.
        Assert.False(ServerInstall.ReconcilePromoted(Bep).Changed);

        // An older live row never supersedes a newer orphan (it may be the record of a later install that failed).
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/new.example.hotdemo.dll", "aa", 1, ServerInstallLedger.CategoryMod,
                "package:Example-HotDemo@0.2.0", later.AddDays(1)),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/example.hotdemo.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod,
                "package:Example-HotDemo@0.1.0", later),
        }).Write(_game);
        Assert.False(ServerInstall.ReconcilePromoted(Bep).Changed);
    }

    /// <summary>
    /// F38 (live 2026-10-08): the test server's real ledger. The pre-rename row's package was <c>OldOwner-HotDemo</c> and
    /// the current one <c>Progenitor-HotDemo</c>, so the same-key rule never fired. The same package name under another
    /// owner supersedes the orphan too; another package name never does.
    /// </summary>
    [Fact]
    public void The_reconcile_drops_the_pre_rename_HotDemo_record_from_the_test_server_ledger()
    {
        byte[] bytes = { 5, 8 };
        string sha = InstalledState.HashBytes(bytes);
        Write(Path.Combine(Bep, InstalledState.ReloadFolder, "progenitor.hotdemo.dll"), bytes);
        Write(Path.Combine(Bep, InstalledState.ReloadFolder, "AutoLatchGates.dll"), bytes);
        new ServerInstallLedger("", "", "", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/AutoLatchGates.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod,
                "package:Modix-AutoLatchGates@3.1.0", new DateTime(2026, 9, 13, 10, 55, 44, DateTimeKind.Utc)),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/oldowner.progenitor.hotdemo.dll", "f0b8", 8192, ServerInstallLedger.CategoryMod,
                "package:OldOwner-HotDemo@0.1.0", new DateTime(2026, 10, 3, 22, 17, 34, DateTimeKind.Utc), pluginId: "oldowner.progenitor.hotdemo"),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/progenitor.hotdemo.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod,
                "package:Progenitor-HotDemo@0.1.0", new DateTime(2026, 10, 8, 12, 47, 45, DateTimeKind.Utc), pluginId: "progenitor.hotdemo"),
            // A gone file of another package name stays (the pin of the reconcile above).
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/Gone.dll", "aa", 1, ServerInstallLedger.CategoryMod,
                "package:Some-Gone@1.0.0", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
        }).Write(_game);

        var result = ServerInstall.ReconcilePromoted(Bep);
        Assert.Equal(new[] { "BepInEx/progenitor-reload/oldowner.progenitor.hotdemo.dll" }, result.Dropped.ToArray());
        Assert.Equal(new[] { "BepInEx/progenitor-reload/AutoLatchGates.dll", "BepInEx/progenitor-reload/progenitor.hotdemo.dll", "BepInEx/progenitor-reload/Gone.dll" },
            ServerInstallLedger.TryRead(_game)!.Files.Select(f => f.Path).ToArray());
        Assert.True(ServerInstall.SamePackageName("OldOwner-HotDemo", "Progenitor-HotDemo"));
        Assert.False(ServerInstall.SamePackageName("OldOwner-HotDemo", "OldOwner-HotDemo2"));
        Assert.False(ServerInstall.SamePackageName("HotDemo", "Other-HotDemo"));
    }

    // ---- Item 5: the pending bar --------------------------------------------------------

    /// <summary>
    /// The bar counts both kinds, its text expands a list of one row per pending mod in its own
    /// color, and the readback carries the counts, the open flag and the items.
    /// What this would catch: counting an uninstalled-but-pending row as a pending install, a list
    /// that is visible before the bar is pressed, or a bar with no Restart now stub.
    /// </summary>
    [Fact]
    public void The_pending_bar_counts_both_kinds_and_expands_to_the_list()
    {
        byte[] bytes = { 7, 3 };
        Ledgered(bytes);
        var loads = Listing("ExampleTeam", "PhialStore", "1.0.0");
        var goes = Listing("ExampleTeam", "CryptSnooze", "2.0.4");
        var catalog = new FakeCatalog();
        catalog.Entries.AddRange(new[] { loads, goes });
        var pane = Pane(catalog);
        pane.UninstallPendingProbe = e => string.Equals(e.Key, goes.Key, StringComparison.Ordinal);
        pane.SetInstalledQuery(InstalledState.Scan(Bep, null, null, "44"));

        Assert.Equal(1, pane.PendingCount());
        Assert.Equal(1, pane.PendingUninstallCount());
        Assert.Equal(
            "1 mod installed · 1 uninstalled — restart the game to apply. »",
            InstallModsCopy.PendingBar(1, 1));

        var panel = NewPanel();
        pane.Build(panel, true, false, null, () => { }, () => { }, DateTime.UtcNow);
        var bar = pane.PendingBarHandles;
        Assert.Equal(3, bar.Count);
        Assert.Equal(InstallModsCopy.PendingBar(1, 1), pane.PendingBarText);
        Assert.False(bar[1].Visible);
        Assert.False(bar[2].Visible);

        pane.TogglePendingList();
        Assert.True(pane.PendingListOpen);
        Assert.True(bar[1].Visible && bar[2].Visible);
        var states = bar.Skip(1).Cast<IUiCellRow>()
            .ToDictionary(
                r => r.Key,
                r => r.Cells.Single(c => c.Key == InstallModsPane.CellPendingState));
        Assert.Equal(
            InstallModsCopy.PendingItemToLoad,
            states[InstallModsPane.PendingItemKeyPrefix + loads.Key].Text);
        Assert.Equal(CellTone.Pending, states[InstallModsPane.PendingItemKeyPrefix + loads.Key].Tone);
        Assert.Equal(
            InstallModsCopy.PendingItemToUninstall,
            states[InstallModsPane.PendingItemKeyPrefix + goes.Key].Text);
        Assert.Equal(CellTone.Warn, states[InstallModsPane.PendingItemKeyPrefix + goes.Key].Tone);

        Assert.True(pane.TryDescribeSidebarRow(bar[0], out var extra));
        var readback = (Dictionary<string, object?>)extra!["pending"]!;
        // POLISH1 defect H added `sides` between `uninstall` and `open`; F20 added `stop` last.
        Assert.Equal(new[] { "count", "uninstall", "sides", "open", "items", "stop" }, readback.Keys.ToArray());
        Assert.Equal(1, readback["count"]);
        Assert.Equal(1, readback["uninstall"]);
        Assert.Equal(InstallModsCopy.SidesClient, readback["sides"]);
        Assert.Equal(true, readback["open"]);
        var items = (List<Dictionary<string, object?>>)readback["items"]!;
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => Equals(i["key"], goes.Key) && Equals(i["state"], "penduninst"));
        Assert.Contains(items, i => Equals(i["key"], loads.Key) && Equals(i["state"], "pending"));
        pane.TogglePendingList();
        Assert.False(pane.PendingListOpen);
        // Revert: counting a marked row in PendingCount, or building the item rows visible, fails this.
    }

    // ---- The MODSPAGE1 seam --------------------------------------------------------------

    /// <summary>
    /// INSTALL2 implements the seam MODSPAGE1 declared, keyed by plugin id: the page's pending read
    /// and its three actions reach the same ledger the Install tab writes, and a plugin no listing
    /// accounts for is refused rather than claimed.
    /// What this would catch: a seam that removes files for a plugin Progenitor never installed, or
    /// one that reports "not pending" by throwing.
    /// </summary>
    /// <summary>
    /// F27 (live 2026-10-07): a mod whose listing left the index is still uninstalled, reactivated and cancelled from its
    /// ledger rows alone; a Cancel install of a copy that already loaded never deletes it and says so; each action logs.
    /// Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_mod_whose_listing_left_the_index_is_acted_on_from_its_ledger_rows()
    {
        var service = new InstallCatalogService(Bep, new NoTransport(), null, new ImmediateClock());
        byte[] bytes = { 2, 4 };
        Ledgered(bytes);
        string reload = Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll");
        var row = new InstalledLedgerRow("ExampleTeam-PhialStore", InstalledState.HashBytes(bytes), "1.0.0", "PhialStore.dll", source: Source,
            category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: reload);
        var seam = new CatalogUninstallSeam(service) { LedgerRowsProbe = id => id == "example.phialstore" ? new[] { row } : Array.Empty<InstalledLedgerRow>() };

        Assert.True(seam.Owns("example.phialstore"));
        Assert.False(seam.Owns("other.mod"));
        var marked = seam.MarkUninstall("example.phialstore", hot: false);
        Assert.True(marked.Ok, marked.Message);
        Assert.True(File.Exists(reload + ".uninstall"));
        Assert.True(seam.IsUninstallPending("example.phialstore"));
        Assert.True(seam.CancelUninstall("example.phialstore").Ok);
        Assert.False(File.Exists(reload + ".uninstall"));

        var cancel = seam.CancelPendingInstall("example.phialstore");
        Assert.False(cancel.Ok);
        Assert.Equal(CatalogUninstallSeam.AlreadyLoaded, cancel.Message);
        Assert.True(File.Exists(reload));
    }

    /// <summary>
    /// Review 2026-10-07 (M4, L9): cancelling a pending update deletes only that pending copy; the loaded version of the same
    /// package stays, and so does a ledgered path outside BepInEx.
    /// </summary>
    [Fact]
    public void Cancelling_a_pending_update_keeps_the_loaded_version()
    {
        byte[] loadedBytes = { 1, 0 };
        byte[] pendingBytes = { 1, 1 };
        string loaded = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), loadedBytes);
        string pending = Write(Path.Combine(Bep, InstalledState.PendingFolder, "index", "PhialStore.dll"), pendingBytes);
        string outside = Write(Path.Combine(_game, "outside", "PhialStore.dll"), pendingBytes);
        const string v11 = "package:ExampleTeam-PhialStore.1.0";
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            Row("BepInEx/progenitor-reload/PhialStore.dll", InstalledState.HashBytes(loadedBytes), 2, ServerInstallLedger.CategoryMod, Source),
            Row("BepInEx/progenitor-pending/index/PhialStore.dll", InstalledState.HashBytes(pendingBytes), 2, ServerInstallLedger.CategoryPending, v11),
            Row("outside/PhialStore.dll", InstalledState.HashBytes(pendingBytes), 2, ServerInstallLedger.CategoryPending, v11),
        }).Write(_game);

        var result = ServerInstall.RemovePendingOnly(Bep, v11);
        Assert.True(result.Ok, result.Message);
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(loaded));
        Assert.True(File.Exists(outside));
        Assert.Contains("outside/PhialStore.dll", result.Skipped);
        var after = ServerInstallLedger.TryRead(_game)!;
        Assert.Contains(after.Files, f => f.Category == ServerInstallLedger.CategoryMod);
    }

    /// <summary>
    /// F36 (live 2026-10-08: progenitor.film was installed from Progenitor-FilmRestart while Progenitor-FilmExample named the
    /// same plugin id; Uninstall said "Removes at the next restart" and nothing was marked): the seam acts on the package the
    /// install record names, a mark that changed nothing never reads as success, and two packages named for one plugin are
    /// acted on through their rows rather than a guess. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void An_uninstall_follows_the_package_its_ledger_names_and_a_no_op_is_never_success()
    {
        var service = new InstallCatalogService(Bep, new NoTransport(), null, new ImmediateClock());
        Assert.True(service.LoadIndexFromJson(TwoListingsJson(), "stamp").Ok);
        string reload = Ledgered(new byte[] { 2, 4 });
        string marker = reload + ".uninstall";
        // The index answers the other listing for this id (here the id is that listing's own key).
        const string id = "ExampleTeam-PhialStoreExample";
        Assert.Equal(id, service.FindEntryForPlugin(id)!.Key);

        // Before F36: the index's listing was acted on; nothing of it is installed, so nothing was marked.
        var unguided = new CatalogUninstallSeam(service).UninstallSides(id, hot: false, client: true, server: false);
        Assert.True(unguided.Ok);
        Assert.True(unguided.NoOp);
        Assert.False(File.Exists(marker));
        Assert.Equal(ModPageCopy.NothingChangedNote, ModPageCopy.SeamNote(unguided, ModPageCopy.RemovesAtRestartNote));

        // The ledger's package first: the installed copy is marked, and the success sentence is earned.
        var seam = new CatalogUninstallSeam(service) { LedgerKeysProbe = p => p == id ? new[] { "ExampleTeam-PhialStore" } : Array.Empty<string>() };
        var marked = seam.UninstallSides(id, hot: false, client: true, server: false);
        Assert.True(marked.Ok, marked.Message);
        Assert.False(marked.NoOp);
        Assert.True(File.Exists(marker));
        Assert.True(seam.IsUninstallPending(id));
        Assert.Equal(ModPageCopy.RemovesAtRestartNote, ModPageCopy.SeamNote(marked, ModPageCopy.RemovesAtRestartNote));
        Assert.True(seam.CancelUninstall(id).Ok);
        Assert.False(File.Exists(marker));

        // Two packages named for one plugin: no listing is guessed; the ledger rows themselves are marked.
        var row = new InstalledLedgerRow("ExampleTeam-PhialStore", InstalledState.HashBytes(new byte[] { 2, 4 }), "1.0.0", "PhialStore.dll",
            source: Source, category: ServerInstallLedger.CategoryMod, root: InstalledState.ClientRoot, filePath: reload);
        var both = new CatalogUninstallSeam(service)
        {
            LedgerKeysProbe = _ => new[] { "ExampleTeam-PhialStore", "ExampleTeam-PhialStoreExample" },
            LedgerRowsProbe = _ => new[] { row },
        };
        var byRows = both.UninstallSides(id, hot: false, client: true, server: false);
        Assert.True(byRows.Ok, byRows.Message);
        Assert.False(byRows.NoOp);
        Assert.True(File.Exists(marker));

        // A refusal keeps its own reason.
        Assert.Equal("why", ModPageCopy.SeamNote(ServerInstallResult.Refuse("why"), ModPageCopy.RemovesAtRestartNote));
    }

    private static string TwoListingsJson() =>
        "[" + ListingJson("PhialStore") + "," + ListingJson("PhialStoreExample") + "]";

    private static string ListingJson(string name) =>
        "{\"name\":\"" + name + "\",\"full_name\":\"ExampleTeam-" + name + "\",\"owner\":\"ExampleTeam\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/ExampleTeam/" + name + "/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"" + name + "\",\"full_name\":\"ExampleTeam-" + name + "-1.0.0\",\"version_number\":\"1.0.0\","
        + "\"description\":\"A phial store.\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/ExampleTeam/" + name + "/1.0.0/\","
        + "\"file_size\":10,\"is_active\":true}]}";

    [Fact]
    public void The_mod_page_seam_maps_a_plugin_id_to_the_listing_it_came_from()
    {
        var service = new InstallCatalogService(Bep, new NoTransport(), null, new ImmediateClock());
        IModUninstallSeam seam = new CatalogUninstallSeam(service);

        // Nothing is installed and the index is empty: every call is refused, and nothing throws.
        Assert.False(seam.IsUninstallPending("example.phialstore"));
        var refused = seam.MarkUninstall("example.phialstore", hot: false);
        Assert.False(refused.Ok);
        Assert.Equal(CatalogUninstallSeam.NotOurs, refused.Message);
        Assert.False(seam.CancelUninstall("example.phialstore").Ok);
        Assert.False(seam.CancelPendingInstall("example.phialstore").Ok);

        // With the listing in the index and a ledgered reload copy, the marker route is reached.
        Assert.True(service.LoadIndexFromJson(IndexJson(), "stamp").Ok);
        byte[] bytes = { 2, 4 };
        Ledgered(bytes);
        Assert.False(seam.IsUninstallPending("ExampleTeam-PhialStore"));
        var marked = seam.MarkUninstall("ExampleTeam-PhialStore", hot: false);
        Assert.True(marked.Ok, marked.Message);
        Assert.False(marked.NoOp);
        Assert.True(seam.IsUninstallPending("ExampleTeam-PhialStore"));
        Assert.True(File.Exists(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll") + ".uninstall"));

        Assert.True(seam.CancelUninstall("ExampleTeam-PhialStore").Ok);
        Assert.False(seam.IsUninstallPending("ExampleTeam-PhialStore"));
        Assert.False(File.Exists(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll") + ".uninstall"));

        // INSTALL2 defect A: the page's own `hot` reading is narrowed here, never trusted. This row
        // carries no `hot` ledger flag, so it is a promoted ordinary mod and is MARKED even though the
        // caller said hot — the live defect deleted the loaded file instead.
        string reload = Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll");
        var claimed = seam.MarkUninstall("ExampleTeam-PhialStore", hot: true);
        Assert.True(claimed.Ok, claimed.Message);
        Assert.True(File.Exists(reload), "a promoted copy is not this process's to unload");
        Assert.True(File.Exists(reload + ".uninstall"));
        Assert.True(seam.IsUninstallPending("ExampleTeam-PhialStore"));
        Assert.True(seam.CancelUninstall("ExampleTeam-PhialStore").Ok);

        // A copy this process hot loaded — the executor's `hot` flag — does take the plain removal:
        // nothing survives to the next start to be marked.
        HotLedgered(new byte[] { 2, 4 });
        var hot = seam.MarkUninstall("ExampleTeam-PhialStore", hot: true);
        Assert.True(hot.Ok, hot.Message);
        Assert.False(File.Exists(reload));
        // Revert: resolving by package name, answering a plugin the index does not carry, or trusting
        // the caller's hot flag, fails this.
    }

    // ---- Helpers --------------------------------------------------------------------------

    private IUiPanel NewPanel()
    {
        var panel = UiApi.Panel("Install mods", _owner, "example.install2");
        _panels.Add(panel);
        return panel;
    }

    private InstallModsPane Pane(FakeCatalog catalog)
    {
        var pane = new InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.LoadedPluginsProbe = () => Array.Empty<string>();
        pane.UninstallPendingProbe = _ => false;
        return pane;
    }

    /// <summary>One ledgered promoted copy of the PhialStore listing on this root.</summary>
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

    /// <summary>
    /// INSTALL2 defect A: the same copy, ledgered with the <c>hot</c> flag the HotLoadExecutor writes —
    /// i.e. one THIS process wrote into the reload folder and can hot unload again.
    /// </summary>
    private string HotLedgered(byte[] bytes)
    {
        string sha = InstalledState.HashBytes(bytes);
        // Production order: the executor's own append sweeps this root's stale flags first and then
        // writes its own, so the sweep can never eat the flag the running process just set.
        ServerInstall.ClearHotFlags(Bep);
        string path = Write(Path.Combine(Bep, InstalledState.ReloadFolder, "PhialStore.dll"), bytes);
        new ServerInstallLedger("1.0", "b", "c", DateTime.UtcNow, new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/progenitor-reload/PhialStore.dll", sha, bytes.Length, ServerInstallLedger.CategoryMod,
                Source, new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), uninstallPending: false, hot: true),
        }).Write(_game);
        return path;
    }

    private static ServerInstallLedgerEntry Row(string path, string sha, long size, string category, string source) =>
        new(path, sha, size, category, source, new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc));

    private static string Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string IndexJson() =>
        "[{\"name\":\"PhialStore\",\"full_name\":\"ExampleTeam-PhialStore\",\"owner\":\"ExampleTeam\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/ExampleTeam/PhialStore/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"PhialStore\",\"full_name\":\"ExampleTeam-PhialStore-1.0.0\",\"version_number\":\"1.0.0\","
        + "\"description\":\"A phial store.\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/ExampleTeam/PhialStore/1.0.0/\","
        + "\"file_size\":10,\"is_active\":true}]}]";

    private static CatalogEntry Listing(string owner, string name, string version) =>
        new(
            new ManifestMod(
                name, version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None,
                true, owner, owner + "-" + name),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-12T00:00:00Z",
            displayName: name == "PhialStore" ? "Phial Store" : name,
            owner: owner,
            packageName: name,
            side: PackageSide.Client,
            description: name + " does a thing.");

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

    private sealed class FakeCatalog : IInstallCatalog
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
