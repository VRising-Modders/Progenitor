using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldRestore;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// BACKUPROWS1 (live 2026-10-19, release 2026.10.133): after Back up now, the server finished in 2 s but the page's Last
/// backup row stayed on "Manual backup by Guide: saving the world…" for over 40 s, and Backups on "No restore points yet".
/// The rows asked for news only from their show-if, which runs when the page is re-evaluated. Pinned here: while a backup
/// runs, the rows' own lines ask (at most every 5 s) and show the result from the answer; once it is done they stop
/// asking; and the server's "backup finished" push asks once with no dialog open. Game-free: a recording requester.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class BackupRowsRefreshTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly List<ServerRulesRequest> _sent = new();
    private readonly List<Action<ServerRulesResponse?>> _answers = new();
    private double _now = 1000;

    public BackupRowsRefreshTests()
    {
        Plugin.Logger = new TestLogSource();
        UiDialog.ForgetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        ServerBackupsRows.Reset();
        ServerRulesRows.Reset();
        ServerBackupsRows.Clock = () => _now;
        ServerRulesRows.Requester = (request, answer) =>
        {
            _sent.Add(request);
            _answers.Add(answer);
            return null;
        };
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        ServerBackupsRows.Reset();
        ServerRulesRows.Reset();
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private static ServerBackupsState Running() => new()
    {
        Count = 0,
        BackupAllowed = true,
        JobRunning = true,
        JobKind = WorldRestorePaths.KindManual,
        JobBy = "Guide",
        JobPhase = "saving the world",
    };

    private static ServerBackupsState Finished()
    {
        var s = new ServerBackupsState
        {
            Count = 1,
            BackupAllowed = true,
            LastBackupOk = true,
            LastBackupSummary = "Made Manual backup from Mon, Oct 19, 2026 1:38 AM: 19 files, 62.1 MB.",
        };
        s.Entries.Add(new ServerBackupView
        {
            Id = "backup:20261019-013841-manual",
            Kind = WorldBackupEntry.KindBackup,
            BackupKind = WorldRestorePaths.KindManual,
            KindLabel = "Manual backup",
            Name = "Manual backup from Mon, Oct 19, 2026 1:38 AM",
            Created = "2026-10-19T01:38:41",
            Size = 62_100_000,
            Restorable = true,
        });
        return s;
    }

    private static ServerRulesResponse Answer(ServerBackupsState state) =>
        new() { Op = ServerRulesRequest.OpBackups, Ok = true, Backups = state };

    private int Asks()
    {
        int n = 0;
        foreach (var r in _sent) if (r.Op == ServerRulesRequest.OpBackups) n++;
        return n;
    }

    [Fact]
    public void While_a_backup_runs_the_rows_ask_for_news_and_show_the_result_then_stop_asking()
    {
        ServerBackupsRows.SetStateForTests(Running());
        Assert.Equal("Manual backup by Guide: saving the world…", ServerBackupsRows.LastBackupLine());
        Assert.Equal(1, Asks());

        // The lines repaint every second; the server is asked at most every 5 s.
        _now += 1;
        ServerBackupsRows.LastBackupLine();
        ServerBackupsRows.SummaryLine();
        Assert.Equal(1, Asks());
        _now += ServerBackupsRows.AskEverySeconds;
        ServerBackupsRows.SummaryLine();
        Assert.Equal(2, Asks());

        // The answer after the backup finished: both rows show it on their next repaint.
        _answers[^1](Answer(Finished()));
        Assert.Equal("Made Manual backup from Mon, Oct 19, 2026 1:38 AM: 19 files, 62.1 MB.", ServerBackupsRows.LastBackupLine());
        Assert.NotEqual("No restore points yet", ServerBackupsRows.SummaryLine());
        Assert.Equal(ServerBackups.Summary(Finished()), ServerBackupsRows.SummaryLine());

        // Nothing runs now: the repaints ask nothing more.
        _now += 60;
        ServerBackupsRows.LastBackupLine();
        ServerBackupsRows.SummaryLine();
        Assert.Equal(2, Asks());
    }

    /// <summary>BACKUPROWS1: the Backups popup's first group heading and its Close keep the kit's gap from the rows next to them.</summary>
    [Fact]
    public void The_backups_list_keeps_a_gap_above_its_first_heading_and_above_Close()
    {
        var content = ServerBackupsRows.ListContent(Finished());
        Assert.True(content.OptionsAsRows);
        Assert.Equal(ServerBackupsRows.ManualHeading, Assert.Single(content.OptionHeadings).Text);
        Assert.Equal(DialogLayout.OptionHeadingGap, DialogLayout.OptionHeadingRowSpec("h", ServerBackupsRows.ManualHeading, first: true).GapAbove);
        Assert.Equal(DialogLayout.OptionHeadingGap, DialogLayout.OptionHeadingRowSpec("h", ServerBackupsRows.ScheduledHeading, first: false).GapAbove);
        Assert.Equal(DialogLayout.OptionListEndGap, DialogLayout.ButtonRowGapAbove(content));
        Assert.Equal(DialogLayout.OptionListEndGap,
            DialogLayout.ButtonRowSpec("b", content.RowButtons(), DialogLayout.RowWidth, DialogLayout.ButtonRowGapAbove(content)).GapAbove);
        // A dialog without a row list keeps the ordinary gap.
        Assert.Equal(-1f, DialogLayout.ButtonRowGapAbove(ServerBackupsRows.BackupNowContent(Finished())));
    }

    [Fact]
    public void The_backup_finished_push_asks_once_with_no_dialog_open_and_a_restore_push_still_asks_nothing()
    {
        ServerBackupsRows.SetStateForTests(Finished());
        // A change of the restore request, no dialog open: as before, nothing is asked (the rows ask on their next draw).
        Assert.Equal("no Backups dialog open", ServerBackupsRows.OnServerChanged("r1", false, "written"));
        Assert.Equal(0, Asks());

        // A backup finished: one request, and its answer is the rows' state.
        string result = ServerBackupsRows.OnServerChanged("", false, ServerBackups.BackupFinishedWhy);
        Assert.Equal("no Backups dialog open; asked for the backup's result", result);
        Assert.Equal(1, Asks());
        var fresh = Finished();
        fresh.LastBackupSummary = "Made Manual backup from Mon, Oct 19, 2026 2:00 AM: 19 files, 62.2 MB.";
        _answers[^1](Answer(fresh));
        Assert.Equal(fresh.LastBackupSummary, ServerBackupsRows.LastBackupLine());

        // Any push while this page last heard of a running backup asks too (an older server sends no "finished" reason).
        ServerBackupsRows.SetStateForTests(Running());
        Assert.StartsWith("no Backups dialog open; asked", ServerBackupsRows.OnServerChanged("", false, "cancelled"));
        Assert.Equal(2, Asks());
    }
}
