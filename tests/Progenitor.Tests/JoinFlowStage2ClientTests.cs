using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW2 end to end through the client and a fake runtime: a refused join's Thunderstore row behind the
/// confirmation (recorded with the trust store), a batch whose result offers Restart and rejoin, the bridge's dry
/// run writing the rejoin record and the relaunch command without quitting, the after-relaunch pickup connecting to
/// the same server, and Join now. Same collection as the other <c>ModManifest</c> suites.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinFlowStage2ClientTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private const string Package = "ExampleTeam-Progenitor_Sample";
    private readonly string _dir;
    private readonly Runtime _runtime = new();
    private readonly FakeInstaller _installer = new();
    private readonly FakeRelauncher _relauncher = new();
    private readonly FakeHost _host = new();
    private double _now = 100;

    public JoinFlowStage2ClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-joinflow2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        JoinFlowPanel.Install();
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.InstallerFactory = _ => _installer;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren" };
        JoinFlowClient.Relauncher = _relauncher;
        JoinFlowClient.ConnectHost = _host;
    }

    public void Dispose()
    {
        JoinFlowPanel.Uninstall();
        JoinFlowClient.Uninstall();
        JoinFlowClient.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestConnectHost = null;
        ModManifestDeny.TestServerAsked = null;
        ModManifestDeny.TestProvided = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private sealed class FakeInstaller : IJoinPackageInstaller
    {
        public JoinInstallPhase Result = JoinInstallPhase.AfterRestart;
        public readonly List<string> Installed = new();

        public Task<JoinInstallOutcome> InstallAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress, CancellationToken ct)
        {
            lock (Installed) Installed.Add(request.PackageVersion);
            progress(JoinInstallPhase.Downloading, 40);
            return Task.FromResult(new JoinInstallOutcome(Result, "", Sample, "x"));
        }
    }

    private sealed class FakeRelauncher : IJoinRelauncher
    {
        public readonly List<string> Calls = new();

        public RelaunchPlan Plan() => new()
        {
            Mode = RelaunchMode.Automatic,
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            Arguments = RelaunchCommand.Arguments(RelaunchCommand.Script(4242, 1, @"C:\Games\VRising\VRising.exe")),
            Script = RelaunchCommand.Script(4242, 1, @"C:\Games\VRising\VRising.exe"),
        };

        public void Start(RelaunchPlan plan, Action<bool, string> done)
        {
            Calls.Add("start");
            done(true, "");
        }

        public void Quit(double delaySeconds) => Calls.Add("quit");
    }

    private sealed class FakeHost : IJoinConnectHost
    {
        public bool World;
        public bool Menu = true;
        public bool Joined;
        public readonly List<string> Calls = new();

        public bool WorldExists => World;
        public bool MainMenuReady => Menu && !World;
        public bool JoinedOk => Joined && World;
        public void RequestLeave() => Calls.Add("leave");
        public void RequestMainMenu() => Calls.Add("menu");

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("launch:" + target.Protocol + "://" + target.Endpoint);
            return true;
        }

        public bool LaunchPasswordPrompt(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("prompt:" + target.Protocol + "://" + target.Endpoint);
            return true;
        }
    }

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    /// <summary>The signed descriptor a refusing server sends, with a Thunderstore package for the failing row.</summary>
    private static byte[] Descriptor()
    {
        using var key = ModManifestCrypto.Generate();
        var overrides = new ManifestOverrides
        {
            ServerName = "Night Garden",
            Mods = new[] { new ManifestModOverride { Id = Sample, Version = "0.1.0", Package = Package } },
        };
        var loaded = new[] { new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        var section = new JoinSection("", null, new[] { new RecommendedMod("maps.mod", "Owner-Maps", "1.0.0", "Better maps") }, new JoinServerZone("UTC", 0));
        var envelope = ModManifest.Build(loaded, overrides, key, DateTime.UtcNow, section);
        return ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope));
    }

    private void Refused()
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch: progenitor.sample: expected 0.1.0, got missing", "127.0.0.1:9976");
        ModManifest.AcceptEnvelope(Descriptor());
        var s = JoinFlowClient.Session!;
        if (s.Page == JoinPage.Message) s.Continue();
        Assert.Equal(JoinPage.Mods, JoinFlowClient.Session!.Page);
    }

    private void WaitForBatch()
    {
        Assert.True(SpinWait.SpinUntil(() => JoinFlowClient.InstallStateOf(Sample)?.Terminal == true, 5000));
        Thread.Sleep(20);
        _now += 1;
        JoinFlowClient.Tick();
    }

    private string TrustFile() => File.ReadAllText(Path.Combine(_dir, ModManifest.TrustedFileName));

    /// <summary>Progenitor's page View mods (bridge <c>show_mods</c>): the mods page while joined; nothing after leaving.</summary>
    [Fact]
    public void View_mods_opens_the_mods_page_while_joined_and_nothing_after_leaving()
    {
        Refused();
        Assert.Equal("mods", JoinFlowTestBridge.ShowMods()["page"]);
        JoinFlowClient.Session!.Close();
        Assert.False(JoinFlowClient.CanShowMods);
        Assert.Equal("no catalog or handshake facts this join", JoinFlowTestBridge.ShowMods()["result"]);
        try
        {
            JoinFlowClient.InWorldReady = () => true;
            Assert.True(JoinFlowClient.CanShowMods);
            var shown = JoinFlowTestBridge.ShowMods();
            Assert.Equal("mods", shown["result"]);
            Assert.Equal("mods", shown["page"]);
            Assert.Equal(true, shown["open"]);
            Assert.Contains(((List<object?>)shown["recommended"]!).Cast<Dictionary<string, object?>>(), r => (string)r["id"]! == "maps.mod");
        }
        finally { JoinFlowClient.InWorldReady = () => false; }
    }

    [Fact]
    public void A_refused_joins_thunderstore_row_installs_behind_one_confirmation_and_offers_restart_and_rejoin()
    {
        Refused();
        Assert.Equal("toggled", JoinFlowPanel.ToggleRecommended("maps.mod"));
        var press = JoinFlowTestBridge.Install();
        Assert.Equal("ts-trust", press["result"]);
        var ts = (Dictionary<string, object?>)press["tsTrust"]!;
        Assert.Equal("Install 2 mods from Thunderstore that Night Garden lists?", ts["intro"]);
        // 2026-10-05: no risk band; the first catalog pinned this key, so no key-change line either.
        Assert.False(ts.ContainsKey("risk"));
        Assert.Equal("", ts["keyChanged"]);
        Assert.Contains((JoinText.TsTrustTitle, true), _runtime.DialogTexts);

        var started = JoinFlowTestBridge.TsInstall();
        Assert.Equal("install", started["result"]);
        WaitForBatch();
        Assert.Equal(new[] { "Owner-Maps@1.0.0", Package + "@0.1.0" }.OrderBy(x => x), _installer.Installed.OrderBy(x => x));
        // The confirmation is recorded with the trust store, without a pin.
        Assert.Contains("\"thunderstoreAcks\"", TrustFile(), StringComparison.Ordinal);
        Assert.Contains(Package + "@0.1.0", TrustFile(), StringComparison.Ordinal);

        var read = JoinFlowTestBridge.Read();
        Assert.Equal("result", read["page"]);
        var result = (Dictionary<string, object?>)read["result"]!;
        Assert.Equal("restart-needed", result["kind"]);
        Assert.Equal(true, result["canRejoin"]);
        Assert.Contains((JoinText.RestartTitle, true), _runtime.DialogTexts);
        var row = ((List<object?>)read["required"]!).Cast<Dictionary<string, object?>>().Single();
        Assert.Equal("after-restart", ((Dictionary<string, object?>)row["install"]!)["phase"]);

        // The dry run writes the record and reports the relaunch command; nothing starts, nothing quits.
        var dry = JoinFlowTestBridge.Restart(dryRun: true);
        Assert.Equal("dry-run", dry["result"]);
        Assert.Empty(_relauncher.Calls);
        var relaunch = (Dictionary<string, object?>)dry["lastRelaunch"]!;
        Assert.Equal("automatic", relaunch["mode"]);
        Assert.Contains("Start-Process 'steam://run/1604030'", (string)relaunch["arguments"]!, StringComparison.Ordinal);
        var record = JoinFlowTestBridge.RejoinRead();
        var r = (Dictionary<string, object?>)record["record"]!;
        Assert.Equal("127.0.0.1", r["address"]);
        Assert.Equal(9976, r["port"]);
        Assert.Equal("Lidgren", r["protocol"]);
        Assert.Equal(true, r["dryRun"]);
        Assert.DoesNotContain("\"password\"", (string)record["raw"]!, StringComparison.Ordinal);

        // A real press starts the relauncher, then quits.
        Assert.Equal("quitting", JoinFlowTestBridge.Restart(dryRun: false)["result"]);
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
        // REJOINSELF1: the record names this process, and this process never picks it up.
        Assert.True(JoinFlowClient.RejoinPickupChecked);
        var real = (Dictionary<string, object?>)JoinFlowTestBridge.RejoinRead()["record"]!;
        Assert.Equal(RejoinStore.ProcessStamp(), real["writer"]);
        Assert.Equal(true, real["ownProcess"]);
        Assert.Equal(false, real["used"]);
        // A second press while quitting never starts a second relauncher.
        Assert.Equal("already restarting", JoinFlowTestBridge.Restart(dryRun: false)["result"]);
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
    }

    [Fact]
    public void The_after_relaunch_pickup_reconnects_to_the_same_server_once()
    {
        Refused();
        JoinFlowTestBridge.Install();
        JoinFlowTestBridge.TsInstall();
        WaitForBatch();
        JoinFlowTestBridge.Restart(dryRun: true);
        JoinFlowClient.Session!.ResultLater();

        // A start leaves a dry run's record for the bridge.
        _now += 1;
        JoinFlowClient.Tick();
        Assert.NotNull(JoinFlowTestBridge.RejoinRead()["record"]);
        Assert.Empty(_host.Calls);

        var now = JoinFlowTestBridge.RejoinNow();
        Assert.Equal("pickup: rejoin", now["result"]);
        Assert.Equal("rejoin", now["page"]);
        var view = (Dictionary<string, object?>)now["rejoin"]!;
        Assert.Equal("Rejoining Night Garden", view["title"]);
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:Lidgren://127.0.0.1:9976" }, _host.Calls);
        _host.World = true;
        _host.Joined = true;
        _now += 1;
        JoinFlowClient.Tick();
        var joined = JoinFlowTestBridge.Read();
        Assert.Equal("joined", ((Dictionary<string, object?>)joined["rejoin"]!)["phase"]);
        Assert.Null(JoinFlowTestBridge.RejoinRead()["record"]);
        Assert.Equal("closed", JoinFlowTestBridge.Cancel()["result"]);
        // Taken once: nothing is left to pick up.
        Assert.Equal("pickup: none", JoinFlowTestBridge.RejoinNow()["result"]);
    }

    [Fact]
    public void Everything_loaded_offers_join_now_which_reconnects_without_a_restart()
    {
        _installer.Result = JoinInstallPhase.Loaded;
        Refused();
        JoinFlowTestBridge.Install();
        JoinFlowTestBridge.TsInstall();
        WaitForBatch();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("join-now", ((Dictionary<string, object?>)read["result"]!)["kind"]);
        Assert.Equal("rejoin", ((Dictionary<string, object?>)read["result"]!)["finishMode"]);
        var join = JoinFlowTestBridge.JoinNow();
        Assert.Equal("joining", join["result"]);
        Assert.Equal("Joining Night Garden", ((Dictionary<string, object?>)join["rejoin"]!)["title"]);
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:Lidgren://127.0.0.1:9976" }, _host.Calls);
        // No record for Join now.
        Assert.Null(JoinFlowTestBridge.RejoinRead()["record"]);
    }

    /// <summary>
    /// INPLACE1 (standing rule 2026-10-07): a refusal's batch that loads everything while the player is still in this
    /// server's world (the server only warned) finishes in place: the handshake is answered again on this connection, the
    /// Result page says what was sent with one Done button, and nothing leaves or reconnects. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void Everything_loaded_while_still_in_the_world_finishes_in_place_without_a_rejoin()
    {
        _installer.Result = JoinInstallPhase.Loaded;
        var keep = JoinFlowClient.ReAnswerHandshake;
        int answers = 0;
        try
        {
            Refused();
            JoinFlowClient.InWorldReady = () => true;
            JoinFlowClient.ReAnswerHandshake = () => { answers++; return true; };
            JoinFlowTestBridge.Install();
            JoinFlowTestBridge.TsInstall();
            WaitForBatch();
            var read = JoinFlowTestBridge.Read();
            Assert.Equal("result", read["page"]);
            var result = (Dictionary<string, object?>)read["result"]!;
            Assert.Equal("in-place", result["finishMode"]);
            Assert.Equal(true, result["inPlaceSent"]);
            Assert.Equal("nothing", result["kind"]);
            Assert.Equal(JoinText.InPlaceTitle, result["title"]);
            Assert.StartsWith("Sent to Night Garden: ", (string)result["body"]!, StringComparison.Ordinal);
            Assert.Contains("installed now", (string)result["body"]!, StringComparison.Ordinal);
            Assert.Equal(new[] { JoinFlowLayout.ButtonLater }, (IReadOnlyList<string>)result["buttons"]!);
            Assert.Equal(JoinText.DoneButton, JoinFlowLayout.ResultButtons(JoinFlowClient.Session!.Result!).Single().Caption);
            Assert.Equal(1, answers);
            Assert.Empty(_host.Calls);
            Assert.Equal("join now is not offered", JoinFlowClient.Session!.ResultJoinNow());
            Assert.Equal("closed", JoinFlowClient.Session!.ResultLater());
            Assert.Empty(_host.Calls);
        }
        finally
        {
            JoinFlowClient.InWorldReady = () => false;
            JoinFlowClient.ReAnswerHandshake = keep;
        }
    }

    [Fact]
    public void An_in_place_answer_that_cannot_go_out_says_so_and_still_stays()
    {
        _installer.Result = JoinInstallPhase.Loaded;
        var keep = JoinFlowClient.ReAnswerHandshake;
        try
        {
            Refused();
            JoinFlowClient.InWorldReady = () => true;
            JoinFlowClient.ReAnswerHandshake = () => false;
            JoinFlowTestBridge.Install();
            JoinFlowTestBridge.TsInstall();
            WaitForBatch();
            var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
            Assert.Equal("in-place", result["finishMode"]);
            Assert.Equal(false, result["inPlaceSent"]);
            Assert.Contains("could not be told on this connection", (string)result["body"]!, StringComparison.Ordinal);
            Assert.Empty(_host.Calls);
        }
        finally
        {
            JoinFlowClient.InWorldReady = () => false;
            JoinFlowClient.ReAnswerHandshake = keep;
        }
    }

    /// <summary>
    /// Review (MEDIUM): a re-answer that would fail the server's own check would get the player kicked, so the fix
    /// finished in the world answers again only when this client's run of that check passes; otherwise the page says
    /// what still mismatches and never "you stay". Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void An_in_place_fix_that_would_still_fail_the_check_does_not_answer_again()
    {
        _installer.Result = JoinInstallPhase.Loaded;
        var keep = JoinFlowClient.ReAnswerHandshake;
        var keepCheck = JoinFlowClient.ReAnswerPreflight;
        int answers = 0;
        try
        {
            Refused();
            JoinFlowClient.InWorldReady = () => true;
            JoinFlowClient.ReAnswerHandshake = () => { answers++; return true; };
            JoinFlowClient.ReAnswerPreflight = () => (true, new[] { "other.mod: expected 2.0.0, got 1.0.0" });
            JoinFlowTestBridge.Install();
            JoinFlowTestBridge.TsInstall();
            WaitForBatch();
            var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
            Assert.Equal(0, answers);
            Assert.Equal(false, result["inPlaceSent"]);
            Assert.Equal(new List<string> { "other.mod: expected 2.0.0, got 1.0.0" }, result["inPlaceProblems"]);
            string body = (string)result["body"]!;
            Assert.Contains("would still refuse this client's mods: other.mod: expected 2.0.0, got 1.0.0", body, StringComparison.Ordinal);
            Assert.DoesNotContain("You stay", body, StringComparison.Ordinal);
            Assert.Empty(_host.Calls);
        }
        finally
        {
            JoinFlowClient.InWorldReady = () => false;
            JoinFlowClient.ReAnswerHandshake = keep;
            JoinFlowClient.ReAnswerPreflight = keepCheck;
        }
    }

    /// <summary>
    /// A6 (live 2026-10-07): a server whose only requirement is its client mod policy asks about no mod; its install window
    /// still waits for this client's answer and the report after it. The re-answer goes, and the report follows it.
    /// </summary>
    [Fact]
    public void A6_a_request_that_asked_about_no_mod_is_still_answered_again_with_the_report()
    {
        try
        {
            ClientModReportClient.ResetForTests();
            Handshake.ForgetServerRequest();
            Assert.False(Handshake.HasRequestToAnswer);

            // ReDeclareSelf answers whenever HasRequestToAnswer (source pin below; it needs a client world to send).
            Handshake.RecordServerRequest(Array.Empty<Handshake.ModRecord>());
            Assert.True(Handshake.ServerRequestSeen);
            Assert.True(Handshake.HasRequestToAnswer);
            string source = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "Handshake.cs");
            Assert.Contains("if (!HasRequestToAnswer)", source, StringComparison.Ordinal);
            // The answer's report follows it to a server that asked (what a policy window waits for after the answer).
            ClientModReportClient.NoteServerRequest(asks: true, rechecks: true);
            int before = ClientModReportClient.TestPackets.Count;
            ClientModReportClient.AfterAnswer("re-answering the handshake");
            Assert.True(ClientModReportClient.TestPackets.Count > before);
            Handshake.PreflightReAnswer(out bool checkable);
            Assert.True(checkable);
        }
        finally
        {
            Handshake.ForgetServerRequest();
            ClientModReportClient.ResetForTests();
        }
    }

    /// <summary>Review (MEDIUM): the preflight is the server's own check on the reply this client would pack now.</summary>
    [Fact]
    public void The_preflight_runs_the_servers_check_on_the_reply_this_client_would_send()
    {
        const string id = "progenitor.preflight.test";
        try
        {
            Handshake.ForgetServerRequest();
            Assert.NotEmpty(Handshake.PreflightReAnswer(out bool none));
            Assert.False(none);
            Handshake.RecordServerRequest(new[] { Must(id, "1.0.0") });
            var missing = Handshake.PreflightReAnswer(out bool checkable);
            Assert.True(checkable);
            Assert.NotEmpty(missing);
            Handshake.Provide(id, "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);
            Assert.Empty(Handshake.PreflightReAnswer(out checkable));
            Assert.True(checkable);
            Handshake.RecordServerRequest(new[] { Must(id, "1.0.0") }, truncated: true);
            Handshake.PreflightReAnswer(out checkable);
            Assert.False(checkable);
        }
        finally
        {
            Handshake.Unprovide(id);
            Handshake.ForgetServerRequest();
        }
    }

    /// <summary>
    /// F22 (live 2026-10-07): after the connected server accepted Progenitor's stop, the connection's end offers "Rejoin
    /// when it's back"; pressed, the rejoin keeps trying while the server is down. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_server_stopped_by_progenitor_is_rejoined_when_it_is_back()
    {
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren", ServerName = "Night Garden" };
        // Nothing armed: a plain connection end offers nothing.
        JoinFlowClient.OnConnectionLost();
        Assert.True(JoinFlowClient.Session == null || JoinFlowClient.Session.Closed);

        JoinFlowClient.ArmRejoinWhenBack("Stop server to apply");
        JoinFlowClient.OnConnectionLost();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("result", read["page"]);
        Assert.Equal(JoinText.RejoinWhenBackButton, JoinFlowLayout.ResultButtons(JoinFlowClient.Session!.Result!).Last().Caption);
        Assert.Equal("Why: Stop server to apply.", (string)read["notice"]!);
        // F31 (live 2026-10-08): the page has its own title and body, not "Ready to join".
        Assert.Equal("Night Garden is restarting", JoinFlowLayout.ResultTitle(JoinFlowClient.Session!.Result!));
        Assert.Equal(JoinText.RestartingBody, JoinFlowLayout.ResultBody(JoinFlowClient.Session!.Result!));

        // Pressed: the first connect gets no answer (the server is still down), the next attempt follows.
        Assert.Equal("joining", JoinFlowTestBridge.JoinNow()["result"]);
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Single(_host.Calls);
        _now += JoinReconnect.ConnectTimeoutSeconds + 1;
        JoinFlowClient.Tick();
        Assert.Contains("Waiting for Night Garden to come back (attempt 1)", (string)((Dictionary<string, object?>)JoinFlowTestBridge.Read()["rejoin"]!)["status"]!, StringComparison.Ordinal);
        _now += JoinFlowClient.WaitRetrySeconds + 1;
        JoinFlowClient.Tick();
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(2, _host.Calls.Count);

        // Review (M2): Cancel ends the wait: the next failure is not tried again.
        JoinFlowTestBridge.Cancel();
        _now += JoinReconnect.ConnectTimeoutSeconds + JoinFlowClient.WaitRetrySeconds + 5;
        JoinFlowClient.Tick();
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(2, _host.Calls.Count);
    }

    /// <summary>
    /// F37 (live 2026-10-08: after Save, stop and start again the page said "Why: Stop server to apply." and asked): the
    /// player's own restart names itself and rejoins without asking, on the Rejoin page whose Cancel ends the wait; the
    /// stop request carries the restart to the rejoin. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_restart_the_player_started_is_named_and_rejoined_without_asking()
    {
        // The stop request tells its listener whether Progenitor starts the server again.
        var seen = new List<bool>();
        var keep = ServerStopRequest.Accepted;
        try
        {
            ServerStopRequest.Accepted = seen.Add;
            var stop = new ServerStopRequest
            {
                Send = _ => true,
                Poll = n => StopServerReply.For(n, true, StopReplyReason.None),
                Clock = () => DateTime.UtcNow,
            };
            Assert.True(stop.Begin("Night Garden", InstallModsCopy.StartingAgainTail, restartsAgain: true));
            stop.Tick();
            Assert.True(stop.RestartsAgain);
            Assert.Equal(new[] { true }, seen);
        }
        finally { ServerStopRequest.Accepted = keep; }

        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren", ServerName = "Night Garden" };
        JoinFlowClient.ArmRejoinWhenBack(InstallModsCopy.StopAndStartAgain, autoRejoin: true);
        JoinFlowClient.OnConnectionLost();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("rejoin", read["page"]);
        var rejoin = (Dictionary<string, object?>)read["rejoin"]!;
        Assert.Equal("Why: Save, stop and start again.", (string)rejoin["body"]!);
        Assert.Equal(JoinText.CancelButton, JoinFlowLayout.RejoinButton(JoinFlowClient.Session!.RejoinView!).Caption);

        // It keeps trying while the server is down, as the pressed button would.
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Single(_host.Calls);
        _now += JoinReconnect.ConnectTimeoutSeconds + 1;
        JoinFlowClient.Tick();
        _now += JoinFlowClient.WaitRetrySeconds + 1;
        JoinFlowClient.Tick();
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(2, _host.Calls.Count);

        // Cancel ends it.
        JoinFlowTestBridge.Cancel();
        _now += JoinReconnect.ConnectTimeoutSeconds + JoinFlowClient.WaitRetrySeconds + 5;
        JoinFlowClient.Tick();
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(2, _host.Calls.Count);

        // A plain Stop server to apply still asks.
        JoinFlowClient.ArmRejoinWhenBack(InstallModsCopy.StopServerToApply);
        JoinFlowClient.OnConnectionLost();
        Assert.Equal("result", JoinFlowTestBridge.Read()["page"]);
        Assert.Equal("Why: Stop server to apply.", (string)JoinFlowTestBridge.Read()["notice"]!);
    }

    /// <summary>
    /// F22 review (M1, M3): a stop that did not happen offers nothing; a connection to another server offers nothing; a
    /// result page's own server never outlives the page. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_rejoin_offer_belongs_to_its_stop_its_connection_and_its_page()
    {
        var a = new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren", ServerName = "Night Garden" };
        var b = new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "Lidgren", ServerName = "Other" };
        JoinFlowClient.TargetProbe = () => a;
        JoinFlowClient.ArmRejoinWhenBack("Stop server to apply");
        JoinFlowClient.DisarmRejoinWhenBack();
        JoinFlowClient.OnConnectionLost();
        Assert.True(JoinFlowClient.Session == null || JoinFlowClient.Session.Closed);

        JoinFlowClient.ArmRejoinWhenBack("Stop server to apply");
        JoinFlowClient.TargetProbe = () => b;
        JoinFlowClient.OnConnectionLost();
        Assert.True(JoinFlowClient.Session == null || JoinFlowClient.Session.Closed);

        JoinFlowClient.TargetProbe = () => a;
        JoinFlowClient.ArmRejoinWhenBack("Stop server to apply");
        JoinFlowClient.OnConnectionLost();
        Assert.Equal("result", JoinFlowTestBridge.Read()["page"]);
        JoinFlowClient.Session!.ResultLater();
        // A later Join with the page gone dials the join's own server, not the page's.
        JoinFlowClient.TargetProbe = () => b;
        typeof(JoinFlowClient).GetMethod("StartJoinNow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:Lidgren://127.0.0.1:9876" }, _host.Calls);
    }

    /// <summary>INPLACE1: the player closed the join screens while the batch ran: the result is a notice in the world.</summary>
    [Fact]
    public void An_in_place_result_after_the_screens_closed_is_a_notice()
    {
        _installer.Result = JoinInstallPhase.Loaded;
        var keep = JoinFlowClient.ReAnswerHandshake;
        var notices = new List<(string Title, string Text)>();
        try
        {
            Refused();
            JoinFlowClient.InWorldReady = () => true;
            JoinFlowClient.ReAnswerHandshake = () => true;
            JoinFlowClient.InPlaceNotice = (title, text) => notices.Add((title, text));
            JoinFlowTestBridge.Install();
            JoinFlowTestBridge.TsInstall();
            JoinFlowClient.Session!.Close();
            WaitForBatch();
            var (title, text) = Assert.Single(notices);
            Assert.Equal(JoinText.InPlaceTitle, title);
            Assert.StartsWith("Sent to Night Garden: ", text, StringComparison.Ordinal);
            Assert.Contains("You stay where you are", text, StringComparison.Ordinal);
            Assert.Empty(_host.Calls);
        }
        finally
        {
            JoinFlowClient.InWorldReady = () => false;
            JoinFlowClient.ReAnswerHandshake = keep;
            JoinFlowClient.InPlaceNotice = null;
        }
    }

    [Fact]
    public void Batch_logs_name_the_source_it_installed_from()
    {
        JoinInstallRowState Row(bool local) => new(new JoinInstallRequest("k", "A", "Owner-A", "1.0.0", JoinInstallKind.Install, true) { LocalTest = local });
        Assert.Equal("Thunderstore", JoinFlowClient.BatchSourceWords(new[] { Row(false) }));
        Assert.Equal("local test source", JoinFlowClient.BatchSourceWords(new[] { Row(true) }));
        Assert.Equal("Thunderstore and local test source", JoinFlowClient.BatchSourceWords(new[] { Row(true), Row(false) }));
    }

    [Fact]
    public void A_server_that_needs_its_password_rejoins_through_the_games_prompt_and_the_password_is_never_written()
    {
        // INSTALLRESTART1, F19 (live 2026-10-07): the after-restart rejoin of a server joined with a password connects without
        // one first (a password manager's autofill marks password-free servers too); the game's own prompt opens only when the
        // server refuses for its password. Nothing is stored.
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", NeedsPassword = true };
        Refused();
        JoinFlowTestBridge.Install();
        JoinFlowTestBridge.TsInstall();
        WaitForBatch();
        JoinFlowTestBridge.Restart(dryRun: true);
        Assert.Equal(true, ((Dictionary<string, object?>)JoinFlowTestBridge.RejoinRead()["record"]!)["needsPassword"]);
        Assert.DoesNotContain("\"password\"", (string)JoinFlowTestBridge.RejoinRead()["raw"]!, StringComparison.Ordinal);
        JoinFlowClient.Session!.ResultLater();
        JoinFlowTestBridge.RejoinNow();
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976" }, _host.Calls);
    }

    /// <summary>
    /// F32 (live 2026-10-08): "a password was used last time" never blocks a connect. An in-session rejoin (Join now, the
    /// rejoin-when-back retry, Join again) of a server marked as joined with a password connects without one first; the
    /// game's prompt opens only if the server refuses for its password. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void An_in_session_rejoin_of_a_server_marked_with_a_password_connects_without_one_first()
    {
        var target = new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Night Garden", NeedsPassword = true };
        typeof(JoinFlowClient).GetMethod("OpenReconnect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { target, false, null });
        Assert.True(target.PromptOnPasswordRefusal);
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976" }, _host.Calls);
        Assert.NotEqual("password", JoinFlowClient.Session?.RejoinView?.Phase);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinFlowClient.Stage2.cs");
        Assert.DoesNotContain("which is never stored: not connecting", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// F19 (live 2026-10-07): a rejoin handed to the game's password prompt that never joins (the prompt closed) does not
    /// leave the player silently at the title screen: the result says so and offers Join, which dials that server again.
    /// </summary>
    [Fact]
    public void A_rejoin_that_never_joins_after_the_password_prompt_shows_a_result_with_join()
    {
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Night Garden", NeedsPassword = true, PromptForPassword = true };
        var target = JoinFlowClient.TargetProbe!();
        typeof(JoinFlowClient).GetMethod("OpenReconnect", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { target, false, null });
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "prompt:SteamIPv4://127.0.0.1:9976" }, _host.Calls);
        _now += JoinFlowClient.PromptWatchSeconds - 5;
        JoinFlowClient.Tick();
        Assert.True(JoinFlowClient.Session == null || JoinFlowClient.Session.Closed);
        _now += 10;
        JoinFlowClient.Tick();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("result", read["page"]);
        var result = (Dictionary<string, object?>)read["result"]!;
        Assert.Equal(true, result["joinAgain"]);
        Assert.StartsWith("Couldn't rejoin Night Garden: the game's password prompt closed", (string)read["notice"]!, StringComparison.Ordinal);
        // Join dials that server again.
        JoinFlowTestBridge.JoinNow();
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(2, _host.Calls.Count);
    }

    [Fact]
    public void A_hosted_private_game_is_told_to_restart_without_a_rejoin()
    {
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "SteamIPv4", HostedByThisClient = true };
        Refused();
        JoinFlowTestBridge.Install();
        JoinFlowTestBridge.TsInstall();
        WaitForBatch();
        var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
        Assert.Equal(false, result["canRejoin"]);
        Assert.Equal(JoinText.RestartHosted, result["body"]);
        Assert.Equal("rejoin is not offered for this server", JoinFlowTestBridge.Restart(dryRun: true)["result"]);
        Assert.Null(JoinFlowTestBridge.RejoinRead()["record"]);
    }

    private sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        public readonly List<(string, bool)> DialogTexts = new();

        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header) =>
            new CellRowTests.FakeCellRow(null, spec, header);

        public IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title)
        {
            DialogTexts.Add((text, title));
            return new CellRowTests.FakeWidget { Text = text };
        }
    }
}
