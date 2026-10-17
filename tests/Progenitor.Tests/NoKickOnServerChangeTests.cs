using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// NOKICK1 (live 2026-10-17): a dev deploy put a sample plugin that declares ServerMustHave on the test server only, then
/// hot-swapped the server's Core. The new generation soft re-asked the connected player ("no answer never disconnects
/// them"), the answer lacked the sample, the judgment treated it as a join (no install window left: the carried join's
/// window counts as spent) and kicked them. A server-side change must never kick an already-connected player: the
/// shortfall is told in place and enforced at their next join; joins keep every rule.
/// </summary>
public sealed class NoKickOnServerChangeTests
{
    private const ulong Steam = 76561198000000077UL;

    private sealed class Slot
    {
        public string? Value;
        public HandshakeSessionMemory Generation() => new(() => Value, v => Value = v);
    }

    private static Handshake.ModRecord Must(string guid, string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(guid, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    [Fact]
    public void A_join_is_refused_and_every_kind_of_admitted_player_is_kept_in_place()
    {
        // A fresh join: nothing says they were admitted.
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(false, false, false, false));
        // Ok in this generation (a re-answer after a server plugin hot-load).
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(true, false, false, false));
        // Carried across a Core swap by the session memory.
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(false, true, false, false));
        // A soft re-ask is waiting on them.
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(false, false, true, false));
        // The answer is to a requirement re-send (only ok users get one).
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(false, false, false, true));
    }

    [Fact]
    public void The_live_sequence_a_core_swap_bringing_a_required_plugin_keeps_the_connected_player()
    {
        var slot = new Slot();
        // Generation 1: the player joined and answered fine.
        slot.Generation().Remember(0, Steam);

        // Generation 2 (the swap that also loaded the sample): the re-ask is soft ...
        var gen2 = slot.Generation();
        bool remembered = gen2.Contains(0, Steam);
        Assert.Equal(HandshakeAskKind.SoftReask, HandshakeReask.KindFor(reask: true, rememberedAnswered: remembered));
        // ... the answer lacks the newly required sample ...
        var server = new List<Handshake.ModRecord> { Must("example.sample", "2026.10.51") };
        var answered = new List<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, answered, out var failed);
        Assert.NotEmpty(problems);
        // ... and the player stays connected (the status in the new generation is not ok yet: before the fix this kicked).
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace,
            HandshakeReask.MismatchOutcome(wasAlreadyOk: false, rememberedThisSession: remembered, softReaskPending: true, requirementResend: false));
        // They are told which mod, with Open Install Mods when they have no copy.
        var items = HandshakeInPlace.Items(failed, server, answered);
        var item = Assert.Single(items);
        Assert.Equal(ClientModProblem.KindMissing, item.Kind);
        Assert.True(item.Added);
        Assert.False(item.KickWorthy);

        // Their next join is a join: the memory is forgotten and the join rules apply to the same answer.
        gen2.Forget(0);
        bool afterJoin = gen2.Contains(0, Steam);
        Assert.Equal(HandshakeAskKind.Join, HandshakeReask.KindFor(reask: false, rememberedAnswered: afterJoin));
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(false, afterJoin, false, false));
    }

    [Fact]
    public void A_server_plugin_hot_loaded_without_a_swap_keeps_ok_players_and_new_joins_are_judged_in_full()
    {
        var server = new List<Handshake.ModRecord> { Must("example.part", "1.0.0") };
        var problems = Handshake.Compare.Evaluate(server, new List<Handshake.ModRecord>(), out _);
        Assert.NotEmpty(problems);
        // The re-send's answer from a player whose handshake was ok.
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(true, true, false, requirementResend: true));
        // A player joining after the load.
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(false, false, false, false));
    }

    [Fact]
    public void Only_a_required_row_fails_an_answer_a_mod_that_runs_without_its_client_part_never_does()
    {
        // Required (ServerMustHave): missing fails. NoNeedForSync (what a Limited or Optional counterpart declares): missing is fine.
        var required = new List<Handshake.ModRecord> { Must("example.required", "1.0.0") };
        var recommended = new List<Handshake.ModRecord>
        {
            Handshake.ModRecord.CreateDeclared("example.optional", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
        };
        Assert.NotEmpty(Handshake.Compare.Evaluate(required, new List<Handshake.ModRecord>()));
        Assert.Empty(Handshake.Compare.Evaluate(recommended, new List<Handshake.ModRecord>()));
    }

    [Fact]
    public void Notice_items_name_missing_and_version_rows_and_progenitor_and_skip_what_the_server_does_not_list()
    {
        var server = new List<Handshake.ModRecord>
        {
            Must("example.missing", "1.0.0"),
            Must("example.older", "2.0.0"),
            Must("example.minor", "1.4.0", NetworkVersionRule.Minor),
        };
        var answered = new List<Handshake.ModRecord>
        {
            Handshake.ModRecord.Undeclared("example.older", "1.0.0"),
            Handshake.ModRecord.Undeclared("example.minor", "1.5.0"),
        };
        var failed = new List<string> { "example.missing", "example.older", "example.minor", "client.only.wants", ProgenitorExact.RowId, "EXAMPLE.MISSING" };
        var items = HandshakeInPlace.Items(failed, server, answered, id => id == "example.missing" ? "Missing Mod" : null);

        Assert.Equal(new[] { "example.missing", "example.older", "example.minor", ProgenitorExact.RowId }, items.Select(i => i.Key).ToArray());
        Assert.Equal("Missing Mod", items[0].Name);
        Assert.Equal(ClientModProblem.KindMissing, items[0].Kind);
        Assert.Equal(ClientModProblem.KindOlder, items[1].Kind);
        Assert.Equal("2.0.0", items[1].Rule);
        Assert.Equal("1.0.0", items[1].Had);
        Assert.Equal(ClientModProblem.KindNewer, items[2].Kind);
        Assert.Equal("1.4.x", items[2].Rule);
        Assert.Equal(HandshakeInPlace.ProgenitorName, items[3].Name);
        Assert.All(items, i => Assert.True(i.Added));
        Assert.All(items, i => Assert.False(i.KickWorthy));
    }

    [Fact]
    public void The_in_place_notice_says_keep_playing_and_offers_install_for_a_mod_with_no_copy()
    {
        var server = new List<Handshake.ModRecord> { Must("example.sample", "1.0.0") };
        var items = HandshakeInPlace.Items(new[] { "example.sample" }, server, Array.Empty<Handshake.ModRecord>());
        items[0].Local = RecheckLocalState.NotInstalled;
        items[0].InstallListed = true;
        var content = ClientPolicyNoticeText.Compose(RecheckNoticeKind.Warn, items, 0, "");

        Assert.Equal(ClientPolicyNoticeText.TitleRequired, content.Title);
        Assert.Contains("You can keep playing", content.Body, StringComparison.Ordinal);
        Assert.Contains("Now required here; install it from", content.Lines[0], StringComparison.Ordinal);
        Assert.Contains("before your next join", content.Lines[0], StringComparison.Ordinal);
        Assert.Equal(ClientPolicyNoticeText.OpenKindInstall, content.OpenKind);
        Assert.Equal("example.sample", content.OpenKey);

        // On the wire it is the client mod policy's warn notice, which every client with that stage reads.
        byte[] packet = ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Warn, 0, items, 1);
        Assert.True(ClientPolicyNoticeWire.TryDecode(packet, out var notice));
        Assert.Equal(RecheckNoticeKind.Warn, notice.Kind);
        Assert.True(Assert.Single(notice.Items).Added);
    }

    [Fact]
    public void Each_problem_is_told_once_until_it_is_fixed_or_the_player_joins_again()
    {
        var told = new HandshakeInPlaceTold();
        var a = new RecheckItem { Key = "a", Kind = ClientModProblem.KindMissing, Added = true };
        var b = new RecheckItem { Key = "b", Kind = ClientModProblem.KindMissing, Added = true };

        Assert.Single(told.Fresh(0, new[] { a }));
        Assert.Empty(told.Fresh(0, new[] { a })); // a soft re-ask resend: nothing new
        Assert.Equal("b", Assert.Single(told.Fresh(0, new[] { a, b })).Key);
        Assert.Equal(2, told.Outstanding(0));
        Assert.Single(told.Fresh(1, new[] { a })); // another player is told on their own

        told.Clear(0); // a clean answer
        Assert.Equal(0, told.Outstanding(0));
        Assert.Single(told.Fresh(0, new[] { a })); // it came back: told again

        told.Forget(0); // a join or a leave
        Assert.Single(told.Fresh(0, new[] { a }));
    }

    [Fact]
    public void Players_let_in_without_an_answer_are_remembered_across_a_swap_so_their_re_ask_is_soft()
    {
        Assert.True(HandshakeReask.RememberAcrossSwap(HandshakeAnswerWatch.Status.Unverified));
        foreach (var s in new[]
        {
            HandshakeAnswerWatch.Status.None, HandshakeAnswerWatch.Status.Pending, HandshakeAnswerWatch.Status.Mismatch,
            HandshakeAnswerWatch.Status.Timeout, HandshakeAnswerWatch.Status.AwaitingInstall, HandshakeAnswerWatch.Status.AwaitingReport,
        })
            Assert.False(HandshakeReask.RememberAcrossSwap(s));
        // Ok users are remembered when they answer; the soft re-ask never disconnects on no answer.
        Assert.Equal(HandshakeReaskStep.KeepConnected, HandshakeReask.After(HandshakeReask.RetryDelays.Count - 1, answered: false, connected: true));
    }

    /// <summary>The live glue, source-checked (it needs the server world).</summary>
    [Fact]
    public void The_handshake_keeps_admitted_players_before_any_refusal_and_the_in_place_path_never_kicks()
    {
        string handshake = Source("src/Progenitor.Core/Networking/Handshake.cs").Replace("\r\n", "\n");
        int ok = handshake.IndexOf("if (problems.Count == 0)\n            {\n                // CLIENTPOLICY1", StringComparison.Ordinal);
        int keep = handshake.IndexOf("HandshakeReask.MismatchOutcome(wasAlreadyOk, rememberedOk, softReask, requirementResend) == HandshakeMismatchOutcome.KeepInPlace", StringComparison.Ordinal);
        int note = handshake.IndexOf("RefusedJoins.Note(user.Index, failedIds, server, answered, user.PlatformId);", StringComparison.Ordinal);
        int defer = handshake.IndexOf("if (TryDeferKick(world, userEntity, user.Index, name, problems, failedIds, server, answered, clientCaps, localFixes))", StringComparison.Ordinal);
        Assert.True(ok > 0 && ok < keep, "the clean answer is accepted first");
        Assert.True(keep > 0 && keep < note && note < defer, "an admitted player is kept before the refusal path and the install window");

        int end = handshake.IndexOf("return;\n            }", keep, StringComparison.Ordinal);
        string inPlace = handshake.Substring(keep, end - keep);
        Assert.Contains("AcceptAnswer(inPlace: true);", inPlace);
        Assert.Contains("TellShortfallInPlace(user.Index, name, failedIds, server, answered);", inPlace);
        Assert.DoesNotContain("Kick", inPlace.Replace("HandshakeReask.KeptAfterRequirementChangeLine", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("Disconnect", inPlace, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionMemory.Forget", inPlace, StringComparison.Ordinal);

        string tell = Between(handshake, "private static void TellShortfallInPlace(", "private static bool StillConnected(");
        Assert.Contains("Notice = RecheckNoticeKind.Warn", tell);
        Assert.DoesNotContain("Disconnect", tell.Replace("RecheckNoticeKind.Disconnect", ""), StringComparison.Ordinal);

        // The told set is forgotten at a join and a leave; the let-in users are remembered before the tables go.
        Assert.Contains("InPlaceTold.Forget(ev.UserIndex);", handshake);
        Assert.Contains("InPlaceTold.Forget(ev.User.Index);", handshake);
        Assert.Contains("RememberLetInBeforeSwap();\n        AnswerWatch.Clear();", handshake);
    }

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        int b = text.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "markers: " + start + " / " + end);
        return text.Substring(a, b - a);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CastlesTests.RepoRootForDumpSources(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
