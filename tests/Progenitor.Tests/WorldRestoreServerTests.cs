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
/// WORLDRESTORE1: the Server rules channel's restore operations (an admin lists the restore points, asks for a restore with a
/// dry run or for real, cancels it, sets the keep count), their wire round trip, and the client's dialog contents (the list,
/// the three wipe answers, the typed RESTORE confirmation). Real temp folders; game-free.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class WorldRestoreServerTests : IDisposable
{
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);
    private static readonly DateTime NowLocal = new(2026, 10, 7, 9, 30, 0);

    private readonly string _dir;
    private readonly string _data;
    private readonly string _save;
    private readonly string _archive;
    private readonly string _bepinex;
    private readonly List<string> _audit = new();
    private int _keep = 5;
    private double _now = 1000;

    public WorldRestoreServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-worldrestore-srv-" + Guid.NewGuid().ToString("N"));
        _data = Path.Combine(_dir, "data");
        _save = Path.Combine(_data, "Saves", "v4", "world1");
        _archive = Path.Combine(_data, "Saves", "v4", "world1_" + new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Local).Ticks);
        _bepinex = Path.Combine(_dir, "server", "BepInEx");
        Directory.CreateDirectory(Path.Combine(_data, "Settings"));
        Directory.CreateDirectory(_save);
        Directory.CreateDirectory(_archive);
        Directory.CreateDirectory(_bepinex);
        File.WriteAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json"),
            "{\n  \"Name\": \"Test\",\n  \"SaveName\": \"world1\",\n  \"ResetDaysInterval\": 30\n}\n");
        File.WriteAllBytes(Path.Combine(_save, "AutoSave_2.save.gz"), FakeWorldFs.Gzip("now"));
        File.WriteAllText(Path.Combine(_save, "StartDate.json"), "09/20/2026");
        File.WriteAllBytes(Path.Combine(_archive, "AutoSave_1.save.gz"), FakeWorldFs.Gzip("then"));
        File.SetLastWriteTimeUtc(Path.Combine(_archive, "AutoSave_1.save.gz"), new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc));
        File.WriteAllText(Path.Combine(_archive, "StartDate.json"), "07/01/2026");

        Plugin.Logger = new TestLogSource();
        ServerRulesServer.Uninstall();
        ServerSettingsLock.Reset();
        ServerRulesServer.Audit = _audit.Add;
        ServerRulesServer.Now = () => _now;
        ServerRulesServer.FactsProvider = Facts;
        ServerBackupsServer.SaveFolderProbe = () => _save;
        ServerBackupsServer.BepInExRootProbe = () => _bepinex;
        ServerBackupsServer.KeepProbe = () => _keep;
        ServerBackupsServer.KeepWrite = n =>
        {
            _keep = n;
            return null;
        };
        ServerBackupsServer.OnlineProbe = () => 2;
        ServerBackupsServer.ArgsProbe = () => new[] { "VRisingServer.exe", "-persistentDataPath", _data, "-saveName", "world1" };
        ServerBackupsServer.NowLocal = () => NowLocal;
        ServerBackupsServer.NowUtc = () => new DateTime(2026, 10, 7, 13, 30, 0, DateTimeKind.Utc);
        ServerBackupsRows.Reset();
    }

    public void Dispose()
    {
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
            Args = new[] { "-persistentDataPath", _data },
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
            StartDateText = File.ReadAllText(Path.Combine(_save, "StartDate.json")),
            NowLocal = NowLocal,
            LockedBy = ServerSettingsLock.LockedBy,
        };
    }

    private ServerRulesResponse Ask(ServerRulesRequest request, RemoteInstallActor? actor = null)
    {
        _now += 10;
        var response = ServerRulesServer.Apply(request, handshakeOk: true, actor ?? Admin);
        // Every answer travels on the wire.
        Assert.True(ServerRulesWire.TryDecodeResponse(ServerRulesWire.EncodeResponse(response), out var decoded));
        return decoded;
    }

    private string ArchiveId => "archive:" + Path.GetFileName(_archive);

    [Fact]
    public void An_admin_reads_the_restore_points_with_sizes_and_the_wipe_facts_and_a_player_is_refused()
    {
        var r = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups });
        Assert.True(r.Ok, r.Error);
        var b = r.Backups!;
        Assert.True(b.RestoreAllowed, b.RestoreRefusal);
        var e = Assert.Single(b.Entries);
        Assert.Equal(ArchiveId, e.Id);
        Assert.Equal("Wipe archive", e.KindLabel);
        Assert.Equal("2026-07-01", e.StartDate);
        Assert.Equal(91, e.AgeDays);
        Assert.True(e.Size > 0);
        Assert.Equal(30, b.NextInterval);
        Assert.True(b.IntervalWritable, b.IntervalReason);
        Assert.Equal(2, b.Online);
        Assert.Equal(5, b.Keep);
        Assert.Equal("2026-10-07T09:30:00", b.ServerNow);
        Assert.Empty(_audit);

        var refused = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, Player);
        Assert.False(refused.Ok);
        Assert.Equal(ServerRulesServer.BackupsAdminError, refused.Error);
        Assert.Null(refused.Backups);
    }

    [Fact]
    public void A_due_archive_needs_a_wipe_answer_a_dry_run_writes_nothing_and_a_request_writes_the_marker()
    {
        var shape = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!.Entries[0].Shape;
        var none = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = shape });
        Assert.False(none.Ok);
        Assert.Contains("wipe date has passed", none.Error);

        var dry = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = shape, Wipe = WipeModes.WipesOff, DryRun = true });
        Assert.True(dry.Ok, dry.Error);
        Assert.StartsWith("Dry run:", dry.Note);
        Assert.Contains("Off", dry.Note);
        Assert.False(File.Exists(WorldRestoreMarker.RequestPath(_bepinex)));

        var real = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = shape, Wipe = WipeModes.Date, Value = "2026-10-20" });
        Assert.True(real.Ok, real.Error);
        Assert.Contains("Tue, Oct 20, 2026", real.Note);
        // BACKUPSDLG1: the note names the world it acts on.
        Assert.StartsWith("World world1 goes back to the wipe archive from Wed, Sep 30, 2026 4:00 AM at the next restart", real.Note);
        Assert.Equal("world1", real.Backups!.SaveName);
        var marker = WorldRestoreMarker.ReadPending(PhysicalWorldFs.Instance, _bepinex, out _)!;
        Assert.Equal(WipeModes.Date, marker.WipeMode);
        Assert.Equal(111, marker.WipeIntervalDays);
        Assert.Equal(Path.Combine(_data, "Settings", "ServerHostSettings.json"), marker.HostSettingsPath);
        Assert.Equal(_save, marker.SaveFolder);
        Assert.Equal(_archive, marker.SourcePath);
        Assert.Equal(shape, marker.ExpectShape);
        Assert.Equal("Admin", marker.RequestedBy);
        Assert.Equal(5, marker.KeepBackups);
        Assert.Equal(marker.RequestId, real.Backups!.PendingId);
        Assert.Equal("Restore of the wipe archive from Wed, Sep 30, 2026 4:00 AM at the next restart (next wipe Tue, Oct 20, 2026).", ServerBackups.PendingLine(real.Backups));
        Assert.Contains(_audit, l => l.Contains("WorldRestore") && l.Contains("wipe=date"));

        var cancel = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestoreCancel });
        Assert.True(cancel.Ok);
        Assert.StartsWith("The restore of world world1 is cancelled", cancel.Note);
        Assert.False(File.Exists(WorldRestoreMarker.RequestPath(_bepinex)));
        Assert.Equal("", cancel.Backups!.PendingId);
    }

    [Fact]
    public void A_changed_or_unknown_restore_point_a_player_and_the_live_server_are_refused()
    {
        Assert.Contains("changed since", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = "nope", Wipe = WipeModes.WipesOff }).Error);
        Assert.Contains("not there", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = "archive:world1_1", Wipe = WipeModes.WipesOff }).Error);
        Assert.Equal(ServerRulesServer.BackupsAdminError, Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId }, Player).Error);
        ServerBackupsServer.ProcessPathProbe = () => @"C:\VRisingServer\VRisingServer.exe";
        var live = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Wipe = WipeModes.WipesOff });
        Assert.Contains("live server", live.Error);
        Assert.False(live.Backups!.RestoreAllowed);
        Assert.False(File.Exists(WorldRestoreMarker.RequestPath(_bepinex)));
    }

    [Fact]
    public void Restart_now_starts_the_countdown_or_says_why_not()
    {
        string? started = null;
        ServerBackupsServer.StartRestartCountdown = id =>
        {
            started = id;
            return null;
        };
        var r = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Wipe = WipeModes.ClockToday, RestartNow = true });
        Assert.True(r.Ok, r.Error);
        Assert.NotNull(started);
        Assert.True(r.Note.Contains("host's own restart must bring it back"), r.Note);
        Assert.True(WorldRestoreMarker.ReadPending(PhysicalWorldFs.Instance, _bepinex, out _)!.RestartNow);

        ServerBackupsServer.StartRestartCountdown = _ => "this server cannot save and stop itself";
        var later = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Wipe = WipeModes.ClockToday, RestartNow = true });
        Assert.True(later.Ok);
        Assert.Contains("waits for the next restart", later.Note);
        Assert.Contains("replaces the earlier request", later.Note);
    }

    /// <summary>
    /// BACKUPSDLG1: a written or cancelled request (in game or through the dev bridge) raises the pending-change event the
    /// Server rules channel pushes to admins; a dry run, a refusal or a cancel with nothing pending does not.
    /// </summary>
    [Fact]
    public void A_written_or_cancelled_request_raises_the_pending_change_and_a_dry_run_or_an_empty_cancel_does_not()
    {
        var changes = new List<BackupsPendingChange>();
        ServerBackupsServer.PendingChanged += changes.Add;
        var shape = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!.Entries[0].Shape;
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = shape, Wipe = WipeModes.WipesOff, DryRun = true }).Ok);
        Assert.False(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = "nope", Wipe = WipeModes.WipesOff }).Ok);
        Assert.Empty(changes);

        var real = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = shape, Wipe = WipeModes.ClockToday });
        Assert.True(real.Ok, real.Error);
        var written = Assert.Single(changes);
        Assert.Equal(real.Backups!.PendingId, written.PendingId);
        Assert.Equal("written", written.Why);
        Assert.False(written.Countdown);

        // The dev bridge's cancel takes the same path as the in-game one.
        changes.Clear();
        var bridge = ServerBackupsTestBridge.ServerCancel();
        Assert.True((bool)bridge["ok"]!);
        Assert.StartsWith("The restore of world world1 is cancelled", (string)bridge["note"]!);
        var cancelled = Assert.Single(changes);
        Assert.Equal("", cancelled.PendingId);
        Assert.Equal("cancelled", cancelled.Why);

        changes.Clear();
        Assert.Equal("No restore was pending for world world1.", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestoreCancel }).Note);
        Assert.Empty(changes);

        // A countdown stopped by a cancel is a change too (the request may already be gone).
        bool stopped = false;
        ServerBackupsServer.CountdownProbe = () => stopped ? -1 : 42;
        ServerBackupsServer.CancelCountdown = () => stopped = true;
        Assert.True(Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestoreCancel }).Ok);
        Assert.True(stopped);
        Assert.Equal("cancelled", Assert.Single(changes).Why);
    }

    /// <summary>BACKUPSDLG1: the push is a small packet older clients drop, sent to every admin who read the Backups but the one who caused it.</summary>
    [Fact]
    public void The_pending_change_push_round_trips_and_skips_the_admin_whose_request_caused_it()
    {
        var bytes = ServerRulesWire.EncodeBackupsChanged("20261007-134800-abcd", true, "written, restart now");
        Assert.Equal(ServerRulesWire.KindBackupsChanged, ServerRulesWire.KindOf(bytes));
        Assert.True(ServerRulesWire.TryDecodeBackupsChanged(bytes, out string id, out bool countdown, out string why));
        Assert.Equal("20261007-134800-abcd", id);
        Assert.True(countdown);
        Assert.Equal("written, restart now", why);
        Assert.False(ServerRulesWire.TryDecodeResponse(bytes, out _));
        Assert.False(ServerRulesWire.TryDecodeChanged(bytes, out _));

        // Only an admin answered a Backups state is told; a player who read the rules is not.
        var admin = ServerRulesServer.Apply(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }, true, Admin);
        var player = ServerRulesServer.Apply(new ServerRulesRequest { Op = ServerRulesRequest.OpGet }, true, Player);
        ServerRulesServer.NoteAnsweredForTests(3, admin);
        ServerRulesServer.NoteAnsweredForTests(4, player);
        ServerRulesServer.NoteAnsweredForTests(7, admin);
        Assert.Equal(new[] { 3, 7 }, ServerRulesServer.BackupsAskedForTests.OrderBy(i => i).ToArray());
        Assert.Equal(new List<int> { 7 }, ServerRulesServer.BackupsRecipients(ServerRulesServer.BackupsAskedForTests, except: 3));
        Assert.Equal(new List<int> { 3, 7 }, ServerRulesServer.BackupsRecipients(ServerRulesServer.BackupsAskedForTests, except: -1));
    }

    [Fact]
    public void The_keep_count_is_checked_and_stored()
    {
        Assert.Contains("2 to 100", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsKeep, Value = "101" }).Error);
        var ok = Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackupsKeep, Value = "0" });
        Assert.True(ok.Ok, ok.Error);
        Assert.Equal(0, _keep);
        Assert.Equal(0, ok.Backups!.Keep);
        Assert.Contains("every Progenitor backup", ok.Note);
    }

    [Fact]
    public void The_page_warns_which_backups_the_next_restore_would_delete()
    {
        string root = Path.Combine(_data, "progenitor-backups", "v4", "world1");
        for (int i = 1; i <= 3; i++)
        {
            string dir = Path.Combine(root, "2026090" + i + "-120000-before-restore");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "AutoSave_1.save.gz"), FakeWorldFs.Gzip("b" + i));
            var side = new BackupSidecar { CreatedUtc = new DateTime(2026, 9, i, 12, 0, 0, DateTimeKind.Utc), RestoreOf = "the wipe archive from x" };
            File.WriteAllBytes(Path.Combine(dir, WorldRestorePaths.SidecarName), side.ToJson());
        }

        _keep = 2;
        var s = State();
        // The next restore makes one more: kept are it and Sep 3; Sep 2 goes; Sep 1 is the chain's oldest.
        var doomed = Assert.Single(s.PruneNext);
        Assert.Equal("backup:20260902-120000-before-restore", doomed.Id);
        var deletes = Assert.Single(ServerBackupsRows.ListContent(s).Facts, f => f.Label == ServerBackupsRows.DeletesLabel);
        Assert.StartsWith("Before restore of the wipe archive from x", deletes.Value);
        Assert.Equal(DialogFactTone.Warn, deletes.Tone);
        var archive = s.Entries.First(e => e.Kind == WorldBackupEntry.KindArchive);
        var c = ServerBackupsRows.ConfirmContent(s, archive, ServerBackupsRows.PlanFor(s, archive, WipeModes.ClockToday, null), null);
        Assert.Contains(c.Facts, f => f.Label == "Retention" && f.Value.Contains("20260902") == false && f.Value.Contains("deletes"));
        // Restoring that backup itself never deletes it.
        var source = s.Find("backup:20260902-120000-before-restore")!;
        Assert.Equal("", ServerBackupsRows.PruneWarning(s, source.Id));
    }

    [Fact]
    public void A_hosted_game_lists_its_restore_points_but_refuses_a_restore()
    {
        ServerBackupsServer.ArgsProbe = () => new[] { "VRisingServer.exe", "-userSave", "-saveName", "world1" };
        var s = State();
        Assert.False(s.RestoreAllowed);
        Assert.Contains("hosted private game", s.RestoreRefusal);
        Assert.Single(s.Entries);
        Assert.Contains("hosted private game", Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Wipe = WipeModes.ClockToday }).Error);
    }

    [Fact]
    public void The_request_fields_travel_on_the_wire()
    {
        var request = new ServerRulesRequest
        {
            Nonce = 7, Op = ServerRulesRequest.OpRestore, Key = ArchiveId, Shape = "abc", Wipe = WipeModes.Date, Value = "2026-10-20",
            RestartNow = true, DryRun = true,
        };
        Assert.True(ServerRulesWire.TryDecodeRequest(ServerRulesWire.EncodeRequest(request), out var back));
        Assert.Equal((request.Op, request.Key, request.Shape, request.Wipe, request.Value, request.RestartNow, request.DryRun),
            (back.Op, back.Key, back.Shape, back.Wipe, back.Value, back.RestartNow, back.DryRun));
    }

    // ---- the client's dialogs --------------------------------------------------------------------------------

    private ServerBackupsState State() => Ask(new ServerRulesRequest { Op = ServerRulesRequest.OpBackups }).Backups!;

    [Fact]
    public void The_list_shows_kind_date_age_and_size_with_the_disk_use_and_the_retention_rule()
    {
        var s = State();
        var list = ServerBackupsRows.ListContent(s);
        Assert.Equal(UiDialogShape.Choose, list.Shape);
        Assert.True(list.OptionsAsRows);
        Assert.Equal("Wipe archive · Wed, Sep 30, 2026 4:00 AM", Assert.Single(list.Options));
        Assert.StartsWith("World age: day 91 · ", Assert.Single(list.OptionNotes));
        Assert.Equal(ServerBackupsRows.ArchivesHeading, Assert.Single(list.OptionHeadings).Text);
        Assert.Equal("", list.Body);
        var facts = list.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.StartsWith("Restore points ", facts[ServerBackupsRows.DiskLabel]);
        Assert.Contains(" · current world ", facts[ServerBackupsRows.DiskLabel]);
        Assert.Equal("newest 5 backups made before a restore; wipe archives are never deleted", facts[ServerBackupsRows.KeptLabel]);
        Assert.False(facts.ContainsKey(ServerBackupsRows.DeletesLabel));
        Assert.Equal("1 restore point, " + WorldBackupCatalog.FormatBytes(s.ArchiveBytes), ServerBackups.Summary(s));

        var detail = ServerBackupsRows.DetailContent(s, s.Entries[0]);
        Assert.Equal("Restore this world", detail.MainCaption);
        Assert.Contains(detail.Facts, f => f.Label == "World age" && f.Value.StartsWith("day 91"));
    }

    [Fact]
    public void A_due_world_offers_the_three_answers_and_only_the_clock_one_when_the_interval_is_not_writable()
    {
        var s = State();
        var e = s.Entries[0];
        var plan = ServerBackupsRows.PlanFor(s, e, WipeModes.None, null);
        Assert.True(plan.Due);
        var wipe = ServerBackupsRows.WipeContent(s, e, plan);
        Assert.Equal(new[] { ServerBackups.WipeClockLabel, ServerBackups.WipesOffLabel, ServerBackups.PickDateLabel }, wipe.Options);
        Assert.Contains("Fri, Jul 31, 2026", wipe.Body);

        s.IntervalWritable = false;
        s.IntervalReason = "the start command sets it.";
        var only = ServerBackupsRows.WipeContent(s, e, plan);
        Assert.Equal(new[] { ServerBackups.WipeClockLabel }, only.Options);
        Assert.Contains("the start command sets it.", only.Lines.Single());

        var date = ServerBackupsRows.DateContent(s, e, "");
        Assert.Equal("Next wipe", date.DateCaption);
        Assert.Equal(new DateTime(2026, 10, 8), ServerBackupsRows.DayOf(date.DateValue));
    }

    [Fact]
    public void The_typed_confirmation_spells_out_the_backup_the_state_what_happens_and_the_next_wipe()
    {
        var s = State();
        var e = s.Entries[0];
        var plan = ServerBackupsRows.PlanFor(s, e, WipeModes.ClockToday, null);
        var c = ServerBackupsRows.ConfirmContent(s, e, plan, null);
        Assert.Equal(UiDialogShape.Prompt, c.Shape);
        Assert.Equal("RESTORE", c.ConfirmWord);
        Assert.True(c.ExtrasNeedWord);
        Assert.False(c.Unlocks("RESTOR"));
        Assert.True(c.Unlocks(" RESTORE "));
        Assert.Equal("Restore at next restart", c.MainCaption);
        Assert.Equal("Restore and restart now", c.ExtraButtons.Single().Caption);
        // Until the word is typed, both answers are drawn disabled.
        Assert.All(c.RowButtons().Where(b => b.Role != DialogButtonRole.Cancel), b => Assert.Equal(CellTone.Disabled, b.Tone));
        var facts = c.Facts.ToDictionary(f => f.Label, f => f.Value);
        Assert.Contains("Before restore of the wipe archive from Wed, Sep 30, 2026 4:00 AM, Wed, Oct 7, 2026 9:30 AM", facts["Backup first"]);
        Assert.Contains("day 91", facts["Restores"]);
        Assert.StartsWith("Wed, Oct 7, 2026 (the wipe clock restarts", facts["World started"]);
        Assert.Contains("2 players are online now", facts["What happens"]);
        Assert.StartsWith("Fri, Nov 6, 2026", facts["Next wipe after"]);
        Assert.Contains("host's own restart", facts["Restart now"]);
        Assert.Equal("Type RESTORE to confirm.", c.Note);
    }

    [Fact]
    public void The_dialog_date_is_midnight_in_the_server_zone()
    {
        ServerBackupsRows.ServerZone = () => TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(-4), "t", "t");
        string iso = ServerBackupsRows.IsoOf(new DateTime(2026, 10, 20));
        Assert.Equal("2026-10-20T04:00:00Z", iso);
        Assert.Equal(new DateTime(2026, 10, 20), ServerBackupsRows.DayOf(iso));
    }
}
