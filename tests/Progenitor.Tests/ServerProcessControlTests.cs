using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SERVERINST3: Stop server (graceful and force) and Start server for the user's own validated
/// local dedicated-server folder. Temp folders and fakes only — no process is ever started,
/// signaled or stopped by these tests, and no command reaches a game. Guard removals named in
/// comments must fail these tests.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ServerProcessControlTests : IDisposable
{
    private readonly string _root;
    private readonly string _server;
    private readonly string _exe;
    private readonly string _outside;
    private DateTime _now = new(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);

    public ServerProcessControlTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-serverproc-" + Guid.NewGuid().ToString("N"));
        _server = Path.Combine(_root, "server");
        _outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(_server);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(_server, "BepInEx", "core", "BepInEx.Core.dll"), "bepinex");
        _exe = Path.Combine(_server, LocalServerFolder.ServerExecutableName);
        File.WriteAllText(_exe, "exe");
        File.WriteAllText(Path.Combine(_outside, LocalServerFolder.ServerExecutableName), "exe");
        ServerInstall.RunningServerProbe = _ => null;
        ServerProcessControl.ResetForTests();
        ServerStopCommand.ResetForTests();
        ServerProcessControl.ClockUtc = () => _now;
        ServerProcessControl.SleepMs = _ => _now = _now.AddSeconds(1);
        // SERVERINST4: the graceful route now also requires a completed Progenitor handshake with
        // that server. Every SERVERINST3 case assumed one, so the default here is "handshake ok"
        // and the cases that test the check set it themselves.
        ServerProcessControl.ProgenitorHandshakeOk = () => true;
        StopServerEvents.ResetForTests();
        LocalServerFolder.WriteAccessOverride = _ => true;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        UiApi.ResetForTests();
        // Review M1/M2 (2026-10-08): the folder's server (pid 4242) is the process bound to the joined port, and the
        // default save folder is a temp one.
        ServerProcessControl.UdpOwnerProbe = _ => new[] { 4242 };
        ServerProcessControl.DefaultSaveRootForTests = Path.Combine(_root, "default-save");
    }

    public void Dispose()
    {
        ServerInstall.RunningServerProbe = null;
        ServerProcessControl.ResetForTests();
        ServerStopCommand.ResetForTests();
        LocalServerFolder.WriteAccessOverride = null;
        ConfigurationPane.ResetServerInstallStateForTests();
        UiApi.ResetForTests();
        ConfigHelper.UninstallAll();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    // --- launch record ---

    [Fact]
    public void Launch_record_round_trips_the_exe_args_and_working_directory_from_a_command_line()
    {
        string commandLine = "\"" + _exe + "\" -persistentDataPath \"C:\\save data\" -serverName \"Progenitor Forms Test\" -saveName test1";
        var record = ServerLaunchRecord.FromProcess(new ServerProcessInfo(4242, _exe, commandLine), _now);
        Assert.Equal(_exe, record.Exe);
        Assert.Equal(_server, record.WorkingDirectory);
        Assert.Equal(
            new[] { "-persistentDataPath", "C:\\save data", "-serverName", "Progenitor Forms Test", "-saveName", "test1" },
            record.Args.ToArray());

        record.Write(_server);
        var read = ServerLaunchRecord.TryRead(_server);
        Assert.NotNull(read);
        Assert.Equal(ServerLaunchRecord.CurrentSchema, read!.Schema);
        Assert.Equal(ServerLaunchRecord.ByProgenitor, read.By);
        Assert.Equal(record.Args.ToArray(), read.Args.ToArray());
        Assert.Equal(_now, read.StoppedUtc);
        Assert.Equal(ServerLaunchRecord.PathFor(_server), Path.Combine(_server, "BepInEx", "progenitor-server-launch.json"));
        // Revert: dropping the quoted-run handling in SplitCommandLine splits "C:\save data" into two arguments.
    }

    [Fact]
    public void A_record_of_another_schema_or_without_an_exe_reads_as_absent()
    {
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx"));
        File.WriteAllText(ServerLaunchRecord.PathFor(_server), "{\"schema\":99,\"exe\":\"" + _exe.Replace("\\", "\\\\") + "\"}");
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        File.WriteAllText(ServerLaunchRecord.PathFor(_server), "{\"schema\":1,\"exe\":\"\"}");
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        File.WriteAllText(ServerLaunchRecord.PathFor(_server), "{ not json");
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        // Revert: reading a foreign schema would let Start server replay arguments this build never wrote.
    }

    // --- graceful stop ---

    [Fact]
    public void Graceful_stop_sends_the_command_and_writes_the_launch_record_when_the_process_exits()
    {
        InstallLedger();
        HostSettings(9976);
        var sent = new List<int>();
        bool running = true;
        ServerProcessControl.RunningProbe = root => running && SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = nonce => { sent.Add(nonce); running = false; return true; };

        Assert.True(ServerProcessControl.GracefulAvailable(_server, out string why), why);
        var outcome = ServerProcessControl.Stop(_server);
        Assert.Equal(ServerStopState.Stopped, outcome.State);
        Assert.Single(sent);
        Assert.NotEqual(0, sent[0]);
        var record = ServerLaunchRecord.TryRead(_server);
        Assert.NotNull(record);
        Assert.Equal(_exe, record!.Exe);
        Assert.Equal(new[] { "-saveName", "test1" }, record.Args.ToArray());
        // Revert: writing the record before the process exits would claim a stop that never happened.
    }

    [Fact]
    public void Start_strips_the_inherited_doorstop_environment_and_nothing_else()
    {
        // Live 2026-09-12: the first Start from the client produced a server without BepInEx because the
        // child inherited DOORSTOP_INVOKE_DLL_PATH / DOORSTOP_PROCESS_PATH and Doorstop skipped injection.
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOORSTOP_INVOKE_DLL_PATH"] = @"C:\client\BepInEx\core\BepInEx.Unity.IL2CPP.dll",
            ["DOORSTOP_PROCESS_PATH"] = @"C:\client\VRising.exe",
            ["doorstop_enabled"] = "1",
            ["SteamAppId"] = "1604030",
            ["PATH"] = @"C:\Windows",
        };
        var removed = ServerProcessControl.StripDoorstopEnvironment(env);
        Assert.Equal(3, removed.Count);
        Assert.Equal(new[] { "PATH", "SteamAppId" }, env.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        // Revert: leaving DOORSTOP_* in place starts a vanilla server (no BepInEx log, no handshake).
    }

    [Fact]
    public void Host_settings_are_found_through_the_live_processes_persistentDataPath_before_any_record()
    {
        // Live 2026-09-12: no launch record yet, the install's StreamingAssets copy says the default
        // port, the running process's -persistentDataPath names the real settings (9976).
        string data = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(data, "Settings"));
        File.WriteAllText(Path.Combine(data, "Settings", "ServerHostSettings.json"), "{\"Port\": 9976}");
        Directory.CreateDirectory(Path.Combine(_server, "VRisingServer_Data", "StreamingAssets", "Settings"));
        File.WriteAllText(Path.Combine(_server, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json"), "{\"Port\": 9876}");
        ServerProcessControl.RunningProbe = root => SameFolder(root)
            ? new ServerProcessInfo(4242, _exe, "VRisingServer.exe -persistentDataPath \"" + data + "\" -saveName test1")
            : null;
        Assert.Equal(9976, ServerProcessControl.HostPort(_server));
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        // Revert: reading only the record/folder copies makes the first graceful stop impossible
        // (the record that names the path is only written by a completed stop).
    }

    [Fact]
    public void Graceful_stop_is_unavailable_without_a_transport_or_the_matching_port_but_needs_no_ledger()
    {
        HostSettings(9976);
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = _ => true;
        // No ledger: a tool-deployed server answers the handshake without one (live 2026-09-12), so
        // the ledger is not part of the gate; the handshake is the proof of Progenitor.
        Assert.True(ServerProcessControl.GracefulAvailable(_server, out string noLedger), noLedger);

        InstallLedger();
        ServerProcessControl.SubmitCommand = null;
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string noTransport));
        Assert.Equal(ServerProcessControl.ReasonNoTransport, noTransport);

        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.ConnectedServerPort = () => 27015;
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string otherServer));
        // SERVERINST5: the mismatch names both ports, so "which server am I on then?" is answered
        // in the reason itself.
        Assert.Equal(
            "This client is connected to port 27015; that folder's server uses port 9976.",
            otherServer);
        Assert.Equal(ServerProcessControl.ReasonNotThisServer(27015, 9976), otherServer);

        // A folder Progenitor cannot identify falls through to the force prompt, never to a kill.
        var outcome = ServerProcessControl.Stop(_server);
        Assert.Equal(ServerStopState.ForcePrompt, outcome.State);
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        // Revert: identifying the server by folder alone would let a stop hit a different instance.
    }

    [Fact]
    public void A_graceful_stop_that_stays_silent_for_twenty_seconds_ends_at_the_force_prompt()
    {
        InstallLedger();
        HostSettings(9976);
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = _ => true;

        DateTime start = _now;
        var outcome = ServerProcessControl.Stop(_server);
        Assert.Equal(ServerStopState.ForcePrompt, outcome.State);
        Assert.Equal(ServerProcessControl.ReasonGracefulTimeout, outcome.Reason);
        Assert.True((_now - start).TotalSeconds >= ServerProcessControl.GracefulTimeoutSeconds);
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        // Revert: waiting forever would hang the UI thread on a server that never answers.
    }

    // --- force stop ---

    [Fact]
    public void Force_stop_kills_only_the_process_matched_inside_the_folder()
    {
        var killed = new List<string>();
        bool running = true;
        ServerProcessControl.RunningProbe = root => running && SameFolder(root) ? Live() : null;
        ServerProcessControl.KillOverride = p => { killed.Add(p.Path); running = false; return true; };

        var outcome = ServerProcessControl.ForceStop(_server);
        Assert.Equal(ServerStopState.Stopped, outcome.State);
        Assert.Equal(new[] { _exe }, killed.ToArray());
        Assert.NotNull(ServerLaunchRecord.TryRead(_server));

        // A server running from another folder is never seen by this folder's probe, so nothing else is killed.
        killed.Clear();
        ServerProcessControl.RunningProbe = _ => null;
        var none = ServerProcessControl.ForceStop(_server);
        Assert.Equal(ServerStopState.Failed, none.State);
        Assert.Equal(ServerProcessControl.ReasonNotRunning, none.Reason);
        Assert.Empty(killed);
        // Revert: matching by process name would let Stop (force) end a VRisingServer.exe outside the folder.
    }

    [Fact]
    public void A_process_that_does_not_exit_after_the_kill_is_a_failure_with_no_launch_record()
    {
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.KillOverride = _ => true;
        var outcome = ServerProcessControl.ForceStop(_server);
        Assert.Equal(ServerStopState.Failed, outcome.State);
        Assert.Equal(ServerProcessControl.ReasonStillRunning, outcome.Reason);
        Assert.Null(ServerLaunchRecord.TryRead(_server));
        // Revert: writing the record on a failed kill would offer Start server for a process still running.
    }

    // --- start ---

    [Fact]
    public void Start_replays_the_recorded_exe_args_and_working_directory()
    {
        ServerLaunchRecord.FromProcess(new ServerProcessInfo(1, _exe, "\"" + _exe + "\" -persistentDataPath \"" + Path.Combine(_server, "save") + "\" -saveName test1"), _now).Write(_server);
        ServerProcessControl.RunningProbe = _ => null;
        string? startedExe = null, startedDir = null;
        IReadOnlyList<string>? startedArgs = null;
        ServerProcessControl.StartOverride = (exe, args, dir) =>
        {
            startedExe = exe;
            startedArgs = args;
            startedDir = dir;
            return 4321;
        };

        var outcome = ServerProcessControl.Start(_server);
        Assert.True(outcome.Ok);
        Assert.Equal(4321, outcome.Pid);
        Assert.Equal("Started (pid 4321)", outcome.Line);
        Assert.Equal(_exe, startedExe);
        Assert.Equal(_server, startedDir);
        Assert.Equal(new[] { "-persistentDataPath", Path.Combine(_server, "save"), "-saveName", "test1" }, startedArgs!.ToArray());
        // Revert: starting with anything but the recorded arguments boots a different world.
    }

    [Fact]
    public void Start_is_unavailable_without_a_record_refused_while_running_and_refused_for_an_exe_outside_the_folder()
    {
        ServerProcessControl.RunningProbe = _ => null;
        ServerProcessControl.StartOverride = (_, _, _) => throw new InvalidOperationException("must not start");
        var none = ServerProcessControl.Start(_server);
        Assert.False(none.Ok);
        Assert.Equal(ServerProcessControl.StartUnavailableLine(), none.Line);
        Assert.Contains("no launch record yet", none.Line);

        new ServerLaunchRecord(Path.Combine(_outside, LocalServerFolder.ServerExecutableName), new[] { "-saveName", "x" }, _outside, _now).Write(_server);
        var foreign = ServerProcessControl.Start(_server);
        Assert.False(foreign.Ok);
        // Review M-d: exactly the folder's own VRisingServer.exe.
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonNotServerExe), foreign.Line);

        ServerLaunchRecord.FromProcess(new ServerProcessInfo(1, _exe, "\"" + _exe + "\""), _now).Write(_server);
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        var already = ServerProcessControl.Start(_server);
        Assert.False(already.Ok);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonAlreadyRunning), already.Line);
        // Revert: replaying a record whose exe is outside the folder would start a process Progenitor never validated.
    }

    // --- review 2026-10-08: the live-server fence and the wait-and-start path (H1, M-a to M-d, L-b) ---

    /// <summary>
    /// Review H1: the live dedicated server is never a folder Progenitor stops, kills or starts, by folder, record exe,
    /// working directory or argument; validation and the picker refuse it too. Nothing is started or signaled.
    /// </summary>
    [Fact]
    public void The_live_dedicated_server_is_never_stopped_or_started()
    {
        string live = Progenitor.WorldRestore.WorldRestorePaths.ForbiddenLiveServerRoot;
        ServerProcessControl.StartOverride = (_, _, _) => throw new InvalidOperationException("must not start");
        ServerProcessControl.KillOverride = _ => throw new InvalidOperationException("must not kill");
        ServerProcessControl.SubmitCommand = _ => throw new InvalidOperationException("must not send");
        ServerProcessControl.RunningProbe = _ => new ServerProcessInfo(9, Path.Combine(live, LocalServerFolder.ServerExecutableName), "x");

        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonLiveServer), ServerProcessControl.Start(live).Line);
        Assert.Equal(ServerProcessControl.ReasonLiveServer, ServerProcessControl.Stop(live).Reason);
        Assert.Equal(ServerProcessControl.ReasonLiveServer, ServerProcessControl.ForceStop(live).Reason);
        Assert.False(ServerProcessControl.GracefulAvailable(live, out string why));
        Assert.Equal(ServerProcessControl.ReasonLiveServer, why);
        Assert.Equal(LocalServerFolder.RefusalLiveServer, LocalServerFolder.Validate(live).Refusal);
        Assert.Equal(LocalServerFolder.RefusalLiveServer, LocalServerFolder.Validate(Path.Combine(live, "sub")).Refusal);

        // A record in a test folder whose arguments name the live server is refused as well.
        ServerProcessControl.RunningProbe = _ => null;
        new ServerLaunchRecord(_exe, new[] { "-persistentDataPath", Path.Combine(live, "save-data") }, _server, _now).Write(_server);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonLiveServer), ServerProcessControl.Start(_server).Line);

        // The picker never offers it, configured or discovered.
        var entries = ServerPicker.Build(new ServerPickerInputs
        {
            ConfiguredPath = live,
            Discovered = new[] { new LocalServerCandidate(live, "Live", "running", running: true), new LocalServerCandidate(_server, "Test", "steam") },
        });
        Assert.DoesNotContain(entries, e => string.Equals(e.Path, live, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Path == _server);
        string folderSrc = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "LocalServerFolder.cs");
        Assert.DoesNotContain("without that file's developer live-server denylist", folderSrc, StringComparison.Ordinal);
    }

    private ServerProcessInfo Stopped(int pid = 4242) =>
        new(pid, _exe, "\"" + _exe + "\" -persistentDataPath \"" + Path.Combine(_server, "save") + "\" -saveName world1");

    /// <summary>
    /// F30, review M-a/M-b: the start after Progenitor's own stop waits on that process (its pid, its path), then needs two
    /// clean reads of the process list, then replays the record this stop wrote. The real path runs; only the process list,
    /// the exit wait and the start are faked.
    /// </summary>
    [Fact]
    public void Start_again_waits_for_the_stopped_process_and_two_clean_scans_then_replays_the_fresh_record()
    {
        var stopped = Stopped();
        var record = ServerProcessControl.WriteLaunchRecord(_server, stopped);
        var waited = new List<(int Pid, string Path)>();
        ServerProcessControl.PidExitProbe = (pid, path, _) => { waited.Add((pid, path)); return true; };
        int scans = 0;
        ServerProcessControl.ScanProbe = _ => { scans++; return (true, null); };
        var starts = new List<IReadOnlyList<string>>();
        ServerProcessControl.StartOverride = (_, args, _) => { starts.Add(args); return 777; };

        var outcome = ServerProcessControl.StartAgainAfterStop(_server, stopped, record, 60);
        Assert.True(outcome.Ok);
        Assert.Equal(new[] { (4242, _exe) }, waited);
        Assert.Equal(2, scans);
        Assert.Equal(new[] { "-persistentDataPath", Path.Combine(_server, "save"), "-saveName", "world1" }, starts.Single());
        Assert.False(ServerProcessControl.StartInFlight);
    }

    [Fact]
    public void Start_again_refuses_when_the_process_does_not_exit_a_scan_fails_or_a_server_is_seen()
    {
        var stopped = Stopped();
        var record = ServerProcessControl.WriteLaunchRecord(_server, stopped);
        ServerProcessControl.StartOverride = (_, _, _) => throw new InvalidOperationException("must not start");

        ServerProcessControl.PidExitProbe = (_, _, _) => false;
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonStillRunning),
            ServerProcessControl.StartAgainAfterStop(_server, stopped, record, 60).Line);

        // Review M-b: a scan that fails is never read as "not running".
        ServerProcessControl.PidExitProbe = (_, _, _) => true;
        ServerProcessControl.ScanProbe = _ => (false, null);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonScanFailed),
            ServerProcessControl.StartAgainAfterStop(_server, stopped, record, 60).Line);

        // The second clean scan sees a server (started meanwhile by someone else): nothing starts.
        int n = 0;
        ServerProcessControl.ScanProbe = _ => (true, ++n >= 2 ? new ServerProcessInfo(99, _exe) : null);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonStillRunning),
            ServerProcessControl.StartAgainAfterStop(_server, stopped, record, 60).Line);

        // Review M-a: a stop whose command line was not read (or with no pid) never replays a launch.
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonNoFreshRecord),
            ServerProcessControl.StartAgainAfterStop(_server, new ServerProcessInfo(4242, _exe), record, 60).Line);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonNoFreshRecord),
            ServerProcessControl.StartAgainAfterStop(_server, new ServerProcessInfo(0, _exe, "x"), record, 60).Line);
        Assert.False(ServerProcessControl.StartInFlight);
    }

    /// <summary>Review M-a: a stop whose command line could not be read never replaces a record that has arguments.</summary>
    [Fact]
    public void A_bare_stop_never_overwrites_a_record_with_arguments()
    {
        ServerProcessControl.WriteLaunchRecord(_server, Stopped());
        var kept = ServerProcessControl.WriteLaunchRecord(_server, new ServerProcessInfo(0, _exe));
        Assert.Equal(4, kept!.Args.Count);
        Assert.Equal(4, ServerLaunchRecord.TryRead(_server)!.Args.Count);
    }

    /// <summary>Review M-c: while the wait-and-start runs, Preferences' Start server is refused; it reads the process list now.</summary>
    [Fact]
    public void One_start_at_a_time_and_a_start_reads_the_process_list_now()
    {
        var stopped = Stopped();
        var record = ServerProcessControl.WriteLaunchRecord(_server, stopped);
        ServerStartOutcome? during = null;
        ServerProcessControl.PidExitProbe = (_, _, _) => { during = ServerProcessControl.Start(_server); return true; };
        ServerProcessControl.ScanProbe = _ => (true, null);
        ServerProcessControl.StartOverride = (_, _, _) => 888;
        Assert.True(ServerProcessControl.StartAgainAfterStop(_server, stopped, record, 60).Ok);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonStartInFlight), during!.Line);

        // Right after that start the process list shows the new server: a second Start is refused.
        ServerProcessControl.ScanProbe = _ => (true, new ServerProcessInfo(888, _exe));
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonAlreadyRunning), ServerProcessControl.Start(_server).Line);
        ServerProcessControl.ScanProbe = _ => (false, null);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonScanFailed), ServerProcessControl.Start(_server).Line);
    }

    /// <summary>Review M-d: the working directory must be inside the folder.</summary>
    [Fact]
    public void A_record_whose_working_directory_is_outside_the_folder_is_refused()
    {
        ServerProcessControl.RunningProbe = _ => null;
        ServerProcessControl.StartOverride = (_, _, _) => throw new InvalidOperationException("must not start");
        new ServerLaunchRecord(_exe, new[] { "-saveName", "x" }, _outside, _now).Write(_server);
        Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonBadWorkingDirectory), ServerProcessControl.Start(_server).Line);
        Assert.True(ServerProcessControl.IsFoldersServerExe(_server, _exe));
        Assert.False(ServerProcessControl.IsFoldersServerExe(_server, Path.Combine(_server, "Other.exe")));
    }

    /// <summary>Review L-b: the server's own minimized, unfocused console; the arguments and environment survive quoting.</summary>
    [Fact]
    public void The_started_server_gets_its_own_unfocused_console_and_a_faithful_command_line()
    {
        Assert.Equal(7, ServerProcessControl.ShowMinNoActive);
        Assert.Equal(0x10u, ServerProcessControl.CreateNewConsole);
        var args = new[] { "-persistentDataPath", @"C:\save data\x", "a\"b", @"trail\", "" };
        var split = ServerLaunchRecord.SplitCommandLine(ServerProcessControl.JoinCommandLine(_exe, args));
        Assert.Equal(new[] { _exe }.Concat(args), split);
        string block = ServerProcessControl.EnvironmentBlock(new Dictionary<string, string?> { ["B"] = "2", ["a"] = "1" });
        Assert.Equal("a=1\0B=2\0\0", block);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "ServerProcessControl.cs");
        Assert.Contains("CreateNewConsole | CreateUnicodeEnvironment", src, StringComparison.Ordinal);
        Assert.Contains("StripDoorstopEnvironment(env);", src, StringComparison.Ordinal);
    }

    // --- server-side command ---

    [Fact]
    public void Stop_server_command_is_denied_for_non_admins_and_never_touches_the_save()
    {
        bool saved = false, quit = false;
        ServerStopCommand.SaveWorld = () => { saved = true; return null; };
        ServerStopCommand.QuitNextFrame = () => quit = true;
        var replies = new List<string>();
        Assert.False(ServerStopCommand.Execute(isAdmin: false, replies.Add));
        Assert.Equal(new[] { ServerStopCommand.DeniedReply }, replies.ToArray());
        Assert.False(saved);
        Assert.False(quit);
        // Revert: dropping adminOnly lets any player stop the server.
    }

    [Fact]
    public void Stop_server_command_saves_then_quits_and_a_failed_save_keeps_the_server_running()
    {
        var order = new List<string>();
        ServerStopCommand.SaveWorld = () => { order.Add("save"); return null; };
        ServerStopCommand.QuitNextFrame = () => order.Add("quit");
        var replies = new List<string>();
        Assert.True(ServerStopCommand.Execute(isAdmin: true, replies.Add));
        Assert.Equal(new[] { "save", "quit" }, order.ToArray());
        Assert.Contains(ServerStopCommand.StoppingReply, replies);

        order.Clear();
        replies.Clear();
        ServerStopCommand.SaveWorld = () => { order.Add("save"); return "disk full"; };
        Assert.False(ServerStopCommand.Execute(isAdmin: true, replies.Add));
        Assert.Equal(new[] { "save" }, order.ToArray());
        Assert.Contains(ServerStopCommand.SaveFailedPrefix + "disk full", replies);

        order.Clear();
        replies.Clear();
        ServerStopCommand.SaveWorld = () => throw new InvalidOperationException("no world");
        Assert.False(ServerStopCommand.Execute(isAdmin: true, replies.Add));
        Assert.DoesNotContain("quit", order);
        // Revert: quitting without a successful save loses everything since the last autosave.
    }

    // --- pane rows and readback ---

    [Fact]
    public void A_running_server_refuses_the_toggle_with_the_new_copy_and_offers_both_stop_rows()
    {
        InstallLedger();
        ServerInstall.RunningServerProbe = root => SameFolder(root) ? _exe : null;
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);

        var panel = new FakePanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.ServerRunningLine);
        Assert.Contains("Stop the server first (Stop server below), then install or remove.", (string)pane.ServerInstallReadback()["reason"]!);
        Assert.DoesNotContain(panel.Labels, l => l.Text.Contains("never stops it for you"));
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        var stop = panel.Buttons.Single(b => b.Text == ConfigurationPane.StopServerButton);
        var force = panel.Buttons.Single(b => b.Text == ConfigurationPane.StopServerForceButton);
        Assert.True(pane.IsServerInstallHandle(stop));
        Assert.True(pane.IsServerInstallHandle(force));

        var readback = pane.ServerInstallReadback();
        var process = Assert.IsType<Dictionary<string, object?>>(readback["process"]);
        Assert.Equal(true, process["running"]);
        Assert.Equal(4242, process["pid"]);
        Assert.Equal(_exe, process["path"]);
        Assert.Equal(false, process["graceful"]);
        Assert.Equal(false, process["launchRecord"]);
        var stopBlock = Assert.IsType<Dictionary<string, object?>>(readback["stop"]);
        Assert.Equal("idle", stopBlock["state"]);
        // Revert: hiding the stop rows behind the install refusal leaves the user with no way to stop it.
    }

    [Fact]
    public void The_force_prompt_asks_before_the_kill_and_Cancel_stops_nothing()
    {
        bool running = true;
        ServerInstall.RunningServerProbe = root => running && SameFolder(root) ? _exe : null;
        ServerProcessControl.RunningProbe = root => running && SameFolder(root) ? Live() : null;
        int kills = 0;
        ServerProcessControl.KillOverride = _ => { kills++; running = false; return true; };
        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);

        var panel = new FakePanel();
        pane.Build(panel);
        panel.Buttons.Single(b => b.Text == ConfigurationPane.StopServerForceButton).Fire();
        Assert.True(pane.ForceStopPromptOpen);
        Assert.Equal(0, kills);

        panel = new FakePanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.StopPromptHeading);
        Assert.Contains(
            panel.Labels,
            l => l.Text == "This ends VRisingServer.exe running from " + _server + " without a save. Unsaved progress since the last autosave is lost.");
        var stopBlock = Assert.IsType<Dictionary<string, object?>>(pane.ServerInstallReadback()["stop"]);
        Assert.Equal("force-prompt", stopBlock["state"]);

        panel.Buttons.Single(b => b.Text == ConfigurationPane.StopPromptCancelButton).Fire();
        Assert.False(pane.ForceStopPromptOpen);
        Assert.Equal(0, kills);
        Assert.True(running);

        Assert.True(pane.RequestForceStop().Completed);
        panel = new FakePanel();
        pane.Build(panel);
        panel.Buttons.Single(b => b.Text == ConfigurationPane.StopPromptStopButton).Fire();
        Assert.Equal(1, kills);
        Assert.False(pane.ForceStopPromptOpen);
        Assert.NotNull(ServerLaunchRecord.TryRead(_server));
        var after = Assert.IsType<Dictionary<string, object?>>(pane.ServerInstallReadback()["stop"]);
        Assert.Equal("stopped", after["state"]);
        // Revert: a Stop button without the prompt ends the process on one stray click.
    }

    [Fact]
    public void Start_server_appears_only_with_a_launch_record_and_says_so_otherwise()
    {
        ServerInstall.RunningServerProbe = _ => null;
        ServerProcessControl.RunningProbe = _ => null;
        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);

        var panel = new FakePanel();
        pane.Build(panel);
        Assert.DoesNotContain(panel.Buttons, b => b.Text == ConfigurationPane.StartServerButton);
        Assert.Contains(panel.Labels, l => l.Text == ServerProcessControl.StartUnavailableLine());

        ServerLaunchRecord.FromProcess(new ServerProcessInfo(1, _exe, "\"" + _exe + "\" -persistentDataPath \"" + Path.Combine(_server, "save") + "\" -saveName test1"), _now).Write(_server);
        ServerProcessControl.StartOverride = (_, _, _) => 777;
        panel = new FakePanel();
        pane.Build(panel);
        var start = panel.Buttons.Single(b => b.Text == ConfigurationPane.StartServerButton);
        Assert.True(pane.IsServerInstallHandle(start));
        start.Fire();
        Assert.Equal("Started (pid 777)", pane.ServerInstallLine);
        var process = Assert.IsType<Dictionary<string, object?>>(pane.ServerInstallReadback()["process"]);
        Assert.Equal(true, process["launchRecord"]);
        // Revert: offering Start without a record would have to invent a command line.
    }

    /// <summary>
    /// SERVERINST5. The port half of the gate now has a production value: Bootstrap records the
    /// address of each <c>GameHelper.LaunchGame</c> join and Core reads it through
    /// <c>BootstrapLink.LastConnectPort</c>. Here the link is a fake; what is tested is the gate's
    /// three answers — open, port mismatch (both values named), and a Bootstrap that records no port
    /// at all, which is a different sentence because a Core reload cannot fix it.
    /// </summary>
    [Fact]
    public void The_recorded_join_port_opens_the_gate_and_a_mismatch_names_both_ports()
    {
        HostSettings(9976);
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.ConnectPortRecorded = () => true;

        ServerProcessControl.ConnectedServerPort = () => 9976;
        Assert.True(ServerProcessControl.GracefulAvailable(_server, out string open), open);

        ServerProcessControl.ConnectedServerPort = () => 9877;
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string mismatch));
        Assert.Equal(
            "This client is connected to port 9877; that folder's server uses port 9976.",
            mismatch);

        // Nothing joined yet: the port is unknown, not "0". The folder's own port is still named.
        ServerProcessControl.ConnectedServerPort = () => 0;
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string none));
        Assert.Equal(
            "This client is connected to port unknown; that folder's server uses port 9976.",
            none);

        // A Bootstrap older than the connect record: named as such, and checked before the
        // comparison so it cannot be reported as a mismatch.
        ServerProcessControl.ConnectPortRecorded = () => false;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string tooOld));
        Assert.Equal(ServerProcessControl.ReasonBootstrapTooOld, tooOld);

        // A throwing marker fails closed to the same answer, never to an open gate.
        ServerProcessControl.ConnectPortRecorded = () => throw new InvalidOperationException("no link");
        Assert.False(ServerProcessControl.GracefulAvailable(_server, out string threw));
        Assert.Equal(ServerProcessControl.ReasonBootstrapTooOld, threw);

        // Unwired (every pre-SERVERINST5 caller): assume the record exists and compare ports.
        ServerProcessControl.ConnectPortRecorded = null;
        Assert.True(ServerProcessControl.GracefulAvailable(_server, out string unwired), unwired);
        // Revert: fold the too-old case back into the mismatch string and the pane sends the user
        // hunting a port problem that only a client restart on the new Bootstrap can clear.
    }

    /// <summary>
    /// SERVERINST5: <c>process.connectedTo</c>. The port comparison is a verdict; this is the
    /// evidence behind it, so a mismatch can be read without guessing which side is wrong.
    /// </summary>
    [Fact]
    public void The_readback_names_the_server_this_client_joined()
    {
        Assert.Equal(
            "SteamIPv4://127.0.0.1:9976",
            ServerProcessControl.FormatConnectedTo("SteamIPv4", "127.0.0.1", 9976));
        // A missing transport label still identifies the server; a missing address or port does not.
        Assert.Equal("127.0.0.1:9976", ServerProcessControl.FormatConnectedTo("", "127.0.0.1", 9976));
        Assert.Equal("", ServerProcessControl.FormatConnectedTo("SteamIPv4", "", 9976));
        Assert.Equal("", ServerProcessControl.FormatConnectedTo("SteamIPv4", "127.0.0.1", 0));

        // Unwired and throwing seams both read as "" rather than failing the whole readback.
        Assert.Equal("", ServerProcessControl.ConnectedTo());
        ServerProcessControl.ConnectedServerDescription = () => throw new InvalidOperationException("no link");
        Assert.Equal("", ServerProcessControl.ConnectedTo());

        InstallLedger();
        HostSettings(9976);
        ServerInstall.RunningServerProbe = _ => null;
        ServerProcessControl.RunningProbe = root => SameFolder(root) ? Live() : null;
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.ConnectedServerDescription = () =>
            ServerProcessControl.FormatConnectedTo("SteamIPv4", "127.0.0.1", 9976);

        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);
        var process = Assert.IsType<Dictionary<string, object?>>(pane.ServerInstallReadback()["process"]);
        Assert.Equal("SteamIPv4://127.0.0.1:9976", process["connectedTo"]);
        Assert.Equal(true, process["graceful"]);
        // Revert: drop connectedTo and `graceful: false` is unexplainable from the readback alone.
    }

    /// <summary>
    /// SERVERINST5: the production wiring itself. <c>StopServerEvents.Install</c>'s client branch is
    /// <c>#if !PROGENITOR_TESTS</c> (it calls <c>CustomEvents</c>), so the seam assignment is checked
    /// against the source text — the same route <c>HarmonySlotHandoffTests</c> uses for Bootstrap
    /// bodies that cannot compile here. Without this, the port seam could go back to being unset in
    /// production with the whole unit suite still green, which is exactly the defect SERVERINST5 fixes.
    /// </summary>
    [Fact]
    public void The_client_branch_wires_the_connect_seams_from_the_link_and_nulls_them_on_uninstall()
    {
        string root = RepositoryRoot();
        string source = File.ReadAllText(
            Path.Combine(root, "src", "Progenitor.Core", "Install", "StopServerEvents.cs"));
        int install = source.IndexOf("internal static unsafe void Install()", StringComparison.Ordinal);
        int uninstall = source.IndexOf("internal static unsafe void Uninstall()", StringComparison.Ordinal);
        Assert.True(install > 0 && uninstall > install, "Install/Uninstall not found in StopServerEvents.cs");
        string installBody = source.Substring(install, uninstall - install);
        string uninstallBody = source.Substring(uninstall);

        Assert.Contains("ServerProcessControl.ConnectedServerPort = () => BootstrapLink.LastConnectPort;", installBody);
        Assert.Contains("ServerProcessControl.ConnectPortRecorded = () => BootstrapLink.RecordsConnectPort;", installBody);
        Assert.Contains("ServerProcessControl.ConnectedServerDescription", installBody);
        Assert.Contains("ServerProcessControl.ConnectedServerPort = null;", uninstallBody);
        Assert.Contains("ServerProcessControl.ConnectPortRecorded = null;", uninstallBody);
        Assert.Contains("ServerProcessControl.ConnectedServerDescription = null;", uninstallBody);

        // The Bootstrap side of the same wire: the record itself, and the capability marker Core
        // keys the too-old sentence on.
        string host = File.ReadAllText(
            Path.Combine(root, "src", "Progenitor.Bootstrap", "ProcessHost.cs"));
        Assert.Contains("public const int ConnectRecordCapability = 1;", host);
        Assert.Contains("public static void NoteConnect(string? protocol, string? address, int port)", host);
        string hook = File.ReadAllText(
            Path.Combine(root, "src", "Progenitor.Bootstrap", "HarmonyPatches.cs"));
        Assert.Contains("ProcessHost.NoteConnect(protocol, host, port);", hook);
        // Core must not bind HarmonySlotHandoff.LaunchGame: Dev's HostCapture owns that slot and a
        // second owner is refused.
        string stopEvents = source;
        Assert.DoesNotContain("HarmonySlotHandoff.LaunchGame", stopEvents);
        // Revert: unset either seam in production and Stop server always falls to the force prompt,
        // which is a kill with no save.
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// SERVERINST4 readback: <c>process.commandLine</c> and the <c>stop.states</c> trail. The
    /// outcome alone cannot tell an operator that the server answered and saved; the trail can.
    /// </summary>
    [Fact]
    public void The_readback_carries_the_redacted_command_line_and_the_whole_stop_state_trail()
    {
        InstallLedger();
        HostSettings(9976);
        bool running = true;
        ServerInstall.RunningServerProbe = _ => null;
        ServerProcessControl.RunningProbe = root => running && SameFolder(root) ? Live() : null;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.PollReply = nonce =>
        {
            running = false;
            return StopServerReply.For(nonce, accepted: true, StopReplyReason.Saving);
        };

        var pane = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);

        var process = Assert.IsType<Dictionary<string, object?>>(pane.ServerInstallReadback()["process"]);
        // The image token is already `path`; the readback carries the arguments only.
        Assert.Equal("-saveName test1", process["commandLine"]);

        pane.StopServer();
        var stop = Assert.IsType<Dictionary<string, object?>>(pane.ServerInstallReadback()["stop"]);
        Assert.Equal("stopped", stop["state"]);
        Assert.Equal(
            new[] { "requested", "replied", "saving", "stopped" },
            Assert.IsType<List<string>>(stop["states"]).ToArray());
        // Revert: report only the final state and `stopped` is indistinguishable from a server that
        // vanished without ever answering or saving.
    }

    private ServerProcessInfo Live() => new(4242, _exe, "\"" + _exe + "\" -saveName test1");

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

    private ConfigurationPane Pane()
    {
        string cfgDir = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(cfgDir);
        var file = new ConfigFile(Path.Combine(cfgDir, ConfigHelper.SharedFileName), saveOnInit: true);
        var bindings = ConfigurationPane.Bind(file);
        return new ConfigurationPane(bindings, new LocalServerFolder(), new object(), scan: null, source: null);
    }

    private sealed class FakePanel : IUiPanel
    {
        public List<FakeLabel> Labels { get; } = new();
        public List<FakeButton> Buttons { get; } = new();
        public List<FakeToggle> Toggles { get; } = new();
        public string ContentAlignment { get; set; } = SettingsAlign.Centre;
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "Configuration";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public UiCaptureMode CaptureMode { get; set; }

        public IUiButton Button(string text)
        {
            var b = new FakeButton { Text = text ?? "" };
            Buttons.Add(b);
            return b;
        }

        public IUiInput Input(string text, string initial = "") => new FakeInput { Text = text ?? "", Value = initial ?? "" };

        public IUiLabel Label(string text)
        {
            var l = new FakeLabel { Text = text ?? "" };
            Labels.Add(l);
            return l;
        }

        public IUiLabel Heading(string text) => Label(text);

        public IUiLabel Note(string text) => Label(text);

        public IUiToggle Toggle(string text, bool initial = false)
        {
            var t = new FakeToggle { Text = text ?? "", Value = initial };
            Toggles.Add(t);
            return t;
        }

        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();
        public void BeginInline() { }
        public void EndInline() { }
        public void Show() { }
        public void Hide() { }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeLabel : IUiLabel
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeButton : IUiButton
    {
        private Action? _handler;
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnClick(Action handler) { _handler = handler; return this; }
        public void Fire() => _handler?.Invoke();
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeToggle : IUiToggle
    {
        private Action<bool>? _handler;
        public string Text { get; set; } = "";
        public bool Value { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<bool> handler) { _handler = handler; return this; }
        public void Fire(bool value) { Value = value; _handler?.Invoke(value); }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeInput : IUiInput
    {
        public string Text { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<string> handler) => this;
        public IDisposable OnSubmit(Action<string> handler) => this;
        public void Dispose() => IsDisposed = true;
    }
}
