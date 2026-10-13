using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// AUTOUPDATE3 (auto-update phase 3, the server), game-free: the settings and mode resolution, the check cadence, the empty
/// gate and its quiet period, live apply versus held versus staged, Restart when empty, Restart now's warnings, the
/// pre-update backup's skip, batch and retention rules, the backups list's new kind, and the admin notice decisions.
/// </summary>
public sealed class ServerUpdateModelTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static ServerUpdateSettings On(UpdateMode mode = UpdateMode.Automatic, bool restart = false, string host = "", int empty = 5,
        bool backup = false, int skip = 6) => new()
    {
        Bound = true, Mode = mode, RestartWhenEmpty = restart, HostRestarts = host, EmptyMinutes = empty, BackupBeforeUpdate = backup,
        PreUpdateBackupSkipHours = skip,
    };

    // ---- settings and modes --------------------------------------------------------------------------------

    [Fact]
    public void Servers_notify_by_default_and_run_off_until_the_section_is_bound()
    {
        var defaults = new ServerUpdateSettings { Bound = true };
        Assert.Equal(UpdateMode.Notify, defaults.Effective);
        Assert.False(defaults.MajorsAutomatic);
        Assert.False(defaults.RestartWhenEmpty);
        Assert.False(defaults.BackupBeforeUpdate);
        Assert.Equal(5, defaults.EmptyMinutes);
        Assert.Equal(2, defaults.PreUpdateBackupKeep);
        Assert.Equal(6, defaults.PreUpdateBackupSkipHours);
        Assert.Equal(6, defaults.CheckHours);
        // Fail closed: an unbound (or unreadable) section is Off whatever its mode says.
        Assert.Equal(UpdateMode.Off, ServerUpdateSettings.Unbound.Effective);
        Assert.Equal(UpdateMode.Off, new ServerUpdateSettings { Bound = false, Mode = UpdateMode.Automatic }.Effective);
        // A server's mode word: anything that is not one of the three words is the default, Notify (never Automatic).
        Assert.Equal(UpdateMode.Notify, ServerUpdateSettings.ParseMode(""));
        Assert.Equal(UpdateMode.Notify, ServerUpdateSettings.ParseMode("automatik"));
        Assert.Equal(UpdateMode.Automatic, ServerUpdateSettings.ParseMode("automatic"));
        Assert.Equal(UpdateMode.Off, ServerUpdateSettings.ParseMode("Off"));
    }

    [Fact]
    public void Ranges_are_clamped()
    {
        Assert.Equal(1, ServerUpdateSettings.ClampCheckHours(0));
        Assert.Equal(168, ServerUpdateSettings.ClampCheckHours(999));
        Assert.Equal(1, ServerUpdateSettings.ClampEmptyMinutes(-3));
        Assert.Equal(120, ServerUpdateSettings.ClampEmptyMinutes(500));
        Assert.Equal(0, ServerUpdateSettings.ClampSkipHours(-1));
        Assert.Equal(168, ServerUpdateSettings.ClampSkipHours(1000));
        Assert.Equal(1, ServerUpdateSettings.ClampWarnMinutes(0));
        Assert.Equal(60, ServerUpdateSettings.ClampWarnMinutes(61));
        Assert.Equal(1, WorldBackupRetention.ClampPreUpdateKeep(0));
        Assert.Equal(20, WorldBackupRetention.ClampPreUpdateKeep(21));
    }

    [Fact]
    public void The_cfg_exclusions_and_pins_lay_over_the_file_without_changing_it()
    {
        var s = new ServerUpdateSettings
        {
            Bound = true,
            Exclude = ServerUpdateSettings.ParseExclude("Team-Chat, Team-Maps;Team-Bags"),
            Pins = ServerUpdateSettings.ParsePins("Team-Old@1.2.0, broken, Team-Odd@"),
        };
        var file = new UpdatePins();
        file.Modes["Team-Maps"] = "Automatic";
        var overlay = s.Overlay(file);
        Assert.Equal(UpdateMode.Off, overlay.ModeOf("Team-Chat"));
        Assert.Equal(UpdateMode.Off, overlay.ModeOf("Team-Maps"));
        Assert.Equal("1.2.0", overlay.Pins["Team-Old"]);
        Assert.False(overlay.Pins.ContainsKey("broken"));
        Assert.False(overlay.Pins.ContainsKey("Team-Odd"));
        Assert.Equal("Automatic", file.Modes["Team-Maps"]);
        Assert.Empty(file.Pins);
    }

    [Fact]
    public void Mode_resolution_follows_the_server_mode_majors_the_per_mod_mode_pins_and_the_failed_list()
    {
        UpdateItem One(ServerUpdateSettings s, UpdatePins? file = null, string installed = "1.0.0", string latest = "1.1.0")
        {
            var m = new ManagedPackage { Key = "Team-Chat", Version = installed };
            m.PluginIds.Add("team.chat");
            var index = new Dictionary<string, IndexListing>(StringComparer.OrdinalIgnoreCase)
            {
                ["Team-Chat"] = new IndexListing { Key = "Team-Chat", Name = "Chat", Latest = latest },
            };
            return UpdateDetection.Detect(new[] { m }, index, s.Overlay(file), s.Effective, s.MajorsAutomatic, _ => false, false, T0).Items.Single();
        }

        Assert.False(One(new ServerUpdateSettings { Bound = true }).Auto);
        Assert.Equal(UpdatePolicy.WhyNotify, One(new ServerUpdateSettings { Bound = true }).NotAutoReason);
        Assert.True(One(On()).Auto);
        // Majors follow the server's own major-version setting.
        Assert.Equal(UpdatePolicy.WhyMajor, One(On(), latest: "2.0.0").NotAutoReason);
        Assert.True(One(new ServerUpdateSettings { Bound = true, Mode = UpdateMode.Automatic, MajorsAutomatic = true }, latest: "2.0.0").Auto);
        // A per-mod Notify (the server's file) wins over Automatic; a cfg pin keeps it.
        var file = new UpdatePins();
        file.Modes["Team-Chat"] = "Notify";
        Assert.False(One(On(), file).Auto);
        Assert.True(One(new ServerUpdateSettings { Bound = true, Mode = UpdateMode.Automatic, Pins = new Dictionary<string, string> { ["Team-Chat"] = "1.0.0" } }).Pinned);
        // A version on the server's failed list is never applied automatically again.
        var failed = new UpdatePins();
        failed.AddFailed("Team-Chat", "1.1.0", "failed to load: boom", T0);
        Assert.Equal("1.1.0 failed to load before", One(On(), failed).NotAutoReason);
    }

    // ---- the check cadence -------------------------------------------------------------------------------

    [Fact]
    public void The_cadence_checks_once_per_interval_and_another_owners_read_counts()
    {
        // The first pass after the world loaded: one request, unless another owner read the index within the interval.
        Assert.Equal(ServerUpdateCadence.Request, ServerUpdateCadence.Due(T0, null, null, 6));
        Assert.Equal(ServerUpdateCadence.Request, ServerUpdateCadence.Due(T0, null, T0.AddHours(-7), 6));
        Assert.Equal(ServerUpdateCadence.Cache, ServerUpdateCadence.Due(T0, null, T0.AddHours(-1), 6));
        // Within the interval nothing is due...
        Assert.Equal("", ServerUpdateCadence.Due(T0.AddHours(2), T0, T0, 6));
        // ...except a pass on a body another owner read since (no request).
        Assert.Equal(ServerUpdateCadence.Cache, ServerUpdateCadence.Due(T0.AddHours(2), T0, T0.AddHours(1), 6));
        // Past the interval: one request, or the cache when another owner read it recently.
        Assert.Equal(ServerUpdateCadence.Request, ServerUpdateCadence.Due(T0.AddHours(6), T0, T0, 6));
        Assert.Equal(ServerUpdateCadence.Cache, ServerUpdateCadence.Due(T0.AddHours(7), T0.AddHours(1), T0.AddHours(6.5), 6));
        // CheckHours is clamped to at least 1.
        Assert.Equal("", ServerUpdateCadence.Due(T0.AddMinutes(30), T0, T0, 0));
    }

    // ---- the empty gate and the quiet period ----------------------------------------------------------------

    [Fact]
    public void The_quiet_period_counts_from_the_last_player_and_a_connect_starts_it_again()
    {
        var gate = new EmptyGate();
        Assert.False(gate.Quiet(T0, 5));
        gate.Observe(2, T0);
        Assert.False(gate.Quiet(T0.AddHours(1), 5));
        Assert.Null(gate.Remaining(T0, 5));
        gate.Observe(0, T0.AddMinutes(1));
        Assert.False(gate.Quiet(T0.AddMinutes(5), 5));
        Assert.Equal(TimeSpan.FromMinutes(1), gate.Remaining(T0.AddMinutes(5), 5));
        Assert.True(gate.Quiet(T0.AddMinutes(6), 5));
        // Still empty: the start of the period does not move.
        gate.Observe(0, T0.AddMinutes(7));
        Assert.True(gate.Quiet(T0.AddMinutes(7), 5));
        // A connect (even one that leaves again before the next observation) starts the period again.
        gate.Connected(T0.AddMinutes(8));
        Assert.False(gate.Quiet(T0.AddMinutes(8), 5));
        gate.Observe(0, T0.AddMinutes(9));
        Assert.False(gate.Quiet(T0.AddMinutes(13), 5));
        Assert.True(gate.Quiet(T0.AddMinutes(14), 5));
    }

    // ---- live apply, held, staged ----------------------------------------------------------------------------

    [Fact]
    public void A_server_only_hot_update_applies_live_one_clients_need_waits_for_empty_and_a_restart_update_is_staged()
    {
        Assert.Equal(ServerApplyRoute.Now, ServerApplyPlan.Decide(hot: true, clientsNeed: false, online: 5, quiet: false).Route);
        var held = ServerApplyPlan.Decide(hot: true, clientsNeed: true, online: 1, quiet: false);
        Assert.Equal(ServerApplyRoute.Hold, held.Route);
        Assert.Equal(ServerApplyPlan.HoldPlayers, held.Why);
        Assert.Equal(ServerApplyPlan.HoldQuiet, ServerApplyPlan.Decide(true, true, 0, quiet: false).Why);
        Assert.Equal(ServerApplyRoute.Now, ServerApplyPlan.Decide(true, true, 0, quiet: true).Route);
        // A restart update is staged with players online or not: nothing changes until the next start.
        Assert.Equal(ServerApplyRoute.Stage, ServerApplyPlan.Decide(false, true, 3, false).Route);
        Assert.Equal(ServerApplyRoute.Stage, ServerApplyPlan.Decide(false, false, 0, true).Route);
        // The re-check in the same step as the write: a join that started meanwhile defers it.
        Assert.Equal(ServerApplyPlan.HoldPlayers, ServerApplyPlan.WriteRefusal(clientsNeed: true, onlineNow: 1));
        Assert.Equal("", ServerApplyPlan.WriteRefusal(clientsNeed: false, onlineNow: 1));
        Assert.Equal("", ServerApplyPlan.WriteRefusal(clientsNeed: true, onlineNow: 0));
    }

    // ---- Restart when empty and Restart now ------------------------------------------------------------------

    [Fact]
    public void Restart_when_empty_needs_the_setting_a_host_that_restarts_staged_updates_and_the_quiet_period()
    {
        var gate = new EmptyGate();
        gate.Observe(0, T0);
        var ok = On(restart: true, host: "a Windows service");
        Assert.Equal("Restart when empty is off", RestartWhenEmptyRule.Decide(On(), 1, gate, T0.AddHours(1), false, null));
        Assert.StartsWith("nothing is known to start the server again", RestartWhenEmptyRule.Decide(On(restart: true), 1, gate, T0.AddHours(1), false, null));
        Assert.Equal("no update waits for a restart", RestartWhenEmptyRule.Decide(ok, 0, gate, T0.AddHours(1), false, null));
        Assert.StartsWith("an update", RestartWhenEmptyRule.Decide(ok, 1, gate, T0.AddHours(1), true, null));
        Assert.StartsWith("the quiet period", RestartWhenEmptyRule.Decide(ok, 1, gate, T0.AddMinutes(4), false, null));
        Assert.Equal("", RestartWhenEmptyRule.Decide(ok, 1, gate, T0.AddMinutes(5), false, null));
        gate.Observe(1, T0.AddMinutes(6));
        Assert.Equal("players are online", RestartWhenEmptyRule.Decide(ok, 1, gate, T0.AddMinutes(6), false, null));
        Assert.Equal("Restart when empty is off", RestartWhenEmptyRule.Decide(ServerUpdateSettings.Unbound, 1, gate, T0, false, null));
    }

    [Fact]
    public void A_failed_restart_is_retried_at_the_next_empty_moment_never_in_a_loop()
    {
        var gate = new EmptyGate();
        gate.Observe(0, T0);
        var s = On(restart: true, host: "panel");
        var failed = T0.AddMinutes(10);
        Assert.StartsWith("the last restart attempt failed", RestartWhenEmptyRule.Decide(s, 1, gate, T0.AddMinutes(11), false, failed));
        // Still empty: an hour after the failure it tries again.
        Assert.Equal("", RestartWhenEmptyRule.Decide(s, 1, gate, failed + RestartWhenEmptyRule.RetryAfterFailure, false, failed));
        // Or the next empty moment: someone came and went, and the quiet period passed again.
        gate.Connected(T0.AddMinutes(20));
        gate.Observe(0, T0.AddMinutes(21));
        Assert.Equal("", RestartWhenEmptyRule.Decide(s, 1, gate, T0.AddMinutes(26), false, failed));
    }

    [Fact]
    public void Restart_now_warns_at_the_full_warning_and_at_one_minute()
    {
        Assert.Equal(new[] { 300, 60 }, RestartCountdown.Announcements(5));
        Assert.Equal(new[] { 60 }, RestartCountdown.Announcements(1));
        Assert.Equal(new[] { 600, 60 }, RestartCountdown.Announcements(10));
        Assert.Equal("The server restarts in 5 minutes to update mods: ChatSkin, BroaderMap.", RestartCountdown.Line(300, new[] { "ChatSkin", "BroaderMap" }));
        Assert.Equal("The server restarts in 1 minute to update mods.", RestartCountdown.Line(60, Array.Empty<string>()));
        Assert.Equal("The server restarts in 30 seconds to update mods.", RestartCountdown.Line(30, Array.Empty<string>()));
    }

    [Fact]
    public void Restart_when_empty_is_refused_while_nothing_restarts_the_server()
    {
        Assert.Equal(ServerUpdateSettings.NoHostRestartsText, ServerUpdateSettings.RestartRefusal(""));
        Assert.Equal(ServerUpdateSettings.NoHostRestartsText, ServerUpdateSettings.RestartRefusal("   "));
        Assert.Null(ServerUpdateSettings.RestartRefusal("NSSM service"));
        Assert.False(On(restart: true).RestartsWhenEmpty);
        Assert.True(On(restart: true, host: "panel").RestartsWhenEmpty);
    }

    // ---- the pre-update backup -------------------------------------------------------------------------------

    private static WorldBackupEntry Backup(string kind, DateTime createdUtc, string name = "", string path = "") => new()
    {
        Kind = WorldBackupEntry.KindBackup, BackupKind = kind, CreatedLocal = createdUtc.ToLocalTime(),
        FolderName = name.Length > 0 ? name : createdUtc.ToString("yyyyMMdd-HHmmss") + "-" + kind, Path = path.Length > 0 ? path : "C:/b/" + kind + createdUtc.Ticks,
    };

    [Fact]
    public void The_pre_update_backup_is_off_by_default_made_once_per_batch_and_skipped_when_a_recent_backup_stands_in()
    {
        Assert.Equal("off", PreUpdateBackupRule.Decide(false, false, null, 6, T0).Action);
        Assert.Equal("done", PreUpdateBackupRule.Decide(true, true, null, 6, T0).Action);
        Assert.True(PreUpdateBackupRule.Decide(true, false, Array.Empty<WorldBackupEntry>(), 6, T0).Take);
        // A manual, scheduled or pre-update backup newer than the hours stands in.
        foreach (string kind in new[] { WorldRestorePaths.KindManual, WorldRestorePaths.KindScheduled, WorldRestorePaths.KindPreUpdate })
        {
            var d = PreUpdateBackupRule.Decide(true, false, new[] { Backup(kind, T0.AddHours(-2), "recent") }, 6, T0);
            Assert.Equal("skip", d.Action);
            Assert.Equal("recent", d.StandIn);
        }

        // Older than the window: one is made.
        Assert.True(PreUpdateBackupRule.Decide(true, false, new[] { Backup(WorldRestorePaths.KindManual, T0.AddHours(-7)) }, 6, T0).Take);
        // A wipe archive and a before-restore copy hold another world: they never stand in.
        var other = new[]
        {
            Backup(WorldRestorePaths.KindBeforeRestore, T0.AddMinutes(-5)),
            new WorldBackupEntry { Kind = WorldBackupEntry.KindArchive, CreatedLocal = T0.AddMinutes(-5).ToLocalTime(), FolderName = "w_1" },
        };
        Assert.True(PreUpdateBackupRule.Decide(true, false, other, 6, T0).Take);
        // 0 hours always makes one.
        Assert.True(PreUpdateBackupRule.Decide(true, false, new[] { Backup(WorldRestorePaths.KindManual, T0.AddMinutes(-1)) }, 0, T0).Take);
        Assert.Equal("ChatSkin 1.3.0, BroaderMap 2.0.2", PreUpdateBackupRule.Label(new[] { ("ChatSkin", "1.3.0"), ("BroaderMap", "2.0.2"), ("", "9") }));
    }

    [Fact]
    public void Pre_update_retention_keeps_its_own_count_and_never_touches_manual_or_scheduled_backups()
    {
        var entries = new List<WorldBackupEntry>
        {
            Backup(WorldRestorePaths.KindPreUpdate, T0.AddHours(-1), "p1"),
            Backup(WorldRestorePaths.KindPreUpdate, T0.AddHours(-2), "p2"),
            Backup(WorldRestorePaths.KindPreUpdate, T0.AddHours(-3), "p3", "C:/b/p3"),
            Backup(WorldRestorePaths.KindPreUpdate, T0.AddHours(-4), "p4"),
            Backup(WorldRestorePaths.KindManual, T0.AddDays(-30), "m-old"),
            Backup(WorldRestorePaths.KindScheduled, T0.AddDays(-30), "s-old"),
            Backup(WorldRestorePaths.KindBeforeRestore, T0.AddDays(-30), "r-old"),
        };
        Assert.Equal(new[] { "p3", "p4" }, WorldBackupRetention.PreUpdatePruneList(entries, 2).Select(e => e.FolderName));
        Assert.Equal(new[] { "p4" }, WorldBackupRetention.PreUpdatePruneList(entries, 2, "C:/b/p3").Select(e => e.FolderName));
        // At least one is always kept.
        Assert.Equal(new[] { "p2", "p3", "p4" }, WorldBackupRetention.PreUpdatePruneList(entries, 0).Select(e => e.FolderName));
        // The manual and scheduled rule never counts or deletes a pre-update backup, and the restore chain's keep count never does either.
        var policy = new BackupPolicy(1, 1);
        Assert.DoesNotContain(WorldBackupRetention.PruneList(entries, policy, T0), e => e.IsPreUpdate);
        Assert.DoesNotContain(WorldBackupCatalog.PruneList(entries, 2), e => e.IsPreUpdate);
        Assert.All(entries.Where(e => e.IsPreUpdate), e => Assert.False(e.IsRestoreChain));
    }

    [Fact]
    public void A_pre_update_backup_reads_as_its_kind_with_the_updates_it_came_before_and_has_its_own_heading()
    {
        var e = Backup(WorldRestorePaths.KindPreUpdate, T0);
        e.Updates = "ChatSkin 1.3.0, BroaderMap 2.0.2";
        Assert.Equal("Before update", e.KindLabel);
        Assert.Equal("Before update · ChatSkin 1.3.0, BroaderMap 2.0.2", e.RowLabel);
        Assert.StartsWith("Before update · ChatSkin 1.3.0, BroaderMap 2.0.2 from ", e.DisplayName);
        Assert.StartsWith("the backup made before an update from ", e.BaseLabel);
        Assert.True(e.IsMadeBackup);
        Assert.Equal(ServerBackups.GroupPreUpdate, ServerBackups.GroupOf(e));
        var view = ServerBackupsServer.View(e);
        Assert.Equal("Before update · ChatSkin 1.3.0, BroaderMap 2.0.2", view.KindLabel);
        Assert.Equal(ServerBackups.GroupPreUpdate, ServerBackups.GroupOf(view));
        // The list: manual, scheduled, before an update, before a restore, wipe archives.
        Assert.Equal(new[] { 0, 1, ServerBackups.GroupPreUpdate, 2, 3 }, ServerBackups.GroupOrder);
        var s = new ServerBackupsState();
        s.Entries.Add(ServerBackupsServer.View(Backup(WorldRestorePaths.KindBeforeRestore, T0.AddHours(-1))));
        s.Entries.Add(view);
        s.Entries.Add(ServerBackupsServer.View(Backup(WorldRestorePaths.KindManual, T0.AddHours(-2))));
        var shown = ServerBackupsRows.ListedEntries(s);
        Assert.Equal(new[] { WorldRestorePaths.KindManual, WorldRestorePaths.KindPreUpdate, WorldRestorePaths.KindBeforeRestore }, shown.Select(x => x.BackupKind));
        Assert.Contains(ServerBackupsRows.ListHeadings(shown), h => h.Text == ServerBackupsRows.PreUpdateHeading);
        s.PreUpdateBytes = 2048;
        s.BackupBytes = 2048;
        Assert.Equal("before update 2 KB", ServerBackups.KindsLine(s));
    }

    [Fact]
    public void The_sidecar_keeps_the_updates_and_an_interrupted_pre_update_copy_is_its_own_leftover()
    {
        var sidecar = new BackupSidecar { Kind = WorldRestorePaths.KindPreUpdate, Trigger = WorldRestorePaths.TriggerUpdate, Updates = "Chat 1.1.0", CreatedUtc = T0 };
        var back = BackupSidecar.Parse(sidecar.ToJson())!;
        Assert.Equal("Chat 1.1.0", back.Updates);
        Assert.Equal(WorldRestorePaths.KindPreUpdate, back.Kind);
        Assert.Equal("", BackupSidecar.Parse(new BackupSidecar { Kind = WorldRestorePaths.KindManual }.ToJson())!.Updates);
        Assert.True(WorldBackupMaker.IsOwnPartial("20261009-120000-pre-update.partial"));
        Assert.True(WorldRestorePaths.IsMadeBackupKind(WorldRestorePaths.KindPreUpdate));
        Assert.Equal("pre-update", ServerBackupsJob.KindWord(WorldRestorePaths.KindPreUpdate));
    }

    [Fact]
    public void A_pre_update_backup_is_made_with_its_label_and_prunes_only_its_own_kind()
    {
        string root = Path.Combine(Path.GetTempPath(), "pau3-bk-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            string save = Path.Combine(root, "Saves", "v4", "world1");
            Directory.CreateDirectory(save);
            File.WriteAllBytes(Path.Combine(save, "AutoSave_1.save"), Encoding.UTF8.GetBytes("save"));
            Assert.True(WorldRestorePaths.TryLayout(save, out var layout, out _));
            var fs = PhysicalWorldFs.Instance;
            DateTime clock = T0;
            var env = new RestoreEnvironment { UtcNow = () => clock, LocalNow = () => clock.ToLocalTime() };
            BackupOutcome Make(string kind, string label = "")
            {
                clock = clock.AddMinutes(1);
                var job = new BackupJob { SaveFolder = save, Kind = kind, Trigger = WorldRestorePaths.TriggerUpdate, SpareBytes = 0, StrictSave = false, Updates = label };
                if (kind == WorldRestorePaths.KindPreUpdate) job.PreUpdateKeep = 2;
                else job.Retention = new BackupPolicy(10, 0);
                return WorldBackupMaker.Make(fs, job, env);
            }

            Assert.True(Make(WorldRestorePaths.KindManual).Ok);
            var first = Make(WorldRestorePaths.KindPreUpdate, "Chat 1.1.0");
            Assert.True(first.Ok, first.Summary);
            Assert.StartsWith("Before update · Chat 1.1.0 from ", first.Name);
            Assert.True(Make(WorldRestorePaths.KindPreUpdate, "Chat 1.2.0").Ok);
            var third = Make(WorldRestorePaths.KindPreUpdate, "Chat 1.3.0");
            Assert.True(third.Ok);
            Assert.Equal(new[] { first.FolderName }, third.Pruned);
            var list = WorldBackupCatalog.List(fs, layout);
            Assert.Equal(2, list.Count(e => e.IsPreUpdate));
            Assert.Single(list, e => e.BackupKind == WorldRestorePaths.KindManual);
            Assert.Contains(list, e => e.Updates == "Chat 1.3.0");

            // Older Bootstrap and Core list only folders with progenitor-backup.json (review 2026-10-09): pre-update backups
            // carry their own sidecar name, so older code never counts them into the restore chain or prunes for them.
            var backupDirs = Directory.GetDirectories(layout.BackupsRoot);
            Assert.Single(backupDirs, d => File.Exists(Path.Combine(d, WorldRestorePaths.SidecarName)));
            Assert.Equal(2, backupDirs.Count(d => File.Exists(Path.Combine(d, WorldRestorePaths.PreUpdateSidecarName))
                && !File.Exists(Path.Combine(d, WorldRestorePaths.SidecarName))));
            // The new code still restores one: the sidecar matches its files and is not one of the world's files.
            var pre = list.First(e => e.IsPreUpdate);
            Assert.True(pre.Restorable);
            Assert.Null(WorldRestoreApply.MatchesSidecar(fs, pre.Path, WorldManifest.Build(fs, pre.Path, hash: true, WorldBackupCatalog.SkipSidecar)));
        }
        finally
        {
            try { Directory.Delete(root, true); }
            catch { /* temp */ }
        }
    }

    // ---- review follow-ups (2026-10-09) ----------------------------------------------------------------------

    [Fact]
    public void A_restart_that_was_not_warned_never_quits_with_a_player_who_came_meanwhile()
    {
        // Nobody: stop.
        Assert.Equal(RestartStopGate.Stop, RestartStopGate.Decide("empty", false, 0, null, T0));
        Assert.Equal(RestartStopGate.Stop, RestartStopGate.Decide("empty", false, 0, T0.AddMinutes(-1), T0));
        // Someone online, or a connect since the restart was decided (they may still be loading in): Restart when empty
        // gives up (the quiet period starts again), Restart now warns them first.
        Assert.Equal(RestartStopGate.Abort, RestartStopGate.Decide("empty", false, 1, null, T0));
        Assert.Equal(RestartStopGate.Abort, RestartStopGate.Decide("empty", false, 0, T0.AddSeconds(30), T0));
        Assert.Equal(RestartStopGate.Warn, RestartStopGate.Decide("now", false, 0, T0.AddSeconds(30), T0));
        Assert.Equal(RestartStopGate.Warn, RestartStopGate.Decide("now", false, 2, null, T0));
        // A failed read counts as someone.
        Assert.Equal(RestartStopGate.Abort, RestartStopGate.Decide("empty", false, -1, null, T0));
        // After the warning ran to its end the players were told: stop.
        Assert.Equal(RestartStopGate.Stop, RestartStopGate.Decide("now", true, 3, T0.AddMinutes(4), T0));
    }

    [Fact]
    public void Whether_clients_need_an_update_is_read_from_the_new_version_too_and_fails_safe()
    {
        Assert.False(ServerApplyPlan.ClientsNeed(false, PackageSide.Server));
        // 1.1 says both sides (it may have become required), or says nothing: clients need it, whatever the old copy said.
        Assert.True(ServerApplyPlan.ClientsNeed(false, PackageSide.Both));
        Assert.True(ServerApplyPlan.ClientsNeed(false, PackageSide.Client));
        Assert.True(ServerApplyPlan.ClientsNeed(false, PackageSide.Unknown));
        // The running copy is required at the handshake: clients need it.
        Assert.True(ServerApplyPlan.ClientsNeed(true, PackageSide.Server));
    }

    [Fact]
    public void Automatic_acts_as_notify_while_remote_installs_are_off()
    {
        var off = new ServerUpdateSettings { Bound = true, Mode = UpdateMode.Automatic, RemoteInstallEnabled = false };
        Assert.Equal(UpdateMode.Notify, off.Effective);
        Assert.True(off.AutomaticHeldByRemoteInstall);
        Assert.Equal(UpdateMode.Automatic, On().Effective);
        Assert.False(On().AutomaticHeldByRemoteInstall);
        // Notify and Off are unchanged; unbound stays Off.
        Assert.Equal(UpdateMode.Off, new ServerUpdateSettings { Bound = true, Mode = UpdateMode.Off, RemoteInstallEnabled = false }.Effective);
        Assert.Equal(UpdateMode.Off, ServerUpdateSettings.Unbound.Effective);
    }

    [Fact]
    public void An_admin_edit_writes_only_its_own_line_so_a_hand_edit_of_another_key_survives()
    {
        // The server read the file with DevThunderstoreUrl = A; an operator (or another session) since changed it to B on
        // disk. The admin edit of [AutoUpdate] Mods rewrites only that line: B survives (before, the whole file was saved
        // from memory and A came back).
        string onDisk = "## Settings file\r\n\r\n[AutoUpdate]\r\n\r\n## Server: Automatic, Notify or Off.\r\n# Setting type: String\r\nMods = Notify\r\n\r\n"
            + "HostRestarts = \r\n\r\n[Install]\r\n\r\nDevThunderstoreUrl = http://127.0.0.1:27460/\r\n";
        string after = CfgLineEdit.Set(onDisk, "AutoUpdate", "Mods", "Automatic");
        Assert.Contains("Mods = Automatic\r\n", after);
        Assert.Contains("DevThunderstoreUrl = http://127.0.0.1:27460/\r\n", after);
        Assert.Equal(onDisk.Replace("Mods = Notify", "Mods = Automatic"), after);
        // A commented line with the same key is not the setting; a key missing from its section is added inside the section.
        string added = CfgLineEdit.Set(after, "AutoUpdate", "WarnMinutes", "7");
        Assert.Contains("HostRestarts = \r\nWarnMinutes = 7\r\n\r\n[Install]", added);
        Assert.Equal(added, CfgLineEdit.Set(added, "AutoUpdate", "WarnMinutes", "7"));
        // Sections and keys match regardless of case (as BepInEx reads them).
        Assert.Equal(added.Replace("Mods = Automatic", "mods = Off"), CfgLineEdit.Set(added, "autoupdate", "mods", "Off"));
        // A comment naming the key is never taken for it.
        Assert.Equal("[S]\n# K = old\nK = 2\n", CfgLineEdit.Set("[S]\n# K = old\nK = 1\n", "S", "K", "2"));
        // A missing section is appended; LF files stay LF.
        Assert.Equal("[A]\nX = 1\n\n[AutoUpdate]\n\nMods = Off\n", CfgLineEdit.Set("[A]\nX = 1\n", "AutoUpdate", "Mods", "Off"));
        Assert.Equal("[AutoUpdate]\n\nMods = Off\n", CfgLineEdit.Set("", "AutoUpdate", "Mods", "Off"));
    }

    // ---- admin notices -------------------------------------------------------------------------------------

    [Fact]
    public void Admins_are_told_once_per_batch_what_waits_what_was_applied_and_what_failed()
    {
        Assert.Null(AdminNoticeRule.Waiting(Array.Empty<string>(), Array.Empty<string>()));
        var one = AdminNoticeRule.Waiting(new[] { "Chat 1.0.0 → 1.1.0" }, Array.Empty<string>())!;
        Assert.Equal("available", one.Kind);
        Assert.Equal("1 server update available", one.Text);
        Assert.False(one.OfferRestart);
        var restart = AdminNoticeRule.Waiting(new[] { "a", "b" }, new[] { "Team-Maps 2.0.0" })!;
        Assert.Equal("2 server updates available · 1 update waiting for a restart", restart.Text);
        Assert.True(restart.OfferRestart);
        Assert.Equal("restart", restart.Kind);
        // The same set in another order is the same batch: not sent twice.
        Assert.False(AdminNoticeRule.ShouldSend(AdminNoticeRule.Waiting(new[] { "b", "a" }, new[] { "Team-Maps 2.0.0" }), restart.Signature));
        Assert.True(AdminNoticeRule.ShouldSend(one, restart.Signature));
        Assert.False(AdminNoticeRule.ShouldSend(null, ""));

        var outcomes = new[]
        {
            new ServerUpdateOutcome { Package = "Team-Chat", Name = "ChatSkin", To = "1.3.0", Result = ServerUpdateOutcome.Applied },
            new ServerUpdateOutcome { Package = "Team-Maps", Name = "BroaderMap", To = "2.0.2", Result = ServerUpdateOutcome.Staged },
            new ServerUpdateOutcome { Package = "Team-Bags", Name = "Bags", To = "1.1.0", Result = ServerUpdateOutcome.RolledBack },
            new ServerUpdateOutcome { Package = "Team-Held", Name = "Held", To = "1.1.0", Result = ServerUpdateOutcome.Held },
        };
        var batch = AdminNoticeRule.Batch(outcomes, 1)!;
        Assert.Equal("failed", batch.Kind);
        Assert.Equal("ChatSkin 1.3.0 applied live · BroaderMap 2.0.2 waits for a restart · Bags 1.1.0 failed and was rolled back", batch.Text);
        Assert.True(batch.OfferRestart);
        Assert.Null(AdminNoticeRule.Batch(new[] { outcomes[3] }, 0));
        Assert.Equal("ChatSkin 1.3.0 applied live", AdminNoticeRule.Batch(new[] { outcomes[0] }, 0)!.Text);
    }

    [Fact]
    public void A_batch_and_what_still_waits_go_out_as_one_notice()
    {
        var batch = new AdminUpdateNotice { Kind = "applied", Text = "Probe 1.1.0 applied live", Signature = "batch|x" };
        var waiting = AdminNoticeRule.Waiting(new[] { "Both (players are online)" }, new[] { "Maps 2.0.0" })!;
        var one = AdminNoticeRule.Combine(batch, waiting)!;
        Assert.Equal("applied", one.Kind);
        Assert.Equal("Probe 1.1.0 applied live · 1 server update available · 1 update waiting for a restart", one.Text);
        Assert.True(one.OfferRestart);
        Assert.Contains(waiting.Signature, one.Signature);
        // A batch that staged an update already says it waits for a restart: only what is available is added.
        var staged = new AdminUpdateNotice { Kind = "applied", Text = "Maps 2.0.0 waits for a restart", OfferRestart = true, Signature = "batch|y" };
        Assert.Equal("Maps 2.0.0 waits for a restart · 1 server update available", AdminNoticeRule.Combine(staged, waiting)!.Text);
        Assert.Equal("Maps 2.0.0 waits for a restart",
            AdminNoticeRule.Combine(staged, AdminNoticeRule.Waiting(Array.Empty<string>(), new[] { "Maps 2.0.0" }))!.Text);
        Assert.Same(batch, AdminNoticeRule.Combine(batch, null));
        Assert.Same(waiting, AdminNoticeRule.Combine(null, waiting));
        Assert.Null(AdminNoticeRule.Combine(null, null));
    }

    [Fact]
    public void The_catalog_names_the_package_Progenitor_installed_a_loaded_mod_from_unless_the_operator_did()
    {
        var loaded = new[]
        {
            new LoadedModInfo("probe.both", "1.1.0", Progenitor.Plugins.NetworkCompatibilityLevel.ServerMustHave, Progenitor.Plugins.NetworkVersionRule.Exact, true),
            new LoadedModInfo("op.named", "1.0.0", Progenitor.Plugins.NetworkCompatibilityLevel.ServerMustHave, Progenitor.Plugins.NetworkVersionRule.Exact, true),
        };
        var overrides = new ManifestOverrides
        {
            ServerName = "S",
            Mods = new[] { new ManifestModOverride { Id = "op.named", Package = "Op-Own" } },
            Recommended = new[] { new RecommendedMod("r.id", "Owner-R", "1.0.0", "") },
        };
        var ledger = new[] { ("probe.both", "Progenitor-UpdateProbeBoth"), ("op.named", "Other-Pkg"), ("not.loaded", "X-Y"), ("bad", "NoDash") };
        var merged = ModManifestJson.WithLedgerPackages(overrides, loaded, ledger);
        var body = ModManifestJson.BuildBody(loaded, merged, new ManifestOperator("", "", ""), T0);
        Assert.Equal("Progenitor-UpdateProbeBoth", body.Mods.Single(m => m.Id == "probe.both").Package);
        Assert.Equal("Progenitor", body.Mods.Single(m => m.Id == "probe.both").Namespace);
        Assert.Equal("Op-Own", body.Mods.Single(m => m.Id == "op.named").Package);
        Assert.DoesNotContain(body.Mods, m => m.Id == "not.loaded");
        Assert.Equal("S", merged.ServerName);
        Assert.Single(merged.Recommended);
        Assert.Same(overrides, ModManifestJson.WithLedgerPackages(overrides, loaded, Array.Empty<(string, string)>()));
    }

    [Fact]
    public void The_audit_line_has_the_remote_install_logs_shape()
    {
        string line = ServerUpdateAudit.Line(T0, "apply", "Team-Chat", "1.0.0", "1.1.0", "hot", "ok", "loaded \"now\"", new[] { "a.dll" });
        Assert.Equal("AutoUpdate 2026-10-09T12:00:00Z op=apply package=Team-Chat from=1.0.0 to=1.1.0 route=hot result=ok detail=\"loaded 'now'\" files=[a.dll]", line);
        Assert.Equal("AutoUpdate 2026-10-09T12:00:00Z op=check package=- from=- to=- route=- result=ok", ServerUpdateAudit.Line(T0, "check"));
    }
}
