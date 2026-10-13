using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.Networking;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// WORLDRESTORE1: retention (keep the newest N Progenitor backups; never a wipe archive), the restored world's wipe clock
/// (the three answers when its wipe date has passed), the host-settings edit, and the request and result files' round trips
/// through Bootstrap's start-time path. Game-free.
/// </summary>
public sealed class WorldRestoreRulesTests
{
    private static WorldRestoreApplyTests.Fixture Fix() => new();

    private static void AddBackup(WorldRestoreApplyTests.Fixture f, string name, DateTime createdUtc, string chain = "")
    {
        string dir = Path.Combine(f.BackupsRoot, name);
        f.Fs.Put(Path.Combine(dir, "AutoSave_1.save.gz"), FakeWorldFs.Gzip(name), createdUtc);
        f.Seal(dir, createdUtc, "x", chain);
    }

    // ---- retention -----------------------------------------------------------------------------------------

    [Fact]
    public void Retention_keeps_the_newest_the_protected_and_each_chain_s_oldest_and_never_an_archive()
    {
        var f = Fix();
        for (int i = 1; i <= 5; i++) AddBackup(f, "202609" + i.ToString("00") + "-120000-before-restore", new DateTime(2026, 9, i, 12, 0, 0, DateTimeKind.Utc));
        var entries = WorldBackupCatalog.List(f.Fs, f.Layout);
        Assert.Equal(7, entries.Count);
        Assert.Single(entries, e => e.Kind == WorldBackupEntry.KindArchive);
        string made = entries.First(e => e.FolderName == "20260920-100000-before-restore").Path;

        // Newest first: Sep 20, 5, 4 stay (keep 3); Sep 3 and 2 go; Sep 1 is the chain's oldest (the world before the first restore).
        Assert.Equal(new[] { "20260903-120000-before-restore", "20260902-120000-before-restore" },
            WorldBackupCatalog.PruneList(entries, 3, made).Select(e => e.FolderName));
        // The source of a pending request is kept too.
        string pending = entries.First(e => e.FolderName == "20260902-120000-before-restore").Path;
        Assert.Equal(new[] { "20260903-120000-before-restore" }, WorldBackupCatalog.PruneList(entries, 3, made, pending).Select(e => e.FolderName));
        Assert.Empty(WorldBackupCatalog.PruneList(entries, 0, made));
        Assert.Empty(WorldBackupCatalog.PruneList(entries, 100, made));
        // A keep of 1 is raised to 2: the one backup could be the world the restore just replaced.
        Assert.Equal(2, WorldBackupCatalog.ClampKeep(1));
        Assert.Equal(0, WorldBackupCatalog.ClampKeep(0));
        Assert.Equal(WorldBackupCatalog.DefaultKeep, WorldBackupCatalog.ClampKeep(-1));
        Assert.Equal(WorldBackupCatalog.MaxKeep, WorldBackupCatalog.ClampKeep(5000));

        // Another chain keeps its own oldest.
        var g = Fix();
        AddBackup(g, "20260901-120000-before-restore", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), "A");
        AddBackup(g, "20260902-120000-before-restore", new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc), "B");
        AddBackup(g, "20260903-120000-before-restore", new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc), "B");
        AddBackup(g, "20260904-120000-before-restore", new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc), "B");
        var all = WorldBackupCatalog.List(g.Fs, g.Layout);
        Assert.Equal(new[] { "20260903-120000-before-restore" }, WorldBackupCatalog.PruneList(all, 2).Select(e => e.FolderName));
    }

    [Fact]
    public void A_restore_prunes_old_backups_past_the_keep_count_and_leaves_archives_and_game_backups()
    {
        var f = Fix();
        for (int i = 1; i <= 6; i++) AddBackup(f, "202608" + i.ToString("00") + "-120000-before-restore", new DateTime(2026, 8, i, 12, 0, 0, DateTimeKind.Utc));
        string gameBackup = Path.Combine(f.Persist, "Saves", "v4", ".BACKUP", "1.0", "AutoSave_1.save.gz");
        f.Fs.Put(gameBackup, FakeWorldFs.Gzip("game backup"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var request = f.Request();
        request.KeepBackups = 5;
        var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
        Assert.True(r.Ok, r.Summary);
        // The new one, Sep 20 and Aug 6, 5, 4 stay (keep 5); Aug 3 and 2 go; Aug 1 is the chain's oldest.
        Assert.Equal(new[] { "20260803-120000-before-restore", "20260802-120000-before-restore" }, r.Pruned);
        var left = WorldBackupCatalog.List(f.Fs, f.Layout);
        Assert.Equal(6, left.Count(e => e.Kind == WorldBackupEntry.KindBackup));
        Assert.Contains(left, e => e.FolderName == "20260801-120000-before-restore");
        Assert.Single(left, e => e.Kind == WorldBackupEntry.KindArchive);
        Assert.True(f.Fs.Has(gameBackup));
        Assert.Contains(left, e => e.Path == r.BackupPath);
        // Deleted by renaming first: nothing half-deleted stays.
        Assert.DoesNotContain(f.Fs.Dirs, d => Path.GetFileName(d).StartsWith(WorldRestorePaths.DeletingPrefix, StringComparison.Ordinal));
        Assert.Contains(f.Fs.Log, l => l.Contains("movedir") && l.Contains(WorldRestorePaths.DeletingPrefix + "20260803-120000-before-restore"));
    }

    [Fact]
    public void The_catalog_skips_partial_staged_and_sidecarless_folders_and_reads_age_and_size()
    {
        var f = Fix();
        f.Fs.Put(Path.Combine(f.BackupsRoot, "20261001-000000-before-restore.partial", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.BackupsRoot, ".incoming-abc", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.BackupsRoot, ".deleting-abc", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.BackupsRoot, "no-sidecar", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.Persist, "Saves", "v4", "world1_notdigits", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.Persist, "Saves", "v4", "world2_123", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        var entries = WorldBackupCatalog.List(f.Fs, f.Layout);
        Assert.Equal(new[] { WorldBackupEntry.KindArchive, WorldBackupEntry.KindBackup }, entries.Select(e => e.Kind));
        var archive = entries[0];
        Assert.Equal("archive:world1_" + f.ArchiveTicks, archive.Id);
        Assert.Equal(new DateTime(2026, 7, 1), archive.StartDate);
        Assert.Equal(91, archive.AgeDays);
        Assert.Equal(6, archive.Files);
        Assert.True(archive.Restorable);
        Assert.Equal("Wipe archive from Wed, Sep 30, 2026 4:00 AM", archive.DisplayName);
        Assert.Equal("the wipe archive from Wed, Sep 30, 2026 4:00 AM", archive.BaseLabel);
        Assert.Equal("Before restore", entries[1].KindLabel);
        Assert.StartsWith("the backup from Sun, Sep 20, 2026 ", entries[1].BaseLabel);
        Assert.StartsWith("Before restore of the wipe archive from Sun, Sep 20, 2026 3:00 AM, Sun, Sep 20, 2026 ", entries[1].DisplayName);
    }

    // ---- the wipe clock ------------------------------------------------------------------------------------

    [Fact]
    public void The_reset_formula_matches_Core_s_server_rules()
    {
        foreach (string day in WorldRestoreWipe.Days)
            for (int interval = 0; interval <= 40; interval += 3)
                for (int d = 0; d < 9; d++)
                {
                    var start = new DateTime(2026, 9, 1).AddDays(d);
                    Assert.Equal(ServerRules.ResetDate(start, interval, day), WorldRestoreWipe.ResetDate(start, interval, day));
                }

        Assert.Equal(ServerRules.ParseStartDate("09/02/2026"), WorldRestoreWipe.ParseStartDate("09/02/2026"));
        Assert.Equal("10/07/2026", WorldRestoreWipe.FormatStartDate(new DateTime(2026, 10, 7, 15, 0, 0)));
    }

    private static readonly DateTime Now = new(2026, 10, 7, 9, 30, 0);

    [Fact]
    public void A_due_wipe_needs_one_of_the_three_answers_and_each_gives_its_next_wipe()
    {
        var start = new DateTime(2026, 7, 1);
        // Every 30 days from Jul 1 is Jul 31: long past.
        var none = ServerBackups.Plan(start, WipeModes.None, null, 30, "Any", true, "", Now);
        Assert.True(none.Due);
        Assert.Equal(new DateTime(2026, 7, 31), none.DueDate);
        Assert.Contains("Choose", none.Error);

        var clock = ServerBackups.Plan(start, WipeModes.ClockToday, null, 30, "Any", true, "", Now);
        Assert.Equal("", clock.Error);
        Assert.Equal(new DateTime(2026, 11, 6), clock.NextWipe);
        Assert.Equal(Now.Date, clock.StartAfter);

        var off = ServerBackups.Plan(start, WipeModes.WipesOff, null, 30, "Any", true, "", Now);
        Assert.Equal("", off.Error);
        Assert.Null(off.NextWipe);
        Assert.Equal(0, off.IntervalDays);

        var date = ServerBackups.Plan(start, WipeModes.Date, new DateTime(2026, 10, 20), 30, "Any", true, "", Now);
        Assert.Equal("", date.Error);
        Assert.Equal(111, date.IntervalDays);
        Assert.Equal("", date.DayOfReset);
        Assert.Equal(new DateTime(2026, 10, 20), date.NextWipe);
    }

    [Fact]
    public void A_picked_date_must_be_after_today_and_the_start_and_moves_the_weekday_to_any_when_needed()
    {
        var start = new DateTime(2026, 7, 1);
        Assert.Contains("after today", ServerBackups.Plan(start, WipeModes.Date, Now.Date, 30, "Any", true, "", Now).Error);
        Assert.Contains("Pick the new wipe date", ServerBackups.Plan(start, WipeModes.Date, null, 30, "Any", true, "", Now).Error);
        // Oct 20, 2026 is a Tuesday: with Wipe on Saturday the weekday would move it, so DayOfReset becomes Any.
        var moved = ServerBackups.Plan(start, WipeModes.Date, new DateTime(2026, 10, 20), 30, "Saturday", true, "", Now);
        Assert.Equal("Any", moved.DayOfReset);
        Assert.Equal(new DateTime(2026, 10, 20), moved.NextWipe);
        var kept = ServerBackups.Plan(start, WipeModes.Date, new DateTime(2026, 10, 20), 30, "Tuesday", true, "", Now);
        Assert.Equal("", kept.DayOfReset);
        // A start date in the future of the picked day.
        Assert.Null(WorldRestoreWipe.ForDate(new DateTime(2026, 11, 1), new DateTime(2026, 10, 20), "Any", Now, 365, out string error));
        Assert.Contains("start date", error);
        Assert.Null(WorldRestoreWipe.ForDate(start, new DateTime(2028, 1, 1), "Any", Now, 365, out error));
        Assert.Contains("At most 365", error);
    }

    [Fact]
    public void Interval_answers_need_a_writable_interval_and_a_wipe_that_is_not_due_needs_no_answer()
    {
        var start = new DateTime(2026, 7, 1);
        Assert.Contains("command", ServerBackups.Plan(start, WipeModes.WipesOff, null, 30, "Any", false, "the start command sets it", Now).Error);
        Assert.Contains("command", ServerBackups.Plan(start, WipeModes.Date, new DateTime(2026, 11, 1), 30, "Any", false, "the start command sets it", Now).Error);
        Assert.Equal("", ServerBackups.Plan(start, WipeModes.ClockToday, null, 30, "Any", false, "x", Now).Error);

        var notDue = ServerBackups.Plan(new DateTime(2026, 10, 1), WipeModes.None, null, 30, "Any", true, "", Now);
        Assert.False(notDue.Due);
        Assert.Equal("", notDue.Error);
        Assert.Equal(new DateTime(2026, 10, 31), notDue.NextWipe);
        Assert.Contains("No wipe choice", ServerBackups.Plan(new DateTime(2026, 10, 1), WipeModes.WipesOff, null, 30, "Any", true, "", Now).Error);
        Assert.False(ServerBackups.Plan(start, WipeModes.None, null, 0, "Any", true, "", Now).Due);
        // An unknown start date with wipes on rules nothing out.
        Assert.True(ServerBackups.Plan(null, WipeModes.None, null, 30, "Any", true, "", Now).Due);
    }

    [Fact]
    public void Clock_today_rewrites_the_restored_start_date_and_keeps_the_original_beside_it()
    {
        var f = Fix();
        var r = WorldRestoreApply.Run(f.Fs, f.Request(wipe: WipeModes.ClockToday), f.Env());
        Assert.True(r.Ok, r.Summary);
        Assert.Equal("10/07/2026", f.Fs.Text(Path.Combine(f.Save, "StartDate.json")));
        Assert.Equal("07/01/2026", f.Fs.Text(Path.Combine(f.Save, WorldRestorePaths.StartDateBeforeRestoreName)));
        // The archive keeps its own start date.
        Assert.Equal("07/01/2026", f.Fs.Text(Path.Combine(f.Archive, "StartDate.json")));
        Assert.Contains("restarts today", r.WipeApplied);
    }

    [Fact]
    public void Wipes_off_and_a_new_date_write_the_next_start_host_settings_and_a_per_save_file_is_written_in_the_restored_world()
    {
        var f = new WorldRestoreApplyTests.Fixture(30);
        var r = WorldRestoreApply.Run(f.Fs, f.Request(wipe: WipeModes.WipesOff), f.Env());
        Assert.True(r.Ok, r.Summary);
        Assert.Equal("{\n  \"Name\": \"Test\",\n  \"ResetDaysInterval\": 0\n}\n", f.Fs.Text(f.GlobalSettings));

        var g = new WorldRestoreApplyTests.Fixture(30);
        var request = g.Request(wipe: WipeModes.Date, interval: 111, day: "Any");
        request.HostSettingsPath = Path.Combine(g.Save, "ServerHostSettings.json");
        var r2 = WorldRestoreApply.Run(g.Fs, request, g.Env());
        Assert.True(r2.Ok, r2.Summary);
        string restored = g.Fs.Text(Path.Combine(g.Save, "ServerHostSettings.json"));
        Assert.Contains("\"ResetDaysInterval\": 111", restored);
        Assert.Contains("\"DayOfReset\": \"Any\"", restored);
        Assert.Contains("current per-save", restored);
        // The backup holds the world's file as it was; the global file is untouched.
        Assert.Equal("{\n  \"Name\": \"current per-save\"\n}\n", g.Fs.Text(Path.Combine(r2.BackupPath, "ServerHostSettings.json")));
        Assert.Contains("\"ResetDaysInterval\": 30", g.Fs.Text(g.GlobalSettings));
    }

    [Theory]
    [InlineData("{\"A\":1,\"ResetDaysInterval\":30}", "{\"A\":1,\"ResetDaysInterval\":0}")]
    [InlineData("{ \"A\": 1 }", "{ \"A\": 1,\n  \"ResetDaysInterval\": 0\n}")]
    [InlineData("{ \"A\": 1, }", "{ \"A\": 1,\n  \"ResetDaysInterval\": 0\n}")]
    [InlineData("{}", "{\n  \"ResetDaysInterval\": 0\n}")]
    [InlineData("{\"N\":{\"ResetDaysInterval\":5},\"resetdaysinterval\":\"7\"}", "{\"N\":{\"ResetDaysInterval\":5},\"resetdaysinterval\":0}")]
    [InlineData("{ // comment\n \"ResetDaysInterval\": 9 }", "{ // comment\n \"ResetDaysInterval\": 0 }")]
    public void The_host_settings_edit_changes_only_the_top_level_key(string before, string after)
    {
        Assert.Equal(after, Encoding.UTF8.GetString(WorldRestoreWipe.SetTopLevel(Encoding.UTF8.GetBytes(before), "ResetDaysInterval", "0")!));
    }

    [Fact]
    public void The_host_settings_edit_keeps_a_bom_and_refuses_a_non_object()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"ResetDaysInterval\":3}")).ToArray();
        var edited = WorldRestoreWipe.SetTopLevel(bom, "ResetDaysInterval", "0")!;
        Assert.Equal(bom.Take(3), edited.Take(3));
        Assert.Equal("{\"ResetDaysInterval\":0}", Encoding.UTF8.GetString(edited, 3, edited.Length - 3));
        Assert.Null(WorldRestoreWipe.SetTopLevel(Encoding.UTF8.GetBytes("[1,2]"), "ResetDaysInterval", "0"));
        Assert.Null(WorldRestoreWipe.SetTopLevel(Encoding.UTF8.GetBytes("{ broken"), "ResetDaysInterval", "0"));
    }

    // ---- the request and result files ----------------------------------------------------------------------

    [Fact]
    public void The_request_round_trips_and_a_bad_one_is_rejected()
    {
        var f = Fix();
        var r = f.Request(wipe: WipeModes.Date, interval: 111, day: "Any");
        r.RestartNow = true;
        var back = RestoreRequest.Parse(r.ToJson(), out string error)!;
        Assert.Equal("", error);
        foreach (var p in typeof(RestoreRequest).GetProperties())
            Assert.Equal(p.GetValue(r), p.GetValue(back));

        Assert.Null(RestoreRequest.Parse(Encoding.UTF8.GetBytes("{\"schema\":2,\"requestId\":\"x\"}"), out error));
        Assert.Contains("schema", error);
        Assert.Null(RestoreRequest.Parse(Encoding.UTF8.GetBytes("{\"schema\":1}"), out error));
        Assert.Contains("no id", error);
        Assert.Null(RestoreRequest.Parse(Encoding.UTF8.GetBytes("not json"), out error));
        Assert.Null(RestoreRequest.Parse(Array.Empty<byte>(), out error));
    }

    [Fact]
    public void Core_writes_reads_and_cancels_the_request_and_Bootstrap_applies_it_once()
    {
        var f = Fix();
        var request = f.Request();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        Assert.Equal(request.RequestId, WorldRestoreMarker.ReadPending(f.Fs, f.BepInEx, out _)!.RequestId);
        Assert.True(WorldRestoreMarker.Cancel(f.Fs, f.BepInEx));
        Assert.Null(WorldRestoreMarker.ReadPending(f.Fs, f.BepInEx, out _));
        Assert.False(WorldRestoreMarker.Cancel(f.Fs, f.BepInEx));
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env()));

        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        var result = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.True(result.Ok, result.Summary);
        Assert.False(f.Fs.Has(WorldRestoreMarker.RequestPath(f.BepInEx)));
        Assert.True(f.Fs.Has(Path.Combine(f.BepInEx, WorldRestorePaths.AppliedName(request.RequestId))));
        var done = WorldRestoreMarker.ReadResult(f.Fs, f.BepInEx)!;
        Assert.Equal(result.Summary, done.Summary);
        Assert.Equal(request.RequestId, done.RequestId);
        Assert.Equal(result.Steps.Count, done.Steps.Count);
        Assert.Equal(result.BackupPath, done.BackupPath);

        // The same request again (a rename that did not happen) is not run twice: the result says what it did (M6).
        var backups = WorldBackupCatalog.List(f.Fs, f.Layout).Count;
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        var again = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.True(again.Ok);
        Assert.Equal(done.Summary, again.Summary);
        Assert.Equal(backups, WorldBackupCatalog.List(f.Fs, f.Layout).Count);
        Assert.False(f.Fs.Has(WorldRestoreMarker.RequestPath(f.BepInEx)));
    }

    [Fact]
    public void An_unreadable_request_is_refused_and_renamed_with_nothing_changed()
    {
        var f = Fix();
        var save = f.Fs.Snapshot(f.Save);
        f.Fs.Put(WorldRestoreMarker.RequestPath(f.BepInEx), "{ not json", DateTime.UtcNow);
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.False(r.Ok);
        Assert.Contains("could not be read", r.Reason);
        Assert.EndsWith("Nothing changed.", r.Summary);
        Assert.False(f.Fs.Has(WorldRestoreMarker.RequestPath(f.BepInEx)));
        Assert.Equal(save, f.Fs.Snapshot(f.Save));
        Assert.False(WorldRestoreMarker.ReadResult(f.Fs, f.BepInEx)!.Ok);
    }

    [Fact]
    public void The_result_and_sidecar_round_trip()
    {
        var r = new RestoreResult { RequestId = "a", Ok = true, Summary = "s", BackupPath = "p", StartedUtc = new DateTime(2026, 10, 7, 1, 2, 3, DateTimeKind.Utc) };
        r.Warnings.Add("w");
        r.Pruned.Add("old");
        r.Steps.Add(new RestoreStep { Name = "validate", Ok = true, Detail = "d", Ms = 5 });
        var back = RestoreResult.Parse(r.ToJson())!;
        Assert.Equal(r.StartedUtc, back.StartedUtc);
        Assert.Equal(new[] { "w" }, back.Warnings);
        Assert.Equal(new[] { "old" }, back.Pruned);
        Assert.Equal(5, back.Steps.Single().Ms);

        var s = new BackupSidecar { CreatedUtc = new DateTime(2026, 10, 7, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7), RestoreOf = "x" };
        s.Files.Add(new WorldManifestEntry { Path = Path.Combine("progenitor", "a.json"), Length = 3, LastWriteUtc = s.CreatedUtc, Sha256 = "ab" });
        var sb = BackupSidecar.Parse(s.ToJson())!;
        Assert.Equal(s.CreatedUtc, sb.CreatedUtc);
        Assert.Equal(s.Files[0].Path, sb.Files[0].Path);
        Assert.Equal(s.Files[0].LastWriteUtc, sb.Files[0].LastWriteUtc);
    }

    [Theory]
    [InlineData(@"C:\VRisingServer\save-data\Saves\v4\world1", true)]
    [InlineData(@"-persistentDataPath C:\VRisingServer", true)]
    [InlineData(@"C:\VRisingServer2\Saves\v4\w", false)]
    [InlineData(@"D:\Games\TestServer\save-data", false)]
    public void The_live_server_fence_matches_the_root_as_a_whole_token(string text, bool live)
    {
        Assert.Equal(live, WorldRestorePaths.MentionsLiveServer(text));
    }

    [Fact]
    public void Only_a_saves_vn_save_folder_has_a_layout()
    {
        string persist = Path.Combine(FakeWorldFs.Root, "p");
        Assert.True(WorldRestorePaths.TryLayout(Path.Combine(persist, "Saves", "v4", "w"), out var l, out _));
        Assert.Equal(Path.Combine(persist, "progenitor-backups", "v4", "w"), l.BackupsRoot);
        Assert.False(WorldRestorePaths.TryLayout(Path.Combine(persist, "CloudSaves", "123", "v4", "w"), out _, out string why));
        Assert.Contains("only that layout", why);
        Assert.False(WorldRestorePaths.TryLayout("", out _, out _));
        Assert.True(WorldRestorePaths.IsArchiveName("w", "w_638900000000000000", out long ticks));
        Assert.Equal(638900000000000000L, ticks);
        Assert.False(WorldRestorePaths.IsArchiveName("w", "w_12a", out _));
        Assert.False(WorldRestorePaths.IsArchiveName("w", "w_", out _));
        Assert.True(WorldRestorePaths.CountsAsSaveFile("AutoSave_1.save.gz"));
        Assert.False(WorldRestorePaths.CountsAsSaveFile(".AutoSave_1.save"));
        Assert.False(WorldRestorePaths.CountsAsSaveFile(Path.Combine("AutoSave_127.save.gz", "progenitor", "x.save")));
    }
}
