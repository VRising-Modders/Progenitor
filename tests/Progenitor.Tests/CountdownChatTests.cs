using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// F41 chat (decided 2026-10-08, docs/client-mod-policy.md): while a disconnect countdown notice is held behind an
/// open confirmation on the client, the server says the countdown as a private System chat line: at the start, then at 30
/// and 10 seconds left, never while the notice is on screen, and not after the countdown ends. Game-free: the wire, the
/// line's words, the server's schedule (re-arms, rate limits), the tracker's armed state and the client's held reports.
/// </summary>
public sealed class CountdownChatTests : IDisposable
{
    public CountdownChatTests()
    {
        ClientModReportClient.ResetForTests();
        ClientPolicyRecheckLimits.Configured = null;
    }

    public void Dispose()
    {
        ClientModReportClient.ResetForTests();
        ClientPolicyRecheckLimits.Configured = null;
    }

    private static RecheckItem Missing(string key, string name, RecheckLocalState local = RecheckLocalState.Unknown) =>
        new() { Key = key, Name = name, Kind = ClientModProblem.KindMissing, Local = local, Seconds = 60 };

    private static RecheckItem NotAllowed(string key, string name, bool pausable = true) =>
        new() { Key = key, Name = name, Kind = ClientModProblem.KindNotAllowed, Pausable = pausable, Seconds = 60 };

    private static Dictionary<string, HeldLocalFacts> Facts(string key, RecheckLocalState state, bool listed = false, bool localTest = false,
        bool paused = false) =>
        new(StringComparer.OrdinalIgnoreCase) { [key] = new HeldLocalFacts { State = state, Listed = listed, LocalTest = localTest, Paused = paused } };

    // ---- The wire ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_held_message_round_trips_and_is_never_read_as_a_report_or_notice()
    {
        var listed = Missing("robes", "Robe Pack", RecheckLocalState.NotInstalled);
        listed.InstallListed = true;
        var paused = Missing("paused.one", "Paused One");
        paused.PausedHere = true;
        var items = new List<RecheckItem> { listed, Missing("ui", "Event UI", RecheckLocalState.TurnedOff), Missing("unknown", "Unknown"), paused };
        byte[] packet = ClientPolicyHeldWire.Encode(true, items);
        Assert.Equal(new byte[] { ClientModReportWire.Version, ClientPolicyHeldWire.KindHeld, 1 }, packet[..3]);
        Assert.True(ClientPolicyHeldWire.IsHeld(packet));
        Assert.True(ClientPolicyHeldWire.TryDecode(packet, out bool held, out var local));
        Assert.True(held);
        Assert.Equal(3, local.Count); // the item with no facts is not sent
        Assert.Equal(RecheckLocalState.NotInstalled, local["ROBES"].State);
        Assert.True(local["robes"].Listed);
        Assert.False(local["robes"].LocalTest);
        Assert.Equal(RecheckLocalState.TurnedOff, local["ui"].State);
        Assert.True(local["paused.one"].Paused);
        Assert.Equal(RecheckLocalState.Unknown, local["paused.one"].State);

        // "No longer held" is the header alone.
        byte[] off = ClientPolicyHeldWire.Encode(false, items);
        Assert.Equal(new byte[] { 1, ClientPolicyHeldWire.KindHeld, 0 }, off);
        Assert.True(ClientPolicyHeldWire.TryDecode(off, out held, out local));
        Assert.False(held);
        Assert.Empty(local);

        // Not a report chunk, not a notice, not a rule; and they are not it.
        Assert.False(ClientModReportWire.TryHeader(packet, out _, out _, out _));
        Assert.False(ClientPolicyNoticeWire.TryDecode(packet, out _));
        Assert.False(ClientPolicyRuleWire.IsRule(packet));
        Assert.False(ClientPolicyHeldWire.IsHeld(ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Disconnect, 60, items, 1)));
        Assert.False(ClientPolicyHeldWire.IsHeld(ClientPolicyRuleWire.Encode(MidSessionPolicy.Disconnect, 60)));

        // Fails closed: an unknown held byte, a body that is not JSON.
        Assert.False(ClientPolicyHeldWire.TryDecode(new byte[] { 1, 3, 2 }, out _, out _));
        Assert.False(ClientPolicyHeldWire.TryDecode(new byte[] { 1, 3, 1, (byte)'x' }, out _, out _));
        Assert.False(ClientPolicyHeldWire.TryDecode(new byte[] { 1, 3 }, out _, out _));
    }

    [Fact]
    public void The_held_message_always_fits_one_packet()
    {
        var items = new List<RecheckItem>();
        for (int n = 0; n < 40; n++) items.Add(Missing(new string('k', 60) + n, "M" + n, RecheckLocalState.NotInstalled));
        byte[] packet = ClientPolicyHeldWire.Encode(true, items);
        Assert.True(packet.Length <= ClientModPolicyLimits.MaxReportPacketBytes);
        Assert.True(ClientPolicyHeldWire.TryDecode(packet, out _, out var local));
        Assert.InRange(local.Count, 1, ClientPolicyRecheckLimits.MaxNoticeItems);
    }

    [Fact]
    public void A_long_id_is_matched_on_its_clipped_form()
    {
        // Review L4: the client clips the id on the wire; the server looked it up by the full key and read "unknown".
        string longKey = "owner.long." + new string('x', ClientModPolicyLimits.MaxGuidChars + 40);
        var item = Missing(longKey, "Long Mod", RecheckLocalState.NotInstalled);
        item.InstallListed = true;
        Assert.True(ClientPolicyHeldWire.TryDecode(ClientPolicyHeldWire.Encode(true, new[] { item }), out _, out var local));
        Assert.Single(local);
        Assert.True(longKey.Length > ClientModPolicyLimits.MaxGuidChars);

        var schedule = new CountdownChatSchedule();
        schedule.OnHeld(1, true, local, 1, 60, 0, out _);
        string line = ClientPolicyNoticeText.CountdownChat(new[] { Missing(longKey, "Long Mod") }, 60, schedule.Local(1));
        Assert.StartsWith("Reinstall Long Mod from Install Mods in the Mods menu within 60 s", line);
    }

    // ---- The words --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_line_says_the_fix_where_the_player_is_and_the_seconds_left()
    {
        Assert.Equal("Reinstall Robe Pack from Install Mods in the Mods menu within 60 s or you'll be disconnected. "
            + ClientPolicyNoticeText.CountdownChatTail,
            ClientPolicyNoticeText.CountdownChat(new[] { Missing("robes", "Robe Pack") }, 60, Facts("robes", RecheckLocalState.NotInstalled, listed: true)));

        Assert.StartsWith("Turn Robe Pack back on in the Mods list within 30 s or you'll be disconnected.",
            ClientPolicyNoticeText.CountdownChat(new[] { Missing("robes", "Robe Pack") }, 30, Facts("robes", RecheckLocalState.TurnedOff)));

        // Not said: as the notice, turn it back on.
        Assert.StartsWith("Turn Robe Pack back on in the Mods list within 10 s",
            ClientPolicyNoticeText.CountdownChat(new[] { Missing("robes", "Robe Pack") }, 10, null));

        Assert.StartsWith("Turn off Test Plain in the Mods list within 45 s",
            ClientPolicyNoticeText.CountdownChat(new[] { NotAllowed("test.plain", "Test Plain") }, 45, null));

        var older = new RecheckItem { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindOlder, Rule = "1.2.0 or newer", Had = "1.1.0" };
        Assert.StartsWith("Update Robe Pack to 1.2.0 or newer (you have 1.1.0) within 20 s",
            ClientPolicyNoticeText.CountdownChat(new[] { older }, 20, null));

        // Several: the deadline first, then each fix.
        string many = ClientPolicyNoticeText.CountdownChat(new[] { Missing("robes", "Robe Pack"), NotAllowed("test.plain", "Test Plain") }, 59,
            Facts("robes", RecheckLocalState.NotInstalled, listed: true));
        Assert.StartsWith("Within 59 s or you'll be disconnected: reinstall Robe Pack from Install Mods in the Mods menu; turn off Test Plain in the Mods list.",
            many);

        Assert.Equal("", ClientPolicyNoticeText.CountdownChat(Array.Empty<RecheckItem>(), 60, null));
    }

    [Fact]
    public void The_line_uses_the_notice_s_facts_for_each_state()
    {
        // Review M2: the chat line contradicted the notice for a mod Install Mods doesn't offer.
        var robes = Missing("robes", "Robe Pack");

        // Not installed and not offered here: as the notice, look for it or ask the admins (never "reinstall").
        string notListed = ClientPolicyNoticeText.CountdownChat(new[] { robes }, 60, Facts("robes", RecheckLocalState.NotInstalled));
        Assert.StartsWith("Look for Robe Pack in Install Mods in the Mods menu, or ask the admins where to get it within 60 s", notListed);
        Assert.DoesNotContain("Reinstall", notListed);
        var noticeItem = Missing("robes", "Robe Pack", RecheckLocalState.NotInstalled);
        Assert.Contains("ask the admins where to get it", ClientPolicyNoticeText.Reason(noticeItem, RecheckPauseOutcome.NotTried));

        // The local test source provides it.
        Assert.StartsWith("Reinstall Robe Pack from Install Mods (local test source) in the Mods menu within 60 s",
            ClientPolicyNoticeText.CountdownChat(new[] { robes }, 60, Facts("robes", RecheckLocalState.NotInstalled, localTest: true)));

        // Paused here for the server: installed, back as the server allows it (a statement, not an order).
        string paused = ClientPolicyNoticeText.CountdownChat(new[] { robes }, 40, Facts("robes", RecheckLocalState.Unknown, paused: true));
        Assert.StartsWith("Within 40 s or you'll be disconnected: Robe Pack is required here; it's installed but paused for this server", paused);

        // Newer than the server allows: no claim that Install Mods offers the older version.
        var newer = new RecheckItem { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindNewer, Rule = "1.2.x", Had = "1.3.0" };
        string newerLine = ClientPolicyNoticeText.CountdownChat(new[] { newer }, 30, null);
        Assert.StartsWith("Within 30 s or you'll be disconnected: Robe Pack is newer than this server allows (1.2.x); you have 1.3.0.", newerLine);
        Assert.DoesNotContain("Install Mods", newerLine);
        var mismatch = new RecheckItem { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindMismatch, Rule = "1.2.0", Had = "1.3.0" };
        Assert.DoesNotContain("Install Mods", ClientPolicyNoticeText.CountdownChat(new[] { mismatch }, 30, null));

        // A mod the server doesn't allow that can't be paused needs a restart, as the notice says.
        Assert.Contains("can't be paused; turn it off in the Mods list and restart V Rising",
            ClientPolicyNoticeText.CountdownChat(new[] { NotAllowed("x", "X", pausable: false) }, 30, null));
    }

    [Fact]
    public void The_line_is_ascii_without_markup_and_never_sends_the_player_to_the_join_screen()
    {
        var evil = new RecheckItem { Key = "x", Name = "<color=red>Bad</color> é", Kind = ClientModProblem.KindMissing };
        string line = ClientPolicyNoticeText.CountdownChat(new[] { evil, Missing("robes", "Robe Pack") }, 60, null);
        Assert.True(line.Length <= 400);
        Assert.DoesNotContain("<", line);
        Assert.DoesNotContain(">", line);
        foreach (char c in line) Assert.InRange(c, ' ', '~');
        foreach (var state in new[] { RecheckLocalState.Unknown, RecheckLocalState.TurnedOff, RecheckLocalState.NotInstalled })
            foreach (bool listed in new[] { false, true })
            {
                string l = ClientPolicyNoticeText.CountdownChat(new[] { Missing("robes", "Robe Pack") }, 60, Facts("robes", state, listed: listed));
                Assert.DoesNotContain("join", l, StringComparison.OrdinalIgnoreCase);
            }
    }

    // ---- The server's schedule ---------------------------------------------------------------------------------------

    [Fact]
    public void The_schedule_says_it_at_the_start_then_at_30_and_10_seconds_left_while_held()
    {
        var s = new CountdownChatSchedule();
        Assert.True(s.OnHeld(1, true, null, 7, 60, 100, out var marks));
        Assert.Equal(new[] { 30, 10 }, marks);
        s.NoteSent(1, "line", 100);

        // The same countdown reported again soon (a second notice held): no new line, no new marks.
        Assert.False(s.OnHeld(1, true, null, 7, 58, 102, out marks));
        Assert.Empty(marks);

        Assert.True(s.MarkDue(1, 7, 30, 130));
        s.NoteSent(1, "line", 130);
        Assert.False(s.MarkDue(1, 7, 30, 131)); // once

        // Shown now: the 10 s mark says nothing.
        Assert.False(s.OnHeld(1, false, null, 7, 25, 135, out _));
        Assert.False(s.MarkDue(1, 7, 10, 150));
        Assert.Equal(2, (int)s.Describe(1, 150)!["lines"]!);
        Assert.False((bool)s.Describe(1, 150)!["held"]!);
    }

    [Fact]
    public void A_mark_too_close_to_the_start_is_skipped_and_another_countdown_starts_over()
    {
        var s = new CountdownChatSchedule();
        Assert.True(s.OnHeld(2, true, null, 1, 32, 0, out var marks));
        Assert.Equal(new[] { 10 }, marks); // 30 is only 2 s away
        s.NoteSent(2, "line", 0);

        // The disconnect was re-armed (another generation): its own marks; the start line keeps its gap from the last one.
        Assert.False(s.OnHeld(2, true, null, 2, 50, 1, out marks));
        Assert.Equal(new[] { 30, 10 }, marks);
        Assert.False(s.MarkDue(2, 1, 10, 22)); // the old countdown's mark
        Assert.True(s.MarkDue(2, 2, 30, 21));

        // Nothing armed: nothing said, nothing scheduled.
        var t = new CountdownChatSchedule();
        Assert.False(t.OnHeld(3, true, null, null, 0, 0, out marks));
        Assert.Empty(marks);
    }

    [Fact]
    public void A_mark_right_after_another_line_is_not_said_twice()
    {
        var s = new CountdownChatSchedule();
        Assert.True(s.OnHeld(1, true, null, 4, 60, 0, out _));
        s.NoteSent(1, "line", 0);
        Assert.False(s.OnHeld(1, false, null, 4, 40, 20, out _));
        // Held again at 31 s left: a line now, and the 30 s mark one second later stays quiet.
        Assert.True(s.OnHeld(1, true, null, 4, 31, 29, out var marks));
        Assert.Empty(marks); // 30 and 10 were scheduled for this countdown already
        s.NoteSent(1, "line", 29);
        Assert.False(s.MarkDue(1, 4, 30, 30));
        Assert.True(s.MarkDue(1, 4, 10, 50));
    }

    [Fact]
    public void The_schedule_keeps_what_the_client_said_it_has()
    {
        var s = new CountdownChatSchedule();
        s.OnHeld(1, true, Facts("robes", RecheckLocalState.NotInstalled), 1, 60, 0, out _);
        Assert.Equal(RecheckLocalState.NotInstalled, s.Local(1)["ROBES"].State);
        s.Forget(1);
        Assert.Empty(s.Local(1));
        Assert.Null(s.Describe(1, 0));
    }

    [Fact]
    public void A_re_armed_disconnect_keeps_the_lines_while_the_notice_is_held()
    {
        // Review M1: A (60 s) and B (75 s) missing, the notice held; A fixed at 40 s re-arms for B with no new notice, so
        // the client reports nothing. The lines move to the new countdown with what the client said before.
        var s = new CountdownChatSchedule();
        Assert.True(s.OnHeld(1, true, Facts("b", RecheckLocalState.NotInstalled, listed: true), 1, 60, 0, out var marks));
        s.NoteSent(1, "line", 0);
        Assert.True(s.MarkDue(1, 1, 30, 30));
        s.NoteSent(1, "line", 30);

        // Re-armed at 40 s: 35 s left for B. A line now (after the gap), then its own 30 and 10 s marks.
        Assert.True(s.OnRearmed(1, 2, 35, 40, out marks));
        Assert.Equal(new[] { 30, 10 }, marks);
        s.NoteSent(1, "line", 40);
        Assert.False(s.MarkDue(1, 1, 10, 50)); // the old countdown's mark is dead
        Assert.True(s.MarkDue(1, 2, 30, 45));
        Assert.Equal(RecheckLocalState.NotInstalled, s.Local(1)["b"].State);

        // The client's own re-report of the same countdown adds nothing.
        Assert.False(s.OnHeld(1, true, Facts("b", RecheckLocalState.NotInstalled, listed: true), 2, 34, 41, out marks));
        Assert.Empty(marks);

        // Not held: a re-arm says nothing.
        var t = new CountdownChatSchedule();
        Assert.False(t.OnRearmed(2, 5, 40, 0, out marks));
        Assert.Empty(marks);
        t.OnHeld(2, true, null, 5, 60, 0, out _);
        t.OnHeld(2, false, null, null, 0, 1, out _);
        Assert.False(t.OnRearmed(2, 6, 40, 10, out marks));
        Assert.Empty(marks);
    }

    [Fact]
    public void The_tracker_re_arms_without_a_notice_when_one_of_two_mods_is_fixed()
    {
        // The tracker side of review M1: the exact case where no new notice reaches the client.
        var policy = new ClientPolicyConfig { MidSession = MidSessionPolicy.Disconnect };
        policy.Required.Add(new ClientModRow("a.mod", "", "", ClientModRule.Any, "", "A"));
        policy.Required.Add(new ClientModRow("b.mod", "", "", ClientModRule.Any, "", "B"));
        ClientModReport R(params string[] ids)
        {
            var r = new ClientModReport();
            foreach (string id in ids) r.Entries.Add(new ClientModEntry(id, id, "1.0.0", ClientModSources.Progenitor));
            return r;
        }

        var t = new ClientPolicyRecheckTracker();
        t.OnJoinAccepted(1, policy, R("a.mod", "b.mod"));
        var first = t.Judge(1, policy, R("b.mod"), new ClientModServerFacts(), "report", 0);
        Assert.True(first.ArmKick);
        var second = t.Judge(1, policy, R(), new ClientModServerFacts(), "report", 15);
        Assert.False(second.ArmKick); // B's deadline is later; A's still arms it
        var fixedA = t.Judge(1, policy, R("a.mod"), new ClientModServerFacts(), "report", 40);
        Assert.True(fixedA.ArmKick);
        Assert.Equal(RecheckNoticeKind.None, fixedA.Notice);
        Assert.Equal(fixedA.KickGeneration, t.ArmedGeneration(1));
        Assert.Equal(35, (int)Math.Round(t.SecondsLeft(1, 40)!.Value));

        // The live glue carries the schedule across that re-arm.
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Networking", "Handshake.ClientPolicy.cs"));
        int apply = source.IndexOf("private static void ApplyRecheck(", StringComparison.Ordinal);
        int rearm = source.IndexOf("RearmHeldChat(userIndex, name, generation);", StringComparison.Ordinal);
        int next = source.IndexOf("private static void SendRecheckNotice(", StringComparison.Ordinal);
        Assert.True(apply > 0 && rearm > apply && rearm < next);
    }

    [Fact]
    public void Held_messages_are_rate_limited_and_start_lines_are_capped()
    {
        var s = new CountdownChatSchedule();
        Assert.False(s.Throttled(1, 0));
        Assert.True(s.Throttled(1, 0.5));
        Assert.False(s.Throttled(1, 1.0));
        Assert.False(s.Throttled(2, 0.5)); // per player

        // A client toggling its held state gets at most MaxStartLines start lines per countdown.
        int lines = 0;
        for (int n = 0; n < 20; n++)
        {
            double now = n * 10;
            if (s.OnHeld(3, true, null, 1, 300, now, out _))
            {
                lines++;
                s.NoteSent(3, "line", now);
            }

            s.OnHeld(3, false, null, null, 0, now + 1, out _);
        }

        Assert.Equal(CountdownChatSchedule.MaxStartLines, lines);
        // Another countdown starts over.
        Assert.True(s.OnHeld(3, true, null, 2, 300, 1000, out _));
    }

    [Fact]
    public void A_mark_says_its_own_number()
    {
        // Review L5: a timer a little early read 31 s at the 30 s mark.
        Assert.Equal(30, CountdownChatSchedule.SecondsToSay(30.02, 30));
        Assert.Equal(10, CountdownChatSchedule.SecondsToSay(9.4, 10));
        Assert.Equal(31, CountdownChatSchedule.SecondsToSay(30.02, null));
        Assert.Equal(60, CountdownChatSchedule.SecondsToSay(60, null));
        Assert.Equal(1, CountdownChatSchedule.SecondsToSay(0.2, null));
        Assert.Equal(25, CountdownChatSchedule.SecondsToSay(24.5, 30)); // far off its mark: what is left
    }

    // ---- The tracker's armed state ------------------------------------------------------------------------------------

    [Fact]
    public void The_tracker_names_the_armed_countdown_and_its_mods()
    {
        var policy = new ClientPolicyConfig { MidSession = MidSessionPolicy.Disconnect };
        policy.Required.Add(new ClientModRow("robes.pack", "", "", ClientModRule.Any, "Robes.", "Robe Pack"));
        var with = new ClientModReport();
        with.Entries.Add(new ClientModEntry("robes.pack", "Robe Pack", "1.0.0", ClientModSources.Progenitor));
        var without = new ClientModReport();

        var t = new ClientPolicyRecheckTracker();
        t.OnJoinAccepted(1, policy, with);
        Assert.Null(t.ArmedGeneration(1));
        Assert.Empty(t.ArmedItems(1));

        var d = t.Judge(1, policy, without, new ClientModServerFacts(), "report", 0);
        Assert.True(d.ArmKick);
        Assert.Equal(d.KickGeneration, t.ArmedGeneration(1));
        var items = t.ArmedItems(1);
        Assert.Single(items);
        Assert.Equal(("robes.pack", ClientModProblem.KindMissing), (items[0].Key, items[0].Kind));

        // Fixed: the countdown is called off, so its lines stop (another generation, none armed).
        var fixedD = t.Judge(1, policy, with, new ClientModServerFacts(), "report", 5);
        Assert.True(fixedD.CancelKick);
        Assert.Null(t.ArmedGeneration(1));
        Assert.Empty(t.ArmedItems(1));
    }

    // ---- The client's held reports --------------------------------------------------------------------------------------

    private static ClientPolicyNoticeWire.Notice Countdown()
    {
        var notice = new ClientPolicyNoticeWire.Notice { Kind = RecheckNoticeKind.Disconnect, Seconds = 60 };
        notice.Items.Add(new RecheckItem { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindMissing, Seconds = 60 });
        return notice;
    }

    private static List<(bool Held, Dictionary<string, HeldLocalFacts> Local)> HeldPackets()
    {
        var list = new List<(bool, Dictionary<string, HeldLocalFacts>)>();
        foreach (var p in ClientModReportClient.TestPackets)
            if (ClientPolicyHeldWire.TryDecode(p, out bool held, out var local)) list.Add((held, local));
        return list;
    }

    [Fact]
    public void The_client_tells_the_server_while_a_countdown_waits_behind_a_confirmation()
    {
        bool open = true;
        var shown = new List<string>();
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: true);
        ClientModReportClient.TestJoinOk = () => true;
        ClientPolicyNoticeClient.ConfirmationOpenProbe = () => open;
        ClientPolicyNoticeClient.Show = c => shown.Add(c.Title);
        ClientPolicyNoticeClient.LocalStateProbe = _ => RecheckLocalState.NotInstalled;
        ClientPolicyNoticeClient.InstallListedProbe = _ => true;

        ClientPolicyNoticeClient.Handle(Countdown());
        Assert.Empty(shown);
        var sent = HeldPackets();
        Assert.Single(sent);
        Assert.True(sent[0].Held);
        Assert.Equal(RecheckLocalState.NotInstalled, sent[0].Local["robes"].State);
        Assert.True(sent[0].Local["robes"].Listed);
        Assert.True(ClientPolicyNoticeClient.HeldReported);

        // A re-armed countdown held again is told again (the server says the new countdown).
        ClientPolicyNoticeClient.Handle(Countdown());
        Assert.Equal(2, HeldPackets().Count);

        // The box closes: the notice shows and the server is told it no longer waits.
        open = false;
        Assert.True(ClientPolicyNoticeClient.ShowHeldWhenFree());
        sent = HeldPackets();
        Assert.Equal(3, sent.Count);
        Assert.False(sent[2].Held);
        Assert.False(ClientPolicyNoticeClient.HeldReported);
        Assert.Single(shown);

        // On screen from the start: nothing is told (no duplicate of a visible notice).
        ClientPolicyNoticeClient.Handle(Countdown());
        Assert.Equal(3, HeldPackets().Count);
        Assert.Equal(3, ClientModReportClient.HeldMessagesSent);
    }

    [Fact]
    public void The_client_tells_the_server_a_required_mod_is_paused_here()
    {
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: true);
        ClientModReportClient.TestJoinOk = () => true;
        ClientPolicyNoticeClient.ConfirmationOpenProbe = () => true;
        ClientPolicyNoticeClient.Show = _ => { };
        ClientPolicyNoticeClient.PausedProbe = id => id == "robes";

        ClientPolicyNoticeClient.Handle(Countdown());
        var sent = HeldPackets();
        Assert.Single(sent);
        Assert.True(sent[0].Local["robes"].Paused);
        Assert.Contains("installed but paused for this server",
            ClientPolicyNoticeText.CountdownChat(new[] { Missing("robes", "Robe Pack") }, 60, sent[0].Local));
    }

    [Fact]
    public void A_held_countdown_replaced_by_the_called_off_notice_ends_the_lines()
    {
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: true);
        ClientModReportClient.TestJoinOk = () => true;
        ClientPolicyNoticeClient.ConfirmationOpenProbe = () => true;
        ClientPolicyNoticeClient.Show = _ => { };

        ClientPolicyNoticeClient.Handle(Countdown());
        ClientPolicyNoticeClient.Handle(new ClientPolicyNoticeWire.Notice { Kind = RecheckNoticeKind.Resolved });
        var sent = HeldPackets();
        Assert.Equal(2, sent.Count);
        Assert.True(sent[0].Held);
        Assert.False(sent[1].Held);
        Assert.Equal(ClientPolicyNoticeText.TitleResolved, ClientPolicyNoticeClient.Held!.Title);
    }

    [Fact]
    public void Nothing_is_told_for_a_warning_an_older_server_or_a_join_not_accepted()
    {
        ClientPolicyNoticeClient.ConfirmationOpenProbe = () => true;
        ClientPolicyNoticeClient.Show = _ => { };
        ClientModReportClient.TestJoinOk = () => true;

        // A warning held: no countdown, nothing to say.
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: true);
        var warn = new ClientPolicyNoticeWire.Notice { Kind = RecheckNoticeKind.Warn };
        warn.Items.Add(new RecheckItem { Key = "robes", Name = "Robe Pack", Kind = ClientModProblem.KindMissing, Added = true });
        ClientPolicyNoticeClient.Handle(warn);
        Assert.Empty(HeldPackets());

        // A server without CountdownChat (it would log the message as a bad report packet).
        ClientPolicyNoticeClient.Reset();
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: false);
        ClientPolicyNoticeClient.Handle(Countdown());
        Assert.Empty(HeldPackets());
        Assert.False(ClientModReportClient.ServerCountdownChat);

        // The join is not accepted.
        ClientPolicyNoticeClient.Reset();
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: true);
        ClientModReportClient.TestJoinOk = () => false;
        ClientPolicyNoticeClient.Handle(Countdown());
        Assert.Empty(HeldPackets());
    }

    [Fact]
    public void The_server_declares_CountdownChat_with_PolicyRecheck_and_the_client_reads_it()
    {
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Progenitor.Core", "Networking", "Handshake.ClientPolicy.cs"));
        Assert.Equal(2, CountOf(source, "Capability.PolicyRecheck | Capability.CountdownChat"));
        Assert.Equal(16384u, (uint)Handshake.Capability.CountdownChat);
        Assert.Equal(Handshake.Capability.None, Handshake.SelfCapabilities & Handshake.Capability.CountdownChat);

        // Review L3: a held message is acted on only while a disconnect is armed, after the rate limit.
        int handler = source.IndexOf("private static void OnNoticeHeld(", StringComparison.Ordinal);
        Assert.True(handler > 0);
        string body = source.Substring(handler, source.IndexOf("private static void RearmHeldChat(", StringComparison.Ordinal) - handler);
        Assert.Contains("HeldChat.Throttled(userIndex, now)", body);
        Assert.Contains("if (throttled || generation == null) return;", body);

        // A server that does not re-check never gets the message, whatever the bit says.
        ClientModReportClient.NoteServerRequest(true, rechecks: false, ServerMidSessionRule.Unknown, countdownChat: true);
        Assert.False(ClientModReportClient.ServerCountdownChat);
        ClientModReportClient.NoteServerRequest(true, rechecks: true, ServerMidSessionRule.Disconnect, countdownChat: true);
        Assert.True(ClientModReportClient.ServerCountdownChat);
        ClientModReportClient.Reset();
        Assert.False(ClientModReportClient.ServerCountdownChat);
    }

    private static int CountOf(string text, string what)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(what, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += what.Length;
        }

        return n;
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Progenitor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
