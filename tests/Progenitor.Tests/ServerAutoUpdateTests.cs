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
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// AUTOUPDATE3 (auto-update phase 3), game-free: the server's passes end to end over a temporary server folder and the real
/// version store: a server-only hot update applied live with players online, one clients need held until the server has been
/// empty for the quiet period, a restart update staged with its apply journal and then Restart when empty with the quiet
/// period and the pre-update backup (one per batch, the second skipped), a failed load rolled back onto the failed list,
/// Notify sending one notice per batch, and Restart now's warning and cancel. A "DLL" is a text file <c>guid|version|salt</c>.
/// </summary>
[Collection("ServerAutoUpdate")]
public sealed class ServerAutoUpdateTests : IDisposable
{
    private const string Id = "team.chat";
    private const string Package = "Team-Chat";

    private readonly string _game;
    private readonly ModVersionPaths _paths;
    private readonly ModVersionService _svc;
    private readonly Queue<(double At, Action Do)> _scheduled = new();
    private readonly List<string> _audit = new();
    private readonly List<AdminUpdateNotice> _notices = new();
    private readonly List<string> _broadcasts = new();
    private readonly List<string> _backups = new();
    private readonly List<WorldBackupEntry> _backupList = new();
    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private ServerUpdateSettings _settings = new() { Bound = true, Mode = UpdateMode.Automatic };
    private int _online;
    private bool _clientsNeed;
    private bool _hot = true;
    private string _loadRegistry = "Loaded";
    private string _latest = "1.1.0";
    private int _stops;
    private PackageSide _side = PackageSide.Server;

    public ServerAutoUpdateTests()
    {
        _game = Path.Combine(Path.GetTempPath(), "pau3-" + Guid.NewGuid().ToString("N").Substring(0, 10));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx", "progenitor-reload"));
        Directory.CreateDirectory(Path.Combine(_game, "BepInEx", "config"));
        _paths = new ModVersionPaths(Path.Combine(_game, "BepInEx"));
        var env = new ModVersionEnv(_paths) { Books = new ModVersionBooks(_paths, "") };
        _svc = new ModVersionService(new ModVersionEngine(env), new Runtime(_paths));
        ServerAutoUpdate.Reset();
        ServerAutoUpdate.SettingsProbe = () => _settings;
        ServerAutoUpdate.BepInExRootProbe = () => _paths.BepInExRoot;
        ServerAutoUpdate.GameRootProbe = () => _game;
        ServerAutoUpdate.UtcNow = () => _now;
        ServerAutoUpdate.OnlineProbe = () => _online;
        ServerAutoUpdate.StoreProbe = () => _svc;
        ServerAutoUpdate.ClientsNeedProbe = _ => _clientsNeed;
        ServerAutoUpdate.RunOnMain = work => Task.FromResult(work());
        ServerAutoUpdate.RunOff = work => work();
        ServerAutoUpdate.Schedule = (seconds, action) => _scheduled.Enqueue((seconds, action));
        ServerAutoUpdate.AuditSink = _audit.Add;
        ServerAutoUpdate.NotifyAdmins = _notices.Add;
        ServerAutoUpdate.Broadcast = _broadcasts.Add;
        ServerAutoUpdate.StopServer = (reply, refused) => { _stops++; reply("World saved. Stopping the server."); };
        ServerAutoUpdate.BackupEntriesProbe = () => _backupList.ToList();
        ServerAutoUpdate.StartBackup = (label, keep, done) =>
        {
            _backups.Add(label + " keep " + keep);
            var entry = new WorldBackupEntry
            {
                Kind = WorldBackupEntry.KindBackup, BackupKind = WorldRestorePaths.KindPreUpdate, CreatedLocal = _now.ToLocalTime(),
                FolderName = "b" + _backups.Count + "-pre-update", Updates = label,
            };
            _backupList.Add(entry);
            done(new BackupOutcome { Ok = true, FolderName = entry.FolderName, Bytes = 10, Kind = WorldRestorePaths.KindPreUpdate });
            return null;
        };
        ServerAutoUpdate.LoadFactsProbe = (id, stem) =>
        {
            // The reloader holds whatever image is in progenitor-reload now; the registry says how its OnLoad went.
            string file = Path.Combine(_paths.Reload, "Chat.dll");
            string sha = File.Exists(file) ? Sha(File.ReadAllBytes(file)) : "";
            return new LoadFacts { Snapshot = (sha.Length > 0, sha), Registry = _loadRegistry };
        };
        ServerAutoUpdate.Pipeline = new ServerUpdatePipeline
        {
            ReadIndex = (request, _) => Task.FromResult(new ServerIndexRead { Ok = true, Requested = request, Entries = new[] { Entry(_latest) } }),
            Inspect = (key, _) => Task.FromResult(new InspectCallResult(true, null, null, Inspection(_side))),
            Plan = (key, _) => Task.FromResult(PlanSet(key)),
            Approve = (set, version) => new ServerApprovedImage
            {
                Bytes = Dll(version), Sha256 = Sha(Dll(version)), Version = version, PluginId = Id, Hot = _hot,
                NotHotReason = _hot ? "" : "the package does not declare hotLoadable",
                Ledger = new ParkedLedgerRow { Category = "mod", Source = "package:" + Package + "@" + version, PluginId = Id },
            },
            Installed = _ => false,
        };
        InstallActive("1.0.0");
        ServerAutoUpdate.SkipStartForTests();
    }

    public void Dispose()
    {
        ServerAutoUpdate.Reset();
        try { Directory.Delete(_game, recursive: true); }
        catch { /* temp */ }
    }

    // ---- rig -------------------------------------------------------------------------------------

    private static byte[] Dll(string version, string salt = "") => Encoding.UTF8.GetBytes(Id + "|" + version + "|" + salt);

    private static string Sha(byte[] bytes) => BitConverter.ToString(SHA256.HashData(bytes)).Replace("-", "").ToLowerInvariant();

    private void InstallActive(string version)
    {
        string path = Path.Combine(_paths.Reload, "Chat.dll");
        byte[] bytes = Dll(version);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
        new ServerInstallLedger("1", "", "", DateTime.UtcNow, new List<ServerInstallLedgerEntry>
        {
            new("BepInEx/progenitor-reload/Chat.dll", Sha(bytes), bytes.Length, "mod", "package:" + Package + "@" + version, DateTime.UtcNow, pluginId: Id),
        }).Write(_game);
    }

    private string ActiveVersion() => File.ReadAllText(Path.Combine(_paths.Reload, "Chat.dll")).Split('|')[1];

    private static CatalogEntry Entry(string version) =>
        new(new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "Team", Package),
            CatalogProvenance.AdaptedListing, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore,
            "2026-10-09T00:00:00Z", displayName: "Chat", owner: "Team", packageName: Package);

    /// <summary>The inspected package as far as the pass reads it: the side its progenitor.json declares.</summary>
    private static PackageInspection Inspection(PackageSide side) =>
        new(true, null, CatalogProvenance.AdaptedListing, "zip", 1, Array.Empty<ArtifactHash>(), null, side, true, true, true, false, null,
            Array.Empty<string>(), Array.Empty<PluginDependency>(), Package);

    private static InstallPlanSet PlanSet(string key)
    {
        var root = new InstallPlan(key + "-hash", Entry("1.1.0"), InstallTarget.LocalServer, InstallDecision.ApplyNow, "test",
            Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(), null, null);
        return InstallPlanSet.Planned(root, new[] { new InstallPlanMember(key, "1.1.0", root, true, "root", "", "") }, Array.Empty<ClosureMember>(),
            InstallTarget.LocalServer);
    }

    /// <summary>Run the scheduled main-thread work (the load waits poll) until the pass finishes.</summary>
    private void Settle()
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (ServerAutoUpdate.Running || _scheduled.Count > 0)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the pass did not finish");
            if (_scheduled.Count > 0)
            {
                var (at, act) = _scheduled.Dequeue();
                _now = _now.AddSeconds(at);
                act();
            }
            else System.Threading.Thread.Sleep(5);
        }
    }

    private void Check(bool request = true)
    {
        Assert.Equal("started", ServerAutoUpdate.BeginCheck(request, "test"));
        Settle();
    }

    private UpdatePins File_() => ClientUpdateInputs.ReadPins(_paths.BepInExRoot);

    private bool Audited(string fragment) => _audit.Any(l => l.Contains(fragment, StringComparison.Ordinal));

    // ---- live apply -------------------------------------------------------------------------------------

    [Fact]
    public void A_server_only_hot_update_applies_live_with_players_online_and_keeps_the_old_copy()
    {
        _online = 3;
        Check();
        Assert.Equal("1.1.0", ActiveVersion());
        Assert.True(Audited("op=check"));
        Assert.True(Audited("op=apply package=Team-Chat from=1.0.0 to=1.1.0 route=hot result=ok"));
        var o = ServerAutoUpdate.RecentOutcomes.Single();
        Assert.Equal(ServerUpdateOutcome.Applied, o.Result);
        Assert.False(o.ClientsNeed);
        // The previous version is kept in the version store, ready to roll back to.
        Assert.Equal("1.0.0", StoreUpdate.RollbackTarget(_svc.Manifest().Find(Id))?.Version);
        // The admins are told once.
        Assert.Equal("ChatSkin 1.1.0 applied live".Replace("ChatSkin", "Chat"), _notices.Single().Text);
        // The same version is not tried again in this session.
        Check();
        Assert.Single(_notices);
    }

    [Fact]
    public void An_update_clients_need_waits_until_the_server_has_been_empty_for_the_quiet_period()
    {
        _clientsNeed = true;
        _online = 1;
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.True(Audited("route=held result=skipped(held)"));
        var held = ServerAutoUpdate.Describe()["held"] as Dictionary<string, string>;
        Assert.Equal(ServerApplyPlan.HoldPlayers, held![Package]);
        Assert.Equal("1 server update available", _notices.Last().Text);

        // The last player leaves: nothing yet; the quiet period runs from that moment.
        _online = 0;
        ServerAutoUpdate.Tick();
        _now = _now.AddMinutes(4);
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        Assert.Equal("1.0.0", ActiveVersion());
        // A player connects during the quiet period: it starts again when they leave.
        ServerAutoUpdate.OnPlayerConnected();
        _now = _now.AddMinutes(2);
        ServerAutoUpdate.Tick();
        Assert.Equal("1.0.0", ActiveVersion());
        _now = _now.AddMinutes(5);
        Assert.Equal("applying held updates", ServerAutoUpdate.Tick());
        Settle();
        Assert.Equal("1.1.0", ActiveVersion());
        Assert.Empty((Dictionary<string, string>)ServerAutoUpdate.Describe()["held"]!);
        Assert.True(Audited("route=hot result=ok"));
    }

    [Fact]
    public void A_join_during_the_write_defers_an_update_clients_need()
    {
        _clientsNeed = true;
        _online = 0;
        ServerAutoUpdate.EmptyState.Observe(0, _now.AddMinutes(-10));
        // Quiet when the pass decided; a player connects before the write: it is held, never failed.
        int reads = 0;
        ServerAutoUpdate.OnlineProbe = () => reads++ == 0 ? 0 : 1;
        Check(request: false);
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.Equal(ServerUpdateOutcome.Held, ServerAutoUpdate.RecentOutcomes.Last().Result);
    }

    // ---- restart updates, Restart when empty, the pre-update backup ------------------------------------------

    [Fact]
    public void A_restart_update_is_staged_with_its_apply_journal_then_restart_when_empty_backs_up_once_and_saves_and_quits()
    {
        _hot = false;
        _online = 2;
        _settings = new ServerUpdateSettings
        {
            Bound = true, Mode = UpdateMode.Automatic, RestartWhenEmpty = true, HostRestarts = "test rig", EmptyMinutes = 5, BackupBeforeUpdate = true,
        };
        Check();
        // Staged for the next start, players or not; the active copy is untouched.
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.True(Audited("op=stage package=Team-Chat from=1.0.0 to=1.1.0 route=restart result=ok"));
        var journal = File_().Verify[Package];
        Assert.Equal(Sha(Dll("1.1.0")), journal.Sha256);
        Assert.Equal("1.0.0", journal.Previous);
        Assert.Equal(new[] { "Team-Chat 1.1.0" }, ServerAutoUpdate.RestartPending());
        Assert.True(_notices.Last().OfferRestart);
        // One pre-update backup for the batch, before its first write.
        Assert.Equal(new[] { "Chat 1.1.0 keep 2" }, _backups);

        // Players online: no restart.
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        _online = 0;
        ServerAutoUpdate.Tick();
        _now = _now.AddMinutes(4);
        ServerAutoUpdate.Tick();
        Assert.Equal(0, _stops);
        _now = _now.AddMinutes(1);
        Assert.Equal("restarting (empty)", ServerAutoUpdate.Tick());
        Settle();
        Assert.Equal(1, _stops);
        // The restart is its own batch, but the backup made at staging is newer than 6 hours: it stands in.
        Assert.Single(_backups);
        Assert.True(Audited("op=backup package=- from=- to=- route=- result=skipped(recent)"));
        Assert.True(Audited("op=restart"));
        Assert.Empty(_broadcasts);
    }

    [Fact]
    public void A_failed_backup_cancels_the_restart_and_it_is_retried_later_not_in_a_loop()
    {
        _hot = false;
        _settings = new ServerUpdateSettings
        {
            Bound = true, Mode = UpdateMode.Automatic, RestartWhenEmpty = true, HostRestarts = "test rig", BackupBeforeUpdate = true,
            PreUpdateBackupSkipHours = 0,
        };
        Check();
        ServerAutoUpdate.StartBackup = (label, keep, done) => "not enough free disk space for a backup";
        _online = 0;
        ServerAutoUpdate.Tick();
        _now = _now.AddMinutes(6);
        Assert.Equal("restarting (empty)", ServerAutoUpdate.Tick());
        Settle();
        Assert.Equal(0, _stops);
        Assert.True(Audited("result=failed(backup)"));
        _now = _now.AddMinutes(1);
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        Assert.Equal(0, _stops);
    }

    [Fact]
    public void Restart_now_warns_the_players_and_can_be_cancelled_and_runs_at_once_on_an_empty_server()
    {
        _hot = false;
        _settings = new ServerUpdateSettings { Bound = true, Mode = UpdateMode.Automatic, WarnMinutes = 5 };
        Check();
        _online = 2;
        Assert.Null(ServerAutoUpdate.RestartNow("Admin"));
        Assert.Equal(300, ServerAutoUpdate.CountdownSeconds);
        Assert.Equal("a restart is already under way", ServerAutoUpdate.RestartNow("Admin"));
        // The first warning at once, then one minute before.
        var (at0, warn0) = _scheduled.Dequeue();
        Assert.Equal(0, at0);
        warn0();
        Assert.Equal("The server restarts in 5 minutes to update mods: Team-Chat.", _broadcasts.Single());
        Assert.Null(ServerAutoUpdate.CancelRestart("Admin"));
        Assert.Equal(-1, ServerAutoUpdate.CountdownSeconds);
        Assert.Equal("The restart is cancelled.", _broadcasts.Last());
        while (_scheduled.Count > 0) _scheduled.Dequeue().Do();
        Assert.Equal(0, _stops);
        Assert.Equal("no restart countdown is running", ServerAutoUpdate.CancelRestart("Admin"));

        // Nobody online: no wait.
        _online = 0;
        Assert.Null(ServerAutoUpdate.RestartNow("Admin"));
        Settle();
        Assert.Equal(1, _stops);
        Assert.True(Audited("op=restart package=- from=- to=- route=- result=ok detail=\"by Admin (now)"));
    }

    [Fact]
    public void Restart_now_is_refused_with_nothing_waiting_for_a_restart()
    {
        Assert.Equal("No update waits for a restart.", ServerAutoUpdate.RestartNow("Admin"));
    }

    // ---- review follow-ups (2026-10-09) -------------------------------------------------------------------

    /// <summary>Stage a restart update with Restart when empty and the pre-update backup on (every backup made, no skip).</summary>
    private void StageRestartUpdate(bool restartWhenEmpty = true)
    {
        _hot = false;
        _settings = new ServerUpdateSettings
        {
            Bound = true, Mode = UpdateMode.Automatic, RestartWhenEmpty = restartWhenEmpty, HostRestarts = "test rig", EmptyMinutes = 5,
            BackupBeforeUpdate = true, PreUpdateBackupSkipHours = 0,
        };
        Check();
        Assert.Single(ServerAutoUpdate.RestartPending());
    }

    /// <summary>The next pre-update backup lets a player in while it copies the world.</summary>
    private void PlayerJoinsDuringTheNextBackup()
    {
        var make = ServerAutoUpdate.StartBackup!;
        ServerAutoUpdate.StartBackup = (label, keep, done) =>
        {
            ServerAutoUpdate.StartBackup = make;
            _online = 1;
            ServerAutoUpdate.OnPlayerConnected();
            return make(label, keep, done);
        };
    }

    [Fact]
    public void Restart_when_empty_does_not_quit_when_a_player_joins_during_the_backup()
    {
        StageRestartUpdate();
        _online = 0;
        ServerAutoUpdate.Tick();
        _now = _now.AddMinutes(6);
        PlayerJoinsDuringTheNextBackup();
        Assert.Equal("restarting (empty)", ServerAutoUpdate.Tick());
        Settle();
        // The backup ran; the stop did not: the player who came is never dropped by an unwarned quit.
        Assert.Equal(2, _backups.Count);
        Assert.Equal(0, _stops);
        Assert.True(Audited("result=skipped(player-joined)"));
        Assert.Empty(_broadcasts);
        // Nothing is counted as a failure: the quiet period simply starts again when they leave.
        Assert.Null(ServerAutoUpdate.Describe()["lastRestartFailureUtc"]);
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        _online = 0;
        ServerAutoUpdate.Tick();
        _now = _now.AddMinutes(5);
        Assert.Equal("restarting (empty)", ServerAutoUpdate.Tick());
        Settle();
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void Restart_now_on_an_empty_server_warns_a_player_who_joins_during_the_backup_instead_of_quitting()
    {
        StageRestartUpdate(restartWhenEmpty: false);
        _online = 0;
        PlayerJoinsDuringTheNextBackup();
        // (The fakes run the restart synchronously; the countdown's steps wait in the queue.)
        Assert.Null(ServerAutoUpdate.RestartNow("Admin"));
        Assert.Equal(0, _stops);
        Assert.True(Audited("result=deferred(player-joined)"));
        // The warned countdown took over: the player hears it, and the admin can still cancel it.
        Assert.Equal(300, ServerAutoUpdate.CountdownSeconds);
        Assert.True(Audited("result=countdown"));
        var (at0, warn0) = _scheduled.Dequeue();
        warn0();
        Assert.Equal("The server restarts in 5 minutes to update mods: Team-Chat.", _broadcasts.Last());
        // At the end of the warning the server stops with the player online (they were told).
        while (_scheduled.Count > 0) _scheduled.Dequeue().Do();
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void A_failed_save_cancels_the_restart_tells_the_admins_and_is_not_retried_in_a_loop()
    {
        StageRestartUpdate();
        ServerAutoUpdate.StopServer = (reply, refused) =>
        {
            _stops++;
            reply("Saving the world.");
            refused("the save was not confirmed");
        };
        _online = 0;
        ServerAutoUpdate.Tick();
        _now = _now.AddMinutes(6);
        Assert.Equal("restarting (empty)", ServerAutoUpdate.Tick());
        Settle();
        Assert.Equal(1, _stops);
        Assert.True(Audited("result=failed(save)"));
        Assert.Contains("could not be saved", _notices.Last().Text);
        Assert.True(_notices.Last().OfferRestart);
        Assert.NotNull(ServerAutoUpdate.Describe()["lastRestartFailureUtc"]);
        // The server keeps running and the staged update keeps waiting; the next empty moment (not the next tick) retries.
        Assert.Single(ServerAutoUpdate.RestartPending());
        _now = _now.AddMinutes(1);
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        Assert.Equal(1, _stops);
    }

    [Fact]
    public void A_new_version_that_does_not_declare_itself_server_only_is_held_while_players_are_online()
    {
        // The running copy is server-only (no handshake row), but 1.1.0 declares both sides: it may now be required.
        _side = PackageSide.Both;
        _online = 2;
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.True(Audited("route=held result=skipped(held)"));
        // No side at all fails safe the same way.
        _side = PackageSide.Unknown;
        ServerAutoUpdate.ForgetAttempts();
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
    }

    [Fact]
    public void Automatic_applies_nothing_while_remote_installs_are_off()
    {
        _settings = new ServerUpdateSettings { Bound = true, Mode = UpdateMode.Automatic, RemoteInstallEnabled = false };
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.True(Audited("route=notify result=skipped(notify)"));
        Assert.Contains("Automatic acts as Notify (remote installs are off)", ServerAutoUpdate.StatusLine());
    }

    [Fact]
    public void No_pass_runs_during_a_restart_countdown_and_a_reload_announces_the_cancelled_restart()
    {
        StageRestartUpdate(restartWhenEmpty: false);
        _online = 1;
        Assert.Null(ServerAutoUpdate.RestartNow("Admin"));
        Assert.Equal("a restart countdown is running", ServerAutoUpdate.BeginCheck(true, "test"));
        Assert.Equal("busy", ServerAutoUpdate.Tick());
        Assert.Equal("a restart countdown is running", ServerAutoUpdate.CheckNow("Admin"));
        // Core unloads mid-countdown: the restart ends with this generation and says so.
        Assert.Equal("The restart is cancelled: Progenitor reloaded. Updates still wait for a restart.", ServerAutoUpdate.OnUnloading());
        Assert.Equal(-1, ServerAutoUpdate.CountdownSeconds);
        Assert.True(Audited("result=cancelled"));
        while (_scheduled.Count > 0) _scheduled.Dequeue().Do();
        Assert.Equal(0, _stops);
        Assert.Null(ServerAutoUpdate.OnUnloading());
    }

    [Fact]
    public void The_apply_journal_keeps_only_staged_updates()
    {
        // A hot update journals before its write and drops the entry once it is not a restart update.
        Check();
        Assert.Equal("1.1.0", ActiveVersion());
        Assert.Empty(File_().Verify);
        // A write the empty re-check refuses drops it as well.
        _clientsNeed = true;
        _latest = "1.2.0";
        ServerAutoUpdate.EmptyState.Observe(0, _now.AddMinutes(-10));
        int reads = 0;
        ServerAutoUpdate.OnlineProbe = () => reads++ == 0 ? 0 : 1;
        Check(request: false);
        Assert.Equal(ServerUpdateOutcome.Held, ServerAutoUpdate.RecentOutcomes.Last().Result);
        Assert.Empty(File_().Verify);
    }

    /// <summary>The start after a restart: Bootstrap promoted 1.1.0 into the reload folder; the journal names it.</summary>
    private void PromotedAfterRestart()
    {
        InstallActive("1.1.0");
        Assert.True(ServerAutoUpdate.EditFile(f =>
        {
            f.Verify[Package] = new StartCheck { PluginId = Id, Version = "1.1.0", Sha256 = Sha(Dll("1.1.0")), Previous = "1.0.0", File = "Chat.dll", Utc = _now.AddMinutes(-2) };
            return true;
        }, out _));
    }

    [Fact]
    public void The_start_check_after_a_restart_keeps_an_update_that_loaded_and_clears_the_journal()
    {
        PromotedAfterRestart();
        ServerAutoUpdate.RunStartCheck();
        Assert.True(Audited("op=startcheck package=Team-Chat from=1.0.0 to=1.1.0 route=restart result=ok"));
        Assert.Empty(File_().Verify);
        Assert.Equal("1.1.0", ActiveVersion());
        Assert.Single(_notices);
    }

    [Fact]
    public void The_start_check_after_a_restart_rolls_back_an_update_whose_load_threw_and_lists_it_as_failed()
    {
        PromotedAfterRestart();
        _loadRegistry = "Failed";
        ServerAutoUpdate.RunStartCheck();
        Assert.True(Audited("op=rollback package=Team-Chat from=1.1.0 to=1.0.0 route=restart result=failed(load)"));
        Assert.Empty(File_().Verify);
        Assert.Equal("1.1.0", File_().Failed[Package].Single().Version);
        Assert.Equal("failed", _notices.Last().Kind);
    }

    // ---- rollback -----------------------------------------------------------------------------------------

    [Fact]
    public void A_hot_update_whose_load_throws_is_rolled_back_and_goes_on_the_failed_list()
    {
        _loadRegistry = "Failed";
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        var o = ServerAutoUpdate.RecentOutcomes.Last();
        Assert.Equal(ServerUpdateOutcome.RolledBack, o.Result);
        Assert.True(o.LoadFailed);
        Assert.True(Audited("op=rollback package=Team-Chat from=1.1.0 to=1.0.0 route=hot result=ok"));
        Assert.Equal("1.1.0", File_().Failed[Package].Single().Version);
        Assert.Equal("failed", _notices.Last().Kind);
        // The next check finds it, but never applies it again automatically.
        _loadRegistry = "Loaded";
        ServerAutoUpdate.ForgetAttempts();
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.Equal("1.1.0 failed to load before", ServerAutoUpdate.Report.Items.Single().NotAutoReason);
        Assert.Equal(1, ServerAutoUpdate.ClearFailed(Package));
        Check();
        Assert.Equal("1.1.0", ActiveVersion());
    }

    [Fact]
    public void A_hot_update_that_never_loads_is_rolled_back_but_not_listed_as_failed()
    {
        ServerAutoUpdate.LoadFactsProbe = (id, stem) => new LoadFacts { Snapshot = (true, Sha(Dll("1.0.0"))), Registry = "Loaded" };
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.Equal(ServerUpdateOutcome.RolledBack, ServerAutoUpdate.RecentOutcomes.Last().Result);
        Assert.False(File_().Failed.ContainsKey(Package));
    }

    // ---- Notify, Off -----------------------------------------------------------------------------------------

    [Fact]
    public void Notify_applies_nothing_and_tells_the_admins_once_per_batch()
    {
        _settings = new ServerUpdateSettings { Bound = true };
        Check();
        Assert.Equal("1.0.0", ActiveVersion());
        Assert.True(Audited("route=notify result=skipped(notify)"));
        Assert.Equal("1 server update available", _notices.Single().Text);
        Check();
        Assert.Single(_notices);
        // An admin who joins later gets it once.
        Assert.NotNull(ServerAutoUpdate.NoticeForJoiningAdmin(7));
        Assert.Null(ServerAutoUpdate.NoticeForJoiningAdmin(7));
        // A newer version is a new batch.
        _latest = "1.2.0";
        Check();
        Assert.Equal(2, _notices.Count);
        Assert.StartsWith("Checked ", ServerAutoUpdate.StatusLine());
        Assert.Contains("1 update available", ServerAutoUpdate.StatusLine());
    }

    [Fact]
    public void Off_and_unbound_check_nothing()
    {
        _settings = ServerUpdateSettings.Unbound;
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        Assert.Empty(_audit);
        Assert.Equal("Server updates are Off ([AutoUpdate] Mods).", ServerAutoUpdate.CheckNow("Admin"));
        Assert.StartsWith("Off", ServerAutoUpdate.StatusLine());
    }

    [Fact]
    public void The_tick_checks_after_the_world_loaded_then_on_the_cadence()
    {
        ServerAutoUpdate.Reset();
        ServerAutoUpdate.UtcNow = () => _now;
        ServerAutoUpdate.SettingsProbe = () => new ServerUpdateSettings { Bound = true };
        ServerAutoUpdate.BepInExRootProbe = () => _paths.BepInExRoot;
        ServerAutoUpdate.GameRootProbe = () => _game;
        ServerAutoUpdate.RunOff = work => work();
        ServerAutoUpdate.AuditSink = _audit.Add;
        int requests = 0;
        ServerAutoUpdate.Pipeline = new ServerUpdatePipeline
        {
            ReadIndex = (request, _) =>
            {
                if (request) requests++;
                return Task.FromResult(new ServerIndexRead { Ok = true, Requested = request, Entries = new[] { Entry("1.1.0") } });
            },
        };
        Assert.Equal("waiting for the world", ServerAutoUpdate.Tick());
        ServerAutoUpdate.OnWorldLoaded();
        Assert.Equal("waiting for the start check", ServerAutoUpdate.Tick());
        _now = _now.AddSeconds(ServerAutoUpdate.StartCheckDelaySeconds);
        Assert.Equal("start check", ServerAutoUpdate.Tick());
        Assert.Equal("waiting for the first pass", ServerAutoUpdate.Tick());
        _now = _now.AddSeconds(ServerAutoUpdate.FirstPassDelaySeconds);
        Assert.Equal("check (request)", ServerAutoUpdate.Tick());
        Assert.Equal(1, requests);
        _now = _now.AddHours(5);
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        // Another owner read the index: a pass on that body, no request.
        ServerAutoUpdate.CacheStampProbe = () => _now.AddMinutes(-1);
        Assert.Equal("check (cache)", ServerAutoUpdate.Tick());
        Assert.Equal(1, requests);
        ServerAutoUpdate.CacheStampProbe = () => null;
        // That pass counts as the check: the next request is an interval after it.
        _now = _now.AddHours(5.9);
        Assert.Equal("idle", ServerAutoUpdate.Tick());
        _now = _now.AddHours(0.2);
        Assert.Equal("check (request)", ServerAutoUpdate.Tick());
        Assert.Equal(2, requests);
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
}

[CollectionDefinition("ServerAutoUpdate", DisableParallelization = true)]
public sealed class ServerAutoUpdateCollection
{
}
