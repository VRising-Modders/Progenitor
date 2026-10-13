using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// WORLDRESTORE1/2 (research restore-world-backup-spec-2026-10-06 §3.2; decisions 2026-10-06; review 2026-10-07): the
/// apply sequence Bootstrap runs at a server start, on a fake file system. A failure injected at any single operation either
/// leaves the world, the source and the host settings exactly as they were, or comes after the swap. A crash at any
/// operation, followed by the next start, ends in the original world or the restored one, never an empty save folder.
/// Game-free.
/// </summary>
public sealed class WorldRestoreApplyTests
{
    internal static readonly DateTime Local0 = new(2026, 10, 7, 9, 30, 0, DateTimeKind.Local);

    /// <summary>One save with a wipe archive, a Progenitor backup and the host settings, on a fake file system.</summary>
    internal sealed class Fixture
    {
        internal readonly FakeWorldFs Fs = new();
        internal readonly string Persist = Path.Combine(FakeWorldFs.Root, "data");
        internal string Save => Path.Combine(Persist, "Saves", "v4", "world1");
        internal readonly long ArchiveTicks = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Local).Ticks;
        internal string Archive => Path.Combine(Persist, "Saves", "v4", "world1_" + ArchiveTicks);
        internal string BackupsRoot => Path.Combine(Persist, "progenitor-backups", "v4", "world1");
        internal string Old => Path.Combine(BackupsRoot, "20260920-100000-before-restore");
        internal string GlobalSettings => Path.Combine(Persist, "Settings", "ServerHostSettings.json");
        internal string BepInEx => Path.Combine(FakeWorldFs.Root, "server", "BepInEx");
        internal readonly List<string> Stops = new();

        internal Fixture(int interval = 0)
        {
            var t = new DateTime(2026, 10, 6, 20, 0, 0, DateTimeKind.Utc);
            Fs.Put(Path.Combine(Save, "AutoSave_10.save.gz"), FakeWorldFs.Gzip("world now, older"), t.AddHours(-1));
            Fs.Put(Path.Combine(Save, "AutoSave_11.save.gz"), FakeWorldFs.Gzip("world now, newest"), t);
            Fs.Put(Path.Combine(Save, "StartDate.json"), "09/02/2026", t.AddDays(-30));
            Fs.Put(Path.Combine(Save, "SessionId.json"), "\"current-session\"", t.AddDays(-1));
            Fs.Put(Path.Combine(Save, "ServerHostSettings.json"), "{\n  \"Name\": \"current per-save\"\n}\n", t.AddDays(-2));
            Fs.Put(Path.Combine(Save, "progenitor", "sample.json"), "{\"now\":true}", t);
            Fs.Put(Path.Combine(Save, ".TEMP", "TempSave_1.save"), "staging", t);

            var a = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
            Fs.Put(Path.Combine(Archive, "AutoSave_3.save.gz"), FakeWorldFs.Gzip("archived world, older"), a.AddHours(-2));
            Fs.Put(Path.Combine(Archive, "AutoSave_4.save.gz"), FakeWorldFs.Gzip("archived world, newest"), a);
            Fs.Put(Path.Combine(Archive, "StartDate.json"), "07/01/2026", a.AddDays(-90));
            Fs.Put(Path.Combine(Archive, "SessionId.json"), "\"archived-session\"", a);
            Fs.Put(Path.Combine(Archive, "ServerHostSettings.json"), "{ \"Name\": \"archived per-save\" }", a);
            Fs.Put(Path.Combine(Archive, "progenitor", "sample.json"), "{\"then\":true}", a);

            var o = new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc);
            Fs.Put(Path.Combine(Old, "AutoSave_1.save.gz"), FakeWorldFs.Gzip("older backup"), o);
            Fs.Put(Path.Combine(Old, "StartDate.json"), "10/01/2026", o);
            Seal(Old, o, "the wipe archive from Sun, Sep 20, 2026 3:00 AM");

            Fs.Put(GlobalSettings, "{\n  \"Name\": \"Test\",\n  \"ResetDaysInterval\": " + interval + "\n}\n", o);
            Fs.Dir(BepInEx);
        }

        /// <summary>Give a backup folder its sidecar, with the SHA-256 list of what it holds.</summary>
        internal void Seal(string dir, DateTime created, string restoreOf, string chain = "")
        {
            var sidecar = new BackupSidecar { CreatedUtc = created, SourceSaveName = "world1", RestoreOf = restoreOf, ChainId = chain };
            sidecar.Files.AddRange(WorldManifest.Build(Fs, dir, hash: true, WorldBackupCatalog.SkipSidecar));
            Fs.Put(Path.Combine(dir, WorldRestorePaths.SidecarName), sidecar.ToJson(), created);
        }

        internal WorldRestorePaths.SaveLayout Layout
        {
            get
            {
                Assert.True(WorldRestorePaths.TryLayout(Save, out var l, out string why), why);
                return l;
            }
        }

        internal WorldBackupEntry Entry(string kind) => WorldBackupCatalog.List(Fs, Layout).First(x => x.Kind == kind);

        internal RestoreRequest Request(string kind = WorldBackupEntry.KindArchive, string wipe = WipeModes.None, int interval = 0, string day = "",
            string date = "2026-10-20")
        {
            var e = Entry(kind);
            return new RestoreRequest
            {
                RequestId = "20261007-093000-abcd",
                RequestedUtc = new DateTime(2026, 10, 7, 13, 30, 0, DateTimeKind.Utc),
                RequestedBy = "Admin",
                SaveFolder = Save,
                SaveName = "world1",
                SourceKind = e.Kind,
                SourcePath = e.Path,
                SourceName = e.BaseLabel,
                ExpectShape = e.Shape,
                WipeMode = wipe,
                WipeIntervalDays = interval,
                WipeDayOfReset = day,
                HostSettingsPath = wipe is WipeModes.WipesOff or WipeModes.Date ? GlobalSettings : "",
                WipeDate = wipe == WipeModes.Date ? date : "",
                WipeLayers = new List<string> { GlobalSettings, Path.Combine(Save, "ServerHostSettings.json") },
                KeepBackups = 5,
            };
        }

        internal RestoreEnvironment Env(List<string>? log = null) => new()
        {
            UtcNow = () => new DateTime(2026, 10, 7, 13, 30, 0, DateTimeKind.Utc),
            LocalNow = () => Local0,
            Args = new[] { "VRisingServer.exe", "-persistentDataPath", Persist, "-saveName", "world1" },
            ProcessPath = Path.Combine(FakeWorldFs.Root, "server", "VRisingServer.exe"),
            ProgenitorVersion = "test",
            BepInExRoot = BepInEx,
            Log = log == null ? _ => { } : log.Add,
            FailClosed = Stops.Add,
            SpareBytes = 0,
        };

        /// <summary>What the save folder holds after a restore of the archive: its files, with the current per-save settings.</summary>
        internal SortedDictionary<string, string> ExpectedFromArchive()
        {
            var map = Fs.Snapshot(Archive);
            var current = Fs.Snapshot(Save);
            map["ServerHostSettings.json"] = current["ServerHostSettings.json"];
            return map;
        }

        internal string Journal => Path.Combine(BepInEx, WorldRestorePaths.JournalFileName);

        internal RestoreJournal? ReadJournal() => Fs.Has(Journal) ? RestoreJournal.Parse(Fs.Bytes(Journal)) : null;

        internal string Outgoing(string id = "20261007-093000-abcd") => Path.Combine(BackupsRoot, WorldRestorePaths.OutgoingName(id));
    }

    internal static void AssertUnchanged(Fixture f, SortedDictionary<string, string> save, SortedDictionary<string, string> archive, string settings,
        RestoreResult r, string context, int backups = 1)
    {
        Assert.False(r.Ok, context);
        Assert.Equal(save, f.Fs.Snapshot(f.Save));
        Assert.Equal(archive, f.Fs.Snapshot(f.Archive));
        Assert.Equal(settings, f.Fs.FileState(f.GlobalSettings));
        Assert.EndsWith("Nothing changed.", r.Summary);
        // No partial copy, staged copy or outgoing folder is left behind, and no journal.
        Assert.DoesNotContain(f.Fs.Dirs, d => Path.GetFileName(d).EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(d).StartsWith(".incoming", StringComparison.Ordinal) || Path.GetFileName(d).StartsWith(".outgoing", StringComparison.Ordinal));
        Assert.False(f.Fs.Has(f.Journal), context + ": the journal is gone");
        Assert.Equal(backups, WorldBackupCatalog.List(f.Fs, f.Layout).Count(e => e.Kind == WorldBackupEntry.KindBackup));
    }

    [Fact]
    public void An_archive_restore_backs_up_and_verifies_the_world_then_copies_the_archive_in_and_keeps_the_archive()
    {
        var f = new Fixture();
        var before = f.Fs.Snapshot(f.Save);
        var archive = f.Fs.Snapshot(f.Archive);
        var expected = f.ExpectedFromArchive();
        var log = new List<string>();

        var r = WorldRestoreApply.Run(f.Fs, f.Request(), f.Env(log));

        Assert.True(r.Ok, r.Summary);
        Assert.Equal(expected, f.Fs.Snapshot(f.Save));
        Assert.Equal(archive, f.Fs.Snapshot(f.Archive));
        var backup = r.BackupPath;
        Assert.Equal(Path.Combine(f.BackupsRoot, "20261007-093000-before-restore"), backup);
        var copy = f.Fs.Snapshot(backup);
        copy.Remove(WorldRestorePaths.SidecarName);
        var expectBackup = new SortedDictionary<string, string>(before.Where(p => !p.Key.StartsWith(".TEMP")).ToDictionary(p => p.Key, p => p.Value),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectBackup, copy);
        var sidecar = BackupSidecar.Parse(f.Fs.ReadAllBytes(Path.Combine(backup, WorldRestorePaths.SidecarName)))!;
        Assert.Equal(WorldRestorePaths.KindBeforeRestore, sidecar.Kind);
        Assert.Equal("the wipe archive from Wed, Sep 30, 2026 4:00 AM", sidecar.RestoreOf);
        Assert.Equal("2026-09-02", sidecar.StartDate);
        Assert.Equal(expectBackup.Count, sidecar.Files.Count);
        Assert.All(sidecar.Files, x => Assert.Equal(64, x.Sha256.Length));
        Assert.Equal("Before restore of the wipe archive from Wed, Sep 30, 2026 4:00 AM, Wed, Oct 7, 2026 9:30 AM", r.BackupName);
        Assert.Equal("Restored the wipe archive from Wed, Sep 30, 2026 4:00 AM. Backup of the previous world: " + r.BackupName + ".", r.Summary);
        var listed = WorldBackupCatalog.List(f.Fs, f.Layout);
        Assert.Contains(listed, e => e.DisplayName == "Before restore of the wipe archive from Wed, Sep 30, 2026 4:00 AM, Wed, Oct 7, 2026 9:30 AM");
        var newest = WorldManifest.NewestSave(WorldManifest.Build(f.Fs, f.Save, hash: false))!;
        Assert.Equal("AutoSave_4.save.gz", newest.Path);
        Assert.Equal(new[] { "validate", "backup", "verify backup", "stage", "wipe check", "swap" }, r.Steps.Select(s => s.Name));
        Assert.All(r.Steps, s => Assert.True(s.Ok));
        // H3: the replaced world stays in .outgoing (a copy of the verified backup) until the restored world saves.
        Assert.True(f.Fs.HasDir(f.Outgoing()));
        var j = f.ReadJournal()!;
        Assert.Equal(RestoreJournal.PhaseSwapped, j.Phase);
        Assert.Equal(f.Outgoing(), j.Outgoing);
        Assert.Contains(log, l => l.Contains("WorldRestore: swap ok"));
        Assert.Empty(f.Stops);
    }

    public static IEnumerable<object[]> Modes() => new[]
    {
        new object[] { WorldBackupEntry.KindArchive, WipeModes.None },
        new object[] { WorldBackupEntry.KindArchive, WipeModes.ClockToday },
        new object[] { WorldBackupEntry.KindArchive, WipeModes.WipesOff },
        new object[] { WorldBackupEntry.KindArchive, WipeModes.Date },
        new object[] { WorldBackupEntry.KindBackup, WipeModes.None },
    };

    private static (int Interval, string Day) WipeArgs(string wipe) => wipe == WipeModes.Date ? (111, "Any") : (0, "");

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_failure_at_any_single_operation_changes_nothing_or_comes_after_the_swap(string kind, string wipe)
    {
        var (interval, day) = WipeArgs(wipe);
        var clean = new Fixture();
        var first = clean.Request(kind, wipe, interval, day);
        int setup = clean.Fs.Ops;
        var ok = WorldRestoreApply.Run(clean.Fs, first, clean.Env());
        Assert.True(ok.Ok, ok.Summary);
        int total = clean.Fs.Ops - setup;
        Assert.True(total > 40, "the run has " + total + " operations");
        int refused = 0, restored = 0;

        for (int k = 1; k <= total; k++)
        {
            var f = new Fixture();
            var request = f.Request(kind, wipe, interval, day);
            var save = f.Fs.Snapshot(f.Save);
            var archive = f.Fs.Snapshot(f.Archive);
            string settings = f.Fs.FileState(f.GlobalSettings);
            var expected = kind == WorldBackupEntry.KindArchive ? f.ExpectedFromArchive() : null;
            f.Fs.FailAt = k + f.Fs.Ops;
            var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
            string context = "failure at " + (f.Fs.Log.FirstOrDefault(l => l.StartsWith(f.Fs.FailAt + " ", StringComparison.Ordinal)) ?? "?") + ": " + r.Summary;
            Assert.False(r.Stranded, context);
            if (r.Ok)
            {
                restored++;
                Assert.Contains(r.Steps, s => s.Name == "swap" && s.Ok);
                if (expected != null && wipe == WipeModes.None) Assert.Equal(expected, f.Fs.Snapshot(f.Save));
                Assert.Equal(archive, f.Fs.Snapshot(f.Archive));
            }
            else
            {
                refused++;
                AssertUnchanged(f, save, archive, settings, r, context);
            }
        }

        Assert.True(refused > 30, refused + " refused");
        Assert.True(restored > 0, "some failures are retried or come after the swap");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void A_crash_at_any_operation_ends_at_the_next_start_in_the_old_world_or_the_restored_one(string kind, string wipe)
    {
        var (interval, day) = WipeArgs(wipe);
        var clean = new Fixture();
        WorldRestoreMarker.Write(clean.Fs, clean.BepInEx, clean.Request(kind, wipe, interval, day));
        int setup = clean.Fs.Ops;
        Assert.True(WorldRestoreMarker.ApplyAtStart(clean.Fs, clean.BepInEx, clean.Env())!.Ok);
        int total = clean.Fs.Ops - setup;
        int old = 0, restored = 0;

        for (int k = 1; k <= total; k++)
        {
            var f = new Fixture();
            var request = f.Request(kind, wipe, interval, day);
            WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
            var save = f.Fs.Snapshot(f.Save);
            var archive = f.Fs.Snapshot(f.Archive);
            string settings = f.Fs.FileState(f.GlobalSettings);
            var expected = kind == WorldBackupEntry.KindArchive && wipe == WipeModes.None ? f.ExpectedFromArchive() : null;
            f.Fs.CrashAt = f.Fs.Ops + k;
            WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
            string at = f.Fs.Log.LastOrDefault() ?? "?";
            f.Fs.Revive();
        f.Stops.Clear(); // a crashed process calls nothing

            // The next start.
            var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
            string context = "crash at " + at + " -> " + (r?.Summary ?? "(no result)");
            Assert.Empty(f.Stops);
            Assert.True(f.Fs.HasDir(f.Save), context + ": the save folder exists");
            Assert.Equal(archive, f.Fs.Snapshot(f.Archive));
            Assert.False(f.Fs.Has(WorldRestoreMarker.RequestPath(f.BepInEx)), context + ": the request is settled");
            var done = WorldRestoreMarker.ReadResult(f.Fs, f.BepInEx)!;
            Assert.Equal(request.RequestId, done.RequestId);
            if (done.Ok)
            {
                restored++;
                if (expected != null) Assert.Equal(expected, f.Fs.Snapshot(f.Save));
                Assert.True(f.Fs.HasDir(done.BackupPath), context);
                // The journal, when it is still there, only waits for the restored world's first save.
                var j = f.ReadJournal();
                if (j != null) Assert.Equal(RestoreJournal.PhaseSwapped, j.Phase);
            }
            else
            {
                old++;
                Assert.True(save.SequenceEqual(f.Fs.Snapshot(f.Save)), context + ": the old world is back");
                Assert.Equal(settings, f.Fs.FileState(f.GlobalSettings));
                Assert.False(f.Fs.Has(f.Journal), context);
                Assert.DoesNotContain(f.Fs.Dirs, d => Path.GetFileName(d).StartsWith(".incoming", StringComparison.Ordinal)
                    || Path.GetFileName(d).StartsWith(".outgoing", StringComparison.Ordinal) || d.EndsWith(".partial", StringComparison.Ordinal));
            }

            // A third start changes nothing more and never runs the request again.
            var third = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
            Assert.True(third == null || third.RequestId == request.RequestId && third.Ok == done.Ok, context);
            Assert.True(f.Fs.HasDir(f.Save));
        }

        Assert.True(old > 0 && restored > 0, old + " old, " + restored + " restored");
    }

    [Fact]
    public void A_crash_between_the_two_renames_is_put_back_at_the_next_start()
    {
        var f = new Fixture();
        var request = f.Request();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        var save = f.Fs.Snapshot(f.Save);
        // Crash at the second rename of the swap: the save folder is gone, the world is in .outgoing.
        f.Fs.FailWhen = (op, path) => op == "movedir" && path.Contains(".incoming", StringComparison.Ordinal) && path.Contains("-> " + f.Save, StringComparison.Ordinal);
        f.Fs.CrashAt = f.Fs.Ops + 1;
        WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
        Assert.False(f.Fs.HasDir(f.Save));
        Assert.True(f.Fs.HasDir(f.Outgoing()));
        Assert.Equal(RestoreJournal.PhaseSwapping, f.ReadJournal()!.Phase);
        f.Fs.Revive();
        f.Stops.Clear(); // a crashed process calls nothing

        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.False(r.Ok);
        Assert.Contains("current world was put back", r.Reason);
        Assert.Equal(save, f.Fs.Snapshot(f.Save));
        Assert.False(f.Fs.Has(f.Journal));
        Assert.True(f.Fs.Has(Path.Combine(f.BepInEx, WorldRestorePaths.RefusedName(request.RequestId))));
        Assert.Empty(f.Stops);
    }

    [Fact]
    public void When_the_world_cannot_be_put_back_the_verified_staged_copy_rolls_forward()
    {
        var f = new Fixture();
        var request = f.Request();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        f.Fs.FailWhen = (op, path) => op == "movedir" && path.Contains(".incoming", StringComparison.Ordinal) && path.Contains("-> " + f.Save, StringComparison.Ordinal);
        f.Fs.CrashAt = f.Fs.Ops + 1;
        var expected = f.ExpectedFromArchive();
        WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
        f.Fs.Revive();
        f.Stops.Clear(); // a crashed process calls nothing
        // At the next start .outgoing is locked for good: the verified staged copy takes the save folder's place.
        f.Fs.Deny = (op, path) => op == "movedir" && path.StartsWith(f.Outgoing(), StringComparison.Ordinal);
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.True(r.Ok, r.Summary);
        Assert.Equal(expected, f.Fs.Snapshot(f.Save));
        Assert.Contains(r.Warnings, w => w.Contains("interrupted during the swap"));
        Assert.Empty(f.Stops);
    }

    [Fact]
    public void A_double_rename_failure_stops_the_server_until_the_folder_can_be_put_back()
    {
        var f = new Fixture();
        var request = f.Request();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        var save = f.Fs.Snapshot(f.Save);
        // Both the second rename and its undo are refused on every retry.
        f.Fs.Deny = (op, path) => op == "movedir" && (path.StartsWith(f.Outgoing(), StringComparison.Ordinal)
            || Path.GetFileName(path.Split(" -> ")[0]).StartsWith(".incoming", StringComparison.Ordinal));
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.True(r.Stranded);
        Assert.StartsWith("STOPPED:", r.Summary);
        Assert.Single(f.Stops);
        Assert.True(f.Fs.Slept > 0, "the renames were retried");
        Assert.True(f.Fs.Has(WorldRestoreMarker.RequestPath(f.BepInEx)), "the request stays for the next start");
        Assert.False(f.Fs.HasDir(f.Save));
        Assert.True(f.Fs.HasDir(r.BackupPath), "the verified backup is kept");

        // Still locked at the next start, with the staged copy gone too: the server stops again, before the game loads.
        f.Fs.DeleteDirectory(f.ReadJournal()!.Incoming);
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())?.Ok == true ? "ran" : null);
        Assert.Equal(2, f.Stops.Count);
        Assert.False(f.Fs.HasDir(f.Save));

        // The lock is gone: the world is renamed back.
        f.Fs.Deny = null;
        var back = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.False(back.Ok);
        Assert.Contains("put back", back.Reason);
        Assert.Equal(save, f.Fs.Snapshot(f.Save));
        Assert.Equal(2, f.Stops.Count);
    }

    [Fact]
    public void A_rename_blocked_for_a_moment_is_retried()
    {
        var f = new Fixture();
        int denied = 0;
        f.Fs.Deny = (op, path) => op == "movedir" && path.Contains(".incoming", StringComparison.Ordinal) && denied++ < 2;
        var r = WorldRestoreApply.Run(f.Fs, f.Request(), f.Env());
        Assert.True(r.Ok, r.Summary);
        Assert.Equal(100 + 250, f.Fs.Slept);
    }

    [Fact]
    public void A_crash_after_the_swap_reports_the_restore_at_the_next_start()
    {
        var f = new Fixture();
        var request = f.Request();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, request);
        var expected = f.ExpectedFromArchive();
        // Crash right after the journal records the swap (before retention and the result file).
        f.Fs.FailWhen = (op, path) => op == "flushdir" && path == f.Layout.VersionRoot;
        f.Fs.CrashAt = f.Fs.Ops + 1;
        WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
        Assert.Equal(RestoreJournal.PhaseSwapped, f.ReadJournal()!.Phase);
        Assert.Null(WorldRestoreMarker.ReadResult(f.Fs, f.BepInEx));
        f.Fs.Revive();
        f.Stops.Clear(); // a crashed process calls nothing

        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.True(r.Ok, r.Summary);
        Assert.Contains(r.Warnings, w => w.Contains("finished at the next start"));
        Assert.Equal(expected, f.Fs.Snapshot(f.Save));
        Assert.True(WorldRestoreMarker.ReadResult(f.Fs, f.BepInEx)!.Ok);
        Assert.True(f.Fs.Has(Path.Combine(f.BepInEx, WorldRestorePaths.AppliedName(request.RequestId))));
    }

    [Fact]
    public void The_replaced_world_is_removed_only_after_the_restored_world_has_saved()
    {
        var f = new Fixture();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, f.Request());
        Assert.True(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!.Ok);
        Assert.True(f.Fs.HasDir(f.Outgoing()));
        Assert.True(f.ReadJournal()!.ResultWritten);

        // A start with no save since: it stays.
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env()));
        Assert.True(f.Fs.HasDir(f.Outgoing()));

        // The save hook marks the journal; the next start removes it.
        Assert.True(WorldRestoreRecovery.NoteSaved(f.Fs, f.Env()));
        Assert.False(WorldRestoreRecovery.NoteSaved(f.Fs, f.Env()));
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env()));
        Assert.False(f.Fs.HasDir(f.Outgoing()));
        Assert.False(f.Fs.Has(f.Journal));

        // Without the hook, a save file newer than the swap is proof too.
        var g = new Fixture();
        WorldRestoreMarker.Write(g.Fs, g.BepInEx, g.Request());
        WorldRestoreMarker.ApplyAtStart(g.Fs, g.BepInEx, g.Env());
        g.Fs.Put(Path.Combine(g.Save, "AutoSave_5.save.gz"), FakeWorldFs.Gzip("saved after"), new DateTime(2026, 10, 7, 14, 0, 0, DateTimeKind.Utc));
        WorldRestoreMarker.ApplyAtStart(g.Fs, g.BepInEx, g.Env());
        Assert.False(g.Fs.HasDir(g.Outgoing()));
    }

    [Fact]
    public void A_new_restore_before_the_restored_world_saved_keeps_the_replaced_world_as_a_backup()
    {
        var f = new Fixture();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, f.Request());
        WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
        Assert.True(f.Fs.HasDir(f.Outgoing()));
        int before = WorldBackupCatalog.List(f.Fs, f.Layout).Count(e => e.Kind == WorldBackupEntry.KindBackup);

        var second = f.Request(WorldBackupEntry.KindBackup);
        second.RequestId = "20261007-100000-beef";
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, second);
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.True(r.Ok, r.Summary);
        Assert.False(f.Fs.HasDir(f.Outgoing()));
        var backups = WorldBackupCatalog.List(f.Fs, f.Layout).Where(e => e.Kind == WorldBackupEntry.KindBackup).ToList();
        // The replaced world became a backup of its own, and the second restore made one more.
        Assert.Equal(before + 2, backups.Count);
        Assert.Contains(backups, e => e.FolderName.EndsWith("-replaced", StringComparison.Ordinal));
    }

    [Fact]
    public void A_damaged_live_world_is_backed_up_as_it_is_and_can_be_restored_over()
    {
        var f = new Fixture();
        var good = FakeWorldFs.Gzip(new string('x', 5000));
        f.Fs.Put(Path.Combine(f.Save, "AutoSave_12.save.gz"), good.Take(good.Length - 10).ToArray(), new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc));
        var r = WorldRestoreApply.Run(f.Fs, f.Request(), f.Env());
        Assert.True(r.Ok, r.Summary);
        Assert.Contains(r.Warnings, w => w.Contains("AutoSave_12.save.gz") && w.Contains("exact copy"));
        Assert.True(f.Fs.Has(Path.Combine(r.BackupPath, "AutoSave_12.save.gz")));
    }

    [Fact]
    public void A_source_whose_newest_save_does_not_decompress_is_refused_at_the_stage()
    {
        var f = new Fixture();
        var good = FakeWorldFs.Gzip(new string('y', 5000));
        f.Fs.Put(Path.Combine(f.Archive, "AutoSave_5.save.gz"), good.Take(good.Length - 10).ToArray(), new DateTime(2026, 9, 30, 13, 0, 0, DateTimeKind.Utc));
        var request = f.Request();
        var save = f.Fs.Snapshot(f.Save);
        var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
        AssertUnchanged(f, save, f.Fs.Snapshot(f.Archive), f.Fs.FileState(f.GlobalSettings), r, r.Summary);
        Assert.Contains("AutoSave_5.save.gz", r.Reason);
        Assert.Equal("stage", r.Steps.Last().Name);
    }

    [Fact]
    public void A_backup_that_does_not_match_its_sidecar_is_refused()
    {
        var f = new Fixture();
        // Same length and write time (the listing's shape is the same): only the SHA-256 record tells.
        var file = Path.Combine(f.Old, "StartDate.json");
        var stamp = f.Fs.Mtime(file);
        var request = f.Request(WorldBackupEntry.KindBackup);
        f.Fs.Put(file, "10/02/2026", stamp);
        var save = f.Fs.Snapshot(f.Save);
        var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
        AssertUnchanged(f, save, f.Fs.Snapshot(f.Archive), f.Fs.FileState(f.GlobalSettings), r, r.Summary);
        Assert.Contains("does not match its own record", r.Reason);
    }

    [Fact]
    public void A_source_that_changed_after_it_was_chosen_is_refused()
    {
        var f = new Fixture();
        var request = f.Request();
        f.Fs.Put(Path.Combine(f.Archive, "AutoSave_5.save.gz"), FakeWorldFs.Gzip("written later"), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        var save = f.Fs.Snapshot(f.Save);
        var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
        AssertUnchanged(f, save, f.Fs.Snapshot(f.Archive), f.Fs.FileState(f.GlobalSettings), r, r.Summary);
        Assert.Contains("changed after it was chosen", r.Reason);
    }

    [Theory]
    [InlineData("live")]
    [InlineData("liveSlashes")]
    [InlineData("liveDevice")]
    [InlineData("liveShare")]
    [InlineData("liveDots")]
    [InlineData("liveArg")]
    [InlineData("saveName")]
    [InlineData("persist")]
    [InlineData("space")]
    [InlineData("kind")]
    [InlineData("sourceOutside")]
    [InlineData("shapeMissing")]
    [InlineData("hosted")]
    [InlineData("settingsOutside")]
    public void Validation_refuses_with_nothing_changed(string what)
    {
        var f = new Fixture();
        var request = f.Request(wipe: what == "settingsOutside" ? WipeModes.WipesOff : WipeModes.None);
        var env = f.Env();
        switch (what)
        {
            case "live": env.ProcessPath = @"C:\VRisingServer\VRisingServer.exe"; break;
            case "liveSlashes": env.ProcessPath = "c:/vrisingserver/VRisingServer.exe"; break;
            case "liveDevice": env.ProcessPath = @"\\?\C:\VRisingServer\VRisingServer.exe"; break;
            case "liveShare": env.ProcessPath = @"\\localhost\C$\VRisingServer\VRisingServer.exe"; break;
            case "liveDots": env.ProcessPath = @"C:\Games\..\VRisingServer\VRisingServer.exe"; break;
            case "liveArg": env.Args = new[] { "x", "-logFile", "C:/VRisingServer/logs/x.log" }; break;
            case "saveName": env.Args = new[] { "x", "-saveName", "world2" }; break;
            case "persist": env.Args = new[] { "x", "-persistentDataPath", Path.Combine(FakeWorldFs.Root, "elsewhere") }; break;
            case "space": f.Fs.Free = 5; break;
            case "kind": request.SourceKind = "autosave"; break;
            case "sourceOutside": request.SourcePath = Path.Combine(FakeWorldFs.Root, "other", "world1_123"); f.Fs.Dir(request.SourcePath); break;
            case "shapeMissing": request.ExpectShape = ""; break;
            case "hosted": env.Args = new[] { "x", "-userSave", "-saveName", "world1" }; break;
            case "settingsOutside": request.HostSettingsPath = Path.Combine(FakeWorldFs.Root, "elsewhere", "ServerHostSettings.json"); break;
        }

        var save = f.Fs.Snapshot(f.Save);
        var r = WorldRestoreApply.Run(f.Fs, request, env);
        AssertUnchanged(f, save, f.Fs.Snapshot(f.Archive), f.Fs.FileState(f.GlobalSettings), r, what + ": " + r.Summary);
        Assert.Equal("validate", r.Steps.Single().Name);
        if (what.StartsWith("live", StringComparison.Ordinal)) Assert.Contains(@"C:\VRisingServer", r.Reason);
        if (what == "hosted") Assert.Contains("hosted private game", r.Reason);
        if (what == "settingsOutside") Assert.Contains("not under this server's folder", r.Reason);
    }

    [Fact]
    public void Bootstrap_touches_no_file_for_a_live_server_process()
    {
        var f = new Fixture();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, f.Request());
        var env = f.Env();
        env.ProcessPath = @"C:\VRisingServer\VRisingServer.exe";
        int ops = f.Fs.Ops;
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, env));
        Assert.Equal(ops, f.Fs.Ops);
    }

    [Fact]
    public void A_wipe_that_is_due_at_apply_time_stops_the_restore_and_puts_the_settings_back()
    {
        // The next start reads ResetDaysInterval 30: the archive (Jul 1) is due, and the request made no choice.
        var f = new Fixture(interval: 30);
        var save = f.Fs.Snapshot(f.Save);
        var r = WorldRestoreApply.Run(f.Fs, f.Request(), f.Env());
        AssertUnchanged(f, save, f.Fs.Snapshot(f.Archive), f.Fs.FileState(f.GlobalSettings), r, r.Summary);
        Assert.Contains("would be wiped at this start", r.Reason);
        Assert.Equal("wipe check", r.Steps.Last().Name);

        // A picked date that is not after today any more: the host settings were written first and are put back exactly.
        var g = new Fixture(interval: 30);
        string settings = g.Fs.FileState(g.GlobalSettings);
        var late = g.Request(wipe: WipeModes.Date, interval: 98, day: "Any", date: "2026-10-07");
        var r2 = WorldRestoreApply.Run(g.Fs, late, g.Env());
        Assert.False(r2.Ok);
        Assert.Contains("not after today", r2.Reason);
        Assert.Equal(settings, g.Fs.FileState(g.GlobalSettings));

        // The command line wins over the files.
        var h = new Fixture();
        var env = h.Env();
        env.Args = new[] { "x", "-resetDaysInterval", "30" };
        Assert.Contains("would be wiped", WorldRestoreApply.Run(h.Fs, h.Request(), env).Reason);
    }

    [Fact]
    public void When_the_second_rename_fails_the_first_is_undone()
    {
        var f = new Fixture();
        var request = f.Request();
        var save = f.Fs.Snapshot(f.Save);
        f.Fs.Deny = (op, path) => op == "movedir" && Path.GetFileName(path.Split(" -> ")[0]).StartsWith(".incoming", StringComparison.Ordinal);
        var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
        AssertUnchanged(f, save, f.Fs.Snapshot(f.Archive), f.Fs.FileState(f.GlobalSettings), r, r.Summary);
        Assert.Contains("the current world is back", r.Reason);
    }

    [Fact]
    public void Leftover_copies_without_a_journal_are_removed_at_the_next_restore()
    {
        var f = new Fixture();
        f.Fs.Put(Path.Combine(f.BackupsRoot, "20261001-000000-before-restore.partial", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.BackupsRoot, ".incoming-old", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.BackupsRoot, ".deleting-old", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        f.Fs.Put(Path.Combine(f.BackupsRoot, ".outgoing-other", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        Assert.True(WorldRestoreApply.Run(f.Fs, f.Request(), f.Env()).Ok);
        Assert.False(f.Fs.HasDir(Path.Combine(f.BackupsRoot, "20261001-000000-before-restore.partial")));
        Assert.False(f.Fs.HasDir(Path.Combine(f.BackupsRoot, ".incoming-old")));
        Assert.False(f.Fs.HasDir(Path.Combine(f.BackupsRoot, ".deleting-old")));
        // An .outgoing folder may be a stranded world: it is never removed without its journal.
        Assert.True(f.Fs.HasDir(Path.Combine(f.BackupsRoot, ".outgoing-other")));
    }

    [Fact]
    public void A_backup_restores_like_an_archive_and_its_sidecar_does_not_come_along()
    {
        var f = new Fixture();
        var r = WorldRestoreApply.Run(f.Fs, f.Request(WorldBackupEntry.KindBackup), f.Env());
        Assert.True(r.Ok, r.Summary);
        Assert.True(f.Fs.Has(Path.Combine(f.Save, "AutoSave_1.save.gz")));
        Assert.False(f.Fs.Has(Path.Combine(f.Save, WorldRestorePaths.SidecarName)));
        Assert.Equal("{\n  \"Name\": \"current per-save\"\n}\n", f.Fs.Text(Path.Combine(f.Save, "ServerHostSettings.json")));
        Assert.True(f.Fs.HasDir(f.Old));
        Assert.StartsWith("Restored the backup from Sun, Sep 20, 2026", r.Summary);
    }

    [Fact]
    public void A_request_without_a_save_file_in_the_current_world_is_refused()
    {
        var f = new Fixture();
        var request = f.Request();
        f.Fs.DeleteFile(Path.Combine(f.Save, "AutoSave_10.save.gz"));
        f.Fs.DeleteFile(Path.Combine(f.Save, "AutoSave_11.save.gz"));
        var r = WorldRestoreApply.Run(f.Fs, request, f.Env());
        Assert.False(r.Ok);
        Assert.Contains("no save file to back up", r.Reason);
        Assert.True(f.Fs.Has(Path.Combine(f.Save, "StartDate.json")));
    }

    [Fact]
    public void The_real_file_system_keeps_write_times_and_swaps_with_renames()
    {
        string root = Path.Combine(Path.GetTempPath(), "progenitor-worldrestore-" + Guid.NewGuid().ToString("N"));
        try
        {
            string persist = Path.Combine(root, "data");
            string save = Path.Combine(persist, "Saves", "v4", "w");
            string bepinex = Path.Combine(root, "server", "BepInEx");
            Directory.CreateDirectory(bepinex);
            long ticks = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Local).Ticks;
            string archive = Path.Combine(persist, "Saves", "v4", "w_" + ticks);
            Directory.CreateDirectory(Path.Combine(save, "progenitor"));
            Directory.CreateDirectory(archive);
            var t = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            File.WriteAllBytes(Path.Combine(save, "AutoSave_2.save.gz"), FakeWorldFs.Gzip("now"));
            File.SetLastWriteTimeUtc(Path.Combine(save, "AutoSave_2.save.gz"), t);
            File.WriteAllText(Path.Combine(save, "progenitor", "m.json"), "{}");
            File.WriteAllBytes(Path.Combine(archive, "AutoSave_1.save.gz"), FakeWorldFs.Gzip("then"));
            File.SetLastWriteTimeUtc(Path.Combine(archive, "AutoSave_1.save.gz"), t.AddDays(-5));
            File.WriteAllText(Path.Combine(archive, "StartDate.json"), "08/01/2026");
            Assert.True(WorldRestorePaths.TryLayout(save, out var layout, out _));
            var e = WorldBackupCatalog.List(PhysicalWorldFs.Instance, layout).Single();
            var request = new RestoreRequest
            {
                RequestId = "r1", SaveFolder = save, SaveName = "w", SourceKind = e.Kind, SourcePath = e.Path, SourceName = e.BaseLabel,
                ExpectShape = e.Shape, KeepBackups = 5,
            };
            var r = WorldRestoreApply.Run(PhysicalWorldFs.Instance, request, new RestoreEnvironment { SpareBytes = 0, BepInExRoot = bepinex });
            Assert.True(r.Ok, r.Summary);
            Assert.Equal(t.AddDays(-5), File.GetLastWriteTimeUtc(Path.Combine(save, "AutoSave_1.save.gz")));
            Assert.False(File.Exists(Path.Combine(save, "AutoSave_2.save.gz")));
            Assert.True(File.Exists(Path.Combine(archive, "AutoSave_1.save.gz")));
            Assert.Equal(t, File.GetLastWriteTimeUtc(Path.Combine(r.BackupPath, "AutoSave_2.save.gz")));
            Assert.True(File.Exists(Path.Combine(r.BackupPath, "progenitor", "m.json")));
            Assert.True(File.Exists(Path.Combine(bepinex, WorldRestorePaths.JournalFileName)));
            Assert.Single(Directory.GetDirectories(layout.BackupsRoot, ".outgoing-*"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp */ }
        }
    }
}
