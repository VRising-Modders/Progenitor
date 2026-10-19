using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// BACKUPNOW1: the Server rules channel's Back up now and backup settings (an admin backs up with a dry run or for real, the
/// save is triggered first and the copy waits for it, the audit names who made it, the settings validate and lock), the
/// schedule's tick, the wire round trip of the new state, the list header (kinds, schedule, the game's autosaves and what
/// Progenitor backups add), and the Back up now confirmation. Real temp folders; game-free.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class WorldBackupServerTests : IDisposable
{
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);
    private static readonly DateTime NowLocal = new(2026, 10, 7, 9, 30, 0);
    private static readonly DateTime Utc0 = new(2026, 10, 7, 13, 30, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly string _data;
    private readonly string _save;
    private readonly string _bepinex;
    private readonly List<string> _audit = new();
    private readonly List<(double Delay, Action Work)> _queue = new();
    private readonly WorldBackupSettings _settings = new();
    private string? _lockText;
    private string[] _args;
    private double _now = 1000;
    private DateTime _utc = Utc0;
    private int _saves;
    private string? _saveRefusal;
    private bool _saveLands = true;

    public WorldBackupServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-worldbackup-srv-" + Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_dir, "data");
        _save = Path.Combine(_data, "Saves", "v4", "world1");
        _bepinex = Path.Combine(_dir, "server", "BepInEx");
        Directory.CreateDirectory(Path.Combine(_data, "Settings"));
        Directory.CreateDirectory(Path.Combine(_save, "progenitor"));
        Directory.CreateDirectory(_bepinex);
        File.WriteAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json"),
            "{\n  \"Name\": \"Test\",\n  \"SaveName\": \"world1\",\n  \"AutoSaveCount\": 10,\n  \"AutoSaveInterval\": 120\n}\n");
        File.WriteAllBytes(Path.Combine(_save, "AutoSave_1.save.gz"), FakeWorldFs.Gzip("first"));
        File.SetLastWriteTimeUtc(Path.Combine(_save, "AutoSave_1.save.gz"), new DateTime(2026, 10, 7, 13, 0, 0, DateTimeKind.Utc));
        File.WriteAllBytes(Path.Combine(_save, "AutoSave_2.save.gz"), FakeWorldFs.Gzip("second"));
        File.SetLastWriteTimeUtc(Path.Combine(_save, "AutoSave_2.save.gz"), new DateTime(2026, 10, 7, 13, 2, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(_save, "StartDate.json"), "09/20/2026");
        File.WriteAllText(Path.Combine(_save, "progenitor", "mod.json"), "{\"a\":1}");
        _args = new[] { "VRisingServer.exe", "-persistentDataPath", _data, "-saveName", "world1" };

        Plugin.Logger = new TestLogSource();
        ServerRulesServer.Uninstall();
        ServerSettingsLock.Reset();
        ServerSettingsLock.Source = () => _lockText;
        ServerRulesServer.Audit = _audit.Add;
        ServerRulesServer.Now = () => _now;
        ServerRulesServer.FactsProvider = Facts;
        ServerBackupsServer.SaveFolderProbe = () => _save;
        ServerBackupsServer.BepInExRootProbe = () => _bepinex;
        ServerBackupsServer.ArgsProbe = () => _args;
        ServerBackupsServer.NowLocal = () => NowLocal;
        ServerBackupsServer.NowUtc = () => _utc;
        ServerBackupsServer.Zone = () => WorldRestoreApplyTests.Zone;
        ServerBackupsServer.BackupSettingsProbe = () => _settings;
        ServerBackupsServer.BackupSettingWrite = (key, value) =>
        {
            switch (key)
            {
                case WorldBackupSettings.AtStartKey: _settings.AtStart = value == "true"; break;
                case WorldBackupSettings.EveryHoursKey: _settings.EveryHours = int.Parse(value); break;
                case WorldBackupSettings.KeepCountKey: _settings.KeepCount = int.Parse(value); break;
                default: _settings.KeepDays = int.Parse(value); break;
            }

            return null;
        };
        ServerBackupsJob.Reset();
        ServerBackupsJob.NowUtc = () => _utc;
        ServerBackupsJob.Schedule = (delay, work) => _queue.Add((delay, work));
        ServerBackupsJob.RunOffThread = work => work();
        ServerBackupsJob.SaveWatch = () =>
        {
            int before = _saves;
            return () => _saveLands && _saves > before;
        };
        ServerBackupsJob.SaveWorld = () =>
        {
            if (_saveRefusal != null) return _saveRefusal;
            // The game's save: a newer autosave and the mod data written with it.
            _saves++;
            string file = Path.Combine(_save, "AutoSave_" + (2 + _saves) + ".save.gz");
            File.WriteAllBytes(file, FakeWorldFs.Gzip("saved " + _saves));
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 10, 7, 13, 10, 0, DateTimeKind.Utc).AddMinutes(_saves));
            File.WriteAllText(Path.Combine(_save, "progenitor", "mod.json"), "{\"a\":" + (1 + _saves) + "}");
            return null;
        };
        ServerBackupsRows.Reset();
    }

    public void Dispose()
    {
        ServerBackupsJob.Reset();
        ServerRulesServer.Uninstall();
        ServerSettingsLock.Reset();
        ServerBackupsRows.Reset();
        ServerRulesRows.Reset();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private ServerRulesFacts Facts()
    {
        var host = ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
        {
            Args = _args,
            Env = _ => null,
            InstallRoot = Path.Combine(_dir, "server"),
            PersistentDataPath = _data,
        });
        var game = new GameSettingsResolution(new GameSettingsInputs
        {
            InstallRoot = Path.Combine(_dir, "server"),
            PersistentDataPath = _data,
            SaveFolder = host.SaveFolder,
        });
        return new ServerRulesFacts
        {
            Host = host,
            Game = game,
            StartDateText = File.Exists(Path.Combine(_save, "StartDate.json")) ? File.ReadAllText(Path.Combine(_save, "StartDate.json")) : "",
            NowLocal = NowLocal,
            LockedBy = ServerSettingsLock.LockedBy,
        };
    }

    private ServerRulesResponse Ask(ServerRulesRequest request, RemoteInstallActor? actor = null)
    {
        _now += 10;
        var response = ServerRulesServer.Apply(request, handshakeOk: true, actor ?? Admin);
        Assert.True(ServerRulesWire.TryDecodeResponse(ServerRulesWire.EncodeResponse(response), out var decoded));
        return decoded;
    }

    /// <summary>Run the scheduled main-thread work (the save checks, the settle, the copy's completion) until none is left.</summary>
    private void Pump(int max = 100)
    {
        for (int i = 0; i < max && _queue.Count > 0; i++)
        {
            var (delay, work) = _queue[0];
            _queue.RemoveAt(0);
            _utc = _utc.AddSeconds(delay);
            work();
        }
    }

    private string BackupsRoot => Path.Combine(_data, "progenitor-backups", "v4", "world1");

    private string[] Backups() =>
        Directory.Exists(BackupsRoot) ? Directory.GetDirectories(BackupsRoot).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()! : Array.Empty<string>();

    // ---- Back up now ---------------------------------------------------------------------------------

    [Fact]
    public void A_dry_run_says_where_how_big_and_what_is_kept_and_writes_nothing()
    {
        var r = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = true });
        Assert.True(r.Ok, r.Error);
        Assert.StartsWith("Dry run: a manual backup of world1 (about ", r.Note);
        Assert.Contains(Path.Combine("progenitor-backups", "v4", "world1"), r.Note);
        Assert.Contains("Nothing was written.", r.Note);
        Assert.Equal(0, _saves);
        Assert.Empty(Backups());
        Assert.Empty(_audit);
        Assert.Equal(Path.Combine("progenitor-backups", "v4", "world1"), r.Backups!.BackupsWhere);
        Assert.True(r.Backups.BackupAllowed);
    }

    [Fact]
    public void Back_up_now_saves_first_then_copies_what_the_save_wrote_and_audits_who_made_it()
    {
        var r = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow });
        Assert.True(r.Ok, r.Error);
        Assert.StartsWith("World world1 is saved now", r.Note);
        Assert.Equal(1, _saves);
        Assert.True(r.Backups!.JobRunning);
        Assert.Equal("saving the world", r.Backups.JobPhase);
        Assert.Equal("Manual backup by Admin: saving the world…", ServerBackups.BackupLine(r.Backups));
        // A second request while it runs is refused.
        Assert.Contains("already running", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow }).Error);

        Pump();
        Assert.Null(ServerBackupsJob.Current);
        var last = ServerBackupsJob.Last!;
        Assert.True(last.Ok, last.Summary);
        var made = Assert.Single(Backups());
        Assert.EndsWith("-manual", made);
        // The copy holds the save the trigger wrote, and the mod data written with it.
        Assert.True(File.Exists(Path.Combine(BackupsRoot, made, "AutoSave_3.save.gz")));
        Assert.Equal("{\"a\":2}", File.ReadAllText(Path.Combine(BackupsRoot, made, "progenitor", "mod.json")));

        // Accepted, the refusal while it ran, and the result: each names who asked.
        Assert.Equal(3, _audit.Count);
        Assert.All(_audit, a => Assert.Contains("Admin", a));
        Assert.All(_audit, a => Assert.Contains("BackupNow", a));
        Assert.Contains("already running", _audit[1]);
        Assert.Contains("backup_now done " + made, _audit[2]);

        var s = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        Assert.False(s.JobRunning);
        Assert.True(s.LastBackupOk);
        Assert.StartsWith("Made Manual backup from ", s.LastBackupSummary);
        var entry = s.Entries.Single(e => e.BackupKind == WorldRestorePaths.KindManual);
        Assert.Equal("Manual backup", entry.KindLabel);
        Assert.True(entry.Restorable);
        Assert.True(s.ManualBytes > 0);
        Assert.True(WorldBackupStatus.Read(PhysicalWorldFs.Instance, _bepinex)[WorldRestorePaths.TriggerManual].Ok);

        // It is restorable through the restore flow (a dry run).
        var restore = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = entry.Id, Shape = entry.Shape, DryRun = true });
        Assert.True(restore.Ok, restore.Error);
        Assert.Contains("the manual backup from", restore.Note);
    }

    /// <summary>
    /// BACKUPROWS1 (live 2026-10-19: the page's Last backup row stayed on "saving the world…" after the backup was made):
    /// a finished backup tells the admins who read the Backups, so their rows ask once for the result.
    /// </summary>
    [Fact]
    public void A_finished_backup_tells_the_admins_who_read_the_backups()
    {
        var changes = new List<BackupsPendingChange>();
        Action<BackupsPendingChange> record = changes.Add;
        ServerBackupsServer.PendingChanged += record;
        try
        {
            Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow }).Ok);
            Assert.Empty(changes);
            Pump();
            Assert.Null(ServerBackupsJob.Current);
            var change = Assert.Single(changes);
            Assert.Equal(ServerBackups.BackupFinishedWhy, change.Why);
            Assert.Equal("", change.PendingId);
        }
        finally { ServerBackupsServer.PendingChanged -= record; }
    }

    [Fact]
    public void Core_unloading_stops_a_running_copy_and_removes_its_partial_folder()
    {
        Action? work = null;
        ServerBackupsJob.RunOffThread = w => work = w;
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow }).Ok);
        Pump(max: 10);
        Assert.NotNull(work);
        // Core goes down (Uninstall resets the job) while the copy thread has not finished.
        ServerBackupsJob.Reset();
        work!();
        Assert.DoesNotContain(Backups(), d => d.EndsWith("-manual", StringComparison.Ordinal) || d.EndsWith(".partial", StringComparison.Ordinal));
        var status = WorldBackupStatus.Read(PhysicalWorldFs.Instance, _bepinex)[WorldRestorePaths.TriggerManual];
        Assert.False(status.Ok);
        Assert.Contains("stopped (Progenitor is unloading)", status.Summary);
    }

    [Fact]
    public void A_save_that_never_lands_or_is_refused_makes_no_backup()
    {
        _saveLands = false;
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow }).Ok);
        Pump();
        Assert.False(ServerBackupsJob.Last!.Ok);
        Assert.Contains("did not land within 15 seconds", ServerBackupsJob.Last.Reason);
        Assert.Empty(Backups());
        Assert.Contains(_audit, a => a.Contains("backup_now done") && a.Contains("did not land"));
        Assert.False(WorldBackupStatus.Read(PhysicalWorldFs.Instance, _bepinex)[WorldRestorePaths.TriggerManual].Ok);

        _saveRefusal = "no server world";
        var r = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow });
        Assert.False(r.Ok);
        Assert.Contains("the world save was refused (no server world)", r.Error);
        Assert.Empty(_queue);
    }

    [Fact]
    public void Back_up_now_is_admin_only_lockable_and_refuses_on_short_disk_space()
    {
        Assert.Equal(ServerRulesServer.BackupsAdminError, Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow }, Player).Error);
        _lockText = ServerBackups.BackupNowSettingId;
        var locked = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = true });
        Assert.False(locked.Ok);
        Assert.Equal(ServerSettingsLock.NoteFor(ServerBackups.BackupNowSettingId), locked.Error);
        Assert.False(locked.Backups!.BackupAllowed);
        Assert.Equal(ServerBackups.BackupNowSettingId, locked.Backups.BackupNowLockedBy);
        _lockText = ServerSettingsLock.GroupServerRules;
        Assert.False(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = true }).Ok);
        _lockText = null;

        ServerBackupsServer.Fs = new LowSpaceFs(PhysicalWorldFs.Instance, 1000);
        var space = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow });
        Assert.False(space.Ok);
        Assert.StartsWith("Not enough free disk space for a backup:", space.Error);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void The_live_server_path_is_refused()
    {
        _args = new[] { "VRisingServer.exe", "-persistentDataPath", @"C:\VRisingServer\save-data" };
        var r = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = true });
        Assert.False(r.Ok);
        Assert.Contains("live server", r.Error);
        Assert.False(r.Backups!.BackupAllowed);
    }

    // ---- the settings ---------------------------------------------------------------------------------

    [Fact]
    public void The_backup_settings_validate_store_audit_and_lock()
    {
        var at = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.AtStartKey, Value = "true" });
        Assert.True(at.Ok, at.Error);
        Assert.True(_settings.AtStart);
        Assert.True(at.Backups!.AtStart);
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.EveryHoursKey, Value = "6" }).Ok);
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepCountKey, Value = "3" }).Ok);
        var days = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepDaysKey, Value = "14" });
        Assert.True(days.Ok);
        Assert.Equal((6, 3, 14), (days.Backups!.EveryHours, days.Backups.KeepCount, days.Backups.KeepDays));
        Assert.Equal("at every server start and every 6 hours", days.Backups.Settings.ScheduleText());
        Assert.Contains(_audit, a => a.Contains("BackupKeepDays") && a.Contains("14"));

        Assert.Equal("Give true or false.", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.AtStartKey, Value = "yes" }).Error);
        Assert.StartsWith("Give 0 (off)", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.EveryHoursKey, Value = "169" }).Error);
        Assert.StartsWith("Give 0 (no count limit)", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepCountKey, Value = "101" }).Error);
        Assert.StartsWith("Give 0 (no age limit)", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepDaysKey, Value = "-1" }).Error);
        Assert.StartsWith("Unknown backup setting", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = "Nope", Value = "1" }).Error);

        _lockText = ServerBackups.EveryHoursSettingId;
        var locked = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.EveryHoursKey, Value = "1" });
        Assert.False(locked.Ok);
        Assert.Equal(6, _settings.EveryHours);
        Assert.Equal(ServerBackups.EveryHoursSettingId, locked.Backups!.EveryHoursLockedBy);
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepCountKey, Value = "4" }).Ok);
        Assert.Equal(ServerRulesServer.BackupsAdminError,
            Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepCountKey, Value = "4" }, Player).Error);
    }

    // ---- the schedule ---------------------------------------------------------------------------------

    [Fact]
    public void The_schedule_tick_starts_a_scheduled_backup_when_it_is_due_and_records_a_failure()
    {
        _settings.EveryHours = 2;
        Assert.Null(ServerBackupsJob.Tick(_settings));
        _utc = Utc0.AddHours(2);
        Assert.Equal("started", ServerBackupsJob.Tick(_settings));
        Pump();
        var made = Assert.Single(Backups());
        Assert.EndsWith("-scheduled", made);
        Assert.Equal(WorldRestorePaths.TriggerInterval, BackupSidecar.Parse(File.ReadAllBytes(Path.Combine(BackupsRoot, made, WorldRestorePaths.SidecarName)))!.Trigger);
        // Not again until two hours after it.
        _utc = _utc.AddHours(1);
        Assert.Null(ServerBackupsJob.Tick(_settings));

        // A refused save: recorded as the schedule's failure, shown in the header, retried 30 minutes later.
        _utc = _utc.AddHours(2);
        _saveRefusal = "no server world";
        Assert.StartsWith("refused: ", ServerBackupsJob.Tick(_settings));
        var s = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        Assert.Contains("the world save was refused", s.ScheduleFailure);
        var facts = ServerBackupsRows.HeaderFacts(s, s.Entries.Count);
        var failed = Assert.Single(facts, f => f.Label == ServerBackupsRows.ScheduleFailedLabel);
        Assert.Equal(DialogFactTone.Warn, failed.Tone);
        _utc = _utc.AddMinutes(10);
        Assert.Null(ServerBackupsJob.Tick(_settings));
        _saveRefusal = null;
        _utc = _utc.AddMinutes(25);
        Assert.Equal("started", ServerBackupsJob.Tick(_settings));
        Pump();
        Assert.Equal("", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!.ScheduleFailure);
    }

    // ---- the header, the autosaves, the confirmation ----------------------------------------------------

    [Fact]
    public void The_header_lists_the_games_autosaves_with_their_effective_settings_and_what_backups_add()
    {
        var s = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        Assert.True(s.AutosaveKnown);
        Assert.Equal(2, s.AutosaveCount);
        Assert.Equal(new FileInfo(Path.Combine(_save, "AutoSave_1.save.gz")).Length + new FileInfo(Path.Combine(_save, "AutoSave_2.save.gz")).Length, s.AutosaveBytes);
        Assert.Equal(10, s.AutosaveKeep);
        Assert.Equal(120, s.AutosaveEvery);
        string line = ServerBackups.AutosaveLine(s);
        Assert.StartsWith("2 files, ", line);
        Assert.EndsWith("; the game keeps 10, one every 2 minutes", line);
        // 13:00 and 13:02 UTC in the server's zone (the tests' UTC-4).
        Assert.Equal("Wed, Oct 7, 2026 9:00 AM to 9:02 AM", ServerBackups.AutosaveSpan(s));

        var facts = ServerBackupsRows.HeaderFacts(s, s.Entries.Count).ToDictionary(f => f.Label, f => f.Value);
        Assert.Equal(line, facts[ServerBackupsRows.AutosavesLabel]);
        Assert.Equal(ServerBackups.AutosaveSpan(s), facts[ServerBackupsRows.AutosaveTimesLabel]);
        // What Progenitor backups add: two label/value lines, once (BACKUPNOTE1: no centered note cut with "…").
        var list = ServerBackupsRows.ListContent(s);
        Assert.Equal("", list.Note);
        Assert.Equal(ServerBackups.AutosavesAreText, facts[ServerBackupsRows.AutosavesAreLabel]);
        Assert.Equal(ServerBackups.BackupsAddText, facts[ServerBackupsRows.BackupsAddLabel]);
        Assert.Contains("Save files only, rotated", facts[ServerBackupsRows.AutosavesAreLabel]);
        Assert.Contains("not restorable here yet", facts[ServerBackupsRows.AutosavesAreLabel]);
        Assert.Contains("whole world folder", facts[ServerBackupsRows.BackupsAddLabel]);
        Assert.Single(list.Facts, f => f.Label == ServerBackupsRows.AutosavesAreLabel);

        // The command line wins over the files, as the game reads it.
        _args = new[] { "VRisingServer.exe", "-persistentDataPath", _data, "-saveName", "world1", "-saveCount", "4", "-saveInterval", "90" };
        var o = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        Assert.Equal((4, 90), (o.AutosaveKeep, o.AutosaveEvery));
        Assert.EndsWith("the game keeps 4, one every 90 seconds", ServerBackups.AutosaveLine(o));

        // No save folder: no autosave line.
        Directory.Delete(_save, recursive: true);
        var gone = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        Assert.False(gone.AutosaveKnown, "known " + gone.AutosaveCount + " exists " + Directory.Exists(_save));
        Assert.DoesNotContain(ServerBackupsRows.HeaderFacts(gone, 0), f => f.Label == ServerBackupsRows.BackupsAddLabel);
        Assert.DoesNotContain(ServerBackupsRows.HeaderFacts(gone, 0), f => f.Label == ServerBackupsRows.AutosavesLabel);
    }

    [Fact]
    public void The_list_groups_manual_scheduled_restore_chain_and_archives_and_the_header_shows_kinds_schedule_and_retention()
    {
        _settings.AtStart = true;
        _settings.KeepCount = 1;
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow }).Ok);
        Pump();
        _settings.EveryHours = 1;
        _utc = Utc0.AddHours(2);
        Assert.Equal("started", ServerBackupsJob.Tick(_settings));
        Pump();
        var s = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        var list = ServerBackupsRows.ListContent(s);
        Assert.Equal(new[] { ServerBackupsRows.ManualHeading, ServerBackupsRows.ScheduledHeading }, list.OptionHeadings.Select(h => h.Text).ToArray());
        Assert.StartsWith("Manual backup · ", list.Options[0]);
        Assert.StartsWith("Scheduled backup · ", list.Options[1]);
        var facts = list.Facts.ToDictionary(f => f.Label, f => f);
        Assert.StartsWith("manual ", facts[ServerBackupsRows.KindsLabel].Value);
        Assert.Contains(" · scheduled ", facts[ServerBackupsRows.KindsLabel].Value);
        Assert.Equal("the newest 1 of each kind; the newest of each kind always stays", facts[ServerBackupsRows.BackupKeptLabel].Value);
        Assert.StartsWith("at every server start and every hour; next ", facts[ServerBackupsRows.ScheduleLabel].Value);
        // The next manual backup would delete the current one (count 1): shown in amber.
        var next = facts[ServerBackupsRows.NextBackupDeletesLabel];
        Assert.Equal(DialogFactTone.Warn, next.Tone);
        Assert.Contains("Manual backup from ", next.Value);
        Assert.Contains("Scheduled backup from ", next.Value);
        // No header line is cut at the narrowest dialog.
        float minRow = DialogLayout.ContentRowWidth(DialogLayout.FrameWidth);
        var clean = DialogFacts.Clean(list.Facts);
        float valueWidth = DialogFacts.ValueWidth(DialogFacts.LabelWidth(clean, minRow), minRow);
        Assert.All(clean, f => Assert.True(MenuPanelLayout.WrappedLines(f.Value, valueWidth, DialogLayout.FactFontSize, 100) <= DialogLayout.MaxFactLines, f.ToString()));
    }

    [Fact]
    public void The_autosave_comparison_is_never_cut_at_the_narrowest_dialog()
    {
        // BACKUPNOTE1 (live 2026-10-07: the note drew "Game autosaves are save files only, rotated in the save fol…").
        var s = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        foreach (var content in new[] { ServerBackupsRows.ListContent(s), ServerBackupsRows.ListContent(new ServerBackupsState { AutosaveKnown = true }) })
        {
            Assert.Equal("", content.Note);
            float minRow = DialogLayout.ContentRowWidth(DialogLayout.FrameWidth);
            var clean = DialogFacts.Clean(content.Facts);
            float labelWidth = DialogFacts.LabelWidth(clean, minRow);
            float valueWidth = DialogFacts.ValueWidth(labelWidth, minRow);
            foreach (string label in new[] { ServerBackupsRows.AutosavesAreLabel, ServerBackupsRows.BackupsAddLabel })
            {
                var fact = Assert.Single(clean, f => f.Label == label);
                // Wraps within the fact's line limit (uncapped, so a value that would be cut fails here).
                int lines = MenuPanelLayout.WrappedLines(fact.Value, valueWidth, DialogLayout.FactFontSize, 100);
                Assert.True(lines <= DialogLayout.MaxFactLines, fact + " needs " + lines + " lines at " + valueWidth);
                // The label itself fits its column.
                Assert.True(CellLayout.EstimateTextWidth(label, DialogLayout.FactFontSize) <= labelWidth, label);
            }
        }
    }

    [Fact]
    public void Every_kind_keeps_its_newest_entry_in_a_long_list()
    {
        var entries = new List<ServerBackupView>();
        for (int i = 0; i < 30; i++)
            entries.Add(new ServerBackupView { Id = "s" + i, Kind = WorldBackupEntry.KindBackup, BackupKind = WorldRestorePaths.KindScheduled, Created = Utc0.AddHours(-i).ToString(ServerBackups.LocalFormat) });
        entries.Add(new ServerBackupView { Id = "m", Kind = WorldBackupEntry.KindBackup, BackupKind = WorldRestorePaths.KindManual, Created = Utc0.AddDays(-5).ToString(ServerBackups.LocalFormat) });
        entries.Add(new ServerBackupView { Id = "a", Kind = WorldBackupEntry.KindArchive, Created = Utc0.AddDays(-50).ToString(ServerBackups.LocalFormat) });
        var picked = ServerBackups.PickNewest(entries, ServerBackups.GroupOf, 10);
        Assert.Equal(10, picked.Count);
        Assert.Contains(picked, e => e.Id == "m");
        Assert.Contains(picked, e => e.Id == "a");
        Assert.Contains(picked, e => e.Id == "s0");
        Assert.DoesNotContain(picked, e => e.Id == "s9");
        var state = new ServerBackupsState { Count = entries.Count };
        state.Entries.AddRange(entries);
        var shown = ServerBackupsRows.ListedEntries(state);
        Assert.Equal("m", shown[0].Id);
        Assert.Equal("a", shown[^1].Id);
    }

    [Fact]
    public void The_back_up_now_confirmation_says_where_how_big_and_what_is_included()
    {
        var s = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;
        var c = ServerBackupsRows.BackupNowContent(s);
        Assert.Equal(UiDialogShape.Confirm, c.Shape);
        Assert.Equal("Back up now", c.MainCaption);
        var facts = c.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Contains(Path.Combine("progenitor-backups", "v4", "world1"), facts["Where"]);
        Assert.StartsWith("about ", facts["Size"]);
        Assert.Contains("StartDate.json", facts["Includes"]);
        Assert.Contains("Not included: other mods' files outside the world folder", facts["Includes"]);

        s.JobRunning = true;
        s.JobPhase = "copying 1 of 4 files (20 %)";
        var busy = ServerBackupsRows.BackupNowContent(s);
        Assert.Equal(UiDialogShape.Notice, busy.Shape);
        Assert.Contains("already running", busy.Body);
    }

    [Fact]
    public void The_new_state_and_the_requests_survive_the_wire()
    {
        var req = new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsSet, Key = WorldBackupSettings.KeepDaysKey, Value = "7", Nonce = 4 };
        Assert.True(ServerRulesWire.TryDecodeRequest(ServerRulesWire.EncodeRequest(req), out var back));
        Assert.Equal((req.Op, req.Key, req.Value), (back.Op, back.Key, back.Value));
        Assert.True(ServerRulesWire.TryDecodeRequest(ServerRulesWire.EncodeRequest(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupNow, DryRun = true }), out var now));
        Assert.True(now.DryRun);

        var s = new ServerBackupsState
        {
            ManualBytes = 5, ScheduledBytes = 6, BackupsWhere = "progenitor-backups\\v4\\w", BackupAllowed = true, AtStart = true, EveryHours = 3,
            KeepCount = 4, KeepDays = 9, KeepDaysLockedBy = "server-rules", NextScheduled = "2026-10-07T12:00:00", JobRunning = true, JobKind = "manual",
            JobBy = "Ann", JobPhase = "checking the copy", LastBackupSummary = "Made x.", LastBackupOk = true, LastBackupFinished = "2026-10-07T11:00:00",
            ScheduleFailure = "Not backed up: y.", ScheduleFailureAt = "2026-10-07T10:00:00", AutosaveKnown = true, AutosaveCount = 3, AutosaveBytes = 77,
            AutosaveNewest = "2026-10-07T09:00:00", AutosaveOldest = "2026-10-07T08:00:00", AutosaveKeep = 10, AutosaveEvery = 120,
        };
        s.PruneNextBackup.Add(new ServerBackupView { Id = "backup:x", Name = "Manual backup from x" });
        s.Entries.Add(new ServerBackupView { Id = "backup:m", Kind = "backup", BackupKind = "scheduled", Trigger = "start", Created = "2026-10-07T09:00:00" });
        var r = new ServerRulesResponse { Op = ServerRulesRequest.OpBackups, Ok = true, Backups = s };
        Assert.True(ServerRulesWire.TryDecodeResponse(ServerRulesWire.EncodeResponse(r), out var got));
        var g = got.Backups!;
        Assert.Equal((5L, 6L, "progenitor-backups\\v4\\w", true, true, 3, 4, 9, "server-rules"),
            (g.ManualBytes, g.ScheduledBytes, g.BackupsWhere, g.BackupAllowed, g.AtStart, g.EveryHours, g.KeepCount, g.KeepDays, g.KeepDaysLockedBy));
        Assert.Equal((true, "manual", "Ann", "checking the copy"), (g.JobRunning, g.JobKind, g.JobBy, g.JobPhase));
        Assert.Equal(("Made x.", true, "Not backed up: y."), (g.LastBackupSummary, g.LastBackupOk, g.ScheduleFailure));
        Assert.Equal((true, 3, 77L, 10, 120, "2026-10-07T08:00:00"), (g.AutosaveKnown, g.AutosaveCount, g.AutosaveBytes, g.AutosaveKeep, g.AutosaveEvery, g.AutosaveOldest));
        Assert.Equal("Manual backup from x", Assert.Single(g.PruneNextBackup).Name);
        Assert.Equal(("scheduled", "start"), (g.Entries[0].BackupKind, g.Entries[0].Trigger));
    }

    /// <summary>The real file system with a fixed free space.</summary>
    private sealed class LowSpaceFs : IWorldFs
    {
        private readonly IWorldFs _inner;
        private readonly long _free;

        internal LowSpaceFs(IWorldFs inner, long free)
        {
            _inner = inner;
            _free = free;
        }

        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public bool FileExists(string path) => _inner.FileExists(path);
        public IReadOnlyList<WorldFileInfo> ListFiles(string root) => _inner.ListFiles(root);
        public IReadOnlyList<string> ListDirectories(string path) => _inner.ListDirectories(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public void CopyFile(string from, string to) => _inner.CopyFile(from, to);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
        public void MoveDirectory(string from, string to) => _inner.MoveDirectory(from, to);
        public void MoveFile(string from, string to, bool overwrite) => _inner.MoveFile(from, to, overwrite);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void WriteAllBytes(string path, byte[] bytes) => _inner.WriteAllBytes(path, bytes);
        public DateTime GetLastWriteUtc(string path) => _inner.GetLastWriteUtc(path);
        public void SetLastWriteUtc(string path, DateTime utc) => _inner.SetLastWriteUtc(path, utc);
        public long? FreeBytes(string path) => _free;
        public bool FlushDirectory(string path) => true;
        public void Sleep(int milliseconds) { }
    }
}
