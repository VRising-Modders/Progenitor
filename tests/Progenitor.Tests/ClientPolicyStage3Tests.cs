using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CLIENTPOLICY3 (docs/client-mod-policy.md, "Stage 3 as built"): the mid-session choice in the schema 2 store and its
/// panel operation (authorization, rate limit, audit, the change hook), the re-check's judgment (Warn and pause it,
/// Disconnect the player, pausable or not, mods loaded before a policy change and their longer grace, a required mod
/// turned off, a requirement added mid-session), the debounce, the alert's Approve, the server's notice wire and words,
/// the client's handling of the notice, the automatic pause before a mid-session report, the re-ask through a re-signed
/// catalog, and the bridge ops. Game-free: nothing here has been observed in the game.
/// </summary>
[Collection("ModManifest")]
public sealed class ClientPolicyStage3Tests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private const string Ok = "ok.mod";
    private const string Hot = "extra.hot";
    private const string Plain = "acme.RetainedPassword";
    private const string Robes = "test.robes.pack";

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private readonly List<string> _changed = new();
    private double _now = 1000;

    public ClientPolicyStage3Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-policy3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo("server.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        ModManifest.TestIsServer = true;
        Plugin.Logger = new TestLogSource();
        int window = 60;
        JoinAdminServer.WindowGet = () => window;
        JoinAdminServer.WindowSet = s => { window = s; return true; };
        JoinAdminServer.Audit = _audit.Add;
        JoinAdminServer.AuditClock = () => T0;
        JoinAdminServer.Now = () => _now;
        JoinAdminServer.PolicyChanged = _changed.Add;
        RefusedJoins.Uninstall();
        RefusedJoins.Clock = () => T0;
        ClientModReportClient.ResetForTests();
        // REQRULE5: these tests pin the timings with a 15-second grace (the old fixed value; the default is now 60).
        ClientPolicyRecheckLimits.Configured = () => 15;
    }

    public void Dispose()
    {
        ClientPolicyRecheckLimits.Configured = null;
        RefusedJoins.Uninstall();
        JoinAdminServer.Uninstall();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ClientModPolicyRecords.Clear();
        ClientModReportClient.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private JoinAdminResponse Apply(JoinAdminRequest r, RemoteInstallActor? actor = null, bool handshakeOk = true)
    {
        _now += 7;
        return JoinAdminServer.Apply(r, handshakeOk, actor ?? Admin);
    }

    private static JoinAdminRequest Req(string op, string id = "", string value = "", string name = "") =>
        new() { Op = op, Id = id, Value = value, Name = name };

    private static ClientModEntry HotMod(string guid, string? name = null) => new(guid, name ?? Title(guid), "1.0.0", ClientModSources.Progenitor);

    private static ClientModEntry PlainMod(string guid, string? name = null) => new(guid, name ?? Title(guid), "1.0.0", ClientModSources.Chainloader);

    private static string Title(string guid) => guid == Plain ? "RetainedPassword" : guid == Hot ? "Extra Hot" : guid;

    private static ClientModReport Report(params ClientModEntry[] entries)
    {
        var r = new ClientModReport();
        r.Entries.AddRange(entries);
        return r;
    }

    private static ClientPolicyConfig Approved(MidSessionPolicy mid, params string[] approved)
    {
        var p = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved, MidSession = mid };
        foreach (string id in approved) p.Approved.Add(new ClientModRow(id, "", "", ClientModRule.Any));
        return p;
    }

    private static ClientPolicyConfig RequiresRobes(MidSessionPolicy mid)
    {
        var p = new ClientPolicyConfig { MidSession = mid };
        p.Required.Add(new ClientModRow(Robes, "Owner-RobePack", "1.2.0", ClientModRule.AtLeast, "Our robes.", "Robe Pack"));
        return p;
    }

    private static readonly ClientModServerFacts Facts = new();

    // ---- the store: the new choice in schema 2 -----------------------------------------------------------------------

    [Fact]
    public void A_schema_2_file_without_the_choice_loads_as_warn_and_the_choice_round_trips()
    {
        const string before = "{\"schema\":2,\"recommended\":[],\"requiredClient\":[],\"clientPolicy\":{\"mode\":\"approved\",\"approved\":[],\"unverified\":\"allow\"}}";
        Assert.True(JoinAdminConfig.TryParse(before, out var config, out var error), error);
        Assert.Equal(MidSessionPolicy.Warn, config.Policy.MidSession);
        Assert.Equal(ClientPolicyMode.Approved, config.Policy.Mode);
        Assert.Contains("\"midSession\": \"warn\"", new JoinAdminConfig().ToJson());

        config.Policy.MidSession = MidSessionPolicy.Disconnect;
        string json = config.ToJson();
        Assert.Contains("\"midSession\": \"disconnect\"", json);
        Assert.True(JoinAdminConfig.TryParse(json, out var again, out _));
        Assert.Equal(MidSessionPolicy.Disconnect, again.Policy.MidSession);
        Assert.Equal(MidSessionPolicy.Disconnect, again.Clone().Policy.MidSession);

        // A schema 1 file keeps the default too; an unknown word is skipped with a note, never read as Disconnect.
        Assert.True(JoinAdminConfig.TryParse("{\"schema\":1,\"recommended\":[]}", out var v1, out _));
        Assert.Equal(MidSessionPolicy.Warn, v1.Policy.MidSession);
        var skipped = new List<string>();
        Assert.True(JoinAdminConfig.TryParse("{\"schema\":2,\"clientPolicy\":{\"mode\":\"any\",\"midSession\":\"sometimes\"}}", out var bad, out _, skipped));
        Assert.Equal(MidSessionPolicy.Warn, bad.Policy.MidSession);
        Assert.Contains(skipped, s => s.Contains("midSession \"sometimes\"") && s.Contains("Warn and pause it kept"));

        Assert.True(ClientModRules.TryParseMidSession("Disconnect", out var d) && d == MidSessionPolicy.Disconnect);
        Assert.True(ClientModRules.TryParseMidSession("warn", out var w) && w == MidSessionPolicy.Warn);
        Assert.False(ClientModRules.TryParseMidSession("ban", out _));
    }

    // ---- the panel operation -----------------------------------------------------------------------------------------

    [Fact]
    public void The_mid_session_op_is_stored_audited_and_raises_the_change_hook()
    {
        var r = Apply(Req(JoinAdminOps.MidSession, value: "disconnect"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(ClientPolicyText.MidSessionDisconnectNote, r.Note);
        Assert.Equal("disconnect", r.State!.MidSession);
        Assert.Contains("\"midSession\": \"disconnect\"", File.ReadAllText(JoinAdminServer.FilePath));
        Assert.Equal(MidSessionPolicy.Disconnect, JoinAdminServer.CurrentPolicy.MidSession);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/mid-session") && l.Contains("accepted") && l.Contains("disconnect"));
        Assert.Equal(new[] { JoinAdminOps.MidSession }, _changed);

        // Unchanged changes nothing (no hook); a bad value is refused and audited.
        var same = Apply(Req(JoinAdminOps.MidSession, value: "disconnect"));
        Assert.True(same.Ok);
        Assert.Equal("Unchanged.", same.Note);
        Assert.Single(_changed);
        var bad = Apply(Req(JoinAdminOps.MidSession, value: "ban"));
        Assert.False(bad.Ok);
        Assert.Equal("Choose warn or disconnect.", bad.Error);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/mid-session") && l.Contains("Choose warn or disconnect."));

        Assert.True(Apply(Req(JoinAdminOps.MidSession, value: "warn")).Ok);
        Assert.Equal("warn", JoinAdminServer.BuildState().MidSession);
        Assert.Equal(2, _changed.Count);
        Assert.True(JoinAdminOps.IsPolicy(JoinAdminOps.MidSession));
        Assert.Contains(JoinAdminOps.MidSession, JoinAdminOps.All);
    }

    [Fact]
    public void The_mid_session_op_has_the_same_authorization_and_rate_limit_as_every_edit()
    {
        var player = Apply(Req(JoinAdminOps.MidSession, value: "disconnect"), Player);
        Assert.False(player.Ok);
        Assert.Equal(JoinAdminPolicy.NotAdminError, player.Error);
        Assert.Null(player.State);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/mid-session") && l.Contains(JoinAdminPolicy.NotAdminError));
        Assert.Equal(JoinAdminPolicy.HandshakeError, Apply(Req(JoinAdminOps.MidSession, value: "disconnect"), handshakeOk: false).Error);
        Assert.Equal(MidSessionPolicy.Warn, JoinAdminServer.CurrentPolicy.MidSession);
        Assert.Empty(_changed);

        var flood = new RemoteInstallActor("Flood", 76561198000000009UL, isAdmin: true);
        int refused = 0;
        for (int i = 0; i < JoinAdminServer.RateMaxEdits + 3; i++)
            if (JoinAdminServer.Apply(Req(JoinAdminOps.MidSession, value: i % 2 == 0 ? "disconnect" : "warn"), true, flood).Error == JoinAdminPolicy.RateLimitedError) refused++;
        Assert.Equal(3, refused);
    }

    [Fact]
    public void The_choice_travels_in_the_request_and_the_state()
    {
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(Req(JoinAdminOps.MidSession, value: "disconnect")), out var req));
        Assert.Equal(JoinAdminOps.MidSession, req.Op);
        Assert.Equal("disconnect", req.Value);

        Assert.True(Apply(Req(JoinAdminOps.MidSession, value: "disconnect")).Ok);
        var response = new JoinAdminResponse { Nonce = 3, Op = JoinAdminOps.Get, Ok = true, State = JoinAdminServer.BuildState() };
        Assert.True(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeResponse(response), out var back));
        Assert.Equal("disconnect", back.State!.MidSession);

        // An older server's state carries no choice: the panel draws no row for it.
        var old = new JoinAdminResponse { Op = JoinAdminOps.Get, Ok = true, State = new JoinAdminState { PolicySupported = true } };
        Assert.True(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeResponse(old), out var oldBack));
        Assert.Equal("", oldBack.State!.MidSession);
    }

    // ---- Approve on the refused-join alert ---------------------------------------------------------------------------

    [Fact]
    public void A_not_approved_line_offers_Approve_which_adds_the_mod_and_the_line_updates()
    {
        Assert.True(Apply(Req(JoinAdminOps.PolicyMode, value: "approved")).Ok);
        RefusedJoins.InjectForTest(Plain, "", "", RefusedJoinLog.KindNotApproved, 3);
        RefusedJoins.InjectForTest("server.mod", "1.0.0", "", "", 1);
        RefusedJoins.Flush();

        var state = JoinAdminServer.BuildState();
        var line = Assert.Single(state.Alert, a => a.Kind == RefusedJoinLog.KindNotApproved);
        Assert.True(line.CanApprove);
        Assert.False(line.Approved);
        Assert.Equal(Plain + ": not approved (3 joins)", line.Text);
        Assert.False(Assert.Single(state.Alert, a => a.Mod == "server.mod").CanApprove);

        // The button sends the existing approve-add for the line's mod (any version).
        var r = Apply(new JoinAdminRequest { Op = JoinAdminOps.ApproveAdd, Id = line.Mod });
        Assert.True(r.Ok, r.Error);
        Assert.Contains(_audit, l => l.Contains("JoinAdmin/approve-add/" + Plain) && l.Contains("accepted"));
        Assert.True(JoinAdminServer.CurrentPolicy.IndexOfApproved(Plain) >= 0);
        var after = Assert.Single(r.State!.Alert, a => a.Kind == RefusedJoinLog.KindNotApproved);
        Assert.True(after.Approved);
        Assert.False(after.CanApprove);
        Assert.Equal(Plain + ": approved now (3 joins refused before)", after.Text);

        // The mark crosses the wire (the panel's rebuild signature includes the line's text, which changed).
        var response = new JoinAdminResponse { Op = JoinAdminOps.Get, Ok = true, State = r.State };
        Assert.True(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeResponse(response), out var back));
        var backLine = Assert.Single(back.State!.Alert, a => a.Kind == RefusedJoinLog.KindNotApproved);
        Assert.True(backLine.Approved);
        Assert.NotEqual(line.Text, backLine.Text);
    }

    [Fact]
    public void Approve_is_offered_only_for_a_plain_not_approved_id()
    {
        Assert.True(new JoinAdminAlertRow { Mod = "a.mod", Kind = RefusedJoinLog.KindNotApproved }.CanApprove);
        Assert.False(new JoinAdminAlertRow { Mod = "a.mod", Kind = RefusedJoinLog.KindNotApproved, Approved = true }.CanApprove);
        Assert.False(new JoinAdminAlertRow { Mod = "not a plain id!", Kind = RefusedJoinLog.KindNotApproved }.CanApprove);
        Assert.False(new JoinAdminAlertRow { Mod = "a.mod", Kind = RefusedJoinLog.KindClientMissing }.CanApprove);
        Assert.False(new JoinAdminAlertRow { Mod = RefusedJoinLog.UncheckedMod, Kind = RefusedJoinLog.KindUnchecked }.CanApprove);

        // An always-allowed or required mod reads as approved too.
        var policy = Approved(MidSessionPolicy.Warn);
        policy.Required.Add(new ClientModRow("req.mod", "", "", ClientModRule.Any));
        var bep = new JoinAdminAlertRow { Mod = "BepInEx.Core", Name = "BepInEx.Core", Kind = RefusedJoinLog.KindNotApproved, Count = 1 };
        JoinAdminServer.MarkApproved(bep, policy);
        Assert.True(bep.Approved);
        Assert.Equal("BepInEx.Core: approved now (1 join refused before)", bep.Text);
        var req = new JoinAdminAlertRow { Mod = "req.mod", Name = "req.mod", Kind = RefusedJoinLog.KindNotApproved, Count = 2 };
        JoinAdminServer.MarkApproved(req, policy);
        Assert.True(req.Approved);
    }

    // ---- the re-check's judgment ------------------------------------------------------------------------------------

    [Fact]
    public void Warn_names_a_newly_loaded_pausable_mod_once_and_never_disconnects()
    {
        var t = new ClientPolicyRecheckTracker();
        var policy = Approved(MidSessionPolicy.Warn, Ok);
        t.OnJoinAccepted(1, policy, Report(HotMod(Ok)));

        var d = t.Judge(1, policy, Report(HotMod(Ok), HotMod(Hot)), Facts, "report", 10);
        Assert.Equal(RecheckNoticeKind.Warn, d.Notice);
        var item = Assert.Single(d.NoticeItems);
        Assert.Equal(Hot, item.Key);
        Assert.True(item.Pausable);
        Assert.False(item.PreChange);
        Assert.False(d.ArmKick || d.KickNow);
        Assert.Equal("warn", d.Word);

        // Told once: the same report again says nothing; gone, then loaded again, it is told again.
        Assert.Equal(RecheckNoticeKind.None, t.Judge(1, policy, Report(HotMod(Ok), HotMod(Hot)), Facts, "report", 13).Notice);
        var gone = t.Judge(1, policy, Report(HotMod(Ok)), Facts, "report", 16);
        Assert.Empty(gone.Items);
        Assert.Equal("ok", gone.Word);
        Assert.Equal(RecheckNoticeKind.Warn, t.Judge(1, policy, Report(HotMod(Ok), HotMod(Hot)), Facts, "report", 19).Notice);
    }

    [Fact]
    public void Warn_tells_a_player_who_cant_pause_a_mod_to_turn_it_off_and_restart()
    {
        var t = new ClientPolicyRecheckTracker();
        var policy = Approved(MidSessionPolicy.Warn, Ok);
        t.OnJoinAccepted(1, policy, Report(HotMod(Ok)));
        var d = t.Judge(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 10);
        Assert.Equal(RecheckNoticeKind.Warn, d.Notice);
        Assert.False(Assert.Single(d.NoticeItems).Pausable);
        Assert.False(d.ArmKick);

        var content = ClientPolicyNoticeText.Compose(d.Notice, d.NoticeItems, d.Seconds, "Night Garden");
        Assert.Equal("Mods not allowed on this server", content.Title);
        Assert.Equal("Night Garden doesn't allow this mod. You can keep playing:", content.Body);
        Assert.Equal("RetainedPassword · Not allowed and can't be paused; turn it off in the Mods list and restart V Rising before you rejoin",
            Assert.Single(content.Lines));
        Assert.Equal(ClientPolicyNoticeText.NextJoinNote, content.Note);
    }

    [Fact]
    public void Disconnect_arms_a_short_grace_for_a_mod_loaded_mid_session_and_kicks_when_it_stays()
    {
        var t = new ClientPolicyRecheckTracker();
        var policy = Approved(MidSessionPolicy.Disconnect, Ok);
        t.OnJoinAccepted(1, policy, Report(HotMod(Ok)));

        var d = t.Judge(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 100);
        Assert.True(d.ArmKick);
        Assert.Equal(ClientPolicyRecheckLimits.DisconnectGraceSeconds, d.Seconds);
        Assert.Equal(15, d.Seconds);
        Assert.Equal(RecheckNoticeKind.Disconnect, d.Notice);
        int gen = d.KickGeneration;
        Assert.False(t.IsDue(1, gen, 110));
        Assert.True(t.IsDue(1, gen, 115));
        Assert.Equal(5.0, t.SecondsLeft(1, 110)!.Value, 3);

        var content = ClientPolicyNoticeText.Compose(d.Notice, d.NoticeItems, d.Seconds, "Night Garden");
        Assert.Equal("Night Garden disconnects players who load mods it doesn't allow. Fix these within 15 seconds, or you'll be disconnected:", content.Body);

        var kick = t.Judge(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts, "deadline", 115);
        Assert.True(kick.KickNow);
        Assert.Equal(RecheckNoticeKind.Disconnected, kick.Notice);
        Assert.Equal("Mods not allowed on this server, loaded during the session: RetainedPassword.", kick.KickReason);
        Assert.False(t.IsDue(1, gen, 200));
        Assert.Null(t.SecondsLeft(1, 120));
    }

    [Fact]
    public void Disconnect_is_called_off_when_the_next_report_is_clean_such_as_after_the_client_paused_the_mod()
    {
        var t = new ClientPolicyRecheckTracker();
        var policy = Approved(MidSessionPolicy.Disconnect, Ok);
        t.OnJoinAccepted(1, policy, Report(HotMod(Ok)));
        var d = t.Judge(1, policy, Report(HotMod(Ok), HotMod(Hot)), Facts, "report", 100);
        Assert.True(d.ArmKick);
        Assert.True(Assert.Single(d.NoticeItems).Pausable);

        var clean = t.Judge(1, policy, Report(HotMod(Ok)), Facts, "report", 103);
        Assert.True(clean.CancelKick);
        Assert.Equal(RecheckNoticeKind.Resolved, clean.Notice);
        Assert.False(t.IsDue(1, d.KickGeneration, 115));
        Assert.Equal(RecheckNoticeKind.None, t.Judge(1, policy, Report(HotMod(Ok)), Facts, "deadline", 115).Notice);
        Assert.Equal("You won't be disconnected", ClientPolicyNoticeText.Compose(clean.Notice, clean.NoticeItems, 0, null).Title);
    }

    [Fact]
    public void Mods_loaded_before_a_policy_change_warn_under_warn_and_get_the_long_grace_under_disconnect()
    {
        // The player joined while RetainedPassword was approved; the admins then unapprove it.
        var before = Approved(MidSessionPolicy.Warn, Ok, Plain);
        var report = Report(HotMod(Ok), PlainMod(Plain));

        var warn = new ClientPolicyRecheckTracker();
        warn.OnJoinAccepted(1, before, report);
        warn.OnPolicyChange(1, report);
        var w = warn.Judge(1, Approved(MidSessionPolicy.Warn, Ok), report, Facts, "policy-change", 50);
        Assert.Equal(RecheckNoticeKind.Warn, w.Notice);
        Assert.True(Assert.Single(w.NoticeItems).PreChange);
        Assert.False(w.ArmKick);

        var disconnect = new ClientPolicyRecheckTracker();
        disconnect.OnJoinAccepted(1, before, report);
        disconnect.OnPolicyChange(1, report);
        var d = disconnect.Judge(1, Approved(MidSessionPolicy.Disconnect, Ok), report, Facts, "policy-change", 50);
        Assert.True(d.ArmKick);
        Assert.Equal(ClientPolicyRecheckLimits.PolicyChangeGraceSeconds, d.Seconds);
        Assert.Equal(60, d.Seconds);

        // A mod loaded after the change shortens it to the ordinary grace.
        var mixed = new ClientPolicyRecheckTracker();
        mixed.OnJoinAccepted(1, before, report);
        mixed.OnPolicyChange(1, report);
        var m = mixed.Judge(1, Approved(MidSessionPolicy.Disconnect, Ok), Report(HotMod(Ok), PlainMod(Plain), PlainMod("new.plain")), Facts, "report", 50);
        Assert.Equal(15, m.Seconds);
    }

    [Fact]
    public void A_mod_unloaded_and_loaded_again_after_the_change_counts_as_a_new_load()
    {
        var t = new ClientPolicyRecheckTracker();
        var report = Report(HotMod(Ok), HotMod(Hot));
        t.OnJoinAccepted(1, Approved(MidSessionPolicy.Warn, Ok, Hot), report);
        t.OnPolicyChange(1, report);
        var policy = Approved(MidSessionPolicy.Warn, Ok);
        Assert.True(Assert.Single(t.Judge(1, policy, report, Facts, "policy-change", 10).Items).PreChange);
        t.Judge(1, policy, Report(HotMod(Ok)), Facts, "report", 13);
        var again = t.Judge(1, policy, report, Facts, "report", 16);
        Assert.False(Assert.Single(again.Items).PreChange);
    }

    [Fact]
    public void A_player_asked_only_after_the_change_has_everything_counted_as_loaded_before_it()
    {
        var t = new ClientPolicyRecheckTracker();
        t.OnPolicyChange(4, null);
        Assert.Equal(true, t.Describe(4, 0)!["awaitingBaseline"]);
        var d = t.Judge(4, Approved(MidSessionPolicy.Disconnect, Ok), Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 20);
        Assert.True(Assert.Single(d.Items).PreChange);
        Assert.Equal(60, d.Seconds);
        Assert.Equal(false, t.Describe(4, 20)!["awaitingBaseline"]);
    }

    [Fact]
    public void A_required_mod_turned_off_mid_session_warns_under_warn_and_disconnects_under_disconnect()
    {
        foreach (var mid in new[] { MidSessionPolicy.Warn, MidSessionPolicy.Disconnect })
        {
            var t = new ClientPolicyRecheckTracker();
            var policy = RequiresRobes(mid);
            t.OnJoinAccepted(1, policy, Report(new ClientModEntry(Robes, "Robe Pack", "1.2.0", ClientModSources.Progenitor)));
            var d = t.Judge(1, policy, Report(), Facts, "report", 30);
            var item = Assert.Single(d.Items);
            Assert.Equal(ClientModProblem.KindMissing, item.Kind);
            Assert.False(item.Added);
            Assert.Equal("Robe Pack · Required here; turn it back on in the Mods list", ClientPolicyNoticeText.Line(item, RecheckPauseOutcome.NotTried));
            if (mid == MidSessionPolicy.Warn)
            {
                Assert.Equal(RecheckNoticeKind.Warn, d.Notice);
                Assert.False(d.ArmKick);
                Assert.Equal("Mods this server requires", ClientPolicyNoticeText.Compose(d.Notice, d.NoticeItems, 0, null).Title);
            }
            else
            {
                Assert.True(d.ArmKick);
                Assert.Equal(15, d.Seconds);
                Assert.Equal("This server disconnects players who turn off a mod it requires. Turn it back on within 15 seconds, or you'll be disconnected:",
                    ClientPolicyNoticeText.Compose(d.Notice, d.NoticeItems, d.Seconds, null).Body);
            }
        }
    }

    [Fact]
    public void A_requirement_the_admins_add_mid_session_is_told_and_never_disconnects()
    {
        var t = new ClientPolicyRecheckTracker();
        var report = Report(HotMod(Ok));
        t.OnJoinAccepted(1, new ClientPolicyConfig(), report);
        t.OnPolicyChange(1, report);
        var policy = RequiresRobes(MidSessionPolicy.Disconnect);
        var d = t.Judge(1, policy, report, Facts, "policy-change", 40);
        Assert.Equal(RecheckNoticeKind.Warn, d.Notice);
        Assert.False(d.ArmKick);
        var item = Assert.Single(d.NoticeItems);
        Assert.True(item.Added);
        Assert.False(item.KickWorthy);
        Assert.Equal("Robe Pack · Now required here; install it before your next join", ClientPolicyNoticeText.Line(item, RecheckPauseOutcome.NotTried));
        Assert.False(t.Judge(1, policy, report, Facts, "deadline", 500).KickNow);
    }

    [Fact]
    public void Switching_to_warn_while_a_disconnect_is_armed_calls_it_off_and_names_the_mods()
    {
        var t = new ClientPolicyRecheckTracker();
        t.OnJoinAccepted(1, Approved(MidSessionPolicy.Disconnect, Ok), Report(HotMod(Ok)));
        var report = Report(HotMod(Ok), PlainMod(Plain));
        var armed = t.Judge(1, Approved(MidSessionPolicy.Disconnect, Ok), report, Facts, "report", 10);
        Assert.True(armed.ArmKick);
        var switched = t.Judge(1, Approved(MidSessionPolicy.Warn, Ok), report, Facts, "policy-change", 12);
        Assert.True(switched.CancelKick);
        Assert.Equal(RecheckNoticeKind.Warn, switched.Notice);
        Assert.Equal(Plain, Assert.Single(switched.NoticeItems).Key);
        Assert.False(t.IsDue(1, armed.KickGeneration, 30));
    }

    [Fact]
    public void Reports_are_judged_at_most_every_two_seconds_with_one_held_judgment()
    {
        var t = new ClientPolicyRecheckTracker();
        Assert.True(t.Admit(1, 100, out _));
        t.Judge(1, Approved(MidSessionPolicy.Warn), Report(), Facts, "report", 100);
        Assert.False(t.Admit(1, 100.5, out double wait));
        Assert.Equal(1.5, wait, 3);
        Assert.False(t.Admit(1, 101, out double again));
        Assert.True(again < 0); // one held judgment is already scheduled; it reads the latest report
        t.ReleaseHeld(1);
        Assert.True(t.Admit(1, 102.1, out _));
        Assert.True(t.Admit(2, 100.5, out _)); // per user
        Assert.Equal(ClientPolicyRecheckLimits.MinJudgeSeconds, 2.0);
    }

    [Fact]
    public void Nothing_is_judged_without_an_active_policy_and_a_disconnect_armed_before_is_called_off()
    {
        var t = new ClientPolicyRecheckTracker();
        t.OnJoinAccepted(1, Approved(MidSessionPolicy.Disconnect, Ok), Report(HotMod(Ok)));
        Assert.True(t.Judge(1, Approved(MidSessionPolicy.Disconnect, Ok), Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 10).ArmKick);
        var off = t.Judge(1, new ClientPolicyConfig { MidSession = MidSessionPolicy.Disconnect }, Report(HotMod(Ok), PlainMod(Plain)), Facts, "policy-change", 11);
        Assert.Empty(off.Items);
        Assert.True(off.CancelKick);
        Assert.Equal(RecheckNoticeKind.Resolved, off.Notice);
    }

    [Fact]
    public void The_disconnect_reason_and_the_chat_line_are_readable_ascii()
    {
        var items = new List<RecheckItem>
        {
            new() { Key = Plain, Name = "Remember<b>Password</b>", Kind = ClientModProblem.KindNotAllowed },
            new() { Key = Robes, Name = "Robe Pack", Kind = ClientModProblem.KindMissing },
        };
        string reason = ClientPolicyNoticeText.KickReason(items);
        Assert.Equal("Mods not allowed on this server, loaded during the session: Remember(b)Password(/b). Required mods turned off during the session: Robe Pack.", reason);
        Assert.True(reason.Length <= Handshake.ResultMessageMaxChars);
        string chat = ClientPolicyNoticeText.Chat(RecheckNoticeKind.Disconnect, items, 15);
        Assert.StartsWith("Mods not allowed on this server. This server disconnects players who load mods it doesn't allow. Fix these within 15 seconds", chat);
        Assert.Contains("Robe Pack - Required here; turn it back on in the Mods list", chat);
        Assert.DoesNotContain('<', chat);
        Assert.All(chat, c => Assert.InRange(c, ' ', '~'));
    }

    // ---- review fixes: tightened and re-added rows, the last check, late join reports, per-item grace, reasons, size --

    private static ClientPolicyConfig RobesAt(string version, MidSessionPolicy mid = MidSessionPolicy.Disconnect)
    {
        var p = new ClientPolicyConfig { MidSession = mid };
        p.Required.Add(new ClientModRow(Robes, "Owner-RobePack", version, ClientModRule.AtLeast, "Our robes.", "Robe Pack"));
        return p;
    }

    private static ClientModReport WithRobes(string version) =>
        Report(HotMod(Ok), new ClientModEntry(Robes, "Robe Pack", version, ClientModSources.Progenitor));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tightening_a_required_row_mid_session_never_disconnects(bool passPolicyToTheChange)
    {
        var t = new ClientPolicyRecheckTracker();
        t.OnJoinAccepted(1, RobesAt("1.2.0"), WithRobes("1.2.0"));
        var tightened = RobesAt("1.3.0");
        t.OnPolicyChange(1, WithRobes("1.2.0"), passPolicyToTheChange ? tightened : null);
        var d = t.Judge(1, tightened, WithRobes("1.2.0"), Facts, "policy-change", 10);
        var item = Assert.Single(d.Items);
        Assert.Equal(ClientModProblem.KindOlder, item.Kind);
        Assert.True(item.Added);
        Assert.False(d.ArmKick);
        Assert.Equal(RecheckNoticeKind.Warn, d.Notice);
        Assert.False(t.Judge(1, tightened, WithRobes("1.2.0"), Facts, "deadline", 500).KickNow);

        // Meeting the tightened row once makes it the player's to keep.
        t.Judge(1, tightened, WithRobes("1.3.0"), Facts, "report", 600);
        Assert.True(t.Judge(1, tightened, WithRobes("1.2.0"), Facts, "report", 610).ArmKick);
    }

    [Fact]
    public void Re_adding_a_removed_required_row_never_disconnects()
    {
        var t = new ClientPolicyRecheckTracker();
        var required = RobesAt("1.2.0");
        t.OnJoinAccepted(1, required, WithRobes("1.2.0"));

        // Removed: the player then unloads the mod (fine); the row comes back.
        var none = new ClientPolicyConfig { MidSession = MidSessionPolicy.Disconnect };
        t.OnPolicyChange(1, WithRobes("1.2.0"), none);
        t.Judge(1, none, WithRobes("1.2.0"), Facts, "policy-change", 10);
        t.Judge(1, none, Report(HotMod(Ok)), Facts, "report", 13);
        t.OnPolicyChange(1, Report(HotMod(Ok)), required);
        var d = t.Judge(1, required, Report(HotMod(Ok)), Facts, "policy-change", 20);
        Assert.True(Assert.Single(d.Items).Added);
        Assert.False(d.ArmKick);
        Assert.Equal(RecheckNoticeKind.Warn, d.Notice);
    }

    [Fact]
    public void A_report_after_the_answer_deadline_is_the_joins_baseline_not_a_mid_session_load()
    {
        var t = new ClientPolicyRecheckTracker();
        var policy = Approved(MidSessionPolicy.Disconnect, Ok);
        policy.Required.Add(new ClientModRow(Robes, "", "", ClientModRule.Any, "", "Robe Pack"));
        t.OnJoinAccepted(1, policy, null); // let in unchecked (Let them join), or the deadline passed first
        var d = t.Judge(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 30);
        var plain = Assert.Single(d.Items, i => i.NotAllowed);
        Assert.True(plain.PreChange);
        Assert.True(Assert.Single(d.Items, i => !i.NotAllowed).Added);
        Assert.True(d.ArmKick);
        Assert.Equal(60, d.Seconds);

        // Under Warn the same late report only warns.
        var w = new ClientPolicyRecheckTracker();
        w.OnJoinAccepted(1, Approved(MidSessionPolicy.Warn, Ok), null);
        Assert.Equal(RecheckNoticeKind.Warn, w.Judge(1, Approved(MidSessionPolicy.Warn, Ok), Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 30).Notice);
    }

    [Fact]
    public void The_last_check_before_the_disconnect_reads_the_latest_report()
    {
        var t = new ClientPolicyRecheckTracker();
        var policy = Approved(MidSessionPolicy.Disconnect, Ok);
        t.OnJoinAccepted(1, policy, Report(HotMod(Ok)));
        Assert.True(t.Judge(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts, "report", 10).ArmKick);
        Assert.True(t.Judge(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts, "deadline", 25).KickNow);

        // In the flush hop: a clean report calls it off; a switch to Warn too; still there, it names the mod.
        Assert.Empty(t.KickWorthyNow(1, policy, Report(HotMod(Ok)), Facts));
        Assert.Empty(t.KickWorthyNow(1, Approved(MidSessionPolicy.Warn, Ok), Report(HotMod(Ok), PlainMod(Plain)), Facts));
        Assert.Equal(Plain, Assert.Single(t.KickWorthyNow(1, policy, Report(HotMod(Ok), PlainMod(Plain)), Facts)).Key);
        Assert.Empty(t.KickWorthyNow(9, policy, Report(PlainMod(Plain)), Facts)); // no record: never
    }

    [Fact]
    public void Each_item_keeps_its_own_grace()
    {
        var before = Approved(MidSessionPolicy.Warn, Ok, Plain);
        var after = Approved(MidSessionPolicy.Disconnect, Ok);
        var t = new ClientPolicyRecheckTracker();
        var joined = Report(HotMod(Ok), PlainMod(Plain));
        t.OnJoinAccepted(1, before, joined);
        t.OnPolicyChange(1, joined, after);
        var first = t.Judge(1, after, joined, Facts, "policy-change", 50);
        Assert.True(first.ArmKick);
        Assert.Equal(60, first.Seconds); // due at 110

        // A mod loaded at 60 has its own 15 s: the disconnect moves to 75 for it, and the first keeps its 60.
        var both = Report(HotMod(Ok), PlainMod(Plain), PlainMod("new.plain"));
        var second = t.Judge(1, after, both, Facts, "report", 60);
        Assert.True(second.ArmKick);
        Assert.Equal(15, second.Seconds);
        Assert.Equal(15, Assert.Single(second.NoticeItems, i => i.Key == "new.plain").Seconds);
        Assert.Equal(50, Assert.Single(second.NoticeItems, i => i.Key == Plain).Seconds);
        var content = ClientPolicyNoticeText.Compose(second.Notice, second.NoticeItems, second.Seconds, "Night Garden");
        Assert.Contains("Fix these in the time shown for each", content.Body);
        Assert.Contains(content.Lines, l => l.StartsWith("new.plain") && l.EndsWith("(15 seconds)"));
        Assert.Contains(content.Lines, l => l.StartsWith("RetainedPassword") && l.EndsWith("(50 seconds)"));

        // Gone before its time: back to the first mod's deadline (re-armed, no new notice).
        var back = t.Judge(1, after, joined, Facts, "report", 70);
        Assert.True(back.ArmKick);
        Assert.Equal(40, back.Seconds);
        Assert.Equal(RecheckNoticeKind.None, back.Notice);
        Assert.False(t.IsDue(1, second.KickGeneration, 75));

        // Loaded again at 80 and kept: at 95 only it is overdue; the reason says it was loaded during the session.
        t.Judge(1, after, both, Facts, "report", 80);
        var kick = t.Judge(1, after, both, Facts, "deadline", 95);
        Assert.True(kick.KickNow);
        Assert.Equal("new.plain", Assert.Single(kick.NoticeItems).Key);
        Assert.Equal("Mods not allowed on this server, loaded during the session: new.plain.", kick.KickReason);
    }

    [Fact]
    public void The_kick_reason_names_each_cause()
    {
        var items = new List<RecheckItem>
        {
            new() { Key = "a", Name = "Alpha", Kind = ClientModProblem.KindNotAllowed },
            new() { Key = "b", Name = "Beta", Kind = ClientModProblem.KindNotAllowed, PreChange = true },
            new() { Key = Robes, Name = "Robe Pack", Kind = ClientModProblem.KindMissing },
            new() { Key = "c", Name = "Gamma", Kind = ClientModProblem.KindOlder, Rule = "1.2.0 or newer", Had = "1.1.0" },
        };
        Assert.Equal("Mods not allowed on this server, loaded during the session: Alpha. Mods this server no longer allows: Beta. "
            + "Required mods turned off during the session: Robe Pack. Required mods at a version this server doesn't accept: Gamma (needs 1.2.0 or newer, had 1.1.0).",
            ClientPolicyNoticeText.KickReason(items));
    }

    [Fact]
    public void A_notice_with_long_names_is_trimmed_to_one_packet_and_still_counts_the_rest()
    {
        string wide = new string('€', 200);
        var items = Enumerable.Range(0, 24).Select(i => new RecheckItem
        {
            Key = "mod." + i + new string('x', 140), Name = wide, Kind = ClientModProblem.KindOlder, Rule = new string('€', 120), Had = new string('9', 80),
        }).ToList();
        byte[] packet = ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Disconnect, 15, items, 3);
        Assert.True(packet.Length <= ClientModPolicyLimits.MaxReportPacketBytes);
        Assert.True(ClientPolicyNoticeWire.TryDecode(packet, out var n));
        Assert.InRange(n.Items.Count, 1, 23);
        Assert.Equal(24, n.Items.Count + n.More);
        var content = ClientPolicyNoticeText.Compose(n.Kind, n.Items, n.Seconds, null, null, n.More);
        Assert.Equal("and " + n.More + " more", content.Lines.Last());
    }

    // ---- the server's notice on the wire ----------------------------------------------------------------------------

    [Fact]
    public void The_notice_round_trips_and_never_reads_as_a_report_or_from_junk()
    {
        var items = new List<RecheckItem>
        {
            new() { Key = Hot, Name = "Extra Hot", Kind = ClientModProblem.KindNotAllowed, Pausable = true, PreChange = true },
            new() { Key = Robes, Name = "Robe Pack", Kind = ClientModProblem.KindOlder, Rule = "1.2.0 or newer", Had = "1.1.0", Added = true },
        };
        byte[] packet = ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Disconnect, 15, items, 7);
        Assert.True(ClientPolicyNoticeWire.TryDecode(packet, out var n));
        Assert.Equal(RecheckNoticeKind.Disconnect, n.Kind);
        Assert.Equal(15, n.Seconds);
        Assert.Equal(7, n.Sequence);
        Assert.Equal(2, n.Items.Count);
        Assert.True(n.Items[0].Pausable && n.Items[0].PreChange && n.Items[0].NotAllowed);
        Assert.Equal("1.2.0 or newer", n.Items[1].Rule);
        Assert.Equal("1.1.0", n.Items[1].Had);
        Assert.True(n.Items[1].Added);

        // Same id both ways, told apart by the kind byte.
        Assert.False(ClientModReportWire.TryHeader(packet, out _, out _, out _));
        var report = ClientModReportWire.Encode(new[] { HotMod(Ok) }, false, 1)[0];
        Assert.False(ClientPolicyNoticeWire.TryDecode(report, out _));
        Assert.False(ClientPolicyNoticeWire.TryDecode(new byte[] { 1, 1, 0, 0, 0, 0, (byte)'{' }, out _));
        Assert.False(ClientPolicyNoticeWire.TryDecode(new byte[] { 1, 1, 0, 0, 0, 0, (byte)'{', (byte)'}' }, out _)); // no action

        // More mods than one notice names are counted.
        var many = Enumerable.Range(0, 30).Select(i => new RecheckItem { Key = "m" + i, Name = "M" + i, Kind = ClientModProblem.KindNotAllowed }).ToList();
        Assert.True(ClientPolicyNoticeWire.TryDecode(ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Warn, 0, many, 1), out var cut));
        Assert.Equal(ClientPolicyRecheckLimits.MaxNoticeItems, cut.Items.Count);
        Assert.Equal(6, cut.More);
        Assert.Equal("and 6 more", ClientPolicyNoticeText.Compose(cut.Kind, cut.Items, 0, null, null, cut.More).Lines.Last());
    }

    // ---- the client: the notice -------------------------------------------------------------------------------------

    private sealed class NoticeRig
    {
        public readonly List<PauseNoticeContent> Shown = new();
        public readonly List<string> PauseCalls = new();
        public bool Auto = true;
        public readonly HashSet<string> Pausable = new(StringComparer.OrdinalIgnoreCase) { Hot };
        public double Now;

        public NoticeRig()
        {
            ClientPolicyNoticeClient.ResetProbes();
            ClientPolicyNoticeClient.Show = Shown.Add;
            ClientPolicyNoticeClient.PausableProbe = () => Pausable;
            ClientPolicyNoticeClient.PausedProbe = _ => false;
            ClientPolicyNoticeClient.AutoPauseProbe = () => Auto;
            ClientPolicyNoticeClient.ServerNameProbe = () => "Night Garden";
            ClientPolicyNoticeClient.Clock = () => Now;
            ClientPolicyNoticeClient.PauseProbe = ids =>
            {
                PauseCalls.AddRange(ids);
                return new JoinPauseResult { Ok = true, Taken = ids.ToList(), Names = ids.ToList() };
            };
        }
    }

    private static ClientPolicyNoticeWire.Notice Notice(RecheckNoticeKind kind, int seconds, params RecheckItem[] items)
    {
        Assert.True(ClientPolicyNoticeWire.TryDecode(ClientPolicyNoticeWire.Encode(kind, seconds, items, 1), out var n));
        return n;
    }

    private static RecheckItem NotAllowed(string key, string name, bool pausable) =>
        new() { Key = key, Name = name, Kind = ClientModProblem.KindNotAllowed, Pausable = pausable };

    [Fact]
    public void The_client_pauses_what_the_notice_names_and_shows_what_happened()
    {
        var rig = new NoticeRig();
        var content = ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Disconnect, 15, NotAllowed(Hot, "Extra Hot", true)));
        Assert.Equal(new[] { Hot }, rig.PauseCalls);
        // Every named mod is paused: no disconnect wording, the paused-mods title.
        Assert.Equal(ClientPolicyNoticeText.TitlePaused, content.Title);
        Assert.Equal("Night Garden doesn't allow this mod, so it's paused while you're on it and comes back when you leave:", content.Body);
        Assert.Equal("Extra Hot · Not allowed; paused for this server, back when you leave", Assert.Single(content.Lines));
        Assert.Same(content, Assert.Single(rig.Shown));

        // The server's "called off" that follows the clean report does not replace the paused popup.
        ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Resolved, 0));
        Assert.Single(rig.Shown);

        // With a mod that can't be paused, the countdown stays and both are named.
        var mixed = ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Disconnect, 15, NotAllowed(Hot, "Extra Hot", true), NotAllowed(Plain, "RetainedPassword", false)));
        Assert.Equal(ClientPolicyNoticeText.TitleNotAllowed, mixed.Title);
        Assert.Contains("within 15 seconds", mixed.Body);
        Assert.Equal(2, mixed.Lines.Count);
        Assert.EndsWith("restart V Rising before you rejoin", mixed.Lines[1]);
        Assert.Equal(3, ClientPolicyNoticeClient.Received);
        Assert.Equal("disconnect", ClientPolicyNoticeClient.Describe()["lastAction"]);

        // Otherwise "called off" is shown.
        var resolved = ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Resolved, 0));
        Assert.Equal(ClientPolicyNoticeText.TitleResolved, resolved.Title);
        Assert.Same(resolved, rig.Shown.Last());
    }

    [Fact]
    public void The_client_does_not_pause_when_automatic_pausing_is_off_for_the_server()
    {
        var rig = new NoticeRig { Auto = false };
        var content = ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Warn, 0, NotAllowed(Hot, "Extra Hot", true)));
        Assert.Empty(rig.PauseCalls);
        Assert.Equal("Extra Hot · Not allowed and couldn't be paused; turn it off in the Mods list", Assert.Single(content.Lines));
        Assert.Equal(ClientPolicyNoticeText.NextJoinNote, content.Note);
        Assert.Equal("refused", ((Dictionary<string, object?>)ClientPolicyNoticeClient.Describe()["pauses"]!)[Hot]);
    }

    /// <summary>
    /// Live 2026-10-05: a required mod this client paused for the server read "install it before your next join". The
    /// line now says it is installed and paused, and comes back as the server allows it (no "next join refused" note).
    /// </summary>
    [Fact]
    public void A_required_mod_this_client_paused_is_named_as_paused_not_missing()
    {
        var rig = new NoticeRig();
        ClientPolicyNoticeClient.PausedProbe = id => id == Robes;
        var content = ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Warn, 0,
            new RecheckItem { Key = Robes, Name = "Robe Pack", Kind = ClientModProblem.KindMissing, Added = true }));
        Assert.Equal("Robe Pack · Required here; it's installed but paused for this server, and it loads again as soon as the server allows it",
            Assert.Single(content.Lines));
        Assert.Equal("", content.Note);
        Assert.Empty(rig.PauseCalls);
    }

    [Fact]
    public void A_policy_update_mid_session_resumes_what_it_now_allows_with_or_without_a_policy()
    {
        ClientRig();
        try
        {
            int calls = 0;
            ClientModReportClient.ResumeOnPolicyUpdate = () => { calls++; return 1; };
            ClientModReportClient.NoteServerRequest(true, rechecks: true);
            ClientModReportClient.OnSignedPolicyMidSession(true);
            ClientModReportClient.OnSignedPolicyMidSession(false);
            Assert.Equal(2, calls);
            Assert.Equal(1, ClientModReportClient.Describe(false)["lastPolicyResumed"]);
            ClientModReportClient.TestJoinOk = () => false;
            ClientModReportClient.OnSignedPolicyMidSession(true);
            Assert.Equal(2, calls);
        }
        finally { ClientModReportClient.ResetForTests(); }
    }

    [Fact]
    public void A_disconnect_notice_is_shown_again_once_after_the_leave()
    {
        var rig = new NoticeRig { Now = 10 };
        var content = ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Disconnected, 0, NotAllowed(Plain, "RetainedPassword", false)));
        Assert.Empty(rig.PauseCalls); // a disconnect pauses nothing
        Assert.Equal("Disconnected for your mods", content.Title);
        Assert.Equal("You were disconnected from Night Garden because of these mods:", content.Body);
        Assert.Equal(true, ClientPolicyNoticeClient.Describe()["reshowPending"]);
        rig.Now = 20;
        Assert.True(ClientPolicyNoticeClient.ReshowAfterLeave());
        Assert.False(ClientPolicyNoticeClient.ReshowAfterLeave());
        Assert.Equal(2, rig.Shown.Count);

        // Too old (the leave was for another reason long after): not shown.
        ClientPolicyNoticeClient.Handle(Notice(RecheckNoticeKind.Disconnected, 0, NotAllowed(Plain, "RetainedPassword", false)));
        rig.Now = 10 + ClientPolicyRecheckLimits.ReshowAfterLeaveSeconds + 30;
        Assert.False(ClientPolicyNoticeClient.ReshowAfterLeave());
    }

    // ---- the client: the mid-session report --------------------------------------------------------------------------

    private static double _clock;

    private static void ClientRig()
    {
        ClientModReportClient.ResetForTests();
        _clock = 0;
        ClientModReportClient.TestClock = () => _clock;
        ClientModReportClient.TestJoinOk = () => true;
    }

    private static List<string> Guids(byte[] packet)
    {
        var asm = new ClientModReportAssembler();
        Assert.True(asm.Accept(0, packet, out var report, out var error), error);
        return report!.Entries.Select(e => e.Guid).ToList();
    }

    [Fact]
    public void A_newly_loaded_mod_is_paused_before_the_debounced_report()
    {
        ClientRig();
        try
        {
            ClientModReportClient.TestObserved.Add(new ClientModObservation(Ok, "OK", "1.0", ClientModSources.Progenitor));
            ClientModReportClient.NoteServerRequest(true, rechecks: true);
            ClientModReportClient.AfterAnswer("join");
            Assert.Single(ClientModReportClient.TestPackets);

            // A hot load of a mod the remembered policy does not allow: the automatic pause takes it before the report.
            int pauses = 0;
            ClientModReportClient.PauseBeforeReport = () =>
            {
                pauses++;
                ClientModReportClient.TestObserved.RemoveAll(o => o.Guid == Hot);
                return 1;
            };
            ClientModReportClient.TestObserved.Add(new ClientModObservation(Hot, "Extra Hot", "1.0", ClientModSources.Progenitor));
            _clock = 1;
            ClientModReportClient.NoteInventoryChanged();
            _clock = 2;
            ClientModReportClient.NoteInventoryChanged(); // inside the settle: still one re-check
            ClientModReportClient.Tick();
            Assert.Equal(0, pauses);
            _clock = 2.6;
            ClientModReportClient.Tick();
            Assert.Equal(1, pauses);
            // Paused: what is loaded equals what the server has, so nothing more is sent.
            Assert.Single(ClientModReportClient.TestPackets);
            Assert.Equal(1, ClientModReportClient.Describe(false)["midSessionPauses"]);

            // A plain plugin turned on can't be paused: the report goes out, debounced, and the server judges it.
            ClientModReportClient.PauseBeforeReport = () => { pauses++; return 0; };
            ClientModReportClient.TestObserved.Add(new ClientModObservation(Plain, "RetainedPassword", "1.0", ClientModSources.Chainloader));
            _clock = 5;
            ClientModReportClient.NoteInventoryChanged();
            _clock = 6.6;
            ClientModReportClient.Tick();
            Assert.Equal(2, pauses);
            Assert.Equal(2, ClientModReportClient.TestPackets.Count);
            Assert.Equal(new[] { Ok, Plain }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), Guids(ClientModReportClient.TestPackets[1]).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            // Nothing changed: the periodic re-check neither pauses nor sends.
            _clock = 30;
            ClientModReportClient.Tick();
            Assert.Equal(2, pauses);
            Assert.Equal(2, ClientModReportClient.TestPackets.Count);
        }
        finally { ClientModReportClient.ResetForTests(); }
    }

    [Fact]
    public void A_policy_re_signed_mid_session_asks_for_a_report_even_from_a_join_that_was_never_asked()
    {
        ClientRig();
        try
        {
            ClientModReportClient.TestObserved.Add(new ClientModObservation(Ok, "OK", "1.0", ClientModSources.Progenitor));
            // A server that does not re-check mid-session is never sent a mid-session report.
            ClientModReportClient.NoteServerRequest(false, rechecks: false);
            ClientModReportClient.OnSignedPolicyMidSession(true);
            Assert.False(ClientModReportClient.ServerAsks);

            ClientModReportClient.NoteServerRequest(false, rechecks: true);
            ClientModReportClient.OnSignedPolicyMidSession(false);
            Assert.False(ClientModReportClient.ServerAsks);

            ClientModReportClient.OnSignedPolicyMidSession(true);
            Assert.True(ClientModReportClient.ServerAsks);
            Assert.Equal(true, ClientModReportClient.Describe(false)["askedBySignedCatalog"]);
            _clock = 2;
            ClientModReportClient.Tick();
            Assert.Equal(new[] { Ok }, Guids(Assert.Single(ClientModReportClient.TestPackets)));

            // Another re-signed policy re-sends an unchanged list (the server re-judges it against the new policy).
            ClientModReportClient.OnSignedPolicyMidSession(true);
            _clock = 4;
            ClientModReportClient.Tick();
            Assert.Equal(2, ClientModReportClient.TestPackets.Count);

            // The admins turn the policy off: reporting stops, even when the mods change.
            ClientModReportClient.OnSignedPolicyMidSession(false);
            Assert.False(ClientModReportClient.ServerAsks);
            ClientModReportClient.TestObserved.Add(new ClientModObservation(Hot, "Extra Hot", "1.0", ClientModSources.Progenitor));
            ClientModReportClient.NoteInventoryChanged();
            _clock = 20;
            ClientModReportClient.Tick();
            Assert.Equal(2, ClientModReportClient.TestPackets.Count);

            // Not while the join is not accepted.
            ClientModReportClient.ResetForTests();
            ClientModReportClient.TestJoinOk = () => false;
            ClientModReportClient.OnSignedPolicyMidSession(true);
            Assert.False(ClientModReportClient.ServerAsks);
        }
        finally { ClientModReportClient.ResetForTests(); }
    }

    // ---- the capability, the live seams and the bridge ops ----------------------------------------------------------

    [Fact]
    public void This_build_declares_the_re_check_capability()
    {
        Assert.Equal(512u, (uint)Handshake.Capability.PolicyRecheck);
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.PolicyRecheck) != 0);
        var row = Handshake.Compare.CreateCapabilityRecord(Handshake.SelfCapabilities);
        Assert.True((Handshake.Compare.ReadCapabilityBlob(row.Version) & Handshake.Capability.PolicyRecheck) != 0);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CastlesTests.RepoRootForDumpSources(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void The_live_path_re_checks_mid_session_reports_and_policy_changes()
    {
        string policy = Source("src/Progenitor.Core/Networking/Handshake.ClientPolicy.cs");
        Assert.Contains("if (midSession) RunRecheck(userIndex, name,", policy);
        Assert.Contains("Recheck.OnJoinAccepted(userIndex, SafePolicy(), report);", policy);
        Assert.Contains("ClientPolicyNoticeWire.Encode(d.Notice", policy);
        // The disconnect re-reads the latest report first (a clean one calls it off).
        Assert.Contains("var still = Recheck.KickWorthyNow(userIndex, policy, latest, JoinAdminServer.PolicyFacts());", policy);
        Assert.Contains("Players.PlayerControl.Disconnect(world, entity, reason)", policy);
        Assert.Contains("Recheck.OnPolicyChange(index, record.Report, policy);", policy);
        Assert.Contains("Capability.ClientPolicy | Capability.PolicyRecheck", policy);
        // UNINSTREQ1: the request row also carries the admins' mid-session rule, which the client reads.
        string handshake = Source("src/Progenitor.Core/Networking/Handshake.cs");
        Assert.Contains("ClientModReportClient.NoteServerRequest((serverCaps & Capability.ClientPolicy) != 0, (serverCaps & Capability.PolicyRecheck) != 0,", handshake);
        // F41 chat: and whether the server says a held countdown in chat (CountdownChat).
        Assert.Contains("ClientModReportClient.RuleOf(serverCaps), (serverCaps & Capability.CountdownChat) != 0);", handshake);
        Assert.Contains("Capability.ClientPolicy | Capability.PolicyRecheck | Capability.CountdownChat\n                | ClientModReportClient.MidSessionBit(policy.MidSession)",
            policy.Replace("\r\n", "\n"));
        Assert.Contains("lock (PolicyAsked) PolicyAsked.Add(index);", policy);
        Assert.Contains("PolicyChanged = Handshake.OnPolicyChangedMidSession;", Source("src/Progenitor.Core/Networking/JoinAdminServer.cs"));
        Assert.Contains("NoteClientCaps(user.Index, clientCaps);", Source("src/Progenitor.Core/Networking/Handshake.cs"));
        Assert.Contains("ClientModReportClient.OnSignedPolicyMidSession(", Source("src/Progenitor.Core/Networking/JoinFlowClient.cs"));
        string client = Source("src/Progenitor.Core/Networking/ClientModReportClient.cs");
        Assert.Contains("PauseBeforeReport = ServerModPause.PausePolicyModsMidSession;", client);
        Assert.Contains("ResumeOnPolicyUpdate = ServerModPause.ResumePolicyPausedNowAllowed;", client);
        Assert.Contains("CustomEvents.OnReceive(ClientModReportWire.EventId, OnServerNotice);", client);
        Assert.Contains("ClientPolicyNoticeClient.Show = ServerModPauseNotice.Open;", client);
        // Live 2026-10-05: the disconnect notice waits for the main menu (no client world), and stays there.
        Assert.Contains("Handshake.ClientReturnedToMenu += OnReturnedToMenu;", client);
        Assert.DoesNotContain("Handshake.ClientConnectionEnded += ", client);
        Assert.Contains("worldUp = world != null && world.IsCreated;", client);
        Assert.Contains("ServerModPauseNotice.Open(content, closeAfterSeconds: 0)", client);
        Assert.Contains("var handlers = ClientReturnedToMenu;", Source("src/Progenitor.Core/Networking/HandshakeRefresh.cs"));
        // ... and the in-world screens close when the connection ends, so the game's own kick banner shows.
        Assert.Contains("Handshake.ClientConnectionEnded += OnConnectionEnded;", Source("src/Progenitor.Core/Networking/JoinFlowLive.cs"));
        Assert.Contains("try { JoinFlowClient.OnConnectionLost(); }", Source("src/Progenitor.Core/Networking/JoinFlowLive.cs"));
        // JOINDRAFT1: Approve stages the existing approve-add (Apply sends it).
        Assert.Contains("Staged(Busy() ?? Draft.Approve(line.Mod, \"\", \"\", \"\", name), \"approve\")", Source("src/Progenitor.Core/Networking/JoinAdminPanel.cs"));
        Assert.Contains("JoinAdminChangeKind.ApproveAdd => new JoinAdminRequest { Op = JoinAdminOps.ApproveAdd", Source("src/Progenitor.Core/Networking/JoinAdminDraft.cs"));
    }

    [Fact]
    public void The_bridge_has_the_stage_3_actions()
    {
        string dev = Source("src/Progenitor.Dev/Ops/JoinAdminOps.cs");
        Assert.Contains("\"mid_session\" => Call(\"MidSession\", Required(args, \"value\"))", dev);
        Assert.Contains("\"alert_approve\" => Call(\"AlertApprove\", Str(args, \"mod\") ?? \"\", Int(args, \"index\", -1))", dev);
        Assert.Contains("\"recheck\" => Call(\"Recheck\")", dev);
        string bridge = Source("src/Progenitor.Core/Networking/JoinAdminTestBridge.cs");
        Assert.Contains("public static Dictionary<string, object?> MidSession(string value) => After(JoinAdminPanel.SetMidSession(value));", bridge);
        Assert.Contains("public static Dictionary<string, object?> AlertApprove(string mod, int index)", bridge);
        Assert.Contains("return After(JoinAdminPanel.ApproveFromAlert(key));", bridge);
        Assert.Contains("public static Dictionary<string, object?> Recheck()", bridge);
        Assert.Contains("u[\"recheck\"] = Handshake.DescribeRecheck(index);", bridge);
    }

    [Fact]
    public void The_panel_words_name_the_grace_periods()
    {
        Assert.Equal("Pause the mod and warn them", ClientPolicyText.MidSessionWarn);
        Assert.Equal("Disconnect them", ClientPolicyText.MidSessionDisconnect);
        Assert.Contains("15 seconds", ClientPolicyText.MidSessionDisconnectHelp);
        Assert.Contains("60 seconds", ClientPolicyText.MidSessionDisconnectHelp);
        Assert.Contains("Nobody is disconnected", ClientPolicyText.MidSessionWarnHelp);
        Assert.Contains("15 seconds", ClientPolicyText.MidSessionDisconnectNote);
        Assert.Equal("Approve", ClientPolicyText.AlertApprove);
    }
}
