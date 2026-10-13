using System;
using System.IO;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// REQRULE2 / REQRULE5 (docs/client-mod-policy.md): the configurable Disconnect them grace (<c>[ClientPolicy]
/// DisconnectGraceSeconds</c>, 60 by default) and the server's rule message, which tells connected clients the rule and
/// the grace after the join and whenever the admins change them. Game-free.
/// </summary>
public sealed class RequiredRuleTests : IDisposable
{
    public RequiredRuleTests()
    {
        ClientModReportClient.ResetForTests();
        ClientPolicyRecheckLimits.Configured = null;
        JoinAdminRows.StateProbe = null;
        RequiredHereFact.Probe = null;
        RequiredHereFact.RechecksProbe = null;
        RequiredHereFact.RuleProbe = null;
        RequiredHereFact.GraceProbe = null;
    }

    public void Dispose()
    {
        ClientModReportClient.ResetForTests();
        ClientPolicyRecheckLimits.Configured = null;
        JoinAdminRows.StateProbe = null;
        RequiredHereFact.Probe = null;
        RequiredHereFact.RechecksProbe = null;
        RequiredHereFact.RuleProbe = null;
        RequiredHereFact.GraceProbe = null;
    }

    private static ClientPolicyConfig Approved(MidSessionPolicy mid, params string[] ids)
    {
        var c = new ClientPolicyConfig { Mode = ClientPolicyMode.Approved, MidSession = mid };
        foreach (string id in ids) c.Approved.Add(new ClientModRow(id, "", "", ClientModRule.Any));
        return c;
    }

    private static ClientModReport Report(params ClientModEntry[] entries)
    {
        var r = new ClientModReport();
        r.Entries.AddRange(entries);
        return r;
    }

    private static ClientModEntry Plain(string id) => new(id, id, "1.0.0", ClientModSources.Chainloader);

    // ---- REQRULE5: the setting ----------------------------------------------------------------------------------

    [Fact]
    public void The_grace_defaults_to_60_and_is_clamped_to_10_through_300()
    {
        Assert.Equal(60, ClientPolicyRecheckLimits.DisconnectGraceSeconds);
        ClientPolicyRecheckLimits.Configured = () => 3;
        Assert.Equal(10, ClientPolicyRecheckLimits.DisconnectGraceSeconds);
        ClientPolicyRecheckLimits.Configured = () => 9999;
        Assert.Equal(300, ClientPolicyRecheckLimits.DisconnectGraceSeconds);
        ClientPolicyRecheckLimits.Configured = () => throw new InvalidOperationException();
        Assert.Equal(60, ClientPolicyRecheckLimits.DisconnectGraceSeconds);

        var spec = ServerOptions.Find(ServerOptionOps.DisconnectGrace)!;
        Assert.Equal(("ClientPolicy", "DisconnectGraceSeconds", ServerOptionKind.Seconds, 10, 300), (spec.Section, spec.Key, spec.Kind, spec.Min, spec.Max));
    }

    [Fact]
    public void A_mod_loaded_before_a_rule_change_gets_at_least_60_seconds()
    {
        Assert.Equal(60, ClientPolicyRecheckLimits.PolicyChangeGraceFor(15));
        Assert.Equal(60, ClientPolicyRecheckLimits.PolicyChangeGraceFor(60));
        Assert.Equal(120, ClientPolicyRecheckLimits.PolicyChangeGraceFor(120));
    }

    [Fact]
    public void The_tracker_arms_the_configured_grace()
    {
        foreach (int configured in new[] { 20, 60, 120 })
        {
            ClientPolicyRecheckLimits.Configured = () => configured;
            var t = new ClientPolicyRecheckTracker();
            var policy = Approved(MidSessionPolicy.Disconnect, "ok.mod");
            t.OnJoinAccepted(1, policy, Report(Plain("ok.mod")));
            var d = t.Judge(1, policy, Report(Plain("ok.mod"), Plain("new.mod")), null, "report", 100);
            Assert.True(d.ArmKick);
            Assert.Equal(configured, d.Seconds);
            Assert.False(t.IsDue(1, d.KickGeneration, 100 + configured - 1));
            Assert.True(t.IsDue(1, d.KickGeneration, 100 + configured));
        }
    }

    [Fact]
    public void The_texts_print_the_configured_number()
    {
        ClientPolicyRecheckLimits.Configured = () => 45;
        Assert.Contains("after 45 seconds", ClientPolicyText.MidSessionDisconnectShort, StringComparison.Ordinal);
        Assert.Contains("after 45 seconds", ClientPolicyText.MidSessionDisconnectHelp, StringComparison.Ordinal);
        Assert.Contains("get 60 seconds", ClientPolicyText.MidSessionDisconnectHelp, StringComparison.Ordinal);
        Assert.Equal("Players who load a mod you don't allow, or remove or turn off a required one, get a notice naming it and 45 seconds to fix it "
            + "(60 for mods you don't allow loaded before you change the rules).", ClientPolicyText.MidSessionDisconnectNote);

        // At or above 60 the pre-change sentence would repeat the number: it is left out.
        ClientPolicyRecheckLimits.Configured = () => 90;
        Assert.Equal("Players who load a mod you don't allow, or remove or turn off a required one, get a notice naming it and 90 seconds to fix it.",
            ClientPolicyText.MidSessionDisconnectNote);
        Assert.DoesNotContain("already loaded", ClientPolicyText.MidSessionDisconnectHelp, StringComparison.Ordinal);
    }

    [Fact]
    public void An_admin_s_texts_use_the_joined_server_s_value_first()
    {
        ClientPolicyRecheckLimits.Configured = () => 30;
        var state = new JoinAdminState { OptionsSupported = true, MidSession = "disconnect" };
        state.Options[ServerOptionOps.DisconnectGrace] = "120";
        JoinAdminRows.StateProbe = () => state;
        Assert.Equal(120, ClientPolicyText.GraceSeconds);
        Assert.Contains("after 120 seconds", ClientPolicyText.MidSessionDisconnectShort, StringComparison.Ordinal);

        // No option (an older server): what the rule message said, else the old fixed 15 from a server that re-checks.
        JoinAdminRows.StateProbe = () => new JoinAdminState { OptionsSupported = true, MidSession = "disconnect" };
        ClientModReportClient.NoteServerRequest(asks: true, rechecks: true, ServerMidSessionRule.Disconnect);
        Assert.Equal(ClientPolicyRecheckLimits.LegacyDisconnectGraceSeconds, ClientPolicyText.GraceSeconds);
        ClientModReportClient.NoteRuleUpdate(ServerMidSessionRule.Disconnect, 75);
        Assert.Equal(75, ClientPolicyText.GraceSeconds);
    }

    [Fact]
    public void The_Server_tab_row_sits_right_below_the_rule_and_says_when_it_is_not_in_effect()
    {
        string page = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Ui", "ProgenitorPage.cs"));
        int rule = page.IndexOf("Admin(page, Networking.JoinAdminRows.MidSession());", StringComparison.Ordinal);
        int grace = page.IndexOf("Admin(page, Networking.JoinAdminRows.DisconnectGrace());", StringComparison.Ordinal);
        int unverified = page.IndexOf("Admin(page, Networking.JoinAdminRows.Unverified());", StringComparison.Ordinal);
        Assert.True(rule > 0 && grace > rule && unverified > grace);

        var row = JoinAdminRows.DisconnectGrace();
        Assert.Equal("Seconds to fix a mod", row.Label);
        Assert.NotEqual(JoinAdminRows.AnswerTimeoutLabel, row.Label);
        Assert.Equal((10, 300), (row.Min, row.Max));
        Assert.True(row.Label.Length <= 30);
        Assert.Equal(SettingPlaces.ModsPlayersNeed, SettingPlaces.Find(row.SettingId)!.Home);

        JoinAdminRows.StateProbe = () => new JoinAdminState { MidSession = "warn" };
        Assert.Equal(JoinAdminRows.DisconnectGraceInactive, row.Note!());
        JoinAdminRows.StateProbe = () => new JoinAdminState { MidSession = "disconnect" };
        Assert.Null(row.Note!());
    }

    // ---- REQRULE2: the rule message -------------------------------------------------------------------------------

    [Fact]
    public void The_rule_message_round_trips_and_fails_closed()
    {
        byte[] packet = ClientPolicyRuleWire.Encode(MidSessionPolicy.Disconnect, 90);
        Assert.Equal(new byte[] { ClientModReportWire.Version, ClientPolicyRuleWire.KindRule, 2, 90, 0 }, packet);
        Assert.True(ClientPolicyRuleWire.TryDecode(packet, out var rule, out int grace));
        Assert.Equal((ServerMidSessionRule.Disconnect, 90), (rule, grace));

        Assert.True(ClientPolicyRuleWire.TryDecode(ClientPolicyRuleWire.Encode(MidSessionPolicy.Warn, 300), out rule, out grace));
        Assert.Equal((ServerMidSessionRule.Warn, 300), (rule, grace));

        // A later version may append bytes; an unknown rule byte or a short packet is dropped.
        Assert.True(ClientPolicyRuleWire.TryDecode(new byte[] { 1, 2, 1, 60, 0, 9, 9 }, out _, out _));
        Assert.False(ClientPolicyRuleWire.TryDecode(new byte[] { 1, 2, 3, 60, 0 }, out _, out _));
        Assert.False(ClientPolicyRuleWire.TryDecode(new byte[] { 1, 2, 1, 60 }, out _, out _));
        // A notice is never read as a rule (and a rule never as a notice).
        Assert.False(ClientPolicyRuleWire.IsRule(ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Warn, 0, null, 1)));
        Assert.False(ClientPolicyNoticeWire.TryDecode(packet, out _));
    }

    [Fact]
    public void A_mid_session_rule_change_reaches_the_Required_by_line()
    {
        // Live 2026-10-08: the player joined under Warn, the admin switched to Disconnect, and the uninstall box still
        // said "you'll be warned in game and can keep playing".
        RequiredHereFact.Probe = _ => ("Night Garden", true, false);
        ClientModReportClient.NoteServerRequest(asks: true, rechecks: true, ServerMidSessionRule.Warn);
        Assert.Contains("you'll be warned in game", RequiredHereFact.ValueFor("robes"), StringComparison.Ordinal);

        Assert.True(ClientModReportClient.HandleServerPacket(ClientPolicyRuleWire.Encode(MidSessionPolicy.Disconnect, 60)));
        Assert.Equal(ServerMidSessionRule.Disconnect, ClientModReportClient.MidSessionRule);
        Assert.Equal(60, ClientModReportClient.ServerGraceSeconds);
        Assert.Equal("Night Garden: it disconnects players who remove a mod it requires unless it's back within 60 seconds; your next join is refused until it's back.",
            RequiredHereFact.ValueFor("robes"));

        Assert.True(ClientModReportClient.HandleServerPacket(ClientPolicyRuleWire.Encode(MidSessionPolicy.Warn, 60)));
        Assert.Contains("you'll be warned in game", RequiredHereFact.ValueFor("robes"), StringComparison.Ordinal);

        // A server from before the rule message: the old fixed 15.
        ClientModReportClient.ResetForTests();
        ClientModReportClient.NoteServerRequest(asks: true, rechecks: true, ServerMidSessionRule.Disconnect);
        Assert.Contains("within 15 seconds", RequiredHereFact.ValueFor("robes"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_required_mod_countdown_is_worded_by_what_this_client_has()
    {
        // F40 (live 2026-10-08): after an uninstall the body said "Turn it back on".
        RecheckItem Missing(RecheckLocalState local) => new() { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindMissing, Local = local, Seconds = 60 };
        string Body(params RecheckItem[] items) => ClientPolicyNoticeText.Compose(RecheckNoticeKind.Disconnect, items, 60, "Night Garden").Body;
        Assert.Equal("Night Garden disconnects players who remove a mod it requires. Reinstall it within 60 seconds, or you'll be disconnected:",
            Body(Missing(RecheckLocalState.NotInstalled)));
        Assert.Equal("Night Garden disconnects players who turn off a mod it requires. Turn it back on within 60 seconds, or you'll be disconnected:",
            Body(Missing(RecheckLocalState.TurnedOff)));
        Assert.Equal("Night Garden disconnects players who turn off a mod it requires. Turn it back on within 60 seconds, or you'll be disconnected:",
            Body(Missing(RecheckLocalState.Unknown)));
        var other = Missing(RecheckLocalState.TurnedOff);
        other.Key = "hats";
        Assert.StartsWith("Night Garden disconnects players who remove or turn off a mod it requires. Put them back within 60 seconds",
            Body(Missing(RecheckLocalState.NotInstalled), other));
    }

    [Fact]
    public void A_server_notice_waits_while_a_confirmation_is_open()
    {
        // F41 (live 2026-10-08): the countdown notice covered the open "install on both" box.
        try
        {
            bool open = true;
            var shown = new System.Collections.Generic.List<string>();
            ClientPolicyNoticeClient.ConfirmationOpenProbe = () => open;
            ClientPolicyNoticeClient.Show = c => shown.Add(c.Title);
            var notice = new ClientPolicyNoticeWire.Notice { Kind = RecheckNoticeKind.Disconnect, Seconds = 60 };
            notice.Items.Add(new RecheckItem { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindMissing, Seconds = 60 });
            ClientPolicyNoticeClient.Handle(notice);
            Assert.Empty(shown);
            Assert.NotNull(ClientPolicyNoticeClient.Held);
            Assert.False(ClientPolicyNoticeClient.ShowHeldWhenFree());

            // A later notice replaces the one waiting; it is shown once, when the box closes.
            var later = new ClientPolicyNoticeWire.Notice { Kind = RecheckNoticeKind.Resolved };
            ClientPolicyNoticeClient.Handle(later);
            open = false;
            Assert.True(ClientPolicyNoticeClient.ShowHeldWhenFree());
            Assert.Equal(new[] { ClientPolicyNoticeText.TitleResolved }, shown.ToArray());
            Assert.False(ClientPolicyNoticeClient.ShowHeldWhenFree());

            // With no box open it shows at once.
            ClientPolicyNoticeClient.Handle(notice);
            Assert.Equal(2, shown.Count);
            Assert.Null(ClientPolicyNoticeClient.Held);
        }
        finally { ClientPolicyNoticeClient.ResetProbes(); }
    }

    [Fact]
    public void A_new_handshake_request_forgets_the_previous_server_s_grace()
    {
        // Review L2: the request resets the rule and the grace together; the rule message after the ok answer sets both.
        ClientModReportClient.NoteServerRequest(asks: true, rechecks: true, ServerMidSessionRule.Warn);
        ClientModReportClient.NoteRuleUpdate(ServerMidSessionRule.Disconnect, 90);
        Assert.Equal(90, ClientModReportClient.ServerGraceSeconds);
        ClientModReportClient.NoteServerRequest(asks: true, rechecks: true, ServerMidSessionRule.Warn);
        Assert.Equal(0, ClientModReportClient.ServerGraceSeconds);
        Assert.Equal(ServerMidSessionRule.Warn, ClientModReportClient.MidSessionRule);
    }

    [Fact]
    public void The_client_declares_it_and_the_server_sends_it_after_the_join_and_on_every_change()
    {
        Assert.Equal(8192u, (uint)Handshake.Capability.PolicyRuleUpdates);
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.PolicyRuleUpdates) != 0);

        string dir = Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Networking");
        string policy = File.ReadAllText(Path.Combine(dir, "Handshake.ClientPolicy.cs"));
        // Only to a client that declared it.
        Assert.Contains("(clientCaps & Capability.PolicyRuleUpdates) == 0", policy);
        // Review L5: the broadcast queries the users once and sends by index, never one query per user.
        int broadcast = policy.IndexOf("internal static void BroadcastPolicyRule(string why)", StringComparison.Ordinal);
        int end = policy.IndexOf("private static BepInEx.Configuration.ConfigEntry<int>? _disconnectGraceSeconds;", broadcast, StringComparison.Ordinal);
        string body = policy.Substring(broadcast, end - broadcast);
        Assert.DoesNotContain("SendPolicyRuleTo(", body);
        Assert.DoesNotContain("ConnectedOkUsers(", body);
        // Review L6: a cfg reload of the grace broadcasts too, through ConfigHelper (removed with this Core generation).
        Assert.Contains("Progenitor.Config.ConfigHelper.AddSettingChanged(file, (_, e) =>", policy);
        Assert.Contains("BroadcastPolicyRule(\"a change of [ClientPolicy] DisconnectGraceSeconds\")", policy);
        int change = policy.IndexOf("internal static void OnPolicyChangedMidSession(string op)", StringComparison.Ordinal);
        Assert.True(policy.IndexOf("BroadcastPolicyRule(op);", change, StringComparison.Ordinal) > change);

        string handshake = File.ReadAllText(Path.Combine(dir, "Handshake.cs"));
        int hints = handshake.IndexOf("ChatCommandHintsServer.OnHandshakeOk(world, userEntity, clientCaps);", StringComparison.Ordinal);
        Assert.True(handshake.IndexOf("SendPolicyRuleTo(world, userEntity, clientCaps);", hints, StringComparison.Ordinal) > hints);

        string server = File.ReadAllText(Path.Combine(dir, "JoinAdminServer.cs"));
        Assert.Contains("GraceChanged = () => Handshake.BroadcastPolicyRule(\"disconnect-grace\");", server);
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Progenitor.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }
}
