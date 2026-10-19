using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// INSTALLRESTART1: the Mods window's pending-restart bar. The bar's states (none, out of a world, a dedicated server, a
/// hosted private game with and without its save name, removals, no relauncher), the records a restart writes (no
/// password; a hosted save), the confirmation's Not now, dry run and real press, leaving the world before the quit, the
/// relauncher-missing fallback, the after-restart line, and Restart and resume through the title screen's Continue.
/// </summary>
[Collection("ModManifest")]
public sealed class InstallRestartTests : IDisposable
{
    private const string ChatId = "example.chatskin";
    private readonly string _dir;
    private readonly FakeRelauncher _relauncher = new();
    private readonly FakeHost _host = new();
    private List<PendingClientChange> _pending = new() { Change("ChatSkin", ChatId) };
    private double _now = 100;

    public InstallRestartTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-installrestart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        UiApi.ResetForTests();
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.Relauncher = _relauncher;
        JoinFlowClient.ConnectHost = _host;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Night Garden" };
        JoinFlowClient.PendingRestartProbe = () => _pending;
    }

    public void Dispose()
    {
        JoinFlowClient.Uninstall();
        JoinFlowClient.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static PendingClientChange Change(string name, string id = "", string action = PendingClientChange.ActionInstall) =>
        new() { Name = name, Id = id, Version = "1.0.0", Action = action };

    private sealed class FakeRelauncher : IJoinRelauncher
    {
        public readonly List<string> Calls = new();
        public RelaunchMode Mode = RelaunchMode.Automatic;
        public string Why = "";
        public bool StartWorks = true;

        public RelaunchPlan Plan() => new()
        {
            Mode = Mode,
            Why = Why,
            FileName = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            Script = RelaunchCommand.Script(4242, 1, @"C:\Games\VRising\VRising.exe"),
            Arguments = RelaunchCommand.Arguments(RelaunchCommand.Script(4242, 1, @"C:\Games\VRising\VRising.exe")),
        };

        public void Start(RelaunchPlan plan, Action<bool, string> done)
        {
            Calls.Add("start");
            done(StartWorks, StartWorks ? "" : "denied");
        }

        public void Quit(double delaySeconds) => Calls.Add("quit");
    }

    private sealed class FakeHost : IJoinConnectHost
    {
        public bool World;
        public bool Menu = true;
        public bool Joined;
        public string? Continue = "";
        public readonly List<string> Calls = new();

        public bool WorldExists => World;
        public bool MainMenuReady => Menu && !World;
        public bool JoinedOk => Joined && World;
        public void RequestLeave() => Calls.Add("leave");
        public void RequestMainMenu() => Calls.Add("menu");

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("launch:" + target.Endpoint);
            return true;
        }

        public bool LaunchPasswordPrompt(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("prompt:" + target.Endpoint);
            return true;
        }

        public string? ContinueText => Continue;

        public bool PressContinue(out string error)
        {
            error = "";
            Calls.Add("continue");
            return true;
        }
    }

    private void Tick()
    {
        _now += 1;
        JoinFlowClient.Tick();
    }

    private string RejoinFile() => File.ReadAllText(Path.Combine(_dir, RejoinStore.FileName));

    private string InstallFile() => Path.Combine(_dir, InstallRestartRecord.FileName);

    // ---- the bar's states ----------------------------------------------------------------------

    private static RelaunchPlan Auto() => new() { Mode = RelaunchMode.Automatic };

    [Fact]
    public void Nothing_pending_draws_no_bar()
    {
        var offer = ClientRestartOffer.Decide(new ClientRestartFacts { Plan = Auto() });
        Assert.False(offer.Shown);
        Assert.Equal("none", ClientRestartText.KindName(offer.Kind));
        _pending = new List<PendingClientChange>();
        Assert.False(JoinFlowClient.CurrentRestartOffer().Shown);
        Assert.Equal("nothing waits for a restart", JoinFlowClient.PressRestartButton());
    }

    [Fact]
    public void Out_of_a_world_the_bar_offers_restart_now()
    {
        var offer = ClientRestartOffer.Decide(new ClientRestartFacts { Pending = new[] { Change("ChatSkin") }, Plan = Auto() });
        Assert.Equal(ClientRestartKind.Restart, offer.Kind);
        Assert.Equal("1 mod loads after a restart", offer.Sentence);
        Assert.Equal("Restart now", offer.Button);
        Assert.Equal("Restart V Rising to finish installing 1 mod?", offer.ConfirmTitle);
        Assert.Equal("V Rising closes and starts again.", offer.ConfirmBody);
        Assert.Equal("Restart", offer.ConfirmYes);
        Assert.Equal("Not now", offer.ConfirmNo);
        Assert.Equal("none", ((Dictionary<string, object?>)offer.Describe()["rejoinTarget"]!)["kind"]);
        Assert.Equal(true, offer.Describe()["canRestart"]);
    }

    [Fact]
    public void On_a_dedicated_server_the_bar_offers_restart_and_rejoin()
    {
        var offer = ClientRestartOffer.Decide(new ClientRestartFacts
        {
            Pending = new[] { Change("ChatSkin"), Change("Maps") },
            InWorld = true,
            Target = new JoinTarget { Address = "10.0.0.5", Port = 9876, Protocol = "SteamIPv4", ServerName = "Night Garden" },
            Plan = Auto(),
        });
        Assert.Equal(ClientRestartKind.RestartAndRejoin, offer.Kind);
        Assert.Equal("2 mods load after a restart", offer.Sentence);
        Assert.Equal("Restart and rejoin Night Garden", offer.Button);
        Assert.Equal("Restart V Rising to finish installing 2 mods?", offer.ConfirmTitle);
        Assert.Equal("You'll rejoin Night Garden automatically.", offer.ConfirmBody);
        var target = (Dictionary<string, object?>)offer.Describe()["rejoinTarget"]!;
        Assert.Equal("dedicated", target["kind"]);
        Assert.Equal("10.0.0.5", target["address"]);
        Assert.Equal(9876, target["port"]);
        Assert.Equal("SteamIPv4", target["protocol"]);
    }

    [Fact]
    public void In_a_hosted_private_game_the_bar_offers_restart_and_resume_or_the_menu_without_a_save_name()
    {
        var hosted = new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "SteamIPv4", ServerName = "Night Garden", HostedByThisClient = true };
        var resume = ClientRestartOffer.Decide(new ClientRestartFacts
        {
            Pending = new[] { Change("ChatSkin") }, InWorld = true, Target = hosted, HostedSave = "Night Garden", Plan = Auto(),
        });
        Assert.Equal(ClientRestartKind.RestartAndResume, resume.Kind);
        Assert.Equal("Restart and resume Night Garden", resume.Button);
        Assert.StartsWith("You'll return to Night Garden automatically", resume.ConfirmBody, StringComparison.Ordinal);
        var target = (Dictionary<string, object?>)resume.Describe()["rejoinTarget"]!;
        Assert.Equal("hosted", target["kind"]);
        Assert.Equal("Night Garden", target["save"]);

        var menu = ClientRestartOffer.Decide(new ClientRestartFacts
        {
            Pending = new[] { Change("ChatSkin") }, InWorld = true, Target = hosted, HostedSave = "", Plan = Auto(),
        });
        Assert.Equal(ClientRestartKind.RestartToMenu, menu.Kind);
        Assert.Equal("Restart now", menu.Button);
        Assert.Equal("1 mod loads after a restart · you'll return to the menu", menu.Sentence);
        Assert.Equal("You'll return to the main menu: " + ClientRestartText.NoSaveName, menu.ConfirmBody);
        Assert.Equal(ClientRestartText.NoSaveName, ((Dictionary<string, object?>)menu.Describe()["rejoinTarget"]!)["why"]);
    }

    /// <summary>
    /// F28 (live 2026-10-08): a client mod marked for removal at the next start counts as needing a restart, even when the
    /// Install tab's index does not list it; a package's files count once, a mod already named is not repeated, and the
    /// local server's rows and pending downloads are not removals. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_pending_client_removal_counts_for_the_restart_offer()
    {
        var ledger = new[]
        {
            new Progenitor.Install.InstalledLedgerRow("SampleArmy", "aa", "1.2.0", "SampleArmy.dll", "package:team-SampleArmy@1.2.0", "mod", "client", @"C:\g\BepInEx\progenitor-reload\SampleArmy.dll", "team.samplearmy"),
            new Progenitor.Install.InstalledLedgerRow("SampleArmy", "ab", "1.2.0", "SampleArmy.Data.dll", "package:team-SampleArmy@1.2.0", "mod", "client", @"C:\g\BepInEx\progenitor-reload\SampleArmy.Data.dll", "team.samplearmy"),
            new Progenitor.Install.InstalledLedgerRow("ServerSide", "bb", "1.0.0", "ServerSide.dll", "package:team-ServerSide@1.0.0", "mod", "server", @"C:\s\BepInEx\progenitor-reload\ServerSide.dll"),
            new Progenitor.Install.InstalledLedgerRow("Fresh", "cc", "1.0.0", "Fresh.dll", "package:team-Fresh@1.0.0", "pending", "client", @"C:\g\BepInEx\progenitor-pending\Fresh.dll"),
            new Progenitor.Install.InstalledLedgerRow("Kept", "dd", "1.0.0", "Kept.dll", "package:team-Kept@1.0.0", "mod", "client", @"C:\g\BepInEx\progenitor-reload\Kept.dll"),
        };
        var list = new List<PendingClientChange> { Change("ChatSkin") };
        ModsMenu.AddLedgerRemovals(list, ledger, path => !path.EndsWith("Kept.dll", StringComparison.Ordinal));
        Assert.Equal(2, list.Count);
        Assert.True(list[1].IsRemoval);
        Assert.Equal("team.samplearmy", list[1].Id);

        var offer = ClientRestartOffer.Decide(new ClientRestartFacts { Pending = list, InWorld = false, Plan = Auto() });
        Assert.True(offer.Shown);
        Assert.Equal("Restart V Rising to finish 1 install and 1 removal?", offer.ConfirmTitle);

        // Named already (the Install tab's own pending-removal row): not counted twice.
        var named = new List<PendingClientChange> { new() { Name = "Other", Id = "team.samplearmy", Action = PendingClientChange.ActionRemove } };
        ModsMenu.AddLedgerRemovals(named, ledger, _ => true);
        Assert.Equal(2, named.Count);
        Assert.DoesNotContain(named, c => c.Name == "ServerSide" || c.Name == "Fresh");
    }

    /// <summary>
    /// Review L-c (2026-10-08): the ledger's pending removals are read at most every few seconds (the list key asks every
    /// 2 s on the main thread) and merged without repeating a named mod; a Mods-page action drops the cached answer.
    /// </summary>
    [Fact]
    public void Pending_removals_are_cached_and_merged_once()
    {
        var list = new List<PendingClientChange> { Change("ChatSkin", id: "team.chatskin") };
        ModsMenu.MergeRemovals(list, new[]
        {
            Change("Old", id: "team.old", action: PendingClientChange.ActionRemove),
            Change("ChatSkin again", id: "team.chatskin", action: PendingClientChange.ActionRemove),
        });
        Assert.Equal(new[] { "ChatSkin", "Old" }, list.Select(c => c.Name));
        Assert.True(ModsMenu.RemovalsCacheSeconds >= 2);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.InstallRestart.cs");
        Assert.Contains("MergeRemovals(list, CachedLedgerRemovals());", src, StringComparison.Ordinal);
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("ForgetLedgerRemovals();", menu, StringComparison.Ordinal);
    }

    [Fact]
    public void A_removal_says_changes_and_a_connection_that_cannot_be_dialed_returns_to_the_menu()
    {
        var offer = ClientRestartOffer.Decide(new ClientRestartFacts
        {
            Pending = new[] { Change("ChatSkin"), Change("Old", action: PendingClientChange.ActionRemove) },
            InWorld = true,
            Target = new JoinTarget { Address = "", Port = 0, Protocol = "SteamP2P" },
            Plan = Auto(),
        });
        Assert.Equal(ClientRestartKind.RestartToMenu, offer.Kind);
        Assert.Equal("2 mod changes apply after a restart · you'll return to the menu", offer.Sentence);
        // F29 (live 2026-10-08): one install and one removal are named as such, not "installing 2 mods".
        Assert.Equal("Restart V Rising to finish 1 install and 1 removal?", offer.ConfirmTitle);
        Assert.Equal("Restart V Rising to finish removing 1 mod?", ClientRestartText.ConfirmTitle(1, 1));
        Assert.Equal("Restart V Rising to finish 2 installs and 3 removals?", ClientRestartText.ConfirmTitle(5, 3));
    }

    [Fact]
    public void Without_a_relauncher_the_confirmation_offers_quit_to_desktop_with_the_manual_start_sentence()
    {
        var offer = ClientRestartOffer.Decide(new ClientRestartFacts
        {
            Pending = new[] { Change("ChatSkin") },
            InWorld = true,
            Target = new JoinTarget { Address = "10.0.0.5", Port = 9876, Protocol = "SteamIPv4", ServerName = "Night Garden" },
            Plan = new RelaunchPlan { Mode = RelaunchMode.Manual, Why = "Steam is not running" },
        });
        Assert.True(offer.Manual);
        Assert.Equal(ClientRestartText.ManualTitle, offer.ConfirmTitle);
        Assert.Equal(ClientRestartText.QuitToDesktop, offer.ConfirmYes);
        Assert.Equal("Steam is not running. Quit to desktop, then start V Rising from Steam. Start it again within 10 minutes and Progenitor will rejoin Night Garden.",
            offer.ConfirmBody);
        Assert.Equal(false, offer.Describe()["canRestart"]);
        var none = ClientRestartOffer.Decide(new ClientRestartFacts { Pending = new[] { Change("ChatSkin") }, Plan = null });
        Assert.True(none.Manual);
        Assert.Equal(ClientRestartText.NoRelauncher, none.ManualWhy);
    }

    [Fact]
    public void The_install_tab_band_is_the_sentence_and_one_key_cap_button()
    {
        var offer = ClientRestartOffer.Decide(new ClientRestartFacts { Pending = new[] { Change("ChatSkin") }, Plan = Auto() });
        var cells = InstallModsPane.RestartBandCells(offer);
        Assert.Equal(InstallModsPane.CellRestartText, cells[0].Key);
        Assert.Equal("1 mod loads after a restart", cells[0].Text);
        Assert.Equal(InstallModsPane.CellRestartButton, cells[2].Key);
        Assert.Equal("Restart now", cells[2].Text);
        Assert.Equal(CellSkin.KeyCap, cells[2].Skin);
    }

    // ---- the press -----------------------------------------------------------------------------

    [Fact]
    public void Not_now_closes_the_confirmation_and_nothing_is_written_or_started()
    {
        Assert.Equal("confirm", JoinFlowClient.PressRestartButton());
        Assert.Equal("not now", JoinFlowClient.AnswerRestart(yes: false, dryRun: false));
        Assert.Empty(_relauncher.Calls);
        Assert.False(File.Exists(InstallFile()));
        Assert.False(File.Exists(Path.Combine(_dir, RejoinStore.FileName)));
    }

    [Fact]
    public void A_dry_run_in_a_world_writes_the_rejoin_record_without_a_password_and_neither_leaves_nor_quits()
    {
        _host.World = true;
        JoinFlowClient.TargetProbe = () => new JoinTarget
        {
            Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Night Garden", NeedsPassword = true,
        };
        Assert.Equal("confirm", JoinFlowClient.PressRestartButton());
        Assert.Equal("dry-run", JoinFlowClient.AnswerRestart(yes: true, dryRun: true));
        Assert.Empty(_relauncher.Calls);
        Assert.Empty(_host.Calls);

        string raw = RejoinFile();
        Assert.DoesNotContain("\"password\"", raw, StringComparison.Ordinal);
        Assert.True(RejoinRecord.TryParse(raw, out var record));
        Assert.Equal("127.0.0.1", record!.Address);
        Assert.Equal(9976, record.Port);
        Assert.Equal("SteamIPv4", record.Protocol);
        Assert.Equal("Night Garden", record.ServerName);
        Assert.Equal(RejoinRecord.KindDedicated, record.Kind);
        Assert.True(record.NeedsPassword);
        Assert.True(record.DryRun);
        // F19: "used a password" is a hint: the rejoin connects without one first and opens the game's own prompt only when
        // the server refuses for its password (RetainedPassword can fill it then).
        Assert.False(record.ToTarget().PromptForPassword);
        Assert.True(record.ToTarget().PromptOnPasswordRefusal);

        var install = InstallRestartRecord.Read(InstallFile());
        Assert.NotNull(install);
        Assert.True(install!.DryRun);
        Assert.Equal("ChatSkin", install.Mods[0].Name);
        var read = (Dictionary<string, object?>)ModsMenu.RestartOp("read", dryRun: false);
        var last = (Dictionary<string, object?>)read["lastRestart"]!;
        Assert.Equal(true, last["dryRun"]);
        Assert.Equal("automatic", last["mode"]);
        Assert.Equal(true, last["leaveFirst"]);
        Assert.Contains("steam://run/1604030", (string)last["arguments"]!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hosted_dry_run_records_the_save_to_resume()
    {
        _host.World = true;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "SteamIPv4", ServerName = "Night Garden", HostedByThisClient = true };
        JoinFlowClient.HostedSaveProbe = () => "Night Garden";
        var read = (Dictionary<string, object?>)ModsMenu.RestartOp("press", dryRun: false);
        var target = (Dictionary<string, object?>)((Dictionary<string, object?>)read["pendingRestart"]!)["rejoinTarget"]!;
        Assert.Equal("hosted", target["kind"]);
        Assert.Equal("Night Garden", target["save"]);
        Assert.Equal("dry-run", ((Dictionary<string, object?>)ModsMenu.RestartOp("confirm", dryRun: true))["result"]);
        Assert.True(RejoinRecord.TryParse(RejoinFile(), out var record));
        Assert.True(record!.IsHosted);
        Assert.Equal("Night Garden", record.Save);
        Assert.True(record.Usable);
    }

    [Fact]
    public void A_real_press_out_of_a_world_starts_the_relauncher_and_quits_once()
    {
        JoinFlowClient.PressRestartButton();
        Assert.Equal("quitting", JoinFlowClient.AnswerRestart(yes: true, dryRun: false));
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
        Assert.Equal("already restarting", JoinFlowClient.PressRestartButton());
        Assert.Equal("already restarting", JoinFlowClient.RunRestart(JoinFlowClient.CurrentRestartOffer(), dryRun: false));
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
        Assert.False(File.Exists(Path.Combine(_dir, RejoinStore.FileName)));
        Assert.False(InstallRestartRecord.Read(InstallFile())!.DryRun);
    }

    [Fact]
    public void In_a_world_the_restart_leaves_first_and_quits_once_the_world_is_gone()
    {
        _host.World = true;
        JoinFlowClient.PressRestartButton();
        Assert.Equal("leaving", JoinFlowClient.AnswerRestart(yes: true, dryRun: false));
        Assert.Equal(new[] { "leave" }, _host.Calls);
        Assert.Empty(_relauncher.Calls);
        Tick();
        Assert.Empty(_relauncher.Calls);
        _host.World = false;
        Tick();
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
        Assert.True(RejoinRecord.TryParse(RejoinFile(), out var record));
        Assert.False(record!.DryRun);
    }

    // ---- REJOINSELF1: the restarting process never spends its own records ---------------------

    private void InstallProbes()
    {
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.Relauncher = _relauncher;
        JoinFlowClient.ConnectHost = _host;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Night Garden" };
        JoinFlowClient.PendingRestartProbe = () => _pending;
    }

    [Fact]
    public void A_real_restart_marks_this_generations_start_pickup_as_done()
    {
        // Joined straight from the server list: this generation never saw the title screen ready.
        _host.World = true;
        Assert.False(JoinFlowClient.RejoinPickupChecked);
        JoinFlowClient.PressRestartButton();
        Assert.Equal("leaving", JoinFlowClient.AnswerRestart(yes: true, dryRun: false));
        Assert.True(JoinFlowClient.RejoinPickupChecked);
        Assert.True(RejoinRecord.TryParse(RejoinFile(), out var written));
        Assert.Equal(RejoinStore.ProcessStamp(), written!.Writer);
        Assert.Equal(RejoinStore.ProcessStamp(), InstallRestartRecord.Read(InstallFile())!.Writer);

        // The Leave lands on the title screen in this same process: the record stays for the relaunched game.
        _host.World = false;
        Tick();
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("launch:", StringComparison.Ordinal));
        Assert.True(RejoinRecord.TryParse(RejoinFile(), out var after));
        Assert.False(after!.Used);
        Assert.True(File.Exists(InstallFile()));
    }

    [Fact]
    public void A_dry_run_leaves_the_start_pickup_alone()
    {
        _host.World = true;
        JoinFlowClient.PressRestartButton();
        Assert.Equal("dry-run", JoinFlowClient.AnswerRestart(yes: true, dryRun: true));
        Assert.False(JoinFlowClient.RejoinPickupChecked);
    }

    [Fact]
    public void After_a_core_swap_during_the_leave_the_same_process_still_leaves_the_record_and_the_next_process_rejoins()
    {
        var stamp = RejoinStore.ProcessStamp;
        try
        {
            RejoinStore.ProcessStamp = () => "100:1";
            _host.World = true;
            JoinFlowClient.PressRestartButton();
            Assert.Equal("leaving", JoinFlowClient.AnswerRestart(yes: true, dryRun: false));

            // A Core swap resets the generation's flags; the new generation sees the title screen first.
            JoinFlowClient.Uninstall();
            InstallProbes();
            Assert.False(JoinFlowClient.RejoinPickupChecked);
            _host.World = false;
            Tick();
            Assert.True(JoinFlowClient.RejoinPickupChecked);
            Assert.Equal("own-process", JoinFlowClient.DescribeInstallRestart()["lastPickup"]);
            Assert.Equal("own-process", JoinFlowTestBridge.Read()["lastPickup"]);
            Assert.Equal(new[] { "leave" }, _host.Calls);
            Assert.True(RejoinRecord.TryParse(RejoinFile(), out var kept));
            Assert.False(kept!.Used);
            Assert.True(File.Exists(InstallFile()));

            // The relaunched game is another process: it picks the record up once and rejoins.
            RejoinStore.ProcessStamp = () => "200:2";
            JoinFlowClient.Uninstall();
            InstallProbes();
            Tick();
            Tick();
            Assert.Equal(new[] { "leave", "launch:127.0.0.1:9976" }, _host.Calls);
            Assert.True(RejoinStore.Peek(Path.Combine(_dir, RejoinStore.FileName))!.Used);
            Assert.False(File.Exists(InstallFile()));
        }
        finally { RejoinStore.ProcessStamp = stamp; }
    }

    [Fact]
    public void A_missing_relauncher_quits_to_desktop_without_starting_anything()
    {
        _relauncher.Mode = RelaunchMode.Manual;
        _relauncher.Why = "no steam:// handler is registered";
        var offer = JoinFlowClient.CurrentRestartOffer();
        Assert.True(offer.Manual);
        Assert.Equal(ClientRestartText.QuitToDesktop, offer.ConfirmYes);
        JoinFlowClient.PressRestartButton();
        Assert.Equal("quitting-manual", JoinFlowClient.AnswerRestart(yes: true, dryRun: false));
        Assert.Equal(new[] { "quit" }, _relauncher.Calls);
    }

    [Fact]
    public void A_relauncher_that_fails_to_start_still_quits_for_a_manual_start()
    {
        _relauncher.StartWorks = false;
        JoinFlowClient.PressRestartButton();
        Assert.Equal("quitting-manual", JoinFlowClient.AnswerRestart(yes: true, dryRun: false));
        Assert.Equal(new[] { "start", "quit" }, _relauncher.Calls);
    }

    [Fact]
    public void With_no_relauncher_at_all_nothing_quits()
    {
        JoinFlowClient.Relauncher = null;
        var offer = JoinFlowClient.CurrentRestartOffer();
        Assert.True(offer.Manual);
        Assert.Equal("restarting is not available here", JoinFlowClient.RunRestart(offer, dryRun: false));
    }

    // ---- after the restart ---------------------------------------------------------------------

    private void WriteInstallRecord(string returnTo, string name)
    {
        var offer = new ClientRestartOffer
        {
            Kind = ClientRestartKind.Restart, Pending = new[] { Change("ChatSkin", ChatId), Change("Maps", "example.maps") },
            ReturnTo = returnTo, ReturnName = name,
        };
        InstallRestartRecord.Write(InstallFile(), InstallRestartRecord.From(offer, DateTime.UtcNow, dryRun: false));
    }

    [Fact]
    public void After_a_plain_restart_the_line_names_what_loaded()
    {
        ModManifest.TestLoadedMods = new[] { new LoadedModInfo(ChatId, "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Minor, true) };
        WriteInstallRecord(ClientRestartOffer.ReturnNone, "");
        Assert.Equal("shown", JoinFlowClient.PickUpInstallRestart("bridge"));
        var read = JoinFlowClient.DescribeInstallRestart();
        Assert.Equal("Installed: ChatSkin. Not loaded: Maps.", read["afterRestart"]);
        Assert.False(File.Exists(InstallFile()));
        // The start leaves a dry run's record for the bridge.
        InstallRestartRecord.Write(InstallFile(), new InstallRestartRecord { Created = ModManifestJson.FormatIssued(DateTime.UtcNow), DryRun = true });
        Assert.Equal("dry-run-waiting", JoinFlowClient.PickUpInstallRestart("start"));
    }

    [Fact]
    public void After_restart_and_rejoin_the_rejoin_page_says_what_was_installed_and_where_it_rejoined()
    {
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo(ChatId, "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Minor, true),
            new LoadedModInfo("example.maps", "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Minor, true),
        };
        WriteInstallRecord(ClientRestartOffer.ReturnDedicated, "Night Garden");
        var rejoin = RejoinRecord.From(new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4", ServerName = "Night Garden" },
            Array.Empty<RejoinRow>(), DateTime.UtcNow);
        RejoinStore.Write(Path.Combine(_dir, RejoinStore.FileName), rejoin);

        // The start: the title screen is ready.
        Tick();
        Tick();
        Assert.Equal(new[] { "launch:127.0.0.1:9976" }, _host.Calls);
        var view = JoinFlowClient.Session!.RejoinView!;
        Assert.Equal("Rejoining Night Garden", JoinText.RejoinTitle(view.ServerName, view.AfterRestart));
        _host.World = true;
        _host.Joined = true;
        Tick();
        Assert.Equal("joined", view.Phase);
        Assert.Equal("Installed: ChatSkin, Maps. Rejoined Night Garden.", view.Status);
    }

    // ---- Restart and resume --------------------------------------------------------------------

    [Fact]
    public void Resume_presses_continue_once_it_names_the_save_and_waits_for_the_world()
    {
        var resume = new JoinResume("Night Garden", _host, 0);
        _host.Continue = "";
        resume.Tick(1);
        Assert.Equal("waiting", resume.Phase);
        _host.Continue = "night garden ";
        resume.Tick(2);
        Assert.Equal("starting", resume.Phase);
        Assert.Equal(new[] { "continue" }, _host.Calls);
        resume.Tick(30);
        Assert.Equal("starting", resume.Phase);
        _host.World = true;
        _host.Joined = true;
        resume.Tick(31);
        Assert.Equal("joined", resume.Phase);
    }

    [Fact]
    public void Resume_refuses_a_continue_for_another_save_or_server()
    {
        var resume = new JoinResume("Night Garden", _host, 0);
        _host.Continue = "Some Other Server";
        resume.Tick(1);
        Assert.Equal("waiting", resume.Phase);
        resume.Tick(JoinResume.ContinueWaitSeconds + 1);
        Assert.Equal("failed", resume.Phase);
        Assert.Equal(ClientRestartText.ResumeMismatch("Some Other Server", "Night Garden"), resume.Error);
        Assert.Empty(_host.Calls);
    }

    [Fact]
    public void A_hosted_rejoin_record_resumes_through_the_title_screens_continue()
    {
        ModManifest.TestLoadedMods = new[] { new LoadedModInfo(ChatId, "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Minor, true) };
        WriteInstallRecord(ClientRestartOffer.ReturnHosted, "Night Garden");
        RejoinStore.Write(Path.Combine(_dir, RejoinStore.FileName), new RejoinRecord
        {
            Kind = RejoinRecord.KindHosted, Save = "Night Garden", ServerName = "Night Garden", Created = ModManifestJson.FormatIssued(DateTime.UtcNow),
        });
        _host.Continue = "Night Garden";
        Tick();
        var view = JoinFlowClient.Session!.RejoinView!;
        Assert.True(view.Resume);
        Assert.Equal("Resuming Night Garden", ClientRestartText.ResumeTitle(view.ServerName));
        Tick();
        Assert.Equal(new[] { "continue" }, _host.Calls);
        _host.World = true;
        _host.Joined = true;
        Tick();
        Assert.Equal("joined", view.Phase);
        Assert.Equal("Installed: ChatSkin. Not loaded: Maps. Resumed Night Garden.", view.Status);
        Assert.False(File.Exists(Path.Combine(_dir, RejoinStore.FileName)));
    }

    [Fact]
    public void An_older_rejoin_record_without_a_kind_reads_as_a_dedicated_server()
    {
        const string json = "{\"address\":\"127.0.0.1\",\"port\":9976,\"protocol\":\"SteamIPv4\",\"schema\":1,\"created\":\"2026-10-06T00:00:00Z\",\"password\":\"x\"}";
        Assert.True(RejoinRecord.TryParse(json, out var r));
        Assert.Equal(RejoinRecord.KindDedicated, r!.Kind);
        Assert.True(r.Usable);
        Assert.DoesNotContain("\"password\"", r.ToJson(), StringComparison.Ordinal);
        Assert.False(new RejoinRecord { Kind = RejoinRecord.KindHosted }.Usable);
    }
}
