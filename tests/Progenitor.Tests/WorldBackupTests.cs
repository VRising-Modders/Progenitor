using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.WorldRestore;
using Xunit;
using Fixture = Progenitor.Tests.WorldRestoreApplyTests.Fixture;

namespace Progenitor.Tests;

/// <summary>
/// BACKUPNOW1: manual and scheduled backups on the fake file system: the consistent copy (an autosave landing mid-copy, a
/// file changing between its hash and its copy, a folder that never settles), the free-space refusal, retention (count, age,
/// both, protected), the schedule's timing, the cfg read, the start backup's order against a pending restore, and the game's
/// autosave summary. Game-free.
/// </summary>
public sealed class WorldBackupTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 7, 13, 30, 0, DateTimeKind.Utc);

    private static BackupJob Job(Fixture f, string kind = WorldRestorePaths.KindManual, bool strict = true, BackupPolicy? policy = null) => new()
    {
        SaveFolder = f.Save,
        Kind = kind,
        Trigger = kind == WorldRestorePaths.KindManual ? WorldRestorePaths.TriggerManual : WorldRestorePaths.TriggerInterval,
        StrictSave = strict,
        RetryWaitMs = 3000,
        SpareBytes = 0,
        Retention = policy,
    };

    private static string[] BackupDirs(Fixture f) =>
        f.Fs.Dirs.Where(d => string.Equals(Path.GetDirectoryName(d), Path.GetFullPath(f.BackupsRoot).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;

    /// <summary>The save folder as a backup must hold it: everything but .TEMP.</summary>
    private static SortedDictionary<string, string> Live(Fixture f)
    {
        var map = f.Fs.Snapshot(f.Save);
        foreach (var k in map.Keys.Where(k => k.StartsWith(".TEMP", StringComparison.OrdinalIgnoreCase)).ToList()) map.Remove(k);
        return map;
    }

    private static SortedDictionary<string, string> Copy(Fixture f, string folder)
    {
        var map = f.Fs.Snapshot(Path.Combine(f.BackupsRoot, folder));
        map.Remove(WorldRestorePaths.SidecarName);
        return map;
    }

    // ---- the consistent copy --------------------------------------------------------------------------

    [Fact]
    public void A_manual_backup_is_the_whole_world_folder_checked_file_by_file_with_a_sidecar()
    {
        var f = new Fixture();
        var o = WorldBackupMaker.Make(f.Fs, Job(f), f.Env());
        Assert.True(o.Ok, o.Summary);
        Assert.Equal("20261007-093000-manual", o.FolderName);
        Assert.Equal(1, o.Attempts);
        Assert.Equal(Live(f), Copy(f, o.FolderName));
        Assert.Contains("progenitor" + Path.DirectorySeparatorChar + "sample.json", Copy(f, o.FolderName).Keys);
        Assert.Contains("StartDate.json", Copy(f, o.FolderName).Keys);
        Assert.DoesNotContain(BackupDirs(f), d => d.EndsWith(".partial", StringComparison.Ordinal));

        var side = BackupSidecar.Parse(f.Fs.Bytes(Path.Combine(o.BackupPath, WorldRestorePaths.SidecarName)))!;
        Assert.Equal(WorldRestorePaths.KindManual, side.Kind);
        Assert.Equal(WorldRestorePaths.TriggerManual, side.Trigger);
        Assert.Equal("", side.ChainId);
        Assert.All(side.Files, e => Assert.Equal(64, e.Sha256.Length));
        Assert.Null(WorldRestoreApply.MatchesSidecar(f.Fs, o.BackupPath, WorldManifest.Build(f.Fs, o.BackupPath, hash: true, WorldRestoreApply.SkipForStaging)));

        // Listed as its own kind, restorable, and never part of the restore chain.
        var e = WorldBackupCatalog.List(f.Fs, f.Layout, WorldRestoreApplyTests.Zone).Single(x => x.FolderName == o.FolderName);
        Assert.Equal("Manual backup", e.KindLabel);
        Assert.Equal("Manual backup from " + WorldBackupCatalog.FormatTime(WorldTime.ToLocal(NowUtc, WorldRestoreApplyTests.Zone)), e.DisplayName);
        Assert.StartsWith("the manual backup from ", e.BaseLabel);
        Assert.True(e.Restorable);
        Assert.True(e.IsMadeBackup);
        Assert.False(e.IsRestoreChain);
    }

    [Fact]
    public void An_autosave_landing_mid_copy_is_caught_and_the_copy_is_made_again()
    {
        var f = new Fixture();
        int copies = 0;
        string newest = Path.Combine(f.Save, "AutoSave_12.save.gz");
        void Land(string name, string path)
        {
            if (name == "copy" && path.Contains(".partial") && ++copies == 2)
            {
                // The game's autosave: a new file, and the oldest one rotated out.
                f.Fs.Put(newest, FakeWorldFs.Gzip("world after the autosave"), new DateTime(2026, 10, 6, 20, 5, 0, DateTimeKind.Utc));
                f.Fs.DeleteFileForTests(Path.Combine(f.Save, "AutoSave_10.save.gz"));
                return;
            }

            f.Fs.Before = Land;
        }

        f.Fs.Before = Land;
        var o = WorldBackupMaker.Make(f.Fs, Job(f), f.Env());
        Assert.True(o.Ok, o.Summary);
        Assert.Equal(2, o.Attempts);
        Assert.True(f.Fs.Slept >= 3000);
        // The backup is the folder after the autosave, never a mix of the two.
        var copy = Copy(f, o.FolderName);
        Assert.Equal(Live(f), copy);
        Assert.Contains("AutoSave_12.save.gz", copy.Keys);
        Assert.DoesNotContain("AutoSave_10.save.gz", copy.Keys);
        Assert.Equal(2, BackupSidecar.Parse(f.Fs.Bytes(Path.Combine(o.BackupPath, WorldRestorePaths.SidecarName)))!.Attempts);
        Assert.Contains(o.Log, l => l.Contains("changed during the copy"));
    }

    [Fact]
    public void A_file_written_between_its_hash_and_its_copy_is_caught_by_the_hash_of_the_source_as_read()
    {
        var f = new Fixture();
        string mod = Path.Combine(f.Save, "progenitor", "sample.json");
        var mtime = f.Fs.Mtime(mod);
        bool done = false;
        void Swap(string name, string path)
        {
            // Same length and write time: the shape check alone could not see it.
            if (!done && name == "copy" && path.EndsWith("sample.json", StringComparison.Ordinal))
            {
                done = true;
                f.Fs.Put(mod, "{\"now\":nope}", mtime);
                return;
            }

            f.Fs.Before = Swap;
        }

        f.Fs.Before = Swap;
        var o = WorldBackupMaker.Make(f.Fs, Job(f), f.Env());
        Assert.True(o.Ok, o.Summary);
        Assert.Equal(2, o.Attempts);
        Assert.Equal("{\"now\":nope}", f.Fs.Text(Path.Combine(o.BackupPath, "progenitor", "sample.json")));
        Assert.Equal(Live(f), Copy(f, o.FolderName));
    }

    [Fact]
    public void A_folder_that_keeps_changing_fails_and_leaves_no_copy()
    {
        var f = new Fixture();
        int n = 20;
        void Always(string name, string path)
        {
            if (name == "copy" && path.Contains(".partial"))
                f.Fs.Put(Path.Combine(f.Save, "AutoSave_" + n++ + ".save.gz"), FakeWorldFs.Gzip("again " + n), new DateTime(2026, 10, 6, 21, 0, 0, DateTimeKind.Utc).AddMinutes(n));
            f.Fs.Before = Always;
        }

        var before = BackupDirs(f);
        f.Fs.Before = Always;
        var o = WorldBackupMaker.Make(f.Fs, Job(f), f.Env());
        f.Fs.Before = null;
        Assert.False(o.Ok);
        Assert.Equal(3, o.Attempts);
        Assert.Contains("kept changing during the copy", o.Reason);
        Assert.StartsWith("Not backed up:", o.Summary);
        Assert.Equal(before, BackupDirs(f));
    }

    [Fact]
    public void Too_little_free_space_is_refused_before_anything_is_written()
    {
        var f = new Fixture();
        long size = WorldBackupMaker.Estimate(f.Fs, f.Save);
        var job = Job(f);
        job.SpareBytes = 1000;
        f.Fs.Free = size + size / 10 + 999;
        var before = BackupDirs(f);
        var o = WorldBackupMaker.Make(f.Fs, job, f.Env());
        Assert.False(o.Ok);
        Assert.Contains("not enough free disk space for a backup", o.Reason);
        Assert.Equal(before, BackupDirs(f));
        Assert.DoesNotContain(f.Fs.Log, l => l.Contains(" mkdir ") || l.Contains(" copy "));

        f.Fs.Free = size + size / 10 + 1000;
        Assert.True(WorldBackupMaker.Make(f.Fs, job, f.Env()).Ok);
        Assert.Null(WorldBackupMaker.SpaceRefusal(size, null, 1000));
    }

    [Fact]
    public void A_newest_save_that_does_not_decompress_fails_a_running_backup_but_is_kept_as_is_at_a_start()
    {
        var f = new Fixture();
        f.Fs.Put(Path.Combine(f.Save, "AutoSave_11.save.gz"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
            new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc));
        var running = WorldBackupMaker.Make(f.Fs, Job(f), f.Env());
        Assert.False(running.Ok);
        Assert.Contains("AutoSave_11.save.gz", running.Reason);

        var start = WorldBackupMaker.Make(f.Fs, Job(f, WorldRestorePaths.KindScheduled, strict: false), f.Env());
        Assert.True(start.Ok, start.Summary);
        Assert.Contains(start.Warnings, w => w.Contains("does not read cleanly"));
        Assert.Contains("did not read cleanly", BackupSidecar.Parse(f.Fs.Bytes(Path.Combine(start.BackupPath, WorldRestorePaths.SidecarName)))!.Note);
    }

    [Fact]
    public void The_live_server_path_and_a_wrong_kind_are_refused()
    {
        var f = new Fixture();
        var job = Job(f);
        job.SaveFolder = @"C:\VRisingServer\save-data\Saves\v4\world1";
        Assert.Contains("live dedicated server", WorldBackupMaker.Make(f.Fs, job, f.Env()).Reason);
        Assert.Contains("unknown backup kind", WorldBackupMaker.Make(f.Fs, Job(f, WorldRestorePaths.KindBeforeRestore), f.Env()).Reason);
    }

    // ---- retention ------------------------------------------------------------------------------------

    private static WorldBackupEntry B(string kind, int daysAgo, string? name = null) => new()
    {
        Kind = WorldBackupEntry.KindBackup,
        BackupKind = kind,
        FolderName = name ?? kind + "-" + daysAgo,
        Path = Path.Combine(FakeWorldFs.Root, "b", name ?? kind + "-" + daysAgo),
        CreatedLocal = NowUtc.AddDays(-daysAgo).AddMinutes(-1).ToLocalTime(),
    };

    private static string[] Names(IEnumerable<WorldBackupEntry> list) => list.Select(e => e.FolderName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static List<WorldBackupEntry> Mixed() => new()
    {
        B("manual", 0), B("manual", 3), B("manual", 10), B("manual", 40),
        B("scheduled", 0), B("scheduled", 1), B("scheduled", 2), B("scheduled", 20),
        B(WorldRestorePaths.KindBeforeRestore, 90), B(WorldRestorePaths.KindBeforeRestore, 100),
        new() { Kind = WorldBackupEntry.KindArchive, FolderName = "archive-200", Path = "a", CreatedLocal = NowUtc.AddDays(-200).ToLocalTime() },
    };

    [Fact]
    public void Retention_by_count_keeps_the_newest_n_of_each_kind_separately()
    {
        Assert.Equal(new[] { "manual-10", "manual-40", "scheduled-2", "scheduled-20" }, Names(WorldBackupRetention.PruneList(Mixed(), new BackupPolicy(2, 0), NowUtc)));
        Assert.Empty(WorldBackupRetention.PruneList(Mixed(), new BackupPolicy(0, 0), NowUtc));
        Assert.Empty(WorldBackupRetention.PruneList(Mixed(), new BackupPolicy(10, 0), NowUtc));
    }

    [Fact]
    public void Retention_by_age_deletes_the_older_ones_but_never_the_newest_of_a_kind()
    {
        Assert.Equal(new[] { "manual-10", "manual-40", "scheduled-20" }, Names(WorldBackupRetention.PruneList(Mixed(), new BackupPolicy(0, 7), NowUtc)));
        // Every manual backup is old: the newest still stays.
        var old = new List<WorldBackupEntry> { B("manual", 30), B("manual", 60) };
        Assert.Equal(new[] { "manual-60" }, Names(WorldBackupRetention.PruneList(old, new BackupPolicy(0, 7), NowUtc)));
    }

    [Fact]
    public void With_both_rules_the_stricter_one_wins()
    {
        // Count 3 keeps manual-0/3/10 and scheduled-0/1/2; 7 days keeps only those within a week: both must pass.
        Assert.Equal(new[] { "manual-10", "manual-40", "scheduled-20" }, Names(WorldBackupRetention.PruneList(Mixed(), new BackupPolicy(3, 7), NowUtc)));
        // Count 1 is stricter than 30 days.
        Assert.Equal(new[] { "manual-10", "manual-3", "manual-40", "scheduled-1", "scheduled-2", "scheduled-20" },
            Names(WorldBackupRetention.PruneList(Mixed(), new BackupPolicy(1, 30), NowUtc)));
        Assert.Equal("the newest 3 of each kind, none older than 7 days", new BackupPolicy(3, 7).Text());
        Assert.Equal("those younger than 1 day", new BackupPolicy(0, 1).Text());
        Assert.Equal("every one", new BackupPolicy(0, 0).Text());
    }

    [Fact]
    public void Retention_never_touches_a_protected_path_the_restore_chain_or_wipe_archives()
    {
        var all = Mixed();
        var pendingSource = all.Single(e => e.FolderName == "manual-40").Path;
        var list = WorldBackupRetention.PruneList(all, new BackupPolicy(1, 1), NowUtc, pendingSource);
        Assert.DoesNotContain(list, e => e.FolderName == "manual-40");
        Assert.DoesNotContain(list, e => e.IsRestoreChain || e.Kind == WorldBackupEntry.KindArchive);
        // And the restore chain's keep count never counts (or deletes) manual and scheduled backups.
        Assert.All(WorldBackupCatalog.PruneList(all, 2), e => Assert.True(e.IsRestoreChain));
        Assert.Empty(WorldBackupCatalog.PruneList(all, 2));
    }

    [Fact]
    public void The_next_backup_preview_counts_the_backup_about_to_be_made()
    {
        var all = new List<WorldBackupEntry> { B("manual", 0), B("manual", 1), B("scheduled", 0) };
        Assert.Empty(WorldBackupRetention.PruneList(all, new BackupPolicy(2, 0), NowUtc));
        Assert.Equal(new[] { "manual-1" }, Names(WorldBackupRetention.NextDeletes(all, new BackupPolicy(2, 0), NowUtc, WorldRestorePaths.KindManual)));
        Assert.Empty(WorldBackupRetention.NextDeletes(all, new BackupPolicy(2, 0), NowUtc, WorldRestorePaths.KindScheduled));
    }

    [Fact]
    public void Retention_after_a_backup_deletes_through_a_deleting_rename_and_keeps_a_pending_restore_source()
    {
        var f = new Fixture();
        var env = f.Env();
        var made = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            int k = i;
            env.LocalNow = () => WorldRestoreApplyTests.Local0.AddMinutes(k);
            env.UtcNow = () => NowUtc.AddMinutes(k);
            var o = WorldBackupMaker.Make(f.Fs, Job(f), env);
            Assert.True(o.Ok, o.Summary);
            made.Add(o.FolderName);
        }

        // A restore of the oldest manual backup is waiting: retention keeps it.
        var oldest = WorldBackupCatalog.List(f.Fs, f.Layout, WorldRestoreApplyTests.Zone).Single(e => e.FolderName == made[0]);
        var request = f.Request();
        request.SourceKind = WorldBackupEntry.KindBackup;
        request.SourcePath = oldest.Path;
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);

        env.LocalNow = () => WorldRestoreApplyTests.Local0.AddMinutes(10);
        env.UtcNow = () => NowUtc.AddMinutes(10);
        var last = WorldBackupMaker.Make(f.Fs, Job(f, policy: new BackupPolicy(1, 0)), env);
        Assert.True(last.Ok, last.Summary);
        Assert.Equal(new[] { made[1], made[2] }, last.Pruned.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Contains(f.Fs.Log, l => l.Contains("movedir") && l.Contains(WorldRestorePaths.DeletingPrefix + made[1]));
        var left = BackupDirs(f);
        Assert.Contains(made[0], left);
        Assert.Contains(last.FolderName, left);
        Assert.Contains("20260920-100000-before-restore", left);
        Assert.DoesNotContain(left, d => d.StartsWith(WorldRestorePaths.DeletingPrefix, StringComparison.Ordinal));
    }

    // ---- the schedule and its settings ----------------------------------------------------------------

    [Fact]
    public void The_interval_schedule_waits_an_interval_after_the_last_scheduled_backup_or_the_start()
    {
        var start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(WorldBackupSchedule.NextDueUtc(0, null, start, null));
        Assert.Equal(start.AddHours(6), WorldBackupSchedule.NextDueUtc(6, null, start, null));
        Assert.Equal(start.AddHours(2), WorldBackupSchedule.NextDueUtc(6, start.AddHours(-4), start, null));
        // Overdue at the start: not before the grace after the start (the world is still loading).
        Assert.Equal(start.AddMinutes(WorldBackupSchedule.StartGraceMinutes), WorldBackupSchedule.NextDueUtc(6, start.AddDays(-2), start, null));
        // A failure retries 30 minutes later, not every minute; a success after it clears it.
        var failed = start.AddHours(7);
        Assert.Equal(failed.AddMinutes(30), WorldBackupSchedule.NextDueUtc(6, start.AddHours(1), start, failed));
        Assert.Equal(start.AddHours(9), WorldBackupSchedule.NextDueUtc(1, start.AddHours(8), start, failed));
        // A 1-hour interval after a failure: 30 minutes; the interval caps the retry.
        Assert.False(WorldBackupSchedule.IsDue(6, null, start, null, start.AddHours(5.9)));
        Assert.True(WorldBackupSchedule.IsDue(6, null, start, null, start.AddHours(6)));
        Assert.Equal(start.AddHours(WorldBackupSettings.MaxEveryHours), WorldBackupSchedule.NextDueUtc(10_000, null, start, null));
    }

    [Fact]
    public void The_start_backup_counts_as_the_last_scheduled_backup()
    {
        var f = new Fixture();
        var job = Job(f, WorldRestorePaths.KindScheduled);
        job.Trigger = WorldRestorePaths.TriggerStart;
        Assert.True(WorldBackupMaker.Make(f.Fs, job, f.Env()).Ok);
        var last = WorldBackupSchedule.LastScheduledUtc(WorldBackupCatalog.List(f.Fs, f.Layout, WorldRestoreApplyTests.Zone));
        Assert.Equal(NowUtc, last);
        Assert.Equal(NowUtc.AddHours(4), WorldBackupSchedule.NextDueUtc(4, last, NowUtc.AddMinutes(-1), null));
    }

    [Fact]
    public void The_settings_are_read_from_the_cfg_text_with_defaults_and_clamps()
    {
        var s = WorldBackupSettings.FromCfgText("[Other]\nBackupAtStart = true\n\n[WorldRestore]\n# a comment\nKeepBackups = 5\nBackupAtStart = true\r\n"
            + "BackupEveryHours = 500\nBackupKeepCount = -3\nBackupKeepDays = 14\n[Next]\nBackupKeepDays = 99\n");
        Assert.True(s.AtStart);
        Assert.Equal(WorldBackupSettings.MaxEveryHours, s.EveryHours);
        Assert.Equal(BackupPolicy.DefaultCount, s.KeepCount);
        Assert.Equal(14, s.KeepDays);
        Assert.Equal("at every server start and every 168 hours", s.ScheduleText());

        var d = WorldBackupSettings.FromCfgText("");
        Assert.False(d.AtStart);
        Assert.Equal(0, d.EveryHours);
        Assert.Equal("off", d.ScheduleText());
        Assert.Equal(new BackupPolicy(BackupPolicy.DefaultCount, 0), d.Policy);
        Assert.Equal("every hour", new WorldBackupSettings { EveryHours = 1 }.ScheduleText());

        var f = new Fixture();
        f.Fs.Put(Path.Combine(f.BepInEx, "config", "progenitor.cfg"), "[WorldRestore]\nBackupAtStart = true\n", NowUtc);
        Assert.True(WorldBackupSettings.FromCfg(f.Fs, f.BepInEx).AtStart);
        Assert.False(WorldBackupSettings.FromCfg(f.Fs, Path.Combine(FakeWorldFs.Root, "nowhere")).AtStart);
    }

    // ---- the start backup and a pending restore ------------------------------------------------------

    private static readonly WorldBackupSettings AtStart = new() { AtStart = true };

    private static WorldBackupAtStart.Report Start(Fixture f, WorldBackupSettings settings, out RestoreResult? restore)
    {
        var env = f.Env();
        restore = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, env, out bool halted);
        return WorldBackupAtStart.Run(f.Fs, f.BepInEx, env, settings, restore, halted);
    }

    [Fact]
    public void A_start_without_a_request_makes_one_scheduled_backup_and_records_it()
    {
        var f = new Fixture();
        var before = BackupDirs(f);
        var report = Start(f, AtStart, out var restore);
        Assert.Null(restore);
        Assert.True(report.Attempted);
        Assert.True(report.Outcome!.Ok, report.Outcome.Summary);
        Assert.Equal(WorldRestorePaths.TriggerStart, report.Outcome.Trigger);
        Assert.Equal(new[] { report.Outcome.FolderName }, BackupDirs(f).Except(before).ToArray());
        Assert.EndsWith("-scheduled", report.Outcome.FolderName);
        var status = WorldBackupStatus.Read(f.Fs, f.BepInEx);
        Assert.True(status[WorldRestorePaths.TriggerStart].Ok);
        Assert.Null(WorldBackupStatus.ScheduledFailure(status));

        // Off: nothing.
        var g = new Fixture();
        var off = Start(g, new WorldBackupSettings(), out _);
        Assert.False(off.Attempted);
        Assert.Equal(new[] { "20260920-100000-before-restore" }, BackupDirs(g));
    }

    [Fact]
    public void With_a_restore_waiting_the_world_before_it_is_copied_once_by_the_restore_itself()
    {
        var f = new Fixture();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, f.Request());
        var before = BackupDirs(f);
        var report = Start(f, AtStart, out var restore);
        Assert.True(restore!.Ok, restore.Summary);
        Assert.True(restore.RanThisStart);
        Assert.False(report.Attempted);
        Assert.Contains("the restore's own backup", report.Skipped);
        // Exactly one copy of the pre-restore world: the restore's before-restore backup (made before the swap).
        var added = BackupDirs(f).Except(before).Where(d => !d.StartsWith(".", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { Path.GetFileName(restore.BackupPath) }, added);
        Assert.EndsWith("-before-restore", added[0]);
    }

    [Fact]
    public void A_refused_restore_leaves_the_world_unchanged_and_the_start_backup_copies_it()
    {
        var f = new Fixture();
        var request = f.Request();
        request.ExpectShape = "changed-since";
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        var live = Live(f);
        var report = Start(f, AtStart, out var restore);
        Assert.False(restore!.Ok);
        Assert.True(report.Attempted);
        Assert.True(report.Outcome!.Ok, report.Outcome.Summary);
        Assert.Equal(live, Copy(f, report.Outcome.FolderName));
        Assert.Single(BackupDirs(f), d => d.EndsWith("-before-restore", StringComparison.Ordinal));
    }

    [Fact]
    public void A_restore_reported_again_does_not_count_as_this_starts_copy()
    {
        var f = new Fixture();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, f.Request());
        Start(f, new WorldBackupSettings(), out var first);
        Assert.True(first!.Ok);
        var again = new RestoreResult { Ok = true, BackupPath = first.BackupPath, BackupName = first.BackupName };
        var report = WorldBackupAtStart.Run(f.Fs, f.BepInEx, f.Env(), AtStart, again, halted: false);
        Assert.True(report.Attempted);
        Assert.True(report.Outcome!.Ok, report.Outcome.Summary);
    }

    [Fact]
    public void A_failed_start_backup_is_recorded_and_never_stops_the_server()
    {
        var f = new Fixture();
        f.Fs.Free = 10;
        var report = Start(f, AtStart, out _);
        Assert.True(report.Attempted);
        Assert.False(report.Outcome!.Ok);
        Assert.Empty(f.Stops);
        var failure = WorldBackupStatus.ScheduledFailure(WorldBackupStatus.Read(f.Fs, f.BepInEx));
        Assert.NotNull(failure);
        Assert.Contains("not enough free disk space", failure!.Summary);

        // A later success clears it.
        f.Fs.Free = null;
        var ok = WorldBackupAtStart.Run(f.Fs, f.BepInEx, f.Env(), AtStart, null, halted: false);
        Assert.True(ok.Outcome!.Ok);
        Assert.Null(WorldBackupStatus.ScheduledFailure(WorldBackupStatus.Read(f.Fs, f.BepInEx)));
    }

    [Fact]
    public void A_halted_start_a_missing_save_folder_and_a_hosted_game_skip_the_start_backup()
    {
        var f = new Fixture();
        Assert.False(WorldBackupAtStart.Run(f.Fs, f.BepInEx, f.Env(), AtStart, null, halted: true).Attempted);

        var env = f.Env();
        env.Args = new[] { "VRisingServer.exe", "-persistentDataPath", f.Persist, "-saveName", "world2" };
        var missing = WorldBackupAtStart.Run(f.Fs, f.BepInEx, env, AtStart, null, halted: false);
        Assert.False(missing.Attempted);
        Assert.Contains("does not exist yet", missing.Skipped);

        env.Args = new[] { "VRisingServer.exe" };
        Assert.Contains("names no -persistentDataPath", WorldBackupAtStart.Run(f.Fs, f.BepInEx, env, AtStart, null, halted: false).Skipped);

        env.Args = new[] { "VRisingServer.exe", "-persistentDataPath", f.Persist, "-saveName", "world1", "-userSave" };
        Assert.Contains("hosted", WorldBackupAtStart.Run(f.Fs, f.BepInEx, env, AtStart, null, halted: false).Skipped);
    }

    // ---- the game's autosaves -------------------------------------------------------------------------

    [Fact]
    public void The_autosave_summary_counts_the_games_autosaves_only()
    {
        var f = new Fixture();
        f.Fs.Put(Path.Combine(f.Save, "MyNamedSave.save.gz"), FakeWorldFs.Gzip("named"), NowUtc);
        f.Fs.Put(Path.Combine(f.Save, "progenitor", "AutoSave_99.save.gz"), "not top level", NowUtc);
        var s = WorldAutosaves.Summarize(f.Fs, f.Save);
        Assert.False(s.Missing);
        Assert.Equal(2, s.Count);
        Assert.Equal(f.Fs.Bytes(Path.Combine(f.Save, "AutoSave_10.save.gz")).Length + f.Fs.Bytes(Path.Combine(f.Save, "AutoSave_11.save.gz")).Length, s.Bytes);
        Assert.Equal(new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc), s.NewestUtc);
        Assert.Equal(new DateTime(2026, 10, 6, 19, 0, 0, DateTimeKind.Utc), s.OldestUtc);

        var missing = WorldAutosaves.Summarize(f.Fs, Path.Combine(f.Persist, "Saves", "v4", "nope"));
        Assert.True(missing.Missing);
        Assert.Equal(0, missing.Count);
        Assert.True(WorldAutosaves.Summarize(f.Fs, null).Missing);

        var empty = new Fixture();
        foreach (string n in new[] { "AutoSave_10.save.gz", "AutoSave_11.save.gz" }) empty.Fs.DeleteFileForTests(Path.Combine(empty.Save, n));
        var none = WorldAutosaves.Summarize(empty.Fs, empty.Save);
        Assert.False(none.Missing);
        Assert.Equal(0, none.Count);
        Assert.Null(none.NewestUtc);
    }
}
