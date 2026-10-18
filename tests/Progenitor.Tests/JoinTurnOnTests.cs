using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// JOINTURNON: "Turn on" for installed-but-off mods on the join screens — the page model's action precedence and the
/// version rule, the hot and restart routes, recommended turn-ons behind the bottom button ("Apply N changes"), and
/// the session's presses. Game-free: the effects are fakes. Nothing here has been observed in the game.
/// </summary>
public sealed class JoinTurnOnTests
{
    private const string A = "alpha.mod";
    private const string B = "beta.mod";

    private static Handshake.ModRecord Must(string id, string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    private static ManifestMod Row(string id, string version, string? package = null, bool shares = false) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false, null, package,
            shares ? new string('c', 64) : null, shares ? 1234 : null);

    private static ManifestBody Catalog(params ManifestMod[] rows) =>
        new(1, "Night Garden", "2026-10-04T12:00:00Z", new ManifestOperator("Ada", "", "a1b2c3d4e5f67890"), null, rows);

    private static JoinPageModel Model(Handshake.ModRecord[] asked, Handshake.ModRecord[] provided, Func<string, JoinOffCopy?>? off,
        ManifestBody? catalog = null, JoinSection? section = null, string[]? now = null, bool refused = true) =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Night Garden",
            Asked = asked,
            Provided = provided,
            Catalog = catalog,
            Section = section,
            Refused = refused,
            OffCopyOf = off,
        });

    private static Func<string, JoinOffCopy?> Off(string id, string version, bool now = true, bool pending = false) =>
        x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)
            ? new JoinOffCopy { Version = version, AppliesNow = now, TurnsOnAtNextStart = pending, TurnsOnWithInstall = true }
            : null;

    // ------------------------------------------------------------ precedence

    [Fact]
    public void Precedence_is_ready_then_turn_off_then_turn_on_then_the_existing_chain()
    {
        var available = Install.PackageVersionAvailability.Available;
        // Ready wins over everything.
        Assert.Equal(JoinRowAction.Ready, JoinPageModel.ActionFor(JoinRowOrigin.ServerRequires, JoinRowStatus.Ready, true, available));
        // A mod only this client requires is turned off, whatever else holds.
        Assert.Equal(JoinRowAction.TurnOff, JoinPageModel.ActionFor(JoinRowOrigin.YouRequire, JoinRowStatus.TurnedOff, true, available));
        Assert.Equal(JoinRowAction.TurnOff, JoinPageModel.ActionFor(JoinRowOrigin.YouRequire, JoinRowStatus.ServerLacks, true, available));
        // Turn on beats Install/Update and Ask the admin.
        Assert.Equal(JoinRowAction.TurnOn, JoinPageModel.ActionFor(JoinRowOrigin.ServerRequires, JoinRowStatus.TurnedOff, true, available));
        Assert.Equal(JoinRowAction.TurnOn, JoinPageModel.ActionFor(JoinRowOrigin.ServerRequires, JoinRowStatus.TurnedOff, false));
        // Without a turned-off copy the chain stands.
        Assert.Equal(JoinRowAction.Install, JoinPageModel.ActionFor(JoinRowOrigin.ServerRequires, JoinRowStatus.Missing, true, available));
        Assert.Equal(JoinRowAction.AskAdmin, JoinPageModel.ActionFor(JoinRowOrigin.ServerRequires, JoinRowStatus.Missing, false));
    }

    [Fact]
    public void An_off_copy_at_the_servers_version_reads_turned_off_and_offers_turn_on_instead_of_a_download()
    {
        var model = Model(new[] { Must(A, "1.13.22") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.13.22"),
            Catalog(Row(A, "1.13.22", "Owner-Alpha", shares: true)), now: new[] { A });
        var row = model.FindRequired(A)!;
        Assert.Equal(JoinRowStatus.TurnedOff, row.Status);
        Assert.Equal(JoinRowAction.TurnOn, row.Action);
        Assert.False(row.Ready);
        Assert.Equal("turned-off", JoinText.StatusName(row.Status));
        Assert.Equal("turn-on", JoinText.ActionName(row.Action));
        Assert.Equal("Turn on", JoinText.ActionCaption(row));
        Assert.Equal("Turned off", JoinText.StatusWordOf(row));
        Assert.Equal("Turned off", JoinFlowLayout.StatusWord(row));
        Assert.Equal("Needs 1.13.22 · You have 1.13.22 (turned off)", JoinText.VersionLine(row));
        Assert.Equal("you have 1.13.22 (turned off)", JoinFlowLayout.CompactVersion(row));
        Assert.Equal(new[] { "applies now" }, JoinText.TagWords(row.Tags));
        // Nothing to download: Install N mods does not count it, and the footer does not name it.
        Assert.Empty(model.InstallTargets(null));
        Assert.Equal("", JoinText.CannotFixFooter(model));
        Assert.Equal(1, model.NeedAttention);
    }

    [Fact]
    public void An_off_copy_at_the_wrong_version_keeps_the_install_path_and_says_it_is_turned_off()
    {
        var model = Model(new[] { Must(A, "1.1.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.0.0"),
            Catalog(Row(A, "1.1.0", "Owner-Alpha")));
        var row = model.FindRequired(A)!;
        Assert.Equal(JoinRowStatus.Missing, row.Status);
        Assert.Equal(JoinRowAction.Install, row.Action);
        Assert.NotNull(row.OffCopy);
        Assert.True(row.OffCopy!.TurnsOnWithInstall);
        Assert.Equal("Needs 1.1.0 · You have 1.0.0 (turned off)", JoinText.VersionLine(row));
        Assert.Equal(new[] { A }, model.InstallTargets(null));
    }

    [Fact]
    public void The_servers_version_rule_decides_whether_the_off_copy_passes()
    {
        // Minor: any 1.2.x passes.
        var minor = Model(new[] { Must(A, "1.2.0", NetworkVersionRule.Minor) }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.2.5"));
        Assert.Equal(JoinRowAction.TurnOn, minor.FindRequired(A)!.Action);
        var otherMinor = Model(new[] { Must(A, "1.2.0", NetworkVersionRule.Minor) }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.3.0"));
        Assert.NotEqual(JoinRowAction.TurnOn, otherMinor.FindRequired(A)!.Action);
        // None: any version passes.
        var none = Model(new[] { Must(A, "1.2.0", NetworkVersionRule.None) }, Array.Empty<Handshake.ModRecord>(), Off(A, "0.9"));
        Assert.Equal(JoinRowAction.TurnOn, none.FindRequired(A)!.Action);
        // An off copy whose version cannot be read never passes.
        var unknown = Model(new[] { Must(A, "1.2.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, ""));
        Assert.Equal(JoinRowStatus.Missing, unknown.FindRequired(A)!.Status);
        Assert.Equal("Needs 1.2.0 · You have a copy (turned off)", JoinText.VersionLine(unknown.FindRequired(A)!));
    }

    [Fact]
    public void A_ready_row_and_a_mod_only_you_require_ignore_the_off_copy()
    {
        var ready = Model(new[] { Must(A, "1.0") }, new[] { Must(A, "1.0") }, Off(A, "1.0"));
        Assert.Equal(JoinRowAction.Ready, ready.FindRequired(A)!.Action);
        Assert.Null(ready.FindRequired(A)!.OffCopy);
        // This client declared B ServerMustHave and the server lacks it: Turn off, as today.
        var mine = Model(Array.Empty<Handshake.ModRecord>(), new[] { Must(B, "2.0") }, Off(B, "2.0"));
        Assert.Equal(JoinRowAction.TurnOff, mine.FindRequired(B)!.Action);
    }

    [Fact]
    public void A_paused_mod_is_not_turned_off_by_the_player_so_no_row_offers_turn_on()
    {
        // The probe answers null for a per-server pause (the server, not the player, holds it): today's chain stands.
        var model = Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), _ => null, Catalog(Row(A, "1.0", "Owner-Alpha")));
        Assert.Equal(JoinRowAction.Install, model.FindRequired(A)!.Action);
        Assert.Null(model.FindRequired(A)!.OffCopy);
    }

    [Fact]
    public void The_route_is_a_tag_now_for_a_hot_mod_and_needs_restart_otherwise()
    {
        var hot = Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: true)).FindRequired(A)!;
        Assert.Equal(JoinRowTags.AppliesNow, hot.Tags);
        Assert.False(hot.TurnOnPending);
        Assert.Equal("now", hot.OffCopy!.Route);
        var restart = Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: false)).FindRequired(A)!;
        Assert.Equal(JoinRowTags.NeedsRestart, restart.Tags);
        Assert.Equal("restart", restart.OffCopy!.Route);
        // Switched on already, waiting for the start: still Turn on (the restart is what remains), pending.
        var pending = Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: true, pending: true)).FindRequired(A)!;
        Assert.Equal(JoinRowAction.TurnOn, pending.Action);
        Assert.Equal(JoinRowTags.NeedsRestart, pending.Tags);
        Assert.True(pending.TurnOnPending);
        Assert.Equal("Needs 1.0 · You have 1.0 (turns on at next start)", JoinText.VersionLine(pending));
    }

    [Fact]
    public void A_turn_on_waiting_for_the_restart_counts_as_satisfied_by_the_restart_after_a_batch()
    {
        var model = Model(new[] { Must(A, "1.0"), Must(B, "2.0") }, Array.Empty<Handshake.ModRecord>(),
            Off(A, "1.0", pending: true), Catalog(Row(B, "2.0", "Owner-Beta")));
        var request = new JoinInstallRequest(B, "Beta", "Owner-Beta", "2.0", JoinInstallKind.Install, required: true, modId: B);
        var loaded = new JoinInstallRowState(request) { Phase = JoinInstallPhase.Loaded };
        Assert.Equal(JoinAfterInstall.RestartNeeded, JoinAfterInstallPolicy.Decide(model, new[] { loaded }));
        // Not switched on yet: the batch alone leaves the row incomplete.
        var off = Model(new[] { Must(A, "1.0"), Must(B, "2.0") }, Array.Empty<Handshake.ModRecord>(),
            Off(A, "1.0", now: false), Catalog(Row(B, "2.0", "Owner-Beta")));
        Assert.Equal(JoinAfterInstall.Incomplete, JoinAfterInstallPolicy.Decide(off, new[] { loaded }));
    }

    // ------------------------------------------------------------ recommended rows and the bottom button

    private static JoinSection Recommends() => new("", null, new[]
    {
        new RecommendedMod(A, "Owner-Alpha", "1.0", "Maps"),
        new RecommendedMod(B, "Owner-Beta", "2.0", "Sounds"),
    }, null);

    [Fact]
    public void A_recommended_mod_turned_off_reads_installed_turned_off_and_its_switch_turns_it_on()
    {
        var model = Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: false), section: Recommends());
        var a = model.FindRecommended(A)!;
        Assert.True(a.TurnedOff);
        Assert.False(a.Installed);
        Assert.Equal("1.0", a.InstalledVersion);
        Assert.Equal("Turned off", JoinText.RecommendedWord(a));
        Assert.Equal("", JoinText.RecommendedWord(model.FindRecommended(B)!));
        // Checked: a turn-on, never a download.
        var both = new[] { A, B };
        Assert.Equal(new[] { B }, model.InstallTargets(both));
        Assert.Equal(new[] { A }, model.TurnOnTargets(both).Select(r => r.Id));
        // Switched on and waiting for the start: installed, nothing to check.
        var pending = Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", pending: true), section: Recommends())
            .FindRecommended(A)!;
        Assert.True(pending.Installed);
        Assert.False(pending.TurnedOff);
        Assert.Equal("On at restart", JoinText.RecommendedWord(pending));
    }

    [Fact]
    public void A_recommended_mod_held_by_the_server_pause_reads_paused_here_and_is_never_downloaded()
    {
        Func<string, JoinOffCopy?> paused = x => x == A ? new JoinOffCopy { Version = "1.0", AppliesNow = true, PausedHere = true } : null;
        var model = Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), paused, section: Recommends(), refused: false);
        var a = model.FindRecommended(A)!;
        Assert.True(a.Installed);
        Assert.False(a.TurnedOff);
        Assert.Equal("Paused here", JoinText.RecommendedWord(a));
        Assert.Empty(model.InstallTargets(new[] { A }));
        Assert.Empty(model.TurnOnTargets(new[] { A }));
        var s = new JoinFlowSession(JoinMode.InWorld, model, new Fx());
        Assert.False(s.ToggleRecommended(A));
        Assert.Equal(0, s.BottomCount);
        Assert.Equal("", s.BottomCaption);
        // Its row has no switch: a spacer where the switch goes.
        var spec = JoinFlowLayout.Recommended(a, false);
        Assert.Equal(CellKind.Spacer, spec.Cells.First(c => c.Key == JoinFlowLayout.CellCheck).Kind);
        // A required row ignores the pause (the pause keeps its own behavior).
        var req = Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), paused).FindRequired(A)!;
        Assert.Null(req.OffCopy);
        Assert.NotEqual(JoinRowAction.TurnOn, req.Action);
    }

    [Fact]
    public void Every_recommended_state_word_fits_its_cell()
    {
        float width = JoinText.RecommendedWordWidth(CellLayout.TextFontSize);
        Assert.True(width >= 110f);
        foreach (string word in JoinText.RecommendedWords)
            Assert.Equal(word, MenuPanelLayout.ClipLabel(word, width - 8f, CellLayout.TextFontSize));
        var row = new JoinRecommendedRow(new RecommendedMod(A, "Owner-Alpha", "1.0", ""), "Alpha", null,
            new JoinOffCopy { Version = "1.0" });
        var cell = JoinFlowLayout.Recommended(row, false).Cells.First(c => c.Key == JoinFlowLayout.CellAction);
        Assert.Equal("Turned off", cell.Text);
        Assert.Equal(CellTone.Amber, cell.Tone);
    }

    [Fact]
    public void The_bottom_button_counts_downloads_and_reads_apply_n_changes_once_a_turn_on_is_checked()
    {
        Assert.Equal("Install 2 mods", JoinText.BottomButton(2, 0));
        Assert.Equal("", JoinText.BottomButton(0, 0));
        Assert.Equal("Apply 1 change", JoinText.BottomButton(0, 1));
        Assert.Equal("Apply 3 changes", JoinText.BottomButton(2, 1));

        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.InWorld,
            Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: false), section: Recommends(), refused: false), fx);
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.True(s.ToggleRecommended(B));
        Assert.Equal("Install 1 mod", s.BottomCaption);
        Assert.True(s.ToggleRecommended(A));
        Assert.Single(s.InstallTargets);
        Assert.Single(s.TurnOnTargets);
        Assert.Equal(2, s.BottomCount);
        Assert.Equal("Apply 2 changes", s.BottomCaption);
        var buttons = JoinFlowLayout.ModsButtons(s);
        Assert.Contains(buttons, b => b.Key == JoinFlowLayout.ButtonInstall && b.Caption == "Apply 2 changes");
    }

    [Fact]
    public void Apply_with_installs_shows_one_confirmation_listing_the_turn_ons_and_turns_them_on_with_install()
    {
        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.InWorld,
            Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: false), section: Recommends(), refused: false), fx);
        s.ToggleRecommended(A);
        s.ToggleRecommended(B);
        Assert.Equal("ts-trust", s.InstallSelected());
        Assert.Empty(fx.TurnedOn);
        var facts = s.TsTrust!;
        Assert.Equal(new[] { B }, facts.Requests.Select(r => r.Key));
        Assert.Equal(new[] { A }, facts.TurnOns.Select(t => t.Id));
        var shown = JoinFlowLayout.TsTrustFacts(facts);
        Assert.Contains(shown, f => f.Label == "Turns on" && f.Value.Contains("1.0 · already installed, no download · at the next start"));
        // Back: nothing installed, nothing turned on.
        Assert.Equal("declined", s.TsTrustBack());
        Assert.Empty(fx.TurnedOn);
        Assert.Empty(fx.Started);
        // Again, and Install: the turn-on, then the batch of downloads only.
        Assert.Equal("ts-trust", s.InstallSelected());
        Assert.Equal("install", s.TsTrustInstall());
        Assert.Equal(new[] { A }, fx.TurnedOn.Select(t => t.Id));
        Assert.Equal(new[] { B }, fx.Started.Select(r => r.Key));
        Assert.Contains("turns on", s.Notice);
    }

    [Fact]
    public void Apply_with_only_turn_ons_needs_no_confirmation()
    {
        var fx = new Fx();
        var s = new JoinFlowSession(JoinMode.InWorld,
            Model(Array.Empty<Handshake.ModRecord>(), Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: false), section: Recommends(), refused: false), fx);
        s.ToggleRecommended(A);
        Assert.Equal("Apply 1 change", s.BottomCaption);
        Assert.Equal("turn-on", s.InstallSelected());
        Assert.Equal(JoinPage.Mods, s.Page);
        Assert.Null(s.TsTrust);
        Assert.Equal(new[] { A }, fx.TurnedOn.Select(t => t.Id));
        Assert.False(fx.TurnedOn[0].Required);
        Assert.Empty(fx.Started);
    }

    [Fact]
    public void A_required_rows_turn_on_goes_through_the_effects_and_shows_their_result_page()
    {
        var fx = new Fx { Offer = new JoinResultOffer { Kind = JoinAfterInstall.RestartNeeded, CanRejoin = true, Summary = "Alpha turns on at the next start of V Rising." } };
        var s = new JoinFlowSession(JoinMode.Refused,
            Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.0", now: false)), fx);
        Assert.Equal("turn-on", s.RowAction(A));
        var t = Assert.Single(fx.TurnedOn);
        Assert.True(t.Required);
        Assert.Equal("1.0", t.Version);
        Assert.False(t.AppliesNow);
        Assert.Equal(JoinPage.Result, s.Page);
        Assert.Equal("Alpha turns on at the next start of V Rising.", s.Notice);
    }

    [Fact]
    public void Without_live_effects_turn_on_says_it_is_not_available()
    {
        var s = new JoinFlowSession(JoinMode.Refused,
            Model(new[] { Must(A, "1.0") }, Array.Empty<Handshake.ModRecord>(), Off(A, "1.0")), new Fx { Default = true });
        Assert.Equal("turn-on", s.RowAction(A));
        Assert.Equal(JoinText.TurnOnNotAvailable, s.Notice);
        Assert.Equal(JoinPage.Mods, s.Page);
    }

    [Fact]
    public void The_after_restart_summary_reads_a_turned_on_row_ready_once_it_runs()
    {
        var record = new RejoinRecord
        {
            Rows = new List<RejoinRow>
            {
                new() { Id = A, Name = "Alpha", Version = "1.2.5", Required = true, Result = "turned-on" },
                new() { Id = B, Name = "Beta", Version = "2.0", Required = true, Result = "turned-off" },
            },
        };
        var s = RejoinSummary.From(record, id => id == A ? "1.2.5" : null);
        Assert.Equal(1, s.RequiredReady);
        Assert.Equal(JoinText.ReadyWord, s.Required[0].Value);
        Assert.Equal(JoinText.SummaryTurnedOff, s.Required[1].Value);
    }

    private sealed class Fx : IJoinFlowEffects
    {
        public bool Default;
        public JoinResultOffer? Offer;
        public readonly List<JoinTurnOnTarget> TurnedOn = new();
        public readonly List<JoinInstallRequest> Started = new();

        public bool CopyToClipboard(string text) => true;
        public void Leave() { }
        public void Closed(bool completed) { }
        public void MarkSeen() { }
        public void PageShown(JoinPage page) { }
        public bool NeedsThunderstoreTrust(IReadOnlyList<JoinInstallRequest> requests) => true;

        public string StartThunderstore(IReadOnlyList<JoinInstallRequest> requests, bool acknowledge)
        {
            Started.AddRange(requests);
            return "Installing " + requests.Count + " mods from Thunderstore…";
        }

        public string TurnOn(IReadOnlyList<JoinTurnOnTarget> targets, out JoinResultOffer? offer)
        {
            if (Default) return ((IJoinFlowEffects)new DefaultFx()).TurnOn(targets, out offer);
            TurnedOn.AddRange(targets);
            offer = Offer;
            return string.Join(" ", targets.Select(t => JoinText.TurnsOnAtNextStart(t.Name)));
        }

        private sealed class DefaultFx : IJoinFlowEffects
        {
            public bool CopyToClipboard(string text) => true;
            public void Leave() { }
            public void Closed(bool completed) { }
            public void MarkSeen() { }
            public void PageShown(JoinPage page) { }
        }
    }
}

/// <summary>
/// JOINTURNON end to end through the client on a refused join: the row readback, the hot route (the copy is declared,
/// the row turns Ready and the Result page offers Join now), the restart route (Restart and rejoin, the record's
/// <c>turned-on</c> row), the bridge's <c>mod_switch</c>, and an install of an off copy at the wrong version turning it on.
/// Same collection as the other <c>ModManifest</c> suites.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinTurnOnClientTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private const string Package = "ExampleTeam-Progenitor_Sample";
    private readonly string _dir;
    private readonly ClientRuntime _runtime = new();
    private readonly FakeRelauncher _relauncher = new();
    private readonly List<string> _switches = new();
    private double _now = 100;

    // The switch's state as the fake Mods list holds it.
    private bool _off = true;
    private bool _hot;
    private string _offVersion = "0.1.0";
    private bool _pausedHere;

    public JoinTurnOnClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-turnon-client-" + Guid.NewGuid().ToString("N"));
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
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren" };
        JoinFlowClient.Relauncher = _relauncher;
        JoinFlowClient.OffCopyProbe = id => id == Sample && _off
            ? new JoinOffCopy { Version = _offVersion, AppliesNow = _hot, TurnsOnWithInstall = true }
            : id == Sample && !_off && !_hot && _switches.Count > 0
                ? new JoinOffCopy { Version = _offVersion, TurnsOnAtNextStart = true, TurnsOnWithInstall = true }
                : null;
        JoinFlowClient.SwitchProbe = (id, on) =>
        {
            _switches.Add(id + ":" + (on ? "on" : "off"));
            _off = !on;
            if (on && _hot)
            {
                // The hot copy loads and declares itself.
                ModManifestDeny.TestProvided = new[] { Must(Sample, _offVersion) };
            }

            if (_pausedHere && on)
                return new JoinSwitchResult { Ok = true, Message = "Turned on. It stays paused for this server until you leave.", Now = true, Mode = "live", PausedHere = true };
            return new JoinSwitchResult { Ok = true, Message = on ? "Turned on." : "Turned off.", Now = _hot, Mode = _hot ? "live" : "restart", Name = "Progenitor Sample" };
        };
        ModVersionJoin.Provided = () => ModManifestDeny.TestProvided ?? Array.Empty<Handshake.ModRecord>();
    }

    public void Dispose()
    {
        JoinFlowPanel.Uninstall();
        JoinFlowClient.Uninstall();
        JoinFlowClient.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ModVersionJoin.Provided = () => Handshake.CopyProvided();
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

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static byte[] Descriptor() => Descriptor("", null);

    private static byte[] Descriptor(string message, ResetSchedule? reset)
    {
        using var key = ModManifestCrypto.Generate();
        var overrides = new ManifestOverrides
        {
            ServerName = "Night Garden",
            Mods = new[] { new ManifestModOverride { Id = Sample, Version = "0.1.0", Package = Package } },
        };
        var loaded = new[] { new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        var envelope = ModManifest.Build(loaded, overrides, key, DateTime.UtcNow, new JoinSection(message, reset, null, new JoinServerZone("UTC", 0)));
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

    private static Dictionary<string, object?> RowOf(Dictionary<string, object?> read) =>
        ((List<object?>)read["required"]!).Cast<Dictionary<string, object?>>().Single(r => (string)r["id"]! == Sample);

    private Dictionary<string, object?> RefusedWithMessage(string message, ResetSchedule? reset)
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = Array.Empty<Handshake.ModRecord>();
        ModManifestDeny.OnJoinDenied("Mod mismatch: progenitor.sample: expected 0.1.0, got missing", "127.0.0.1:9976");
        ModManifest.AcceptEnvelope(Descriptor(message, reset));
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("message", read["page"]);
        return read;
    }

    [Fact]
    public void The_times_line_is_drawn_only_when_a_token_drew_a_time()
    {
        var future = new ResetSchedule(ResetCountdown.FormatUtc(DateTime.UtcNow.AddDays(3)), ResetRepeat.None, 1, ResetPrecision.Hour, "now");
        var read = RefusedWithMessage("Reset in {reset}.", future);
        Assert.Equal(true, ((Dictionary<string, object?>)read["message"]!)["timesLine"]);
        Assert.Equal(true, ((Dictionary<string, object?>)read["drawn"]!)["timesLine"]);
        Assert.Equal("Times shown in your local time.", ((Dictionary<string, object?>)read["message"]!)["timesLineText"]);
    }

    [Fact]
    public void No_times_line_without_a_resolved_time()
    {
        var read = RefusedWithMessage("Welcome! Reset {reset}.", null);
        Assert.Equal(false, ((Dictionary<string, object?>)read["message"]!)["timesLine"]);
        Assert.Equal(false, ((Dictionary<string, object?>)read["drawn"]!)["timesLine"]);
    }

    [Fact]
    public void The_readback_names_turn_on_its_status_and_route()
    {
        Refused();
        var row = RowOf(JoinTestBridgeRead());
        Assert.Equal("turn-on", row["action"]);
        Assert.Equal("turned-off", row["status"]);
        Assert.Equal("Turn on", row["actionCaption"]);
        var turnOn = (Dictionary<string, object?>)row["turnOn"]!;
        Assert.Equal("restart", turnOn["route"]);
        Assert.Equal(false, turnOn["appliesNow"]);
        Assert.Equal(new[] { "needs restart" }, (IReadOnlyList<string>)row["tags"]!);
    }

    private static Dictionary<string, object?> JoinTestBridgeRead() => JoinFlowTestBridge.Read();

    [Fact]
    public void A_hot_turn_on_loads_now_and_a_refused_join_offers_join_now_once_it_is_declared()
    {
        _hot = true;
        Refused();
        string name = (string)RowOf(JoinFlowTestBridge.Read())["name"]!;
        Assert.Equal("applies now", Assert.Single((IReadOnlyList<string>)RowOf(JoinFlowTestBridge.Read())["tags"]!));
        var press = JoinFlowTestBridge.Row(Sample);
        Assert.Equal("turn-on", press["result"]);
        Assert.Equal(new[] { Sample + ":on" }, _switches);
        Assert.Equal(JoinText.TurnedOnLoading(name), press["notice"]);
        Assert.Equal("mods", press["page"]);
        _now += 1;
        JoinFlowClient.Tick();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("ready", RowOf(read)["action"]);
        Assert.Equal("result", read["page"]);
        Assert.Equal("join-now", ((Dictionary<string, object?>)read["result"]!)["kind"]);
        Assert.Equal(JoinText.TurnedOnRefused(name), read["notice"]);
    }

    [Fact]
    public void A_restart_turn_on_offers_restart_and_rejoin_and_the_record_says_turned_on()
    {
        Refused();
        string name = (string)RowOf(JoinFlowTestBridge.Read())["name"]!;
        var press = JoinFlowTestBridge.Row(Sample);
        Assert.Equal("turn-on", press["result"]);
        Assert.Equal("result", press["page"]);
        Assert.Equal(new[] { Sample + ":on" }, _switches);
        var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
        Assert.Equal("restart-needed", result["kind"]);
        Assert.Equal(true, result["canRejoin"]);
        Assert.Equal(JoinText.TurnsOnAtNextStart(name), result["summary"]);
        // The row now waits for the start (the switch is on).
        var row = RowOf(JoinFlowTestBridge.Read());
        Assert.Equal(true, ((Dictionary<string, object?>)row["turnOn"]!)["turnsOnAtNextStart"]);
        var turned = Assert.Single((List<object?>)JoinFlowTestBridge.Read()["turnedOnThisJoin"]!);
        Assert.Equal("restart", ((Dictionary<string, object?>)turned!)["route"]);

        Assert.Equal("dry-run", JoinFlowTestBridge.Restart(dryRun: true)["result"]);
        var record = (Dictionary<string, object?>)JoinFlowTestBridge.RejoinRead()["record"]!;
        var rec = ((List<object?>)record["rows"]!).Cast<Dictionary<string, object?>>().Single(r => (string)r["id"]! == Sample);
        Assert.Equal("turned-on", rec["result"]);
        Assert.Equal("0.1.0", rec["version"]);
        Assert.Equal(true, rec["required"]);
    }

    [Fact]
    public void A_turn_on_the_server_pause_holds_says_so_in_one_sentence_and_waits_for_nothing()
    {
        _hot = true;
        _pausedHere = true;
        Refused();
        string name = (string)RowOf(JoinFlowTestBridge.Read())["name"]!;
        var press = JoinFlowTestBridge.Row(Sample);
        Assert.Equal(JoinText.TurnedOnPausedHere(name), press["notice"]);
        Assert.DoesNotContain("loads in a moment", (string)press["notice"]!);
        Assert.DoesNotContain("Turned on. It stays", (string)press["notice"]!);
        _now += 20;
        JoinFlowClient.Tick();
        Assert.Equal(JoinText.TurnedOnPausedHere(name), JoinFlowTestBridge.Read()["notice"]);
        var turned = (Dictionary<string, object?>)Assert.Single((List<object?>)JoinFlowTestBridge.Read()["turnedOnThisJoin"]!)!;
        Assert.Equal("paused", turned["route"]);
    }

    [Fact]
    public void The_bridge_switch_turns_a_mod_off_and_on_through_the_same_switch()
    {
        _off = false;
        _hot = true;
        Refused();
        var off = JoinFlowTestBridge.ModSwitch(Sample, on: false);
        Assert.Equal("mod-switch ok", off["result"]);
        var sw = (Dictionary<string, object?>)off["switch"]!;
        Assert.Equal("Turned off.", sw["message"]);
        Assert.Equal("live", sw["mode"]);
        Assert.Equal("turn-on", RowOf(off)["action"]);
        var on = JoinFlowTestBridge.ModSwitch(Sample, on: true);
        Assert.Equal("mod-switch ok", on["result"]);
        Assert.Equal(new[] { Sample + ":off", Sample + ":on" }, _switches);
    }

    [Fact]
    public void A_process_with_no_record_of_what_it_dialed_offers_no_join_now_and_never_the_catalogs_host()
    {
        // Live 2026-10-05: a fresh process's connect record was empty; the catalog's host (the server's public
        // address) was dialed instead of the LAN address the player used.
        JoinFlowClient.TargetProbe = () => new JoinTarget();
        ModManifest.TestConnectHost = "203.0.113.83:9976";
        JoinFlowClient.OnJoinAttempt();
        _hot = true;
        Refused();
        JoinFlowTestBridge.Row(Sample);
        _now += 1;
        JoinFlowClient.Tick();
        var read = JoinFlowTestBridge.Read();
        Assert.Equal("ready", RowOf(read)["action"]);
        Assert.NotEqual("result", read["page"]);
        Assert.EndsWith("Join the server again from the server list.", (string)read["notice"]!);
        Assert.Equal("not on the result page", JoinFlowTestBridge.JoinNow()["result"]);
    }

    [Fact]
    public void Join_now_uses_the_address_this_join_dialed_when_the_live_record_is_gone()
    {
        JoinFlowClient.OnJoinAttempt();      // the record at the join: 127.0.0.1:9976 Lidgren
        JoinFlowClient.TargetProbe = () => new JoinTarget();
        ModManifest.TestConnectHost = "203.0.113.83:9976";
        _hot = true;
        Refused();
        JoinFlowTestBridge.Row(Sample);
        _now += 1;
        JoinFlowClient.Tick();
        var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
        Assert.Equal("join-now", result["kind"]);
        Assert.Equal(true, result["canRejoin"]);
    }

    [Fact]
    public void Without_the_switch_the_bridge_says_so()
    {
        JoinFlowClient.SwitchProbe = null;
        var r = JoinFlowTestBridge.ModSwitch(Sample, on: true);
        Assert.Equal("mod-switch refused", r["result"]);
        Assert.Contains("not available", (string)((Dictionary<string, object?>)r["switch"]!)["message"]!);
    }

    [Fact]
    public void An_install_of_an_off_copy_at_the_wrong_version_turns_the_mod_on()
    {
        _offVersion = "0.0.9";
        _hot = false;
        Refused();
        Assert.Equal("install", RowOf(JoinFlowTestBridge.Read())["action"]);
        JoinFlowClient.InstallerFactory = _ => new NoInstaller();
        Assert.Equal("ts-trust", JoinFlowTestBridge.Row(Sample)["result"]);
        Assert.Empty(_switches);
        JoinFlowTestBridge.TsInstall();
        Assert.Equal(new[] { Sample + ":on" }, _switches);
    }

    private sealed class NoInstaller : IJoinPackageInstaller
    {
        public System.Threading.Tasks.Task<JoinInstallOutcome> InstallAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress,
            System.Threading.CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new JoinInstallOutcome(JoinInstallPhase.AfterRestart, "", Sample, "x"));
    }

    private sealed class FakeRelauncher : IJoinRelauncher
    {
        public RelaunchPlan Plan() => new() { Mode = RelaunchMode.Automatic, FileName = "ps", Arguments = "", Script = "" };

        public void Start(RelaunchPlan plan, Action<bool, string> done) => done(true, "");

        public void Quit(double delaySeconds) { }
    }

    private sealed class ClientRuntime : CellRowTests.PlainRuntime, IUiRuntime
    {
        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header) =>
            new CellRowTests.FakeCellRow(null, spec, header);

        public IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title) => new CellRowTests.FakeWidget { Text = text };
    }
}

/// <summary>
/// JOINTURNON: the live off-copy probe (<c>ModsMenu.JoinOffCopyOf</c>) against the Mods list's switch state: the
/// player's choice (<c>ModEnableState</c>) makes an off copy, a per-server pause does not. Game-free: a fake pause
/// backend stands in for Bootstrap's reloader and a temp folder for <c>BepInEx/config</c>.
/// </summary>
[Collection("PluginLogger")]
public sealed class JoinTurnOnProbeTests : IDisposable
{
    private const string A = "alpha.mod";
    private readonly string _dir;
    private readonly Backend _backend = new();

    public JoinTurnOnProbeTests()
    {
        ModRegistry.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-turnon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModEnableState.TestDirectory = _dir;
        ModSwitch.BackendOverride = () => _backend;
    }

    public void Dispose()
    {
        ModRegistry.ResetForTests();
        ModSwitch.ResetForTests();
        ModEnableState.TestDirectory = null;
        ModEnableState.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class Backend : IServerModPauseBackend
    {
        public readonly List<string> Hot = new();
        public readonly List<string> Out = new();
        public bool Supported { get; set; } = true;
        public string UnsupportedReason => "";
        public string PendingRequest { get; set; } = "";
        public string ResumeReports { get; set; } = "";
        public IReadOnlyList<string> Suspendable() => Hot;
        public IReadOnlyList<PausedPlugin> Suspended() => Out.Select(g => new PausedPlugin(g, g, "9.9.9")).ToList();
        public IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal) { refusal = ""; return guids; }
        public IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report) { report = ""; return guids; }
        public IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report) { report = ""; return guids ?? Array.Empty<string>(); }
        public bool? HostsPrivateGame() => false;
        public IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids) => guids;
        public string ConnectionIdentity() => "";
    }

    private static void Loaded(string id, string version) =>
        ModRegistry.NoteLoading(id, "Alpha", version, Array.Empty<string>(), null);

    private static void TurnOff(string id) =>
        Assert.True(ModEnableState.Set(id, "Alpha", enabled: false, heldWith: null, DateTime.UtcNow, out _));

    [Fact]
    public void A_server_pause_is_not_an_off_copy()
    {
        Loaded(A, "1.2.0");
        ModRegistry.NoteUnloaded(A);
        _backend.Out.Add(A);
        var paused = ModsMenu.JoinOffCopyOf(A)!;
        Assert.True(paused.PausedHere);
        Assert.False(paused.TurnsOnWithInstall);
        // So the required row keeps today's answer.
        var model = JoinPageModel.Build(new JoinFacts
        {
            Asked = new[] { Handshake.ModRecord.CreateDeclared(A, "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact) },
            Refused = true,
            OffCopyOf = ModsMenu.JoinOffCopyOf,
        });
        Assert.Equal(JoinRowAction.AskAdmin, model.FindRequired(A)!.Action);
    }

    [Fact]
    public void A_hot_mod_the_player_turned_off_turns_on_now()
    {
        Loaded(A, "1.2.0");
        ModRegistry.NoteUnloaded(A);
        _backend.Out.Add(A);
        TurnOff(A);
        var off = ModsMenu.JoinOffCopyOf(A)!;
        Assert.Equal("1.2.0", off.Version);
        Assert.True(off.AppliesNow);
        Assert.False(off.TurnsOnAtNextStart);
        Assert.True(off.TurnsOnWithInstall);
    }

    [Fact]
    public void A_restart_only_mod_the_player_turned_off_turns_on_at_the_next_start()
    {
        Loaded(A, "1.2.0");
        ModRegistry.NoteSkipped(A, "off");
        TurnOff(A);
        var off = ModsMenu.JoinOffCopyOf(A)!;
        Assert.Equal("1.2.0", off.Version);
        Assert.False(off.AppliesNow);
        Assert.Equal("restart", off.Route);
    }

    [Fact]
    public void A_mod_switched_on_again_after_this_start_skipped_it_waits_for_the_next_start()
    {
        _backend.Supported = false;
        Loaded(A, "1.2.0");
        ModRegistry.NoteSkipped(A, "off");
        var off = ModsMenu.JoinOffCopyOf(A)!;
        Assert.True(off.TurnsOnAtNextStart);
        // Off only because a dependency is off: its own switch cannot bring it back, so no Turn on.
        ModRegistry.NoteSkipped(A, "dep off", "dep.mod");
        Assert.Null(ModsMenu.JoinOffCopyOf(A));
    }

    [Fact]
    public void A_running_or_unknown_mod_has_no_off_copy()
    {
        Assert.Null(ModsMenu.JoinOffCopyOf(A));
        Loaded(A, "1.2.0");
        ModRegistry.NoteLoaded(A);
        Assert.Null(ModsMenu.JoinOffCopyOf(A));
    }
}

/// <summary>
/// Requested 2026-10-05: the message page's line under the box reads only "Times shown in your local time." and
/// only when a token in the message produced a time (a countdown, a date or a relative time).
/// </summary>
public sealed class JoinMessageTimesLineTests
{
    private static readonly DateTime Now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ResetSchedule Reset(DateTime next) =>
        new(ResetCountdown.FormatUtc(next), ResetRepeat.None, 1, ResetPrecision.Minute, "now");

    private static JoinSection Section(string message, ResetSchedule? reset) => new(message, reset, null, new JoinServerZone("UTC", 0));

    [Theory]
    [InlineData("Welcome to the server.", false)]
    [InlineData("Reset in {reset}.", true)]
    [InlineData("Reset {reset:relative}.", true)]
    [InlineData("Reset on {reset:date}.", true)]
    [InlineData("Literal {foo} only.", false)]
    public void A_scheduled_reset_shows_the_line_only_when_a_token_draws_it(string message, bool shown)
    {
        Assert.Equal(shown, JoinText.ShowsTimesLine(Section(message, Reset(Now.AddDays(2))), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Not_scheduled_and_the_passed_text_draw_no_time()
    {
        Assert.False(JoinText.ShowsTimesLine(Section("Reset in {reset} ({reset:relative}).", null), Now, TimeZoneInfo.Utc));
        var passed = Section("Reset {reset}, {reset:relative}.", Reset(Now.AddDays(-1)));
        Assert.False(JoinText.ShowsTimesLine(passed, Now, TimeZoneInfo.Utc));
        // A passed reset's date still names a time in the player's zone.
        Assert.True(JoinText.ShowsTimesLine(Section("It was {reset:date}.", Reset(Now.AddDays(-1))), Now, TimeZoneInfo.Utc));
        Assert.False(JoinText.ShowsTimesLine(null, Now));
        Assert.Equal("Times shown in your local time.", JoinText.MessageTimesLine);
    }

    [Fact]
    public void Render_reports_whether_a_time_was_drawn()
    {
        ResetCountdown.Render("{reset}", Reset(Now.AddHours(5)), Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc, out bool live, out bool time);
        Assert.True(live);
        Assert.True(time);
        ResetCountdown.Render("{reset}", null, Now, TimeZoneInfo.Utc, TimeZoneInfo.Utc, out live, out time);
        Assert.False(live);
        Assert.False(time);
    }
}
