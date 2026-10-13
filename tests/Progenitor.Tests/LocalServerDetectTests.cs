using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Install;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LOCALDETECT1 (decided 2026-10-08): any local Progenitor server folder is detected as local automatically. The
/// detector's checks with injected UDP-table, process and folder data, the route decision it feeds, the per-port cache and
/// the picker. Temp folders and fakes only: no process is read, started or signaled.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class LocalServerDetectTests : IDisposable
{
    private readonly string _dir;
    private readonly string _serverA;
    private readonly string _serverB;
    private readonly Dictionary<int, IReadOnlyCollection<int>?> _owners = new();
    private readonly Dictionary<int, ServerProcessInfo> _processes = new();

    public LocalServerDetectTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-detect-" + Guid.NewGuid().ToString("N"));
        _serverA = MakeServer("ServerA", "Alpha Test", 9876);
        _serverB = MakeServer("ServerB", "Bravo Test", 9900);
        ServerProcessControl.ResetForTests();
        LocalServerDetector.ResetForTests();
        LocalServerFolder.WriteAccessOverride = _ => true;
        LocalServerDetector.PortOwnersProbe = port => _owners.TryGetValue(port, out var o) ? o : Array.Empty<int>();
        LocalServerDetector.ProcessProbe = pid => _processes.TryGetValue(pid, out var p) ? p : null;
        LocalServerDetector.DefaultSaveRootProbe = () => Path.Combine(_dir, "default-save");
        ServerProcessControl.DefaultSaveRootForTests = Path.Combine(_dir, "default-save");
        WorldRestorePaths.LiveRootForTests = Path.Combine(_dir, "LiveServerStandIn");
        WorldRestorePaths.ForgetLiveRootForms();
    }

    public void Dispose()
    {
        LocalServerDetector.ResetForTests();
        ServerProcessControl.ResetForTests();
        LocalServerFolder.WriteAccessOverride = null;
        WorldRestorePaths.LiveRootForTests = null;
        WorldRestorePaths.ForgetLiveRootForms();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private string MakeServer(string folder, string name, int port, bool progenitor = true)
    {
        string root = Path.Combine(_dir, folder);
        Directory.CreateDirectory(Path.Combine(root, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(root, "Settings"));
        File.WriteAllText(Path.Combine(root, LocalServerFolder.ServerExecutableName), "exe");
        if (progenitor) File.WriteAllText(Path.Combine(root, "BepInEx", "plugins", LocalServerDetector.BootstrapDllName), "dll");
        WriteSettings(Path.Combine(root, "Settings"), name, port);
        return root;
    }

    private static void WriteSettings(string settingsDir, string name, int port)
    {
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, "ServerHostSettings.json"),
            "{ \"Name\": \"" + name + "\", \"Port\": " + port + ", \"QueryPort\": " + (port + 1) + " }");
    }

    /// <summary>A server process on <paramref name="port"/>; it gets its own save folder unless the arguments name one.</summary>
    private void Run(int pid, string root, int port, string args = "", bool ownSaveFolder = true)
    {
        string exe = Path.Combine(root, LocalServerFolder.ServerExecutableName);
        if (ownSaveFolder && args.IndexOf("-persistentDataPath", StringComparison.OrdinalIgnoreCase) < 0)
            args = (args + " -persistentDataPath \"" + Path.Combine(root, "save-data") + "\"").Trim();
        _processes[pid] = new ServerProcessInfo(pid, exe, "\"" + exe + "\"" + (args.Length > 0 ? " " + args : ""), root);
        _owners[port] = new[] { pid };
    }

    // ---- the detector --------------------------------------------------------------------------------------------

    [Fact]
    public void Two_local_servers_on_different_ports_each_resolve_to_their_own_folder()
    {
        Run(100, _serverA, 9876);
        Run(200, _serverB, 9900);

        var a = LocalServerDetector.Detect(addressIsThisMachine: true, 9876);
        var b = LocalServerDetector.Detect(addressIsThisMachine: true, 9900);

        Assert.True(a.Found, a.Why);
        Assert.True(b.Found, b.Why);
        Assert.Equal(_serverA, a.Root, ignoreCase: true);
        Assert.Equal(_serverB, b.Root, ignoreCase: true);
        Assert.Equal(Path.Combine(_serverB, "BepInEx"), b.BepInExRoot, ignoreCase: true);
        Assert.Equal("Alpha Test", a.Name);
        Assert.Equal("Bravo Test", b.Name);
        Assert.Equal(200, b.Pid);
    }

    [Fact]
    public void A_joined_address_that_is_not_this_machine_is_never_detected()
    {
        Run(100, _serverA, 9876);
        var d = LocalServerDetector.Detect(addressIsThisMachine: false, 9876);
        Assert.False(d.Found);
        Assert.Contains("not this machine", d.Why);
    }

    [Fact]
    public void An_unreadable_udp_table_is_not_detected()
    {
        Run(100, _serverA, 9876);
        _owners[9876] = null;
        var d = LocalServerDetector.Detect(true, 9876);
        Assert.False(d.Found);
        Assert.Contains("could not be read", d.Why);
    }

    [Fact]
    public void Two_processes_on_the_port_are_ambiguous()
    {
        Run(100, _serverA, 9876);
        Run(200, _serverB, 9900);
        _owners[9876] = new[] { 100, 200 };
        var d = LocalServerDetector.Detect(true, 9876);
        Assert.False(d.Found);
        Assert.Contains("ambiguous", d.Why);
    }

    [Fact]
    public void A_port_held_by_another_program_is_not_a_server()
    {
        string other = Path.Combine(_dir, "Other");
        Directory.CreateDirectory(other);
        _processes[300] = new ServerProcessInfo(300, Path.Combine(other, "SomethingElse.exe"), "x", other);
        _owners[9876] = new[] { 300 };
        var d = LocalServerDetector.Detect(true, 9876);
        Assert.False(d.Found);
        Assert.Contains("not " + LocalServerFolder.ServerExecutableName, d.Why);
    }

    [Fact]
    public void An_unreadable_process_path_is_not_detected()
    {
        _owners[9876] = new[] { 400 };
        var d = LocalServerDetector.Detect(true, 9876);
        Assert.False(d.Found);
        Assert.Contains("could not be read", d.Why);
    }

    [Fact]
    public void The_live_server_folder_is_never_detected()
    {
        string live = Path.Combine(_dir, "LiveServerStandIn");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(live, "BepInEx", "plugins"));
        File.WriteAllText(Path.Combine(live, "BepInEx", "plugins", LocalServerDetector.BootstrapDllName), "dll");
        WriteSettings(Path.Combine(live, "Settings"), "Production", 9876);
        Run(100, live, 9876);

        var d = LocalServerDetector.Detect(true, 9876);

        Assert.False(d.Found);
        Assert.Contains("live dedicated server", d.Why);
        Assert.Equal("", d.Root);
    }

    [Fact]
    public void A_server_whose_save_data_is_the_live_servers_is_never_detected()
    {
        string liveData = Path.Combine(_dir, "LiveServerStandIn", "save-data");
        WriteSettings(Path.Combine(liveData, "Settings"), "Production", 9876);
        Run(100, _serverA, 9876, "-persistentDataPath \"" + liveData + "\"");

        var d = LocalServerDetector.Detect(true, 9876);

        Assert.False(d.Found);
        Assert.Contains("live dedicated server", d.Why);
    }

    [Fact]
    public void Its_own_settings_must_name_the_joined_port()
    {
        Run(100, _serverA, 9999);
        var d = LocalServerDetector.Detect(true, 9999);
        Assert.False(d.Found);
        Assert.Contains("9876", d.Why);
    }

    [Fact]
    public void The_persistent_data_path_settings_win_over_the_folders_own()
    {
        string data = Path.Combine(_dir, "data-a");
        WriteSettings(Path.Combine(data, "Settings"), "Alpha Moved", 9950);
        Run(100, _serverA, 9950, "-persistentDataPath \"" + data + "\" -serverName \"Alpha Override\"");

        var d = LocalServerDetector.Detect(true, 9950);

        Assert.True(d.Found, d.Why);
        Assert.Equal(_serverA, d.Root, ignoreCase: true);
        // -serverName on the command line names the server (the server advertises it).
        Assert.Equal("Alpha Override", d.Name);
    }

    [Fact]
    public void A_folder_without_progenitor_is_not_detected()
    {
        string plain = MakeServer("Plain", "Vanilla", 9960, progenitor: false);
        Run(100, plain, 9960);
        var d = LocalServerDetector.Detect(true, 9960);
        Assert.False(d.Found);
        Assert.Contains("Progenitor is not installed", d.Why);
    }

    [Fact]
    public void Progenitor_in_a_package_folder_under_plugins_counts()
    {
        string packaged = MakeServer("Packaged", "Packaged", 9970, progenitor: false);
        string sub = Path.Combine(packaged, "BepInEx", "plugins", "Progenitor-Progenitor");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, LocalServerDetector.BootstrapDllName), "dll");
        Run(100, packaged, 9970);
        Assert.True(LocalServerDetector.Detect(true, 9970).Found);
    }

    [Fact]
    public void A_folder_without_bepinex_is_not_detected()
    {
        string bare = Path.Combine(_dir, "Bare");
        Directory.CreateDirectory(Path.Combine(bare, "Settings"));
        File.WriteAllText(Path.Combine(bare, LocalServerFolder.ServerExecutableName), "exe");
        WriteSettings(Path.Combine(bare, "Settings"), "Bare", 9980);
        Run(100, bare, 9980);
        var d = LocalServerDetector.Detect(true, 9980);
        Assert.False(d.Found);
        Assert.Contains(LocalServerFolder.RefusalMissingBepInEx, d.Why);
    }

    // ---- the route decision ----------------------------------------------------------------------------------------

    private static ServerRouteFacts Facts(string configured = "", int folderPort = 0, int port = 9900, bool local = true,
        bool handshake = true, string detected = "", int detectedPort = 0, bool treatRemote = false, string note = "") => new()
    {
        ConfiguredPath = configured,
        PathValid = configured.Length > 0,
        CanonicalPath = configured,
        FolderPort = folderPort,
        Connected = true,
        ConnectedAddress = local ? "127.0.0.1" : "203.0.113.5",
        ConnectedPort = port,
        IsLocalMachine = local,
        HandshakeOk = handshake,
        DetectedFolder = detected,
        DetectedPort = detectedPort,
        DetectedName = detected.Length > 0 ? "Bravo Test" : "",
        DetectedNote = note,
        TreatLocalAsRemote = treatRemote,
    };

    [Fact]
    public void A_detected_server_on_another_port_than_the_configured_folder_is_the_local_route()
    {
        var d = ServerRouteSelector.Decide(Facts(configured: _serverA, folderPort: 9876, port: 9900, detected: _serverB, detectedPort: 9900));
        Assert.Equal(ServerRouteKind.Local, d.Route.Kind);
        Assert.True(d.ConnectedIsLocal);
        Assert.Equal(LocalFolderSource.Detected, d.Source);
        Assert.Equal(_serverB, d.LocalFolder);
        Assert.Contains("auto-detected", d.Why);
    }

    [Fact]
    public void A_detected_server_needs_no_configured_folder()
    {
        var d = ServerRouteSelector.Decide(Facts(detected: _serverB, detectedPort: 9900));
        Assert.Equal(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal("detected", d.SourceName);
    }

    [Fact]
    public void The_process_that_owns_the_port_wins_over_a_configured_folder_matching_by_settings_alone()
    {
        var d = ServerRouteSelector.Decide(Facts(configured: _serverA, folderPort: 9900, port: 9900, detected: _serverB, detectedPort: 9900));
        Assert.Equal(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal(LocalFolderSource.Detected, d.Source);
        Assert.Equal(_serverB, d.LocalFolder);
    }

    [Fact]
    public void The_configured_folder_that_is_the_detected_one_stays_configured()
    {
        var d = ServerRouteSelector.Decide(Facts(configured: _serverB, folderPort: 9900, port: 9900, detected: _serverB + "\\", detectedPort: 9900));
        Assert.Equal(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal(LocalFolderSource.Configured, d.Source);
        Assert.Equal(_serverB, d.LocalFolder);
    }

    [Fact]
    public void Without_a_detection_the_configured_folder_is_the_fallback()
    {
        var d = ServerRouteSelector.Decide(Facts(configured: _serverA, folderPort: 9900, port: 9900, note: "2 processes hold UDP port 9900, so the server is ambiguous"));
        Assert.Equal(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal(LocalFolderSource.Configured, d.Source);
        Assert.Equal(_serverA, d.LocalFolder);
    }

    [Fact]
    public void Without_a_detection_or_a_matching_folder_the_route_is_remote_and_says_why()
    {
        var d = ServerRouteSelector.Decide(Facts(configured: _serverA, folderPort: 9876, port: 9900, note: "Progenitor is not installed"));
        Assert.NotEqual(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal(LocalFolderSource.None, d.Source);
        Assert.Equal("", d.LocalFolder);
        Assert.Contains("not auto-detected: Progenitor is not installed", d.Why);
    }

    [Theory]
    [InlineData(false, true, 9900)]  // the joined address is not this machine
    [InlineData(true, false, 9900)]  // no Progenitor handshake
    [InlineData(true, true, 9876)]   // the detection answered for another port
    public void A_detection_applies_only_to_this_machine_the_joined_port_and_a_progenitor_server(bool local, bool handshake, int detectedPort)
    {
        var d = ServerRouteSelector.Decide(Facts(port: 9900, local: local, handshake: handshake, detected: _serverB, detectedPort: detectedPort));
        Assert.NotEqual(ServerRouteKind.Local, d.Route.Kind);
        Assert.NotEqual(LocalFolderSource.Detected, d.Source);
        Assert.Equal("", d.LocalFolder);
    }

    [Fact]
    public void Treat_local_server_as_remote_still_forces_a_detected_server_remote()
    {
        var d = ServerRouteSelector.Decide(Facts(detected: _serverB, detectedPort: 9900, treatRemote: true));
        Assert.True(d.Route.ForcedRemote);
        Assert.NotEqual(ServerRouteKind.Local, d.Route.Kind);
        Assert.True(d.ConnectedIsLocal);
    }

    [Fact]
    public void A_detected_folder_that_is_the_live_server_is_refused_by_the_route_too()
    {
        string live = Path.Combine(_dir, "LiveServerStandIn");
        Directory.CreateDirectory(live);
        var d = ServerRouteSelector.Decide(Facts(detected: live, detectedPort: 9900));
        Assert.NotEqual(ServerRouteKind.Local, d.Route.Kind);
        Assert.Contains("live dedicated server", d.Why);
    }

    [Fact]
    public void Not_connected_the_configured_folder_is_the_route()
    {
        var facts = new ServerRouteFacts { ConfiguredPath = _serverA, PathValid = true, CanonicalPath = _serverA };
        var d = ServerRouteSelector.Decide(facts);
        Assert.Equal(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal(LocalFolderSource.Configured, d.Source);
        Assert.Equal(_serverA, d.LocalFolder);
    }

    // ---- the per-connection cache -----------------------------------------------------------------------------------

    [Fact]
    public void The_cache_answers_per_joined_port_and_never_blocks()
    {
        Run(100, _serverA, 9876);
        Run(200, _serverB, 9900);
        var queued = new List<Action>();
        LocalServerDetectCache.Scheduler = queued.Add;
        var lines = new List<string>();
        LocalServerDetectCache.LogSink = lines.Add;

        // The first read starts a refresh and reads nothing yet: not detected.
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        Assert.Single(queued);
        queued[0]();
        queued.Clear();
        Assert.Equal(_serverA, LocalServerDetectCache.Get(true, 9876)!.Root, ignoreCase: true);
        Assert.Contains(lines, l => l.Contains("auto-detected for joined port 9876"));

        // A connection to the other local server: the old port's answer is never served for it.
        Assert.Null(LocalServerDetectCache.Get(true, 9900));
        Assert.Single(queued);
        queued[0]();
        queued.Clear();
        Assert.Equal(_serverB, LocalServerDetectCache.Get(true, 9900)!.Root, ignoreCase: true);

        // Not this machine: no detection at all.
        Assert.Null(LocalServerDetectCache.Get(false, 9900));
    }

    [Fact]
    public void A_join_boundary_refreshes_the_answer()
    {
        Run(100, _serverA, 9876);
        LocalServerDetectCache.Scheduler = run => run();
        LocalServerDetectCache.LogSink = _ => { };
        LocalServerDetectCache.Get(true, 9876);
        Assert.True(LocalServerDetectCache.Get(true, 9876)!.Found);

        // The server on that port is now another folder's (the first stopped, the second started on the same port).
        Run(200, _serverB, 9876);
        WriteSettings(Path.Combine(_serverB, "Settings"), "Bravo Test", 9876);
        var queued = new List<Action>();
        LocalServerDetectCache.Scheduler = queued.Add;
        LocalServerDetectCache.RefreshAll();
        // Review: the very first read after the join boundary is "not detected", never server A's folder.
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(_serverB, LocalServerDetectCache.Get(true, 9876)!.Root, ignoreCase: true);
    }

    [Fact]
    public void A_read_in_flight_across_a_join_boundary_or_a_clear_is_discarded()
    {
        Run(100, _serverA, 9876);
        var queued = new List<Action>();
        LocalServerDetectCache.Scheduler = queued.Add;
        LocalServerDetectCache.LogSink = _ => { };

        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        var stale = queued[0];
        queued.Clear();
        LocalServerDetectCache.RefreshAll();
        // A new generation starts its own refresh even though the old one has not landed.
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        Assert.Single(queued);
        stale(); // lands after the boundary: discarded
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        queued[0]();
        Assert.True(LocalServerDetectCache.Get(true, 9876)!.Found);

        // Core unload: everything goes, and a read that lands afterwards writes nothing.
        queued.Clear();
        LocalServerDetectCache.RefreshAll(); // starts its refresh at once
        Assert.Single(queued);
        var late = queued[0];
        LocalServerDetectCache.Clear();
        LocalServerDetectCache.Scheduler = _ => { };
        late();
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
    }

    /// <summary>
    /// Review (live 2026-10-10): the join boundary re-runs detection at once instead of waiting for the next read, and an
    /// answer made just before it is reused only while the same process still holds the port.
    /// </summary>
    [Fact]
    public void A_join_boundary_redetects_at_once_and_reuses_only_the_same_process()
    {
        Run(100, _serverA, 9876);
        LocalServerDetectCache.LogSink = _ => { };
        var queued = new List<Action>();
        LocalServerDetectCache.Scheduler = queued.Add;
        LocalServerDetectCache.Get(true, 9876);
        queued[0]();
        queued.Clear();
        Assert.True(LocalServerDetectCache.Get(true, 9876)!.Found);

        int computed = 0;
        LocalServerDetectCache.Compute = (local, port) => { computed++; return LocalServerDetector.Detect(local, port); };

        // Same process on the port: the refresh starts with the boundary, and reuses the answer without a new detection.
        LocalServerDetectCache.RefreshAll();
        Assert.Single(queued);
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        queued[0]();
        queued.Clear();
        Assert.Equal(0, computed);
        Assert.Equal(_serverA, LocalServerDetectCache.Get(true, 9876)!.Root, ignoreCase: true);

        // Another process on the port: detected afresh, never the reused folder.
        Run(200, _serverB, 9876);
        WriteSettings(Path.Combine(_serverB, "Settings"), "Bravo Test", 9876);
        LocalServerDetectCache.RefreshAll();
        Assert.Null(LocalServerDetectCache.Get(true, 9876));
        queued[0]();
        Assert.Equal(1, computed);
        Assert.Equal(_serverB, LocalServerDetectCache.Get(true, 9876)!.Root, ignoreCase: true);
    }

    [Fact]
    public void The_same_process_test_fails_on_a_second_holder_or_another_path()
    {
        Run(100, _serverA, 9876);
        var d = LocalServerDetector.Detect(true, 9876);
        Assert.True(LocalServerDetector.StillHolds(d));
        _owners[9876] = new[] { 100, 300 };
        Assert.False(LocalServerDetector.StillHolds(d));
        _owners[9876] = new[] { 100 };
        _processes[100] = new ServerProcessInfo(100, Path.Combine(_serverB, LocalServerFolder.ServerExecutableName), "x", _serverB);
        Assert.False(LocalServerDetector.StillHolds(d));
        _owners[9876] = null;
        Assert.False(LocalServerDetector.StillHolds(d));
    }

    /// <summary>
    /// Review (live 2026-10-10): while joining or connected, the configured folder is the route only when its port is the
    /// joined port. Detection pending (or refused) on another port is "not local", never the configured folder.
    /// </summary>
    [Theory]
    [InlineData("detection pending")]
    [InlineData("Progenitor is not installed")]
    public void While_connected_elsewhere_the_configured_folder_is_never_the_route(string note)
    {
        var d = ServerRouteSelector.Decide(Facts(configured: _serverA, folderPort: 9976, port: 9986, note: note));
        Assert.NotEqual(ServerRouteKind.Local, d.Route.Kind);
        Assert.Equal(LocalFolderSource.None, d.Source);
        Assert.Equal("", d.LocalFolder);
        Assert.False(d.ConnectedIsLocal);
    }

    // ---- fail closed on unknown arguments and the default save folder -----------------------------------------------

    [Fact]
    public void An_unreadable_command_line_is_refused()
    {
        string exe = Path.Combine(_serverA, LocalServerFolder.ServerExecutableName);
        _processes[100] = new ServerProcessInfo(100, exe, "", _serverA);
        _owners[9876] = new[] { 100 };
        var d = LocalServerDetector.Detect(true, 9876);
        Assert.False(d.Found);
        Assert.Contains("command line", d.Why);
    }

    [Fact]
    public void A_server_on_the_default_save_folder_is_refused()
    {
        Run(100, _serverA, 9876, ownSaveFolder: false);
        var none = LocalServerDetector.Detect(true, 9876);
        Assert.False(none.Found);
        Assert.Equal(ServerProcessControl.ReasonNoPersistentDataPath, none.Why);

        ServerProcessControl.DefaultSaveRootForTests = Path.Combine(_dir, "default-save");
        Run(100, _serverA, 9876, "-persistentDataPath \"" + Path.Combine(_dir, "default-save", "Saves") + "\"");
        var dflt = LocalServerDetector.Detect(true, 9876);
        Assert.False(dflt.Found);
        Assert.Equal(ServerProcessControl.ReasonDefaultSavePath, dflt.Why);
    }

    [Fact]
    public void The_stop_gate_for_a_detected_server_fails_closed()
    {
        string exe = Path.Combine(_serverA, LocalServerFolder.ServerExecutableName);
        var current = new ServerProcessInfo(100, exe, "", _serverA);
        ServerProcessControl.RunningProbe = _ => current;
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.ConnectedServerPort = () => 9876;
        ServerProcessControl.ProgenitorHandshakeOk = () => true;
        ServerProcessControl.UdpOwnerProbe = _ => new[] { 100 };
        ServerProcessControl.DefaultSaveRootForTests = Path.Combine(_dir, "default-save");

        // The ordinary gate passes this process; the detected one refuses its unread command line.
        Assert.True(ServerProcessControl.GracefulAvailable(_serverA, out _));
        Assert.False(ServerProcessControl.GracefulAvailableDetected(_serverA, out string why));
        Assert.Equal(ServerProcessControl.ReasonCommandLineUnknown, why);

        current = new ServerProcessInfo(100, exe, "\"" + exe + "\" -serverName X", _serverA);
        Assert.False(ServerProcessControl.GracefulAvailableDetected(_serverA, out why));
        Assert.Equal(ServerProcessControl.ReasonNoPersistentDataPath, why);

        current = new ServerProcessInfo(100, exe, "\"" + exe + "\" -persistentDataPath \"" + Path.Combine(_serverA, "save-data") + "\"", _serverA);
        Assert.True(ServerProcessControl.GracefulAvailableDetected(_serverA, out why), why);
    }

    // ---- the connected server's identity ------------------------------------------------------------------------------

    [Theory]
    [InlineData("KEY-B", "KEY-B", "Bravo Test", true)]
    [InlineData("KEY-A", "KEY-B", "Bravo Test", false)]   // a known operator key that differs
    [InlineData("", "KEY-B", "Alpha Test", false)]        // a known name that differs
    [InlineData("", "KEY-B", "V Rising", true)]           // the catalog's default name names no server
    [InlineData("", "", "", true)]                        // nothing known: the port owner decided
    public void The_connected_servers_identity_must_match_the_detected_folder(string connectedKey, string folderKey, string connectedName, bool local)
    {
        var f = Facts(detected: _serverB, detectedPort: 9900);
        var facts = new ServerRouteFacts
        {
            Connected = f.Connected, ConnectedAddress = f.ConnectedAddress, ConnectedPort = f.ConnectedPort, IsLocalMachine = true,
            HandshakeOk = true, DetectedFolder = _serverB, DetectedPort = 9900, DetectedName = "Bravo Test",
            DetectedOperatorKey = folderKey, ConnectedOperatorKey = connectedKey, ConnectedServerName = connectedName,
        };
        var d = ServerRouteSelector.Decide(facts);
        Assert.Equal(local, d.Route.Kind == ServerRouteKind.Local);
        Assert.Equal(local ? LocalFolderSource.Detected : LocalFolderSource.None, d.Source);
        if (!local) Assert.Contains("not auto-detected", d.Why);
    }

    // ---- the picker ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_picker_names_the_detected_folder_as_the_connected_server()
    {
        var inputs = new ServerPickerInputs
        {
            Connected = true,
            ConnectedServer = ServerIdentity.Connected("", "127.0.0.1", 9900),
            ConfiguredPath = _serverA,
            ConfiguredName = "Alpha Test",
            ConfiguredPort = 9876,
            ConnectedPort = 9900,
            ConnectedIsThisMachine = true,
            DetectedFolder = _serverB,
            DetectedName = "Bravo Test",
            DetectedPort = 9900,
        };
        var folder = ServerPicker.ConnectedFolder(inputs);
        Assert.NotNull(folder);
        Assert.Equal(_serverB, folder!.Value.Path);
        Assert.Equal("Bravo Test", ServerPicker.ConnectedName(inputs));
        var entries = ServerPicker.Build(inputs);
        Assert.Equal(ServerPickKind.Connected, entries[0].Kind);
        Assert.Equal(_serverB, entries[0].Path);
        // The configured folder is still offered on its own.
        Assert.Contains(entries, e => e.Kind == ServerPickKind.LocalFolder && ServerPicker.SameId(e.Path, _serverA));
    }
}
