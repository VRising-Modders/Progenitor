using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldRestore;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// BACKUPSDLG1 (live 2026-10-07: a "Restore requested" notice stayed open after the client left and showed on the title
/// screen, without saying which server or world, and its request had meanwhile been cancelled through the bridge). Pinned
/// here: leaving the server closes every Backups dialog and drops answers of that session; the notices and confirmations
/// name the server and the world; and a pending request changed elsewhere redraws the open view from one fresh answer,
/// with no polling. Game-free: the fake UI runtime and a recording requester.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class BackupsDialogSessionTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly object _owner = new();
    private readonly List<ServerRulesRequest> _sent = new();
    private readonly List<Action<ServerRulesResponse?>> _answers = new();

    private const string ArchiveId = "archive:world1_1";

    public BackupsDialogSessionTests()
    {
        Plugin.Logger = new TestLogSource();
        UiDialog.ForgetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        ServerBackupsRows.Reset();
        ServerRulesRows.Reset();
        ServerRulesRows.Requester = (request, answer) =>
        {
            _sent.Add(request);
            _answers.Add(answer);
            return null;
        };
        ServerBackupsRows.DialogFactory = title => UiApi.Panel(title, _owner, SettingsPanel.PluginId);
        ServerBackupsRows.ServerNameProbe = () => "Test server";
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        ServerBackupsRows.Reset();
        ServerRulesRows.Reset();
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private static ServerBackupsState State(string pendingId = "", int countdown = -1)
    {
        var s = new ServerBackupsState
        {
            Count = 1,
            RestoreAllowed = true,
            IntervalWritable = true,
            ServerNow = "2026-10-07T09:30:00",
            SaveName = "world1",
            PendingId = pendingId,
            PendingName = pendingId.Length > 0 ? "the manual backup from Wed, Oct 7, 2026 10:48 AM" : "",
            Countdown = countdown,
        };
        s.Entries.Add(new ServerBackupView
        {
            Id = ArchiveId,
            Kind = WorldBackupEntry.KindArchive,
            KindLabel = "Wipe archive",
            Name = "Wipe archive from Wed, Sep 30, 2026 4:00 AM",
            Label = "the wipe archive from Wed, Sep 30, 2026 4:00 AM",
            Created = "2026-09-30T04:00:00",
            StartDate = "2026-09-20",
            AgeDays = 10,
            Size = 1024,
            Restorable = true,
            Shape = "abc",
        });
        return s;
    }

    private static ServerRulesResponse Answer(string op, bool ok, string note, ServerBackupsState? state) =>
        new() { Op = op, Ok = ok, Note = ok ? note : "", Error = ok ? "" : note, Backups = state };

    private static UiDialog Open() => Assert.IsType<UiDialog>(ServerBackupsRows.OpenDialog());

    // ---- 1: leaving the server -----------------------------------------------------------------------

    [Fact]
    public void Leaving_the_server_closes_every_Backups_dialog_and_drops_answers_of_that_session()
    {
        ServerBackupsRows.SetStateForTests(State());
        Assert.NotNull(ServerBackupsRows.ShowConfirm(ArchiveId, WipeModes.None, null));
        Assert.Equal("confirm-typed", Open().Kind);
        // A restore asked for just before the leave: its answer is still on the way.
        Assert.Null(ServerBackupsRows.CancelPending());
        var late = _answers[^1];
        int session = ServerBackupsRows.Session;

        Assert.Equal(1, ServerBackupsRows.Leave("server-context-cleared"));
        Assert.Null(UiDialog.Current);
        Assert.Null(ServerBackupsRows.State);
        Assert.Equal(session + 1, ServerBackupsRows.Session);

        // The answer (or the "no answer" a leave gives every waiting request) arrives on the title screen: nothing opens.
        late(Answer(ServerRulesRequest.OpRestoreCancel, true, "The restore of world world1 is cancelled.", State()));
        late(null);
        Assert.Null(UiDialog.Current);
        Assert.Null(ServerBackupsRows.State);
        Assert.Equal("", ServerBackupsRows.LastAnswer);
    }

    [Fact]
    public void A_leave_closes_a_notice_already_open_and_leaves_other_dialogs_alone()
    {
        ServerBackupsRows.SetStateForTests(State());
        Assert.Null(ServerBackupsRows.CancelPending());
        _answers[^1](Answer(ServerRulesRequest.OpRestoreCancel, true, "The restore of world world1 is cancelled.", State()));
        var notice = Open();
        Assert.Equal("notice", notice.Kind);
        Assert.Equal(1, ServerBackupsRows.Leave("disconnect"));
        Assert.False(notice.IsOpen);
        Assert.Null(UiDialog.Current);

        // A dialog of another tag is not the Backups' to close.
        var other = UiDialog.Open(title => UiApi.Panel(title, _owner, SettingsPanel.PluginId), "install-restart", "confirm", "restart",
            "Restart the game?", "", "Restart", LabelTone.Normal, "Not now");
        Assert.Equal(0, ServerBackupsRows.Leave("disconnect"));
        Assert.True(other.IsOpen);
        other.Close();
    }

    // ---- 2: the server and the world -----------------------------------------------------------------

    [Fact]
    public void Notices_name_the_server_and_the_server_note_names_the_world()
    {
        ServerBackupsRows.SetStateForTests(State());
        Assert.NotNull(ServerBackupsRows.ShowConfirm(ArchiveId, WipeModes.None, null));
        Open().Submit();
        // The typed word is missing: nothing is sent.
        Assert.Empty(_sent);

        Assert.Null(ServerBackupsRows.CancelPending());
        _answers[^1](Answer(ServerRulesRequest.OpRestoreCancel, true,
            "The restore of world world1 is cancelled: the next restart loads the current world.", State()));
        var notice = Open();
        Assert.Equal("Restore cancelled", notice.Content.Title);
        Assert.Equal(ServerBackupsRows.NoticePendingKey, notice.Key);
        Assert.Contains("On Test server: the restore of world world1 is cancelled", notice.Content.Body);

        Assert.Null(ServerBackupsRows.CancelPending());
        _answers[^1](Answer(ServerRulesRequest.OpRestoreCancel, false, "The BepInEx folder of this server is not known.", State()));
        Assert.Equal("Not cancelled", Open().Content.Title);
        Assert.Contains("On Test server: the BepInEx folder of this server is not known.", Open().Content.Body);
    }

    [Fact]
    public void The_confirmation_the_list_and_back_up_now_name_the_server_and_world_and_the_request_they_replace()
    {
        var s = State(pendingId: "20261007-104800-abcd");
        var e = s.Find(ArchiveId)!;
        var confirm = ServerBackupsRows.ConfirmContent(s, e, ServerBackupsRows.PlanFor(s, e, WipeModes.None, null), null);
        Assert.Equal(ServerBackupsRows.ServerFactLabel, confirm.Facts[0].Label);
        Assert.Equal("Test server, world world1", confirm.Facts[0].Value);
        var replaces = Assert.Single(confirm.Facts, f => f.Label == "Replaces");
        Assert.Equal("Restore of the manual backup from Wed, Oct 7, 2026 10:48 AM at the next restart.", replaces.Value);

        Assert.Equal("Test server, world world1", ServerBackupsRows.ListContent(s).Facts[0].Value);
        Assert.Contains(ServerBackupsRows.DetailContent(s, e).Facts, f => f.Label == ServerBackupsRows.PendingLabel);
        s.BackupAllowed = true;
        Assert.Equal("Test server, world world1", ServerBackupsRows.BackupNowContent(s).Facts[0].Value);

        // An older server sends no save name: the next start's SaveName from the rules state stands in; no names, no line.
        ServerBackupsRows.ServerNameProbe = () => "";
        s.SaveName = "";
        Assert.DoesNotContain(ServerBackupsRows.ListContent(s).Facts, f => f.Label == ServerBackupsRows.ServerFactLabel);
    }

    [Fact]
    public void The_wording_helpers_keep_the_server_sentence_and_lower_only_its_first_letter()
    {
        Assert.Equal("On Test server: world world1 goes back to the manual backup.", ServerBackups.OnServer("Test server", "World world1 goes back to the manual backup."));
        Assert.Equal("World world1 goes back.", ServerBackups.OnServer("", "World world1 goes back."));
        Assert.Equal("On X: EOS is not ready.", ServerBackups.OnServer("X", "EOS is not ready."));
        Assert.Equal("Test server, world world1", ServerBackups.WhereLine("Test server", "world1"));
        Assert.Equal("world world1", ServerBackups.WhereLine("", "world1"));
        Assert.Equal("Test server", ServerBackups.WhereLine("Test server", ""));
        Assert.Equal("", ServerBackups.WhereLine(null, null));
        Assert.Equal("the world", ServerBackupsServer.WorldWords(""));
        Assert.Equal("world world1", ServerBackupsServer.WorldWords("world1"));
    }

    // ---- 3: a request changed elsewhere --------------------------------------------------------------

    [Theory]
    [InlineData("list", ArchiveId, "List")]
    [InlineData("detail", ArchiveId, "Detail")]
    [InlineData("detail", "archive:gone", "List")]
    [InlineData("confirm-typed", ArchiveId, "Detail")]
    [InlineData("confirm-typed", "archive:gone", "List")]
    [InlineData("wipe", ArchiveId, "None")]
    [InlineData("wipe-date", "archive:gone", "List")]
    [InlineData("notice", ServerBackupsRows.NoticePendingKey, "List")]
    [InlineData("notice", "", "None")]
    [InlineData("backup-now", "", "None")]
    public void An_open_view_whose_request_changed_falls_back_to_a_view_that_matches(string kind, string key, string expectedName)
    {
        var expected = Enum.Parse<ServerBackupsRows.Refresh>(expectedName);
        var now = State();
        var (action, note) = ServerBackupsRows.PlanRefresh(kind, key, "20261007-104800-abcd", now);
        Assert.Equal(expected, action);
        if (expected != ServerBackupsRows.Refresh.None) Assert.Equal("Changed on the server: no restore is pending now.", note);
        // Drawn with the request the server has now: nothing to redraw.
        Assert.Equal(ServerBackupsRows.Refresh.None, ServerBackupsRows.PlanRefresh(kind, key, ServerBackups.PendingSignature(now), now).Action);
    }

    [Fact]
    public void A_countdown_that_starts_or_stops_is_a_change_and_the_note_says_what_waits_now()
    {
        var running = State(pendingId: "r1", countdown: 40);
        Assert.NotEqual(ServerBackups.PendingSignature(State(pendingId: "r1")), ServerBackups.PendingSignature(running));
        Assert.Equal("Changed on the server: restore of the manual backup from Wed, Oct 7, 2026 10:48 AM when the server stops in 40 s.",
            ServerBackups.ChangedNote(running));
    }

    [Fact]
    public void A_push_redraws_an_open_notice_from_one_fresh_answer_and_asks_nothing_when_no_dialog_is_open()
    {
        // No Backups dialog open: nothing is asked (the rows ask on their next draw, only while the page shows them).
        Assert.Equal("no Backups dialog open", ServerBackupsRows.OnServerChanged("r1", false, "written"));
        Assert.Empty(_sent);

        // The live case: the notice of a request written here, then the request cancelled elsewhere.
        ServerBackupsRows.SetStateForTests(State());
        Assert.Null(ServerBackupsRows.CancelPending());
        _answers[^1](Answer(ServerRulesRequest.OpRestore, true, "World world1 goes back to the manual backup.", State(pendingId: "r1")));
        Assert.Equal(ServerBackupsRows.NoticePendingKey, Open().Key);
        int sent = _sent.Count;

        // The push for the request this notice already shows changes nothing.
        Assert.Equal("the open view is current", ServerBackupsRows.OnServerChanged("r1", false, "written"));
        Assert.Equal(sent, _sent.Count);

        Assert.StartsWith("asked again", ServerBackupsRows.OnServerChanged("", false, "cancelled"));
        Assert.Equal(sent + 1, _sent.Count);
        Assert.Equal(ServerRulesRequest.OpBackups, _sent[^1].Op);
        _answers[^1](Answer(ServerRulesRequest.OpBackups, true, "", State()));
        var list = Open();
        Assert.Equal("list", list.Kind);
        Assert.Equal("Changed on the server: no restore is pending now.", list.Content.Note);
        Assert.Contains("the notice view shows the list again", (string)ServerBackupsRows.Readback()["lastPush"]!);
    }

    [Fact]
    public void A_push_turns_a_typed_confirmation_for_an_old_request_back_into_the_restore_point()
    {
        ServerBackupsRows.SetStateForTests(State());
        Assert.NotNull(ServerBackupsRows.ShowConfirm(ArchiveId, WipeModes.None, null));
        Open().SetText("REST");
        ServerBackupsRows.OnServerChanged("r2", true, "written, restart now");
        _answers[^1](Answer(ServerRulesRequest.OpBackups, true, "", State(pendingId: "r2", countdown: 55)));
        var detail = Open();
        Assert.Equal("detail", detail.Kind);
        Assert.Equal(ArchiveId, detail.Key);
        Assert.StartsWith("Changed on the server: restore of the manual backup", detail.Content.Note);
        Assert.Contains(detail.Content.Facts, f => f.Label == ServerBackupsRows.PendingLabel && f.Value.Contains("stops in 55 s"));
    }

    [Fact]
    public void A_fresh_answer_after_a_leave_redraws_nothing()
    {
        ServerBackupsRows.SetStateForTests(State());
        Assert.NotNull(ServerBackupsRows.ShowList());
        ServerBackupsRows.OnServerChanged("r3", false, "written");
        var answer = _answers[^1];
        ServerBackupsRows.Leave("disconnect");
        answer(Answer(ServerRulesRequest.OpBackups, true, "", State(pendingId: "r3")));
        Assert.Null(UiDialog.Current);
        Assert.Null(ServerBackupsRows.State);
    }
}
