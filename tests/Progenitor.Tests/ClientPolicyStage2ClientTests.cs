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
/// CLIENTPOLICY2 through the client and a fake runtime: a join refused for the client mod policy draws the policy rows on
/// the refusal screens from the signed section the refusal carries (the required client mod with Install and the admin's
/// reason, the "Not allowed on this server" rows), remembers the policy for the next join's automatic pause, and the
/// rows' presses reach the pause and the switch (Join now / Restart and rejoin). Same collection as the other
/// <c>ModManifest</c> suites. Game-free: nothing here has been observed in the game.
/// </summary>
[Collection("ModManifest")]
public sealed class ClientPolicyStage2ClientTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private const string Robes = "test.robes.pack";
    private const string Extra = "extra.hot";
    private const string Plain = "plain.plugin";
    private const string Host = "127.0.0.1:9976";
    private readonly string _dir;
    private readonly List<ClientModEntry> _mods = new();
    private readonly HashSet<string> _pausable = new(StringComparer.OrdinalIgnoreCase) { Extra };
    private readonly HashSet<string> _offAtRestart = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _calls = new();
    private double _now = 100;

    public ClientPolicyStage2ClientTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-policy2-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = Host;
        ServerProfiles.ResetForTests();
        ServerProfiles.TestDirectory = _dir;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(new Runtime());
        JoinFlowPanel.Install();
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren" };
        JoinFlowClient.ClientModsProbe = () => _mods.ToList();
        JoinFlowClient.PausableProbe = () => _pausable.ToList();
        JoinFlowClient.TurnsOffAtRestartProbe = id => _offAtRestart.Contains(id);
        JoinFlowClient.PausedHereProbe = () => Array.Empty<ClientModEntry>();
        JoinFlowClient.PolicyPauseProbe = ids =>
        {
            _calls.Add("pause:" + string.Join(",", ids));
            _mods.RemoveAll(m => ids.Contains(m.Guid));
            return new JoinPauseResult { Ok = true, Taken = ids.ToList(), Names = ids.Select(i => i.ToUpperInvariant()).ToList() };
        };
        JoinFlowClient.SwitchProbe = (id, on) =>
        {
            _calls.Add("switch:" + id + "=" + (on ? "on" : "off"));
            if (!on) _offAtRestart.Add(id);
            return new JoinSwitchResult { Ok = true, Now = false, Mode = "restart", Name = id };
        };
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
        ServerProfiles.TestDirectory = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static ClientModEntry Mod(string guid, string version = "1.0.0", string source = ClientModSources.Progenitor) =>
        new(guid, guid.ToUpperInvariant(), version, source, "");

    /// <summary>The signed descriptor a server refusing for its policy sends: no failing row, the join section with the policy.</summary>
    private static byte[] Descriptor(bool withRequired)
    {
        using var key = ModManifestCrypto.Generate();
        var overrides = new ManifestOverrides { ServerName = "Night Garden" };
        var loaded = new[] { new LoadedModInfo(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        var policy = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved };
        if (withRequired) policy.Required.Add(new ClientModRow(Robes, "Owner-RobePack", "1.2.0", ClientModRule.AtLeast, "Our events use these robes.", "Robe Pack"));
        var section = JoinSectionJson.WithClientPolicy(new JoinSection("House rules apply.", null, null, new JoinServerZone("UTC", 0)), policy,
            new[] { Sample });
        var envelope = ModManifest.Build(loaded, overrides, key, DateTime.UtcNow, section);
        return ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope));
    }

    private void Refused(bool withRequired)
    {
        ModManifestDeny.TestServerAsked = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.TestProvided = new[] { Must(Sample, "0.1.0") };
        ModManifestDeny.OnJoinDenied("Mods not allowed on this server: EXTRA.HOT, PLAIN.PLUGIN.", Host);
        ModManifest.AcceptEnvelope(Descriptor(withRequired));
        var s = JoinFlowClient.Session!;
        Assert.Equal(JoinMode.Refused, s.Mode);
        // Through the panel, so the mods page is drawn (the readback's drawn sections).
        if (s.Page == JoinPage.Message) JoinFlowPanel.Press(JoinFlowLayout.ButtonContinue);
        Assert.Equal(JoinPage.Mods, JoinFlowClient.Session!.Page);
    }

    [Fact]
    public void A_policy_refusal_draws_the_policy_rows_from_the_signed_section_and_remembers_the_policy()
    {
        _mods.AddRange(new[] { Mod("progenitor"), Mod(Sample, "0.1.0"), Mod(Extra), Mod(Plain, source: ClientModSources.Chainloader) });
        Refused(withRequired: true);

        var read = JoinFlowTestBridge.Read();
        Assert.Equal(true, ((Dictionary<string, object?>)read["policy"]!)["checksClientMods"]);
        var required = ((List<object?>)read["required"]!).Cast<Dictionary<string, object?>>().ToList();
        var robes = required.Single(r => (string)r["id"]! == Robes);
        Assert.Equal("required-client", robes["kind"]);
        Assert.Equal("required-client", robes["origin"]);
        Assert.Equal("install", robes["action"]);
        Assert.Equal("Our events use these robes.", robes["reason"]);
        Assert.Equal("at-least", robes["rule"]);
        Assert.Equal("Needs 1.2.0 or newer · You have nothing", robes["versionLine"]);
        Assert.Equal("server", required.Single(r => (string)r["id"]! == Sample)["kind"]);

        var notAllowed = ((List<object?>)read["notAllowed"]!).Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal(new[] { Extra, Plain }.OrderBy(x => x), notAllowed.Select(r => (string)r["id"]!).OrderBy(x => x));
        var extra = notAllowed.Single(r => (string)r["id"]! == Extra);
        Assert.Equal("not-allowed", extra["kind"]);
        Assert.Equal("pause", extra["action"]);
        Assert.Equal(true, extra["pausable"]);
        Assert.Equal("Pause for this server", extra["actionCaption"]);
        var plain = notAllowed.Single(r => (string)r["id"]! == Plain);
        Assert.Equal("turn-off-restart", plain["action"]);
        Assert.Equal(false, plain["pausable"]);
        Assert.Equal("2 required mods (1 client). 1 needs attention. 2 mods aren't allowed here.", read["summary"]);

        // The red band drew the sub-heading.
        var drawn = (Dictionary<string, object?>)read["drawn"]!;
        Assert.Equal("red", ((Dictionary<string, object?>)drawn["sections"]!)["notAllowed"]);

        // The policy is remembered for the next join's automatic pause (the request carries only a bit).
        var remembered = ClientPolicyView.FromRemembered(ServerProfiles.PolicyOf(Host));
        Assert.NotNull(remembered);
        Assert.Equal(ClientPolicyMode.Approved, remembered!.PolicyMode);
        Assert.Equal(new[] { Sample }, remembered.ServerRuns);
        Assert.Equal(Robes, remembered.RequiredClient.Single().Id);
    }

    [Fact]
    public void Pause_for_this_server_then_turn_off_and_restart_lead_to_restart_and_rejoin()
    {
        _mods.AddRange(new[] { Mod("progenitor"), Mod(Sample, "0.1.0"), Mod(Extra), Mod(Plain, source: ClientModSources.Chainloader) });
        Refused(withRequired: false);

        var paused = JoinFlowTestBridge.NotAllowed(Extra);
        Assert.Equal("pause", paused["result"]);
        Assert.Equal(new[] { "pause:" + Extra }, _calls);
        // The plain plugin still needs its fix, so no Join now yet; the line says what was paused.
        Assert.Equal("mods", paused["page"]);
        Assert.Equal("Paused 1 mod this server doesn't allow: EXTRA.HOT. It comes back when you leave.", paused["notice"]);
        var afterPause = ((List<object?>)paused["notAllowed"]!).Cast<Dictionary<string, object?>>().Select(r => (string)r["id"]!).ToList();
        Assert.Equal(new[] { Plain }, afterPause);

        // A confirmation first, with the sentence the row is too narrow for; nothing is switched yet.
        var asked = JoinFlowTestBridge.NotAllowed(Plain);
        Assert.Equal("turn-off-restart", asked["result"]);
        Assert.Equal("Confirm turning off PLAIN.PLUGIN.", asked["notice"]);
        Assert.DoesNotContain("switch:" + Plain + "=off", _calls);
        var dialog = UiDialog.Current!;
        Assert.Equal("Turn off and restart?", dialog.Content.Title);
        Assert.Contains("It stays off until you turn it back on in the Mods list.", dialog.Content.Body);
        var off = JoinFlowTestBridge.NotAllowedConfirm(yes: true);
        Assert.Equal("confirmed", off["result"]);
        Assert.Contains("switch:" + Plain + "=off", _calls);
        Assert.Equal("result", off["page"]);
        var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
        Assert.Equal("restart-needed", result["kind"]);
        Assert.Contains("It stays off until you turn it back on in the Mods list.", (string)result["summary"]!);
    }

    private sealed class Runtime : CellRowTests.PlainRuntime, IUiRuntime
    {
        public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header) =>
            new CellRowTests.FakeCellRow(null, spec, header);

        public IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title) => new CellRowTests.FakeWidget { Text = text };
    }

    [Fact]
    public void Pausing_the_last_problem_on_a_refused_join_offers_Join_now()
    {
        _mods.AddRange(new[] { Mod("progenitor"), Mod(Sample, "0.1.0"), Mod(Extra) });
        Refused(withRequired: false);
        var paused = JoinFlowTestBridge.NotAllowed(Extra);
        Assert.Equal("pause", paused["result"]);
        Assert.Equal("result", paused["page"]);
        var result = (Dictionary<string, object?>)JoinFlowTestBridge.Read()["result"]!;
        Assert.Equal("join-now", result["kind"]);
        Assert.Equal("EXTRA.HOT is paused for this server. Join now; no download or restart needed.", result["summary"]);
    }
}
