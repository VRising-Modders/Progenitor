using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Progenitor.Hosted;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SERVERINST4: the graceful <b>Stop server</b> end to end, offline. Fakes and byte fixtures only —
/// nothing here starts, stops, opens, signals or reads a real process, and no packet leaves the
/// test assembly.
///
/// <para>Each test names the revert that breaks it, because the value of this file is that the four
/// things SERVERINST4 added cannot silently come undone: the correlated reply (so a refusal is not
/// read as silence), the admin check <em>before</em> the save path, the save confirmation before the
/// quit, and the PEB decode that puts real arguments in the launch record.</para>
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class StopServerTransportTests : IDisposable
{
    private readonly string _root;
    private readonly string _server;
    private readonly string _exe;
    private DateTime _now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);

    public StopServerTransportTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-stopwire-" + Guid.NewGuid().ToString("N"));
        _server = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(_server, "BepInEx", "core", "BepInEx.Core.dll"), "bepinex");
        _exe = Path.Combine(_server, LocalServerFolder.ServerExecutableName);
        File.WriteAllText(_exe, "exe");

        ServerInstall.RunningServerProbe = _ => null;
        ServerProcessControl.ResetForTests();
        ServerStopCommand.ResetForTests();
        StopServerEvents.ResetForTests();
        WorldSaveTrigger.ResetForTests();
        ServerProcessControl.ClockUtc = () => _now;
        ServerProcessControl.SleepMs = _ => _now = _now.AddSeconds(1);
        ServerProcessControl.ProgenitorHandshakeOk = () => true;
        LocalServerFolder.WriteAccessOverride = _ => true;
        InstallLedger();
        HostSettings(9976);
        // Review M1 (2026-10-08): the folder's server (pid 4242) is the process bound to the joined port.
        ServerProcessControl.UdpOwnerProbe = _ => new[] { 4242 };
    }

    public void Dispose()
    {
        ServerInstall.RunningServerProbe = null;
        ServerProcessControl.ResetForTests();
        ServerStopCommand.ResetForTests();
        StopServerEvents.ResetForTests();
        WorldSaveTrigger.ResetForTests();
        LocalServerFolder.WriteAccessOverride = null;
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    // --- client: request, reply, states ---------------------------------------------------------

    [Fact]
    public void An_accepted_reply_walks_requested_replied_saving_stopped_and_records_the_real_arguments()
    {
        bool running = true;
        var states = new List<string>();
        ServerProcessControl.StopProgress = s => states.Add(ServerProcessControl.StopStateName(s));
        ServerProcessControl.RunningProbe = root => running && SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = nonce =>
        {
            StopServerEvents.ClearReply();
            StopServerEvents.AcceptReply(StopServerReply.For(nonce, accepted: true, StopReplyReason.Saving));
            return true;
        };
        ServerProcessControl.PollReply = nonce =>
        {
            // The server saves, then exits: the process disappears one poll after the reply.
            var reply = StopServerEvents.ReplyFor(nonce);
            if (reply != null) running = false;
            return reply;
        };

        var outcome = ServerProcessControl.Stop(_server);

        Assert.Equal(ServerStopState.Stopped, outcome.State);
        Assert.Equal(new[] { "requested", "replied", "saving", "stopped" }, states.ToArray());
        var record = ServerLaunchRecord.TryRead(_server);
        Assert.NotNull(record);
        Assert.Equal(
            new[] { "-persistentDataPath", @"C:\GameModding\_testserver\forms-save-data-20260906", "-saveName", "test1" },
            record!.Args.ToArray());
        Assert.Equal(@"C:\GameModding\_testserver\server", record.WorkingDirectory);
        // Revert: drop the nonce echo (reply for any nonce) and a stale reply from a previous
        // attempt satisfies this one, so a stop nobody answered reads as accepted.
    }

    [Fact]
    public void A_not_admin_reply_is_its_own_state_and_never_falls_through_to_a_kill()
    {
        var states = new List<string>();
        ServerProcessControl.StopProgress = s => states.Add(ServerProcessControl.StopStateName(s));
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        bool killed = false;
        ServerProcessControl.KillOverride = _ => { killed = true; return true; };
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.PollReply = nonce =>
            StopServerReply.For(nonce, accepted: false, StopReplyReason.NotAdmin);

        var outcome = ServerProcessControl.Stop(_server);

        Assert.Equal(ServerStopState.NotAdmin, outcome.State);
        Assert.Equal(ServerProcessControl.ReasonNotAdmin, outcome.Reason);
        Assert.Equal(new[] { "requested", "not-admin" }, states.ToArray());
        Assert.False(killed);
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        // The force row is still reachable — it is built whenever the server runs — but this route
        // never takes it by itself.
        // Revert: map a refused reply onto ForcePrompt again and a server that said "you are not an
        // admin" would be offered to the same user as an unsaved kill.
    }

    [Fact]
    public void A_refused_save_and_a_save_timeout_are_named_rather_than_read_as_silence()
    {
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = _ => true;

        ServerProcessControl.PollReply = nonce =>
            StopServerReply.For(nonce, accepted: false, StopReplyReason.SaveTimeout);
        var timedOut = ServerProcessControl.Stop(_server);
        Assert.Equal(ServerStopState.SaveTimeout, timedOut.State);
        Assert.Equal(ServerProcessControl.ReasonSaveTimeout, timedOut.Reason);

        ServerProcessControl.PollReply = nonce =>
            StopServerReply.For(nonce, accepted: false, StopReplyReason.SaveFailed);
        var failed = ServerProcessControl.Stop(_server);
        Assert.Equal(ServerStopState.Failed, failed.State);
        Assert.Equal(ServerProcessControl.ReasonSaveFailed, failed.Reason);
        // Revert: collapse both onto ForcePrompt and "the world would not save" becomes an offer to
        // kill the process that is protecting it.
    }

    [Fact]
    public void No_reply_at_all_still_times_out_to_the_force_prompt()
    {
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.PollReply = _ => null;

        DateTime start = _now;
        var outcome = ServerProcessControl.Stop(_server);

        Assert.Equal(ServerStopState.ForcePrompt, outcome.State);
        Assert.Equal(ServerProcessControl.ReasonGracefulTimeout, outcome.Reason);
        Assert.True((_now - start).TotalSeconds >= ServerProcessControl.GracefulTimeoutSeconds);
        // Revert: wait on the reply with no deadline and the pane hangs on a server that is gone.
    }

    [Fact]
    public void A_reply_for_another_nonce_is_not_this_attempts_answer()
    {
        StopServerEvents.AcceptReply(StopServerReply.For(1234, accepted: true, StopReplyReason.Saving));
        Assert.NotNull(StopServerEvents.ReplyFor(1234));
        Assert.Null(StopServerEvents.ReplyFor(1235));
        StopServerEvents.ClearReply();
        Assert.Null(StopServerEvents.ReplyFor(1234));

        // A reply from a schema this build does not read is not stored at all.
        StopServerEvents.AcceptReply(new StopServerReply { Schema = 99, Nonce = 7, Accepted = 1 });
        Assert.Null(StopServerEvents.ReplyFor(7));

        int a = StopServerEvents.NextNonce();
        int b = StopServerEvents.NextNonce();
        Assert.NotEqual(a, b);
        Assert.NotEqual(0, a);
        // Revert: store a foreign-schema reply and a future field layout is read as today's.
    }

    [Fact]
    public void The_graceful_route_is_refused_to_a_server_that_never_completed_a_handshake()
    {
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.PollReply = _ => null;

        ServerProcessControl.ProgenitorHandshakeOk = () => false;
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string reason));
        Assert.Equal(ServerProcessControl.ReasonNoHandshake, reason);

        ServerProcessControl.ProgenitorHandshakeOk = () => throw new InvalidOperationException("no client world");
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out reason));
        Assert.Equal(ServerProcessControl.ReasonNoHandshake, reason);

        ServerProcessControl.ProgenitorHandshakeOk = () => true;
        Assert.True(ServerProcessControl.GracefulAvailable(_server, out reason), reason);
        // Revert: drop the handshake requirement and the pane offers a Progenitor stop to whatever
        // is listening on that port, on the strength of a folder and a number.
    }

    // --- server: the handler ---------------------------------------------------------------------

    [Fact]
    public void A_non_admin_request_is_refused_before_the_save_path_is_touched()
    {
        bool saved = false, quit = false;
        ServerStopCommand.SaveWorld = () => { saved = true; return null; };
        ServerStopCommand.QuitNextFrame = () => quit = true;

        var sent = new List<StopServerReply>();
        StopServerEvents.Handle(nonce: 5, isAdmin: false, send: sent.Add);

        Assert.Single(sent);
        Assert.False(sent[0].IsAccepted);
        Assert.Equal(StopReplyReason.NotAdmin, sent[0].Reason);
        Assert.Equal(5, sent[0].Nonce);
        Assert.False(saved);
        Assert.False(quit);
        // Revert: check admin after calling SaveWorld and any connected client can make an admin's
        // server write a save file on demand.
    }

    [Fact]
    public void An_admin_request_replies_first_then_saves_then_quits()
    {
        var order = new List<string>();
        ServerStopCommand.SaveWorld = () => { order.Add("save"); return null; };
        ServerStopCommand.QuitNextFrame = () => order.Add("quit");

        var sent = new List<StopServerReply>();
        StopServerEvents.Handle(nonce: 9, isAdmin: true, send: r => { order.Add("reply"); sent.Add(r); });

        Assert.Equal(new[] { "reply", "save", "quit" }, order.ToArray());
        Assert.Single(sent);
        Assert.True(sent[0].IsAccepted);
        Assert.Equal(StopReplyReason.Saving, sent[0].Reason);
        // Revert: send the accepted reply after the save and the client sees nothing at all for as
        // long as the serializer takes, which is exactly the silence the force prompt reacts to.
    }

    [Fact]
    public void A_failed_save_replies_save_failed_and_never_quits()
    {
        bool quit = false;
        ServerStopCommand.SaveWorld = () => "TriggerSave returned false";
        ServerStopCommand.QuitNextFrame = () => quit = true;

        var sent = new List<StopServerReply>();
        StopServerEvents.Handle(nonce: 3, isAdmin: true, send: sent.Add);

        Assert.Equal(2, sent.Count);
        Assert.True(sent[0].IsAccepted);
        Assert.False(sent[1].IsAccepted);
        Assert.Equal(StopReplyReason.SaveFailed, sent[1].Reason);
        Assert.False(quit);
        // Revert: quit on a failed save and the graceful route loses exactly the progress it exists
        // to protect.
    }

    // --- server: who may stop, and the log (F21, live 2026-10-07) ----------------------------------

    [Fact]
    public void An_admin_list_member_without_adminauth_is_accepted_like_a_remote_install()
    {
        // F21: the live admin was on the server's admin list and had not run adminauth, so
        // User.IsAdmin was false. The remote-install channel accepted them (its audit log reads
        // authorizedBy=adminlist); the stop channel refused them, silently.
        const ulong member = 76561198000000101UL;
        string pdp = Path.Combine(_root, "save");
        Directory.CreateDirectory(Path.Combine(pdp, "Settings"));
        File.WriteAllText(Path.Combine(pdp, "Settings", "adminlist.txt"), member + "\n");
        ServerAdminList.Shared = new ServerAdminList(() => pdp, _server);
        try
        {
            var by = ServerAdminList.AuthorizeShared(member, isAdmin: false);
            Assert.Equal(RemoteAuthorizedBy.AdminList, by);
            // The same people remote installs authorize.
            Assert.True(new RemoteInstallActor("Admin", member, false, by).Authorized);

            bool saved = false;
            ServerStopCommand.SaveWorld = () => { saved = true; return null; };
            ServerStopCommand.QuitNextFrame = () => { };
            var sent = new List<StopServerReply>();
            var log = new List<string>();
            StopServerEvents.Handle(11, by, "Admin", sent.Add, log.Add);

            Assert.True(Assert.Single(sent).IsAccepted);
            Assert.True(saved);
            Assert.Contains(log, l => l.Contains("accepted request 11 from 'Admin' (authorized by adminlist)"));
            Assert.Equal(RemoteAuthorizedBy.None, ServerAdminList.AuthorizeShared(76561198000000102UL, isAdmin: false));
        }
        finally
        {
            ServerAdminList.Shared = null;
        }
        // Revert: authorize with ServerAdminList.IsProgenitorAdmin (adminauth or host only) and the
        // admin-list admin is answered not-admin while the same server lets them install mods.
    }

    [Fact]
    public void Every_refusal_is_logged_and_answered()
    {
        var sent = new List<StopServerReply>();
        var log = new List<string>();
        StopServerEvents.Handle(21, RemoteAuthorizedBy.None, "Guest", sent.Add, log.Add);
        Assert.Equal(StopReplyReason.NotAdmin, Assert.Single(sent).Reason);
        Assert.Contains("StopServer: refused request 21 from 'Guest' (authorized by none): not-admin", Assert.Single(log));

        sent.Clear();
        log.Clear();
        ServerStopCommand.SaveWorld = () => "TriggerSave returned false";
        ServerStopCommand.QuitNextFrame = () => { };
        StopServerEvents.Handle(22, RemoteAuthorizedBy.AdminAuth, "Admin", sent.Add, log.Add);
        Assert.Equal(2, sent.Count);
        Assert.Equal(StopReplyReason.SaveFailed, sent[1].Reason);
        Assert.Contains(log, l => l.Contains("refused request 22") && l.Contains("save-failed"));
        Assert.False(StopServerEvents.StoppingForTests);

        sent.Clear();
        log.Clear();
        ServerStopCommand.SaveWorld = null;
        StopServerEvents.Handle(23, RemoteAuthorizedBy.AdminList, "Admin", sent.Add, log.Add);
        Assert.Equal(StopReplyReason.NoSavePath, sent[1].Reason);
        Assert.Contains(log, l => l.Contains("refused request 23") && l.Contains("no-save-path"));
        // Revert: drop the log callback from any branch and a refused stop leaves the server log
        // as empty as it was in the live F21 run.
    }

    [Fact]
    public void A_second_request_while_saving_starts_nothing_new()
    {
        int saves = 0;
        var pending = new List<Action>();
        ServerStopCommand.ClockUtc = () => _now;
        ServerStopCommand.SaveWorld = () => { saves++; return null; };
        ServerStopCommand.SaveWatch = () => () => false;
        ServerStopCommand.Schedule = (_, action) => pending.Add(action);
        ServerStopCommand.QuitNextFrame = () => { };

        var sent = new List<StopServerReply>();
        var log = new List<string>();
        StopServerEvents.Handle(31, RemoteAuthorizedBy.AdminList, "Admin", sent.Add, log.Add);
        StopServerEvents.Handle(32, RemoteAuthorizedBy.AdminList, "Admin", sent.Add, log.Add);

        Assert.Equal(1, saves);
        Assert.Equal(2, sent.Count);
        Assert.True(sent[1].IsAccepted);
        Assert.Equal(32, sent[1].Nonce);
        Assert.Contains(log, l => l.Contains("request 32") && l.Contains("already saving"));
        Assert.True(StopServerEvents.StoppingForTests);
        // Revert: run Execute for every accepted request and two clicks trigger two saves and two
        // confirmation loops racing to quit.
    }

    /// <summary>Review (L1, L2): at most 5 stop requests per sender a minute; an accepted stop that never quit stops blocking.</summary>
    [Fact]
    public void Stop_requests_are_rate_limited_and_a_stale_stop_releases()
    {
        var start = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
        var now = start;
        StopServerEvents.ClockOverride = () => now;
        ServerStopCommand.ClockUtc = () => _now;
        int saves = 0;
        ServerStopCommand.SaveWorld = () => { saves++; return null; };
        ServerStopCommand.SaveWatch = () => () => false;
        ServerStopCommand.Schedule = (_, _) => { };
        ServerStopCommand.QuitNextFrame = () => { };
        var sent = new List<StopServerReply>();
        var log = new List<string>();
        for (int i = 0; i < 6; i++) StopServerEvents.Handle(40 + i, RemoteAuthorizedBy.AdminList, "Admin", sent.Add, log.Add, senderId: 7);
        Assert.Equal(StopReplyReason.RateLimited, sent[^1].Reason);
        Assert.False(sent[^1].IsAccepted);
        Assert.Contains(log, l => l.Contains("rate-limited"));
        Assert.Equal(1, saves);

        // Another sender is not limited by the first; the stop accepted 2 minutes ago that never quit no longer blocks.
        now = start.AddSeconds(StopServerEvents.StoppingStaleSeconds + 1);
        StopServerEvents.Handle(50, RemoteAuthorizedBy.AdminList, "Other", sent.Add, log.Add, senderId: 8);
        Assert.Equal(2, saves);
        Assert.Contains(log, l => l.Contains("never quit the process"));
    }

    [Fact]
    public void Every_reason_has_a_word_and_a_sentence()
    {
        foreach (StopReplyReason reason in Enum.GetValues(typeof(StopReplyReason)))
        {
            Assert.False(StopServerEvents.ReasonWord(reason).StartsWith("reason-", StringComparison.Ordinal), reason.ToString());
            Assert.NotEqual("", StopServerEvents.RefusalText(reason));
        }

        Assert.Equal("reason-99", StopServerEvents.ReasonWord((StopReplyReason)99));
    }

    // --- server: the save confirmation ------------------------------------------------------------

    [Fact]
    public void The_quit_waits_for_the_save_file_to_advance()
    {
        bool landed = false, quit = false;
        var pending = new List<Action>();
        ServerStopCommand.ClockUtc = () => _now;
        ServerStopCommand.SaveWorld = () => null;
        ServerStopCommand.SaveWatch = () => () => landed;
        ServerStopCommand.Schedule = (_, action) => pending.Add(action);
        ServerStopCommand.QuitNextFrame = () => quit = true;

        Assert.True(ServerStopCommand.Execute(isAdmin: true, reply: _ => { }));
        Assert.Single(pending);
        Assert.False(quit);

        // One poll with no new save file: still nothing quits, and the watch re-arms.
        Drain(pending);
        Assert.False(quit);
        Assert.Single(pending);

        landed = true;
        Drain(pending);
        Assert.True(quit);
        // Revert: quit on acceptance while a watch is available and the process leaves before the
        // queued SavePersistentDataEvent is serialized — an accepted save that never reached disk.
    }

    [Fact]
    public void A_save_that_never_lands_times_out_without_quitting()
    {
        bool quit = false;
        var pending = new List<Action>();
        var replies = new List<StopReplyReason>();
        ServerStopCommand.ClockUtc = () => _now;
        ServerStopCommand.SaveWorld = () => null;
        ServerStopCommand.SaveWatch = () => () => false;
        ServerStopCommand.Schedule = (_, action) => pending.Add(action);
        ServerStopCommand.QuitNextFrame = () => quit = true;

        Assert.True(ServerStopCommand.Execute(isAdmin: true, reply: _ => { }, failed: replies.Add));
        Drain(pending);
        Assert.False(quit);

        _now = _now.AddSeconds(WorldSaveTrigger.SaveConfirmTimeoutSeconds + 1);
        Drain(pending);

        Assert.False(quit);
        Assert.Empty(pending);
        Assert.Equal(new[] { StopReplyReason.SaveTimeout }, replies.ToArray());
        // Revert: quit on the timeout instead of refusing and a stuck serializer costs the world.
    }

    [Fact]
    public void Without_an_observable_save_the_quit_still_follows_acceptance()
    {
        bool quit = false;
        ServerStopCommand.SaveWorld = () => null;
        ServerStopCommand.QuitNextFrame = () => quit = true;
        Assert.True(ServerStopCommand.Execute(isAdmin: true, reply: _ => { }));
        Assert.True(quit);
        // Revert: refuse when nothing can observe completion and a server whose save folder cannot
        // be listed could never be stopped gracefully at all.
    }

    // --- the save fence and the landing probe -----------------------------------------------------

    [Fact]
    public void The_fence_is_Cores_and_refuses_the_live_dedicated_root_in_every_input()
    {
        string liveExe = HostedPathRules.ForbiddenLiveServerRoot + @"\VRisingServer.exe";
        Assert.False(WorldSaveTrigger.TryAllowWorldSave(null, liveExe, null, out string byExe));
        Assert.Equal(WorldSaveTrigger.LiveServerRefusal, byExe);

        Assert.False(WorldSaveTrigger.TryAllowWorldSave(
            null, null, "VRisingServer.exe -persistentDataPath " + HostedPathRules.ForbiddenLiveServerRoot + @"\save-data",
            out string byArgs));
        Assert.Equal(WorldSaveTrigger.LiveServerRefusal, byArgs);

        Assert.True(WorldSaveTrigger.TryAllowWorldSave(
            @"C:\GameModding\_testserver\server\save-data\Saves\v4\test1",
            @"C:\GameModding\_testserver\server\VRisingServer.exe",
            null,
            out string ok), ok);
        Assert.Equal("", ok);

        // A longer name that merely starts with the root is a different folder.
        Assert.True(WorldSaveTrigger.ContainsBoundedToken(@"-p C:\VRisingServer\x", HostedPathRules.ForbiddenLiveServerRoot));
        Assert.False(WorldSaveTrigger.ContainsBoundedToken(@"-p C:\VRisingServer2\x", HostedPathRules.ForbiddenLiveServerRoot));
        // Revert: drop the bounded-token scan for a plain Contains and C:\VRisingServer2 is refused
        // as if it were the live root; drop it entirely and a -persistentDataPath into the live root
        // is allowed.
    }

    [Fact]
    public void A_save_has_landed_only_when_a_save_files_stamp_moves_past_the_sample()
    {
        string folder = Path.Combine(_root, "saves");
        Directory.CreateDirectory(folder);
        string save = Path.Combine(folder, "AutoSave_1.save.gz");
        File.WriteAllText(save, "old");
        File.SetLastWriteTimeUtc(save, new DateTime(2026, 9, 12, 11, 0, 0, DateTimeKind.Utc));

        DateTime before = WorldSaveTrigger.NewestSaveWriteUtc(folder);
        Assert.False(WorldSaveTrigger.SaveLanded(folder, before));

        File.SetLastWriteTimeUtc(save, before.AddSeconds(30));
        Assert.True(WorldSaveTrigger.SaveLanded(folder, before));

        // An empty or unreadable folder never confirms a save.
        Assert.Equal(DateTime.MinValue, WorldSaveTrigger.NewestSaveWriteUtc(Path.Combine(_root, "nope")));
        Assert.False(WorldSaveTrigger.SaveLanded(null, DateTime.MinValue));
        // Revert: treat "no stamp" as landed and the quit fires on a folder nothing was written to.
    }

    // --- PEB decoding ------------------------------------------------------------------------------

    [Fact]
    public void The_PEB_process_parameters_decode_to_the_command_line_and_the_current_directory()
    {
        const string commandLine =
            "\"C:\\GameModding\\_testserver\\server\\VRisingServer.exe\" "
            + "-persistentDataPath \"C:\\GameModding\\_testserver\\forms-save-data-20260906\" "
            + "-serverName \"Progenitor Forms Test\" -saveName \"test1\" "
            + "-logFile \"C:\\GameModding\\_testserver\\logs\\VRisingServer.log\"";
        const string currentDirectory = @"C:\GameModding\_testserver\server";

        var block = new byte[ServerProcessControl.CommandLineOffset + ServerProcessControl.UnicodeStringBytes];
        WriteUnicodeString(block, ServerProcessControl.CommandLineOffset, commandLine, 0x1000);
        WriteUnicodeString(block, ServerProcessControl.CurrentDirectoryDosPathOffset, currentDirectory, 0x2000);

        Assert.True(ServerProcessControl.TryDecodeUnicodeString(
            block, ServerProcessControl.CommandLineOffset, out int cmdBytes, out ulong cmdAddress));
        Assert.Equal(0x1000UL, cmdAddress);
        Assert.Equal(commandLine.Length * 2, cmdBytes);
        Assert.Equal(commandLine, ServerProcessControl.DecodeWideString(Wide(commandLine), cmdBytes));

        Assert.True(ServerProcessControl.TryDecodeUnicodeString(
            block, ServerProcessControl.CurrentDirectoryDosPathOffset, out int dirBytes, out ulong dirAddress));
        Assert.Equal(0x2000UL, dirAddress);
        Assert.Equal(currentDirectory, ServerProcessControl.DecodeWideString(Wide(currentDirectory), dirBytes));

        // The four-byte gap between MaximumLength and Buffer is the whole reason the pointer is at
        // +8: reading it at +4 would take the padding plus half the pointer.
        Assert.Equal(0, block[ServerProcessControl.CommandLineOffset + 4]);
        Assert.Equal(0, block[ServerProcessControl.CommandLineOffset + 5]);

        // Torn or impossible structures decode to nothing rather than to a wrong string.
        Assert.False(ServerProcessControl.TryDecodeUnicodeString(new byte[8], 0, out _, out _));
        var odd = (byte[])block.Clone();
        odd[ServerProcessControl.CommandLineOffset] = 3;           // an odd UTF-16 byte length
        odd[ServerProcessControl.CommandLineOffset + 1] = 0;
        Assert.False(ServerProcessControl.TryDecodeUnicodeString(odd, ServerProcessControl.CommandLineOffset, out _, out _));
        var nullBuffer = (byte[])block.Clone();
        for (int i = 0; i < 8; i++) nullBuffer[ServerProcessControl.CommandLineOffset + 8 + i] = 0;
        Assert.False(ServerProcessControl.TryDecodeUnicodeString(nullBuffer, ServerProcessControl.CommandLineOffset, out _, out _));
        // Revert: read Buffer at +4, or read Length as characters instead of bytes, and the launch
        // record gets half a command line — which Start server would then replay.
    }

    [Fact]
    public void The_test_servers_real_arguments_split_and_round_trip_through_the_record()
    {
        // Verbatim from <testserver>\start-test-server.bat, read as text.
        const string commandLine =
            "VRisingServer.exe -persistentDataPath \"C:\\GameModding\\_testserver\\forms-save-data-20260906\" "
            + "-serverName \"Progenitor Forms Test\" -saveName \"test1\" "
            + "-logFile \"C:\\GameModding\\_testserver\\logs\\VRisingServer.log\"";

        var record = ServerLaunchRecord.FromProcess(
            new ServerProcessInfo(4242, _exe, commandLine, @"C:\GameModding\_testserver\server"), _now);

        Assert.Equal(
            new[]
            {
                "-persistentDataPath", @"C:\GameModding\_testserver\forms-save-data-20260906",
                "-serverName", "Progenitor Forms Test",
                "-saveName", "test1",
                "-logFile", @"C:\GameModding\_testserver\logs\VRisingServer.log",
            },
            record.Args.ToArray());
        Assert.Equal(@"C:\GameModding\_testserver\server", record.WorkingDirectory);

        // A round trip through the file keeps every argument, spaces included.
        record.Write(_server);
        var read = ServerLaunchRecord.TryRead(_server);
        Assert.NotNull(read);
        Assert.Equal(record.Args.ToArray(), read!.Args.ToArray());
        // Revert: split on spaces without honoring quotes and "Progenitor Forms Test" becomes
        // three arguments, so Start server launches a differently named server.
    }

    [Fact]
    public void The_command_line_readback_drops_the_image_requotes_values_and_is_capped()
    {
        Assert.Equal(
            "-saveName test1 -serverName \"Progenitor Forms Test\"",
            ServerProcessControl.RedactCommandLine(
                "\"C:\\x\\VRisingServer.exe\" -saveName test1 -serverName \"Progenitor Forms Test\""));
        Assert.Equal("", ServerProcessControl.RedactCommandLine(null));
        Assert.Equal("", ServerProcessControl.RedactCommandLine("VRisingServer.exe"));

        string long_ = "VRisingServer.exe" + string.Concat(Enumerable.Repeat(" -flagwithalongname value", 80));
        string redacted = ServerProcessControl.RedactCommandLine(long_);
        Assert.True(redacted.Length <= ServerProcessControl.CommandLineReadbackMax, redacted.Length.ToString());
        Assert.EndsWith("…", redacted);
        // Revert: keep the image token and process.path is printed twice; drop the cap and one
        // readback can carry an unbounded string.
    }

    // --- fixture helpers ----------------------------------------------------------------------------

    private static void Drain(List<Action> pending)
    {
        var batch = new List<Action>(pending);
        pending.Clear();
        foreach (var action in batch) action();
    }

    private static byte[] Wide(string text) => Encoding.Unicode.GetBytes(text);

    private static void WriteUnicodeString(byte[] block, int offset, string text, ulong address)
    {
        int bytes = text.Length * 2;
        block[offset] = (byte)(bytes & 0xFF);
        block[offset + 1] = (byte)((bytes >> 8) & 0xFF);
        block[offset + 2] = (byte)((bytes + 2) & 0xFF);
        block[offset + 3] = (byte)(((bytes + 2) >> 8) & 0xFF);
        // bytes 4..7 stay zero: that is the x64 padding between MaximumLength and Buffer.
        for (int i = 0; i < 8; i++) block[offset + 8 + i] = (byte)((address >> (i * 8)) & 0xFF);
    }

    private ServerProcessInfo Live() => new(
        4242,
        _exe,
        "\"" + _exe + "\" -persistentDataPath \"C:\\GameModding\\_testserver\\forms-save-data-20260906\" -saveName \"test1\"",
        @"C:\GameModding\_testserver\server");

    private bool SameFolder(string root) =>
        string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), _server, StringComparison.OrdinalIgnoreCase);

    private void InstallLedger() =>
        new ServerInstallLedger("0.1.0", "a", "b", _now, Array.Empty<ServerInstallLedgerEntry>()).Write(_server);

    private void HostSettings(int port)
    {
        string dir = Path.Combine(_server, "Settings");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ServerHostSettings.json"), "{\"Name\":\"test\",\"Port\":" + port + "}");
    }
}
