using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// AUTOUPDATE2 (M6.2), game-free: the apply window, the automatic policy (per-mod modes, majors, new dependencies, the
/// failed list), updates through the version store (hot, restart, superseding a queued copy and another install's pending
/// copy), Roll back, the automatic rollback after a failed load (hot, and after a restart), the notice and the file.
/// A "DLL" is a text file <c>guid|version|salt</c>, as in the version store's own tests.
/// </summary>
[Collection("ClientUpdates")]
public sealed class ClientAutoUpdateTests : IDisposable
{
    private const string Id = "team.chat";
    private const string Package = "Team-Chat";

    private readonly string _game;
    private readonly ModVersionPaths _paths;
    private readonly Runtime _rt;

    public ClientAutoUpdateTests()
    {
        _game = Path.Combine(Path.GetTempPath(), "pau2-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx", "progenitor-reload"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx", "config"));
        _paths = new ModVersionPaths(Path.Combine(_game, "BepInEx"));
        _rt = new Runtime(_paths);
        ClientUpdates.Reset();
        ClientAutoUpdate.Reset();
        ModVersionJoin.ResetForTests();
        ClientUpdates.BepInExRootProbe = () => _paths.BepInExRoot;
        ClientUpdates.GameRootProbe = () => _game;
        ClientUpdates.RunOff = work => Task.FromResult(work());
        ClientUpdates.PostMain = a => a();
    }

    public void Dispose()
    {
        ClientUpdates.Reset();
        ClientAutoUpdate.Reset();
        ModVersionJoin.ResetForTests();
        try { Directory.Delete(_game, recursive: true); }
        catch { /* temp */ }
    }

    // ---- rig -------------------------------------------------------------------------------------

    private static byte[] Dll(string version, string salt = "") => Encoding.UTF8.GetBytes(Id + "|" + version + "|" + salt);

    private ModVersionService Store()
    {
        var env = new ModVersionEnv(_paths) { Books = new ModVersionBooks(_paths, Path.Combine(_paths.BepInExRoot, "config", "no-record.txt")) };
        return new ModVersionService(new ModVersionEngine(env), _rt);
    }

    /// <summary>The active copy 1.0.0 in progenitor-reload with its ledger row (what an install from the Install tab leaves).</summary>
    private string InstallActive(string version = "1.0.0")
    {
        string path = Path.Combine(_paths.Reload, "Chat.dll");
        byte[] bytes = Dll(version);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        WriteLedger(new ServerInstallLedgerEntry("BepInEx/progenitor-reload/Chat.dll", Sha(bytes), bytes.Length, "mod",
            "package:" + Package + "@" + version, DateTime.UtcNow, pluginId: Id));
        return path;
    }

    private void WriteLedger(params ServerInstallLedgerEntry[] rows)
    {
        var old = ServerInstallLedger.TryRead(_game);
        var list = old == null ? new List<ServerInstallLedgerEntry>() : old.Files.ToList();
        foreach (var r in rows)
        {
            list.RemoveAll(f => string.Equals(f.Path, r.Path, StringComparison.OrdinalIgnoreCase));
            list.Add(r);
        }

        new ServerInstallLedger("1", "", "", DateTime.UtcNow, list).Write(_game);
    }

    private List<ServerInstallLedgerEntry> Ledger() => ServerInstallLedger.TryRead(_game)?.Files.ToList() ?? new List<ServerInstallLedgerEntry>();

    private static StoreUpdateRequest Update(string version, bool hot = true) => new()
    {
        Package = Package,
        Version = version,
        PluginId = Id,
        Bytes = Dll(version),
        HotImage = hot,
        NotHotReason = hot ? "" : "the package does not declare hotLoadable",
        Ledger = new ParkedLedgerRow { Category = "mod", Source = "package:" + Package + "@" + version, PluginId = Id },
    };

    private bool Remove(string package, string path, out string message) => PendingSupersede.TryRemove(_game, package, path, out message);

    private static string Sha(byte[] bytes) => BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();

    private static ApplyWindowFacts OpenWindow() => new() { AtTitle = true };

    // ---- the apply window -------------------------------------------------------------------------

    [Fact]
    public void The_apply_window_is_open_only_at_the_title_screen_with_nothing_running()
    {
        Assert.Equal("", ApplyWindow.Closed(OpenWindow()));
        Assert.Equal("in a world", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, InWorld = true }));
        Assert.Equal("a join is in progress", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, Joining = true }));
        Assert.Equal("the pre-join prompt holds a join", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, PreJoinHolding = true }));
        Assert.Equal("a private game is hosted", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, Hosting = true }));
        Assert.Equal("not at the title screen", ApplyWindow.Closed(new ApplyWindowFacts()));
        Assert.StartsWith("the loader is busy", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, LoaderBusy = true }));
        Assert.Equal("the version store is not available", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, StoreMissing = true }));
        Assert.Equal("a version swap is being finished", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, StoreBusy = true }));
        Assert.StartsWith("your versions are still being restored", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, RestoreDue = true }));
        Assert.Equal("another update is running", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, RunnerBusy = true }));
        // A world always wins, even with every other fact clear and the title flag set.
        Assert.Equal("in a world", ApplyWindow.Closed(new ApplyWindowFacts { AtTitle = true, InWorld = true, RunnerBusy = true }));
    }

    [Fact]
    public void A_closed_window_starts_no_pass_and_an_open_one_starts_one_per_version()
    {
        InstallActive();
        ClientUpdates.EntriesProbe = () => new[] { Entry("1.1.0") };
        ClientUpdates.Recheck("test");
        Assert.True(ClientUpdates.Report.Items.Single().Auto);
        double now = 0;
        ClientAutoUpdate.Clock = () => now;
        var facts = new ApplyWindowFacts { AtTitle = true, InWorld = true };
        ClientAutoUpdate.WindowProbe = () => facts;
        var calls = new List<string>();
        ClientUpdates.Pipeline = new UpdatePipeline
        {
            Inspect = (k, _) => { calls.Add("inspect " + k); return Task.FromResult(new InspectCallResult(false, "offline", null, null)); },
        };

        ClientAutoUpdate.Tick();
        now = 20;
        Assert.StartsWith("window closed: in a world", ClientAutoUpdate.Tick());
        Assert.Empty(calls);

        facts = OpenWindow();
        Assert.StartsWith("started", ClientAutoUpdate.Tick());
        SpinUntil(() => !ClientUpdates.Busy);
        Assert.Equal(new[] { "inspect Team-Chat" }, calls);
        // Attempted this session: not again, even with the window open.
        Assert.Equal("nothing due", ClientAutoUpdate.Tick());
        Assert.Single(calls);
        Assert.Equal(1, ClientAutoUpdate.ForgetAttempts());
    }

    // ---- the policy ---------------------------------------------------------------------------------

    private static CatalogEntry Entry(string version) =>
        new(new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "Team", Package),
            CatalogProvenance.AdaptedListing, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore,
            "2026-10-09T00:00:00Z", displayName: "Chat", owner: "Team", packageName: Package);

    private static UpdateReport Detect(UpdatePins file, UpdateMode global, bool majors, string installed = "1.0.0", string latest = "1.1.0",
        params string[] deps)
    {
        var m = new ManagedPackage { Key = Package, Version = installed };
        m.PluginIds.Add(Id);
        var index = new Dictionary<string, IndexListing>(StringComparer.OrdinalIgnoreCase)
        {
            [Package] = new IndexListing { Key = Package, Name = "Chat", Latest = latest, Dependencies = deps },
        };
        return UpdateDetection.Detect(new[] { m }, index, file, global, majors, _ => false, false, DateTime.UtcNow);
    }

    [Fact]
    public void The_mods_own_mode_wins_over_the_global_one()
    {
        Assert.True(Detect(new UpdatePins(), UpdateMode.Automatic, false).Items.Single().Auto);
        Assert.Equal(UpdatePolicy.WhyNotify, Detect(new UpdatePins(), UpdateMode.Notify, false).Items.Single().NotAutoReason);

        var notify = new UpdatePins();
        notify.Modes[Package] = "Notify";
        var item = Detect(notify, UpdateMode.Automatic, false).Items.Single();
        Assert.False(item.Auto);
        Assert.Equal("Notify", item.PerMod);

        var auto = new UpdatePins();
        auto.Modes[Package] = "Automatic";
        Assert.True(Detect(auto, UpdateMode.Notify, false).Items.Single().Auto);

        var off = new UpdatePins();
        off.Modes[Package] = "Off";
        var r = Detect(off, UpdateMode.Automatic, false);
        Assert.Empty(r.Items);
        Assert.Contains(r.Skipped, s => s.Package == Package && s.Reason == UpdateDetection.SkipOff);
    }

    [Fact]
    public void Majors_new_dependencies_failed_versions_and_pins_only_notify()
    {
        var major = Detect(new UpdatePins(), UpdateMode.Automatic, false, "1.4.2", "2.0.0").Items.Single();
        Assert.False(major.Auto);
        Assert.Equal(UpdatePolicy.WhyMajor, major.NotAutoReason);
        Assert.True(Detect(new UpdatePins(), UpdateMode.Automatic, true, "1.4.2", "2.0.0").Items.Single().Auto);
        Assert.False(UpdateSettings.MajorAutomatic(UpdateSettings.DefaultMajor));

        var deps = Detect(new UpdatePins(), UpdateMode.Automatic, false, "1.0.0", "1.1.0", "dyno-ExampleCommandFramework-0.9.0").Items.Single();
        Assert.False(deps.Auto);
        Assert.Contains("dyno-ExampleCommandFramework", deps.NotAutoReason);

        var failed = new UpdatePins();
        failed.AddFailed(Package, "1.1.0", "failed to load: it did not load within 15 seconds", DateTime.UtcNow);
        var f = Detect(failed, UpdateMode.Automatic, false).Items.Single();
        Assert.False(f.Auto);
        Assert.Contains("failed to load before", f.Line);
        // The next newer version is tried.
        Assert.True(Detect(failed, UpdateMode.Automatic, false, "1.0.0", "1.2.0").Items.Single().Auto);

        var pinned = new UpdatePins();
        pinned.Pins[Package] = "1.0.0";
        var p = Detect(pinned, UpdateMode.Automatic, false).Items.Single();
        Assert.False(p.Auto);
        Assert.Equal(UpdatePolicy.WhyKept, p.NotAutoReason);
    }

    [Fact]
    public void The_file_keeps_pins_modes_the_failed_list_and_start_checks()
    {
        var f = new UpdatePins();
        f.Pins[Package] = "1.0.0";
        f.Modes["Team-Maps"] = "notify";
        f.AddFailed(Package, "1.1.0", "first", new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc));
        f.AddFailed(Package, "1.1.0", "again", new DateTime(2026, 10, 9, 2, 0, 0, DateTimeKind.Utc));
        for (int i = 0; i < 20; i++) f.AddFailed("Team-Maps", "1." + i + ".0", "x", DateTime.UtcNow);
        f.Verify[Package] = new StartCheck { PluginId = Id, Version = "1.2.0", Sha256 = "ab", Previous = "1.1.0", Utc = DateTime.UtcNow };

        var back = UpdatePins.Parse(f.ToJson());
        Assert.Equal("1.0.0", back.Pins[Package]);
        Assert.Equal(UpdateMode.Notify, back.ModeOf("Team-Maps"));
        Assert.Equal("Notify", back.Modes["Team-Maps"]);
        Assert.Equal("again", Assert.Single(back.Failed[Package]).Reason);
        Assert.Equal(UpdatePins.MaxFailedPerMod, back.Failed["Team-Maps"].Count);
        Assert.Equal("1.4.0", back.Failed["Team-Maps"][0].Version);
        Assert.Equal("1.1.0", back.Verify[Package].Previous);
        Assert.Null(back.ModeOf("Team-None"));
        // A phase 1 file (pins only) still reads.
        Assert.Equal("1.0.0", UpdatePins.Parse("{\"schema\":1,\"pins\":{\"Team-Chat\":\"1.0.0\"}}").Pins[Package]);
    }

    [Fact]
    public void The_per_mod_choice_and_the_failed_list_are_saved_and_cleared()
    {
        Assert.True(ClientAutoUpdate.SetModeChoice(Package, "Keep", "1.0.0", out _));
        Assert.Equal("Keep", ClientAutoUpdate.ModeChoiceOf(Package));
        Assert.Equal("1.0.0", ClientAutoUpdate.ReadFile().Pins[Package]);
        Assert.True(ClientAutoUpdate.SetModeChoice(Package, "Notify", "1.0.0", out _));
        Assert.Equal("Notify", ClientAutoUpdate.ModeChoiceOf(Package));
        Assert.False(ClientAutoUpdate.ReadFile().Pins.ContainsKey(Package));
        Assert.True(ClientAutoUpdate.SetModeChoice(Package, "Global", "1.0.0", out _));
        Assert.Equal("Global", ClientAutoUpdate.ModeChoiceOf(Package));
        Assert.False(ClientAutoUpdate.SetModeChoice(Package, "Keep", "", out string why));
        Assert.Contains("not known", why);

        ClientAutoUpdate.RecordFailed(Package, "1.1.0", "failed to load");
        Assert.Equal("1.1.0", ClientAutoUpdate.FailedOf(Package).Single().Version);
        Assert.Equal(1, ClientAutoUpdate.ClearFailed(Package));
        Assert.Empty(ClientAutoUpdate.FailedOf(Package));
        Assert.Equal(0, ClientAutoUpdate.ClearFailed(Package));
    }

    // ---- the version store route --------------------------------------------------------------------

    [Fact]
    public void A_hot_update_parks_the_old_copy_then_replaces_the_file_in_place()
    {
        string active = InstallActive();
        var svc = Store();
        var r = StoreUpdate.Apply(svc, Update("1.1.0"), Remove);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("now", r.Route);
        Assert.Equal("1.0.0", r.Previous);
        Assert.Equal(active, r.Path);
        Assert.Equal(Dll("1.1.0"), File.ReadAllBytes(active));
        Assert.Single(Directory.GetFiles(_paths.Reload, "*.dll"));
        var e = svc.Manifest().Find(Id)!;
        Assert.Equal("1.1.0", e.Active!.Version);
        var kept = Assert.Single(e.Parked);
        Assert.Equal("1.0.0", kept.Version);
        Assert.True(kept.UserInstalled);
        Assert.Equal("package:Team-Chat@1.0.0", kept.Ledger!.Source);
        // The ledger names the active copy only, at its new version.
        var row = Assert.Single(Ledger());
        Assert.Equal("package:Team-Chat@1.1.0", row.Source);
        Assert.Equal(Id, row.PluginId);
        Assert.Equal("mod", row.Category);
        Assert.False(svc.Engine.JournalPending);
    }

    [Fact]
    public void A_restart_update_marks_the_old_copy_and_queues_the_new_one()
    {
        string active = InstallActive();
        var svc = Store();
        var r = StoreUpdate.Apply(svc, Update("1.1.0", hot: false), Remove);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("restart", r.Route);
        Assert.Contains("hotLoadable", r.Message);
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(active));
        Assert.True(File.Exists(UninstallMarkers.MarkerFor(active)));
        Assert.Equal(Dll("1.1.0"), File.ReadAllBytes(Path.Combine(_paths.Pending, "Chat.dll")));
        var e = svc.Manifest().Find(Id)!;
        Assert.Equal("1.1.0", e.Pending!.Version);
        var row = Assert.Single(Ledger());
        Assert.Equal("pending", row.Category);
        Assert.Equal("package:Team-Chat@1.1.0", row.Source);
    }

    [Fact]
    public void A_newer_update_supersedes_a_copy_still_waiting_for_a_restart()
    {
        InstallActive();
        var svc = Store();
        Assert.True(StoreUpdate.Apply(svc, Update("1.1.0", hot: false), Remove).Ok);

        var r = StoreUpdate.Apply(svc, Update("1.2.0", hot: false), Remove);

        Assert.True(r.Ok, r.Message);
        Assert.Equal("restart", r.Route);
        Assert.Contains("superseded the queued 1.1.0", r.Superseded);
        Assert.Equal(Dll("1.2.0"), File.ReadAllBytes(Path.Combine(_paths.Pending, "Chat.dll")));
        var e = svc.Manifest().Find(Id)!;
        Assert.Equal("1.2.0", e.Pending!.Version);
        Assert.Contains(e.Parked, p => p.Version == "1.1.0");
        Assert.Contains(e.Parked, p => p.Version == "1.0.0");
        // One pending row, the newer one: the superseded copy's row went with it.
        var row = Assert.Single(Ledger());
        Assert.Equal("package:Team-Chat@1.2.0", row.Source);
        Assert.Equal("pending", row.Category);
    }

    [Fact]
    public void Cancelling_a_queued_update_keeps_the_copy_in_use_after_the_next_start()
    {
        string active = InstallActive();
        var svc = Store();
        Assert.True(StoreUpdate.Apply(svc, Update("1.1.0", hot: false), Remove).Ok);
        string pending = Path.Combine(_paths.Pending, "Chat.dll");
        Assert.True(PendingSupersede.TryRemove(_game, Package, pending, out string why), why); // what Cancel install does

        var lines = svc.Engine.UnqueueOrphans();

        Assert.Single(lines);
        Assert.False(File.Exists(UninstallMarkers.MarkerFor(active)));
        Assert.Null(svc.Manifest().Find(Id)?.Pending);
        var row = Assert.Single(Ledger());
        Assert.Equal("package:Team-Chat@1.0.0", row.Source);
        Assert.Equal("mod", row.Category);
        Assert.Empty(svc.Engine.UnqueueOrphans());
    }

    [Fact]
    public void A_pending_copy_another_install_wrote_for_this_package_is_superseded_and_a_foreign_one_is_not()
    {
        InstallActive();
        string pending = Path.Combine(_paths.Pending, "index", "Chat.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.WriteAllBytes(pending, Dll("1.1.0", "install"));
        WriteLedger(new ServerInstallLedgerEntry("BepInEx/progenitor-pending/index/Chat.dll", Sha(Dll("1.1.0", "install")), 10, "pending",
            "package:" + Package + "@1.1.0", DateTime.UtcNow, pluginId: Id));
        var svc = Store();

        var r = StoreUpdate.Apply(svc, Update("1.2.0"), Remove);

        Assert.True(r.Ok, r.Message);
        Assert.Contains("superseded the pending Team-Chat@1.1.0", r.Superseded);
        Assert.False(File.Exists(pending));
        Assert.DoesNotContain(Ledger(), l => l.Category == "pending");
        Assert.Equal("1.2.0", svc.Manifest().Find(Id)!.Active!.Version);

        // A copy nobody ledgered (or another package's) still refuses the update.
        File.WriteAllBytes(pending, Dll("9.9.9", "foreign"));
        var refused = StoreUpdate.Apply(svc, Update("1.3.0"), Remove);
        Assert.False(refused.Ok);
        Assert.Contains("waits for the next start", refused.Message);
        Assert.Null(PendingSupersede.OwnRow(_game, Package, pending, out string why));
        Assert.Contains("does not name it", why);
    }

    [Fact]
    public void Without_an_active_reload_copy_the_install_route_writes_it()
    {
        var r = StoreUpdate.Apply(Store(), Update("1.1.0"), Remove);
        Assert.True(r.NotApplicable);
        Assert.True(StoreUpdate.Apply(null, Update("1.1.0"), Remove).NotApplicable);
    }

    // ---- Roll back --------------------------------------------------------------------------------

    [Fact]
    public void Roll_back_returns_the_previous_version_and_can_forget_the_failed_one()
    {
        InstallActive();
        var svc = Store();
        Assert.True(StoreUpdate.Apply(svc, Update("1.1.0"), Remove).Ok);
        var e = svc.Manifest().Find(Id);
        Assert.Equal("1.0.0", StoreUpdate.RollbackTarget(e)!.Version);

        var r = StoreUpdate.RollBack(svc, Id, "1.0.0", forget: "1.1.0");

        Assert.True(r.Ok, r.Message);
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(Path.Combine(_paths.Reload, "Chat.dll")));
        e = svc.Manifest().Find(Id);
        Assert.Equal("1.0.0", e?.Active?.Version ?? "1.0.0");
        Assert.DoesNotContain(e?.Parked ?? new List<ParkedVersion>(), p => p.Version == "1.1.0");
        Assert.Equal("package:Team-Chat@1.0.0", Assert.Single(Ledger()).Source);
    }

    [Fact]
    public void Roll_back_from_the_mod_page_pins_the_mod_at_the_version_it_returns_to()
    {
        InstallActive();
        var svc = Store();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientAutoUpdate.WindowProbe = OpenWindow;
        Assert.True(StoreUpdate.Apply(svc, Update("1.1.0"), Remove).Ok);
        Assert.Equal("1.0.0", ClientAutoUpdate.RollbackVersionOf(Id));

        string line = ClientAutoUpdate.RollBack(Id, Package);

        Assert.StartsWith("Rolled back to 1.0.0", line);
        Assert.Equal("1.0.0", ClientAutoUpdate.ReadFile().Pins[Package]);
        Assert.Equal("Keep", ClientAutoUpdate.ModeChoiceOf(Package));
        // The newer copy stays kept: the player can go forward again (Updates), and Roll back offers only older copies.
        Assert.Contains(svc.Manifest().Find(Id)!.Parked, p => p.Version == "1.1.0");
        Assert.Equal("", ClientAutoUpdate.RollbackVersionOf(Id));
        Assert.Equal("No earlier version of it is kept.", ClientAutoUpdate.RollBack(Id, Package));

        ClientAutoUpdate.WindowProbe = () => new ApplyWindowFacts { AtTitle = true, InWorld = true };
        Assert.StartsWith("Can't roll back now: in a world", ClientAutoUpdate.RollBack(Id, Package));
    }

    // ---- the runner: automatic rollback after a failed load ----------------------------------------

    private static InstallPlanSet PlanSet()
    {
        var plan = new InstallPlan(Package + "-hash", Entry("1.1.0"), InstallTarget.Client, InstallDecision.ApplyNow, "test",
            Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(), Id, "Chat.dll");
        return InstallPlanSet.Planned(plan, new[] { new InstallPlanMember(Package, "1.1.0", plan, true, "root", "", "") },
            Array.Empty<ClosureMember>(), InstallTarget.Client);
    }

    private UpdatePipeline StorePipeline(ModVersionService svc, bool loads, List<string> calls) =>
        StorePipeline(svc, loads ? LoadVerdict.Loaded : LoadVerdict.Failed, calls);

    private UpdatePipeline StorePipeline(ModVersionService svc, LoadVerdict verdict, List<string> calls, Func<string>? window = null) => new()
    {
        Inspect = (k, _) => Task.FromResult(new InspectCallResult(true, null, null, null)),
        Plan = (k, _) => Task.FromResult(PlanSet()),
        Execute = _ => { calls.Add("execute"); return ExecuteSetResult.Refuse("the install route is not expected"); },
        // As the live side does: an automatic pass's write checks the window in the same step.
        ApplyStore = (p, set) => Task.FromResult<StoreUpdateResult?>(
            p.RequireWindow && (window?.Invoke() ?? "") is { Length: > 0 } closed ? StoreUpdateResult.Defer(closed) : StoreUpdate.Apply(svc, Update(p.To), Remove)),
        WaitLoaded = (id, sha) => { calls.Add("wait " + id); return Task.FromResult((verdict, verdict == LoadVerdict.Failed ? "Its load threw: boom" : "it is not loaded yet")); },
        RollBackFailed = (p, why) => Task.FromResult(ClientAutoUpdate.RollBackFailed(p, why)),
        NoteRestart = ClientAutoUpdate.NoteRestart,
        WindowClosed = () => window?.Invoke() ?? "",
        RunOnMain = w => Task.FromResult(w()),
        Post = a => a(),
    };

    private void Detected()
    {
        ClientUpdates.EntriesProbe = () => new[] { Entry("1.1.0") };
        ClientUpdates.Recheck("test");
        Assert.Single(ClientUpdates.Report.Items);
    }

    [Fact]
    public void A_hot_update_that_loads_is_reported_loaded_and_says_so_under_mods()
    {
        InstallActive();
        Detected();
        var svc = Store();
        var calls = new List<string>();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientUpdates.Pipeline = StorePipeline(svc, loads: true, calls);
        IReadOnlyList<PreparedUpdate>? done = null;

        Assert.True(ClientUpdates.BeginUpdate(new[] { Package }, "auto", false, (p, e) => { ClientAutoUpdate.AfterPass(p, e); done = p; }, out _));
        SpinUntil(() => done != null);

        var u = done!.Single();
        Assert.Equal("loaded", u.Outcome);
        Assert.Equal("store", u.Route);
        Assert.Equal("1.0.0", u.Previous);
        Assert.Equal(new[] { "wait team.chat" }, calls);
        Assert.Equal("1 mod updated", ClientAutoUpdate.Notice!.Line);
        Assert.False(ClientAutoUpdate.Notice!.Problem);
        ClientAutoUpdate.NoticeSeen();
        Assert.Null(ClientAutoUpdate.Notice);
        Assert.Equal("1 mod updated", ClientAutoUpdate.LastSeenNotice!.Line);
        Assert.Contains("Chat 1.1.0", ClientAutoUpdate.LastSeenNotice!.Detail);
    }

    [Fact]
    public void A_hot_update_that_does_not_load_is_rolled_back_and_never_tried_again()
    {
        InstallActive();
        Detected();
        var svc = Store();
        var calls = new List<string>();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientUpdates.Pipeline = StorePipeline(svc, loads: false, calls);
        IReadOnlyList<PreparedUpdate>? done = null;

        Assert.True(ClientUpdates.BeginUpdate(new[] { Package }, "auto", false, (p, e) => { ClientAutoUpdate.AfterPass(p, e); done = p; }, out _));
        SpinUntil(() => done != null);

        var u = done!.Single();
        Assert.Equal("failed", u.Outcome);
        Assert.True(u.RolledBack);
        Assert.Empty(u.HotWrites);
        Assert.Contains("rolled back to 1.0.0", u.OutcomeDetail);
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(Path.Combine(_paths.Reload, "Chat.dll")));
        Assert.DoesNotContain(svc.Manifest().Find(Id)?.Parked ?? new List<ParkedVersion>(), p => p.Version == "1.1.0");
        Assert.Equal("1.1.0", ClientAutoUpdate.FailedOf(Package).Single().Version);
        Assert.Equal("Update failed", ClientAutoUpdate.Notice!.Line);
        Assert.True(ClientAutoUpdate.Notice!.Problem);
        Assert.Contains("failed to load and was rolled back to 1.0.0", ClientUpdates.ResultBody(done!));

        // The next pass sees the failed version and does not apply it automatically.
        ClientUpdates.Recheck("again");
        SpinUntil(() => ClientUpdates.Report.Items.Count == 1 && ClientUpdates.Report.Items[0].FailedBefore.Length > 0);
        Assert.False(ClientUpdates.Report.Items.Single().Auto);
        Assert.Empty(ClientAutoUpdate.Due());
    }

    // ---- the start check ------------------------------------------------------------------------------

    [Fact]
    public void After_a_restart_an_update_that_did_not_load_is_rolled_back_and_one_that_did_is_cleared()
    {
        InstallActive();
        var svc = Store();
        ClientAutoUpdate.StoreProbe = () => svc;
        var r = StoreUpdate.Apply(svc, Update("1.1.0", hot: false), Remove);
        ClientAutoUpdate.NoteRestart(new PreparedUpdate { Package = Package, To = "1.1.0", Previous = r.Previous, PluginIds = new[] { Id } }, r.Sha256, r.Path);

        // Still queued (no restart yet, or a Core swap): nothing is decided.
        Assert.Contains("still queued", ClientAutoUpdate.CheckAfterStart().Single());

        // The start promoted it (Bootstrap's work: the marked copy goes, the pending copy moves in).
        string active = Path.Combine(_paths.Reload, "Chat.dll");
        File.Delete(UninstallMarkers.MarkerFor(active));
        File.Move(Path.Combine(_paths.Pending, "Chat.dll"), active, overwrite: true);
        svc.Engine.Reconcile();
        Assert.Null(svc.Manifest().Find(Id)!.Pending);

        // Its load threw (the reloader's record after the start).
        ClientAutoUpdate.LoadFactsProbe = (_, _) => new LoadFacts
        {
            Snapshot = (false, ""),
            Record = new ReloadRecord { Loaded = false, Failure = "InvalidOperationException: boom", WrittenUtc = DateTime.UtcNow },
        };
        var lines = ClientAutoUpdate.CheckAfterStart();

        Assert.Contains("Its load threw: InvalidOperationException: boom", lines.Single());
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(active));
        Assert.Equal("1.1.0", ClientAutoUpdate.FailedOf(Package).Single().Version);
        Assert.Empty(ClientAutoUpdate.ReadFile().Verify);
        Assert.Equal("Update failed", ClientAutoUpdate.Notice!.Line);

        // A loaded one is simply cleared.
        ClientAutoUpdate.NoteRestart(new PreparedUpdate { Package = Package, To = "1.0.0", PluginIds = new[] { Id } }, Sha(Dll("1.0.0")), active);
        ClientAutoUpdate.LoadFactsProbe = (_, _) => new LoadFacts { Snapshot = (true, Sha(Dll("1.0.0"))) };
        Assert.Contains("loaded after the restart", ClientAutoUpdate.CheckAfterStart().Single());
        Assert.Empty(ClientAutoUpdate.ReadFile().Verify);
    }

    // ---- review fixes (H1, H2, M1, M2, M3, L1, L3, L4) ----------------------------------------------------

    [Fact]
    public void An_automatic_write_is_deferred_when_the_window_closed_after_the_pass_started()
    {
        InstallActive();
        Detected();
        var svc = Store();
        var calls = new List<string>();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientAutoUpdate.WindowProbe = OpenWindow;
        string window = "";
        ClientUpdates.Pipeline = StorePipeline(svc, LoadVerdict.Loaded, calls, () => window);
        ClientUpdates.Pipeline.Inspect = (k, _) => { window = "in a world"; return Task.FromResult(new InspectCallResult(true, null, null, null)); };
        Assert.Single(ClientAutoUpdate.Due());
        IReadOnlyList<PreparedUpdate>? done = null;

        Assert.StartsWith("started", ClientAutoUpdate.RunPass("auto", ClientAutoUpdate.Due()));
        ClientUpdates.Pipeline.Post = a => a();
        SpinUntil(() => ClientUpdates.RunState == "done");
        done = ClientUpdates.Prepared;

        var u = done.Single();
        Assert.Equal("deferred", u.Outcome);
        Assert.Contains("in a world", u.OutcomeDetail);
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(Path.Combine(_paths.Reload, "Chat.dll")));
        Assert.Empty(calls);
        Assert.Empty(ClientAutoUpdate.FailedOf(Package));
        // Not counted as an attempt: the next window tries it again; and nothing under MODS for it.
        Assert.Single(ClientAutoUpdate.Due());
        Assert.Null(ClientAutoUpdate.Notice);
    }

    [Fact]
    public void The_install_route_also_defers_an_automatic_write_but_never_a_manual_one()
    {
        LedgerOnly();
        Detected();
        var calls = new List<string>();
        var pipe = new UpdatePipeline
        {
            Inspect = (k, _) => Task.FromResult(new InspectCallResult(true, null, null, null)),
            Plan = (k, _) => Task.FromResult(PlanSet()),
            Execute = set => { calls.Add("execute"); return ExecuteSetResult.Written(new[] { new ExecuteMemberResult(Package, "1.1.0", "p", false, null, true) }); },
            WindowClosed = () => "a join is in progress",
            RunOnMain = w => Task.FromResult(w()),
            Post = a => a(),
        };
        ClientUpdates.Pipeline = pipe;
        IReadOnlyList<PreparedUpdate>? auto = null;
        Assert.True(ClientUpdates.BeginUpdate(new[] { Package }, "auto", false, (p, e) => auto = p, out _));
        SpinUntil(() => auto != null);
        Assert.Equal("deferred", auto!.Single().Outcome);
        Assert.Empty(calls);

        IReadOnlyList<PreparedUpdate>? manual = null;
        Assert.True(ClientUpdates.BeginUpdate(new[] { Package }, "pre-join", false, (p, e) => manual = p, out _));
        SpinUntil(() => manual != null);
        Assert.Equal("restart", manual!.Single().Outcome);
        Assert.Equal(new[] { "execute" }, calls);
    }

    /// <summary>A ledgered package with no copy in the reload folder (the install route writes it).</summary>
    private void LedgerOnly()
    {
        string file = Path.Combine(_paths.Pending, "index", "Chat.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, Dll("1.0.0"));
        WriteLedger(new ServerInstallLedgerEntry("BepInEx/progenitor-pending/index/Chat.dll", Sha(Dll("1.0.0")), 10, "pending",
            "package:" + Package + "@1.0.0", DateTime.UtcNow, pluginId: Id));
    }

    [Fact]
    public void A_hot_update_that_times_out_is_rolled_back_but_not_listed_as_failed()
    {
        InstallActive();
        Detected();
        var svc = Store();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientUpdates.Pipeline = StorePipeline(svc, LoadVerdict.Waiting, new List<string>());
        IReadOnlyList<PreparedUpdate>? done = null;
        Assert.True(ClientUpdates.BeginUpdate(new[] { Package }, "auto", false, (p, e) => { ClientAutoUpdate.AfterPass(p, e); done = p; }, out _));
        SpinUntil(() => done != null);

        var u = done!.Single();
        Assert.True(u.RolledBack);
        Assert.False(u.LoadFailed);
        Assert.Contains("tried again next session", u.OutcomeDetail);
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(Path.Combine(_paths.Reload, "Chat.dll")));
        Assert.Empty(ClientAutoUpdate.FailedOf(Package));
        Assert.Contains("it did not load in time", ClientUpdates.ResultBody(done!));
    }

    [Fact]
    public void An_unverifiable_load_is_kept_and_said_so_not_called_loaded()
    {
        InstallActive();
        Detected();
        var svc = Store();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientUpdates.Pipeline = StorePipeline(svc, LoadVerdict.Unknown, new List<string>());
        IReadOnlyList<PreparedUpdate>? done = null;
        Assert.True(ClientUpdates.BeginUpdate(new[] { Package }, "auto", false, (p, e) => done = p, out _));
        SpinUntil(() => done != null);
        var u = done!.Single();
        Assert.True(u.Unverified);
        Assert.False(u.RolledBack);
        Assert.Contains("could not be checked", ClientUpdates.ResultBody(done!));
    }

    [Fact]
    public void The_load_check_detects_what_it_can_and_says_unknown_otherwise()
    {
        var t = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        string sha = "aa";
        // Progenitor plugin, OnLoad threw: registry Failed with the new image.
        Assert.Equal(LoadVerdict.Failed, LoadCheck.Judge(new LoadFacts { Snapshot = (true, sha), Registry = "Failed" }, sha, t).Verdict);
        // Plain plugin whose load threw: the reloader's record after the write.
        var threw = new ReloadRecord { Loaded = false, Failure = "TypeLoadException: x", WrittenUtc = t.AddSeconds(1) };
        Assert.Equal(LoadVerdict.Failed, LoadCheck.Judge(new LoadFacts { Snapshot = (false, ""), Record = threw }, sha, t).Verdict);
        // An old failure record from before the write is not this load.
        var old = new ReloadRecord { Loaded = false, Failure = "x", WrittenUtc = t.AddMinutes(-5) };
        Assert.Equal(LoadVerdict.Waiting, LoadCheck.Judge(new LoadFacts { Snapshot = (false, ""), Record = old }, sha, t).Verdict);
        // Loaded and registered; a plain plugin needs no registration; turned off counts as in place.
        Assert.Equal(LoadVerdict.Loaded, LoadCheck.Judge(new LoadFacts { Snapshot = (true, sha), Registry = "Loaded" }, sha, t).Verdict);
        Assert.Equal(LoadVerdict.Loaded, LoadCheck.Judge(new LoadFacts { Snapshot = (true, sha) }, sha, t).Verdict);
        Assert.Equal(LoadVerdict.Loaded, LoadCheck.Judge(new LoadFacts { Snapshot = (true, sha), Registry = "SkippedOff" }, sha, t).Verdict);
        // Held by the reloader but not registered yet (Loading, or the old copy's Unloaded): wait.
        Assert.Equal(LoadVerdict.Waiting, LoadCheck.Judge(new LoadFacts { Snapshot = (true, sha), Registry = "Unloaded" }, sha, t).Verdict);
        Assert.Equal(LoadVerdict.Waiting, LoadCheck.Judge(new LoadFacts { Snapshot = (true, sha), Registry = "Loading" }, sha, t).Verdict);
        // The old image still loaded: wait.
        Assert.Equal(LoadVerdict.Waiting, LoadCheck.Judge(new LoadFacts { Snapshot = (true, "bb"), Registry = "Loaded" }, sha, t).Verdict);
        // An older Bootstrap (no snapshot) with the reloader's record of this image: loaded; with nothing: unknown.
        var rec = new ReloadRecord { Loaded = true, ImageSha = sha, WrittenUtc = t.AddSeconds(1) };
        Assert.Equal(LoadVerdict.Loaded, LoadCheck.Judge(new LoadFacts { Record = rec }, sha, t).Verdict);
        Assert.Equal(LoadVerdict.Unknown, LoadCheck.Judge(new LoadFacts(), sha, t).Verdict);
        // The record parser.
        var parsed = ReloadRecord.Parse(Progenitor.Reload.DeltaApply.FormatFailedState("X", "boom").Replace("baseline=", "baseline=AA"), t)!;
        Assert.False(parsed.Loaded);
        Assert.Equal("aa", parsed.ImageSha);
        Assert.Equal("boom", parsed.Failure);
        // The deadline: 15 s, pushed back while the loader is busy, never past 120 s.
        Assert.False(LoadCheck.PastDeadline(14, 0));
        Assert.True(LoadCheck.PastDeadline(15, 0));
        Assert.False(LoadCheck.PastDeadline(30, 20));
        Assert.True(LoadCheck.PastDeadline(120, 500));
    }

    [Fact]
    public void A_start_check_judges_only_the_image_it_queued()
    {
        InstallActive();
        var svc = Store();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientAutoUpdate.WindowProbe = OpenWindow;
        var r = StoreUpdate.Apply(svc, Update("1.1.0", hot: false), Remove);
        ClientAutoUpdate.NoteRestart(new PreparedUpdate { Package = Package, To = "1.1.0", Previous = r.Previous, PluginIds = new[] { Id } }, r.Sha256, r.Path);
        ClientAutoUpdate.LoadFactsProbe = (_, _) => new LoadFacts { Snapshot = (false, "") };

        // (C) a newer copy superseded the queue: the old check is replaced (same package), the queue holds 1.2.0.
        var r2 = StoreUpdate.Apply(svc, Update("1.2.0", hot: false), Remove);
        Assert.True(r2.Ok, r2.Message);
        // A check left from another path (here, written by hand for 1.1.0) does not judge the 1.2.0 queue.
        ClientAutoUpdate.EditFile(f => { f.Verify["Team-Other"] = new StartCheck { PluginId = Id, Version = "1.1.0", Sha256 = r.Sha256, Previous = "1.0.0", Utc = DateTime.UtcNow }; return true; }, out _);
        var lines = ClientAutoUpdate.CheckAfterStart();
        Assert.Contains(lines, l => l.Contains("the queued copy changed; not judged"));
        Assert.Empty(ClientAutoUpdate.FailedOf("Team-Other"));
        Assert.False(ClientAutoUpdate.ReadFile().Verify.ContainsKey("Team-Other"));

        // (A) the queue is cancelled (its pending copy removed): the start check goes with it, nothing is listed.
        string pending = Path.Combine(_paths.Pending, "Chat.dll");
        ClientAutoUpdate.NoteRestart(new PreparedUpdate { Package = Package, To = "1.2.0", Previous = "1.0.0", PluginIds = new[] { Id } }, r2.Sha256, r2.Path);
        Assert.True(PendingSupersede.TryRemove(_game, Package, pending, out string why), why);
        svc.Engine.UnqueueOrphans(out var dropped);
        Assert.Equal(new[] { Id }, dropped);
        foreach (var id in dropped) ClientAutoUpdate.ForgetStartCheck(id, "test");
        Assert.Empty(ClientAutoUpdate.ReadFile().Verify);
        Assert.Empty(ClientAutoUpdate.FailedOf(Package));

        // Even a check that survived would see 1.0.0 on disk (not its image) and drop it silently.
        ClientAutoUpdate.EditFile(f => { f.Verify[Package] = new StartCheck { PluginId = Id, Version = "1.2.0", Sha256 = r2.Sha256, Previous = "1.0.0", Utc = DateTime.UtcNow }; return true; }, out _);
        Assert.Contains("another copy of it is in place; not judged", ClientAutoUpdate.CheckAfterStart().Single());
        Assert.Empty(ClientAutoUpdate.FailedOf(Package));
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(Path.Combine(_paths.Reload, "Chat.dll")));
    }

    [Fact]
    public void Rolling_back_from_the_mod_page_drops_the_start_check_of_the_queued_update()
    {
        InstallActive();
        var svc = Store();
        ClientAutoUpdate.StoreProbe = () => svc;
        ClientAutoUpdate.WindowProbe = OpenWindow;
        Assert.True(StoreUpdate.Apply(svc, Update("1.1.0"), Remove).Ok);            // hot: 1.1.0 active, 1.0.0 kept
        var r = StoreUpdate.Apply(svc, Update("1.2.0", hot: false), Remove);       // 1.2.0 queued
        ClientAutoUpdate.NoteRestart(new PreparedUpdate { Package = Package, To = "1.2.0", Previous = r.Previous, PluginIds = new[] { Id } }, r.Sha256, r.Path);

        // (B) the player rolls back to 1.0.0 and pins it: the 1.2.0 check must not later override that.
        Assert.StartsWith("Rolled back to 1.0.0", ClientAutoUpdate.RollBack(Id, Package));
        Assert.Empty(ClientAutoUpdate.ReadFile().Verify);
        Assert.Equal("1.0.0", svc.Manifest().Find(Id)!.Pending!.Version);
    }

    [Fact]
    public void A_requeue_is_journaled_and_a_crash_either_way_leaves_one_pending_copy()
    {
        foreach (var step in new[] { "requeue:journal", "requeue:placed", "requeue:books" })
        {
            var game = Path.Combine(Path.GetTempPath(), "pau2r-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "progenitor-reload"));
            var paths = new ModVersionPaths(Path.Combine(game, "BepInEx"));
            try
            {
                string active = Path.Combine(paths.Reload, "Chat.dll");
                File.WriteAllBytes(active, Dll("1.0.0"));
                bool armed = false;
                var env = new ModVersionEnv(paths) { Fault = s => { if (armed && s == step) throw new FaultException(s); } };
                var engine = new ModVersionEngine(env);
                var svc = new ModVersionService(engine, new Runtime(paths));
                Assert.True(StoreUpdate.Apply(svc, Update("1.1.0", hot: false), null).Ok);
                Assert.True(engine.AddCopy(Id, "1.2.0", "Chat.dll", Dll("1.2.0"), null, ModVersionSource.Thunderstore, "x").Ok);
                armed = true;
                Assert.Throws<FaultException>(() => engine.Requeue(Id, "1.2.0"));
                armed = false;
                Assert.True(engine.JournalPending);

                engine.Recover();

                Assert.False(engine.JournalPending);
                var files = Directory.GetFiles(paths.Pending, "*.dll", SearchOption.AllDirectories);
                Assert.Single(files);
                var q = engine.Read().Find(Id)!.Pending!;
                Assert.Equal(ModVersionFiles.Sha256Hex(File.ReadAllBytes(files[0])), q.Sha256);
                Assert.Equal(step == "requeue:journal" ? "1.1.0" : "1.2.0", q.Version);
            }
            finally
            {
                try { Directory.Delete(game, true); }
                catch { /* temp */ }
            }
        }
    }

    [Fact]
    public void A_copy_placed_for_a_server_is_not_updated_and_the_kept_copy_serves_an_exact_server()
    {
        InstallActive();
        var svc = Store();
        Assert.True(StoreUpdate.Apply(svc, Update("1.1.0"), Remove).Ok);

        // The kept 1.0.0 is what an Exact 1.0.0 server is given at the handshake (no download).
        var asked = new[] { Handshake.ModRecord.CreateDeclared(Id, "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var provided = new[] { Handshake.ModRecord.CreateDeclared(Id, "1.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) };
        var pick = ModVersionSelect.PickFor(Id, asked, provided, svc.Manifest().Find(Id));
        Assert.Equal("1.0.0", pick!.Version);

        // Swapped in for that server: an update now is deferred, nothing written.
        Assert.True(svc.Swap(Id, "1.0.0", "1.2.3.4:9876", "Test server").Ok);
        var r = StoreUpdate.Apply(svc, Update("1.2.0"), Remove);
        Assert.True(r.Deferred);
        Assert.Contains("placed for Test server", r.Message);
        Assert.Equal(Dll("1.0.0"), File.ReadAllBytes(Path.Combine(_paths.Reload, "Chat.dll")));
        Assert.Equal("1.2.3.4:9876", svc.Manifest().Find(Id)!.Active!.ForServer);
    }

    [Fact]
    public void A_torn_auto_update_file_is_kept_aside_not_written_over_and_edits_do_not_lose_each_other()
    {
        string path = ClientUpdateInputs.PinsPath(_paths.BepInExRoot);
        File.WriteAllText(path, "{ \"schema\": 1, \"pins\": { \"Team-Chat\": ");
        Assert.True(ClientAutoUpdate.SetModeChoice("Team-A", "Notify", "1.0.0", out _));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, UpdatePins.FileName + ".bad-*"));
        Assert.Equal("Notify", ClientAutoUpdate.ReadFile().Modes["Team-A"]);

        System.Threading.Tasks.Parallel.For(0, 24, i => ClientAutoUpdate.SetModeChoice("Team-" + i, "Off", "1.0.0", out _));
        var f = ClientAutoUpdate.ReadFile();
        for (int i = 0; i < 24; i++) Assert.Equal("Off", f.Modes["Team-" + i]);
        Assert.Equal("Notify", f.Modes["Team-A"]);
    }

    [Fact]
    public void The_orphan_check_reads_the_manifest_only_when_it_changed()
    {
        InstallActive();
        var svc = Store();
        int before = svc.Engine.OrphanManifestReads;
        svc.Engine.UnqueueOrphans();
        svc.Engine.UnqueueOrphans();
        Assert.True(svc.Engine.OrphanManifestReads - before <= 1);
        var r = StoreUpdate.Apply(svc, Update("1.1.0", hot: false), Remove);
        Assert.True(r.Ok);
        svc.Engine.UnqueueOrphans();
        int afterQueue = svc.Engine.OrphanManifestReads;
        svc.Engine.UnqueueOrphans();
        Assert.Equal(afterQueue, svc.Engine.OrphanManifestReads);
    }

    // ---- the notice -----------------------------------------------------------------------------------

    [Fact]
    public void The_title_line_is_generic_and_counts_add_up_until_the_mods_window_opens()
    {
        PreparedUpdate P(string name, string outcome, bool rolled = false, string error = "") =>
            new() { Package = "Team-" + name, Name = name, From = "1.0.0", To = "1.1.0", Previous = "1.0.0", Outcome = outcome, RolledBack = rolled, Error = error };

        Assert.Null(ClientAutoUpdate.NoticeFor(Array.Empty<PreparedUpdate>(), UpdateRunEnd.Applied));
        Assert.Null(ClientAutoUpdate.NoticeFor(new[] { P("Chat", "loaded") }, UpdateRunEnd.Cancelled));
        Assert.Equal("1 mod updated", ClientAutoUpdate.NoticeFor(new[] { P("Chat", "restart") }, UpdateRunEnd.Applied)!.Line);
        Assert.Equal("Update failed", ClientAutoUpdate.NoticeFor(new[] { P("Chat", "", error: "offline") }, UpdateRunEnd.NothingPrepared)!.Line);
        Assert.Equal("2 updates failed", ClientAutoUpdate.NoticeFor(new[] { P("Chat", "failed", rolled: true), P("Maps", "", error: "x") }, UpdateRunEnd.Applied)!.Line);
        // Updated wins over failed (the user's order), and no line names a mod or a version.
        var many = ClientAutoUpdate.NoticeFor(new[] { P("Chat", "loaded"), P("Maps", "restart"), P("Bags", "failed", rolled: true) }, UpdateRunEnd.Applied)!;
        Assert.Equal("2 mods updated", many.Line);
        Assert.True(many.Problem);
        Assert.Contains("Bags 1.1.0 failed to load", many.Detail);

        ClientAutoUpdate.SetNotice(ClientAutoUpdate.NoticeFor(new[] { P("Chat", "loaded") }, UpdateRunEnd.Applied));
        ClientAutoUpdate.SetNotice(ClientAutoUpdate.NoticeFor(new[] { P("Maps", "loaded") }, UpdateRunEnd.Applied));
        Assert.Equal("2 mods updated", ClientAutoUpdate.Notice!.Line);
        ClientAutoUpdate.NoticeSeen();
        Assert.Null(ClientAutoUpdate.Notice);
        Assert.Equal("1 update available", ClientUpdates.BadgeTextFor(1));
        Assert.Equal("3 updates available", ClientUpdates.BadgeTextFor(3));
    }

    // ---- fake runtime -------------------------------------------------------------------------------------

    private sealed class Runtime : IModVersionRuntime
    {
        private readonly ModVersionPaths _paths;

        public Runtime(ModVersionPaths paths) => _paths = paths;

        private static (string Guid, string Version) Identify(string file)
        {
            try
            {
                var parts = File.ReadAllText(file).Split('|');
                return parts.Length >= 2 ? (parts[0], parts[1]) : ("", "");
            }
            catch { return ("", ""); }
        }

        public IReadOnlyList<ModCopy> CopiesOf(string modId)
        {
            var list = new List<ModCopy>();
            if (Directory.Exists(_paths.Reload))
                foreach (var f in Directory.GetFiles(_paths.Reload, "*.dll"))
                    if (Identify(f).Guid == modId) list.Add(new ModCopy { Path = f, Folder = "reload", Version = Identify(f).Version });
            if (Directory.Exists(_paths.Pending))
                foreach (var f in Directory.GetFiles(_paths.Pending, "*.dll", SearchOption.AllDirectories))
                    if (Identify(f).Guid == modId) list.Add(new ModCopy { Path = f, Folder = "pending", Version = Identify(f).Version });
            return list;
        }

        public string? ManagedBy(string modId) => null;
        public bool UnsafeHarmony(string modId) => false;
        public int Dependents(string modId) => 0;
        public bool LoaderBusy => false;

        public string? LoadedVersion(string modId)
        {
            foreach (var c in CopiesOf(modId)) if (c.Folder == "reload") return c.Version;
            return null;
        }

        public bool? ReloaderHolds(string modId) => null;
        public (string Level, string Rule) Declared(string modId) => ("", "");
        public string? ServerRecordHost(string fileName) => null;
    }

    private static void SpinUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            System.Threading.Thread.Sleep(5);
        }
    }
}
