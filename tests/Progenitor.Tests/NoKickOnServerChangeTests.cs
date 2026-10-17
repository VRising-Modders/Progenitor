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
/// window counts as spent) and kicked them. A server-initiated change must never kick an already-connected player: the
/// shortfall is told in place, masked for them, and enforced at their next join. Joins, and re-answers caused by a
/// client-side change, keep every existing rule.
/// </summary>
public sealed class NoKickOnServerChangeTests
{
    private const ulong Steam = 76561198000000077UL;

    private sealed class Slot
    {
        public string? Value;
        public HandshakeSessionMemory Memory() => new(() => Value, v => Value = v);
        public HandshakeInPlaceTold Told() => new(() => Value, v => Value = v);
    }

    private static Handshake.ModRecord Must(string guid, string version, NetworkVersionRule rule = NetworkVersionRule.Exact) =>
        Handshake.ModRecord.CreateDeclared(guid, version, NetworkCompatibilityLevel.ServerMustHave, rule);

    [Fact]
    public void Only_a_server_initiated_ask_keeps_the_player_in_place()
    {
        // The soft re-ask after a server Core swap (a remembered user answering while it waits).
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(softReaskPending: true, requirementResend: false));
        // The requirement re-send after a server plugin's Require or hot-load.
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(softReaskPending: false, requirementResend: true));
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(softReaskPending: true, requirementResend: true));
        // A join, or a re-answer the client started: the existing flow (install window, refusal).
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(softReaskPending: false, requirementResend: false));
    }

    [Fact]
    public void A_client_side_change_keeps_the_existing_flow_even_for_an_ok_remembered_player()
    {
        // The client hot-unloaded a required mod (or swapped its own Core to a mismatched version) and re-answered: the
        // player is ok in this generation and remembered, but nothing on the server asked, so no soft re-ask is pending
        // and no re-send is owed.
        var slot = new Slot();
        slot.Memory().Remember(0, Steam);
        Assert.True(slot.Memory().Contains(0, Steam));
        var server = new List<Handshake.ModRecord> { Must("example.required", "1.0.0") };
        Assert.NotEmpty(Handshake.Compare.Evaluate(server, new List<Handshake.ModRecord>()));
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(softReaskPending: false, requirementResend: false));

        // The client's own Core swap asks for a refresh; the refresh is answered without a re-send being owed.
        var watch = new HandshakeRequirementWatch();
        watch.NoteSent(0, "stamp");
        Assert.False(watch.TakeResendAnswer(0));
    }

    [Fact]
    public void The_live_sequence_a_core_swap_bringing_a_required_plugin_keeps_the_connected_player()
    {
        var slot = new Slot();
        // Generation 1: the player joined and answered fine.
        slot.Memory().Remember(0, Steam);

        // Generation 2 (the swap that also loaded the sample): the re-ask is soft ...
        var gen2 = slot.Memory();
        bool remembered = gen2.Contains(0, Steam);
        Assert.Equal(HandshakeAskKind.SoftReask, HandshakeReask.KindFor(reask: true, rememberedAnswered: remembered));
        // ... the answer lacks the newly required sample ...
        var server = new List<Handshake.ModRecord> { Must("example.sample", "2026.10.51") };
        var answered = new List<Handshake.ModRecord>();
        Assert.NotEmpty(Handshake.Compare.Evaluate(server, answered, out var failed));
        // ... and the player stays connected (before the fix this kicked).
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(softReaskPending: true, requirementResend: false));
        var item = Assert.Single(HandshakeInPlace.Items(failed, server, answered));
        Assert.Equal(ClientModProblem.KindMissing, item.Kind);
        Assert.True(item.Added);
        Assert.False(item.KickWorthy);

        // Their next join is a join: the memory is forgotten, no soft re-ask is pending, the join rules apply.
        gen2.Forget(0);
        Assert.Equal(HandshakeAskKind.Join, HandshakeReask.KindFor(reask: false, rememberedAnswered: gen2.Contains(0, Steam)));
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(softReaskPending: false, requirementResend: false));
    }

    [Fact]
    public void A_server_plugin_hot_loaded_without_a_swap_keeps_ok_players_through_the_re_send()
    {
        var watch = new HandshakeRequirementWatch();
        watch.NoteSent(0, "before");
        Assert.True(watch.IsStale(0, "after"));
        watch.NoteResendSent(0);
        bool owed = watch.TakeResendAnswer(0);
        Assert.True(owed);
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(softReaskPending: false, requirementResend: owed));
        // The next answer owes nothing: a later client-side change is judged as before.
        Assert.False(watch.TakeResendAnswer(0));
    }

    [Fact]
    public void Only_a_required_row_fails_an_answer_a_mod_that_runs_without_its_client_part_never_does()
    {
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

        byte[] packet = ClientPolicyNoticeWire.Encode(RecheckNoticeKind.Warn, 0, items, 1);
        Assert.True(ClientPolicyNoticeWire.TryDecode(packet, out var notice));
        Assert.Equal(RecheckNoticeKind.Warn, notice.Kind);
        Assert.True(Assert.Single(notice.Items).Added);
    }

    [Fact]
    public void Each_problem_is_told_once_until_it_is_fixed_or_the_player_joins_again()
    {
        var told = new Slot().Told();
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
    public void What_was_told_survives_a_core_swap_so_the_popup_is_not_repeated()
    {
        string? slot = null;
        var item = new RecheckItem { Key = "example.sample", Kind = ClientModProblem.KindMissing, Added = true };
        var gen1 = new HandshakeInPlaceTold(() => slot, v => slot = v);
        Assert.Single(gen1.Fresh(3, new[] { item }));

        // A new Core generation over the same process string: the same problem says nothing new.
        var gen2 = new HandshakeInPlaceTold(() => slot, v => slot = v);
        Assert.Empty(gen2.Fresh(3, new[] { item }));
        Assert.Equal(1, gen2.Outstanding(3));

        // The encoding round-trips and skips what it cannot hold.
        var decoded = HandshakeInPlaceTold.Decode("3=missing:a,older:b;bad;-1=x;4=");
        Assert.Equal(new[] { 3 }, decoded.Keys.ToArray());
        Assert.Equal(2, decoded[3].Count);
        Assert.Equal("", HandshakeInPlaceTold.Encode(new Dictionary<int, HashSet<string>> { [1] = new() { "a;b" } }));
    }

    [Fact]
    public void A_version_mismatched_mod_is_masked_so_the_server_part_sees_it_as_absent()
    {
        var mask = new HandshakeInPlaceMask();
        var told = new Slot().Told();
        var server = new List<Handshake.ModRecord> { Must("example.missing", "1.0.0"), Must("example.versioned", "2.0.0") };
        var answered = new List<Handshake.ModRecord> { Handshake.ModRecord.Undeclared("example.versioned", "1.0.0") };
        var failed = new List<string> { "example.missing", "example.versioned", ProgenitorExact.RowId };

        var d = HandshakeInPlace.Decide(5, failed, server, answered, told, mask);
        Assert.Equal(3, d.Items.Count);
        Assert.Equal(3, d.Fresh.Count);
        Assert.Equal(new[] { "example.missing", "example.versioned" }, d.Masked.ToArray());
        Assert.True(mask.IsMasked(5, "EXAMPLE.VERSIONED"));
        Assert.False(mask.IsMasked(5, ProgenitorExact.RowId));
        Assert.False(mask.IsMasked(6, "example.versioned"));

        // The client's table lists the mismatched mod; the peer view reads it as absent while it is masked.
        var table = new FeatureTable();
        var row = new FeatureTableMod("example.versioned", "1.0.0");
        row.Features["feature"] = 1;
        table.Mods["example.versioned"] = row;
        var view = new PeerTableView("example.versioned", () => table, masked: g => mask.IsMasked(5, g));
        Assert.Null(view.ModVersion("example.versioned"));
        Assert.False(view.Has("feature"));
        Assert.False(view.Has("example.versioned", "feature"));
        var unmasked = new PeerTableView("example.versioned", () => table);
        Assert.Equal("1.0.0", unmasked.ModVersion("example.versioned"));
        Assert.True(unmasked.Has("feature"));

        // A second judgment of the same answer tells nothing new; a clean answer clears the mask.
        Assert.Empty(HandshakeInPlace.Decide(5, failed, server, answered, told, mask).Fresh);
        mask.Clear(5);
        Assert.Null(new PeerTableView("example.versioned", () => table, masked: g => mask.IsMasked(5, g)).ModVersion("missing.mod"));
        Assert.Equal("1.0.0", new PeerTableView("example.versioned", () => table, masked: g => mask.IsMasked(5, g)).ModVersion("example.versioned"));
    }

    [Fact]
    public void Players_let_in_without_an_answer_are_remembered_across_a_swap_so_their_re_ask_is_soft()
    {
        var slot = new Slot();
        var memory = slot.Memory();
        memory.Remember(1, 11);
        var users = new List<(int, ulong, bool, HandshakeAnswerWatch.Status)>
        {
            (0, 10, true, HandshakeAnswerWatch.Status.Unverified),   // let in: remembered
            (1, 11, true, HandshakeAnswerWatch.Status.Ok),           // answered: already remembered
            (2, 12, true, HandshakeAnswerWatch.Status.Pending),      // mid-join: asked as a join
            (3, 13, false, HandshakeAnswerWatch.Status.Unverified),  // offline: nothing
            (4, 14, true, HandshakeAnswerWatch.Status.AwaitingInstall),
        };
        Assert.Equal(1, HandshakeReask.RememberLetIn(users, memory));
        var next = slot.Memory();
        Assert.True(next.Contains(0, 10));
        Assert.True(next.Contains(1, 11));
        Assert.False(next.Contains(2, 12));
        Assert.False(next.Contains(3, 13));
        Assert.False(next.Contains(4, 14));
        Assert.Equal(HandshakeAskKind.SoftReask, HandshakeReask.KindFor(reask: true, rememberedAnswered: next.Contains(0, 10)));
        Assert.Equal(0, HandshakeReask.RememberLetIn(users, next)); // idempotent
        // The soft re-ask never disconnects on no answer.
        Assert.Equal(HandshakeReaskStep.KeepConnected, HandshakeReask.After(HandshakeReask.RetryDelays.Count - 1, answered: false, connected: true));
    }

    /// <summary>The live glue, source-checked (it needs the server world).</summary>
    [Fact]
    public void The_handshake_keeps_server_initiated_answers_before_any_refusal_and_the_in_place_path_never_kicks()
    {
        string handshake = Source("src/Progenitor.Core/Networking/Handshake.cs").Replace("\r\n", "\n");
        int ok = handshake.IndexOf("if (problems.Count == 0)\n            {\n                // CLIENTPOLICY1", StringComparison.Ordinal);
        int keep = handshake.IndexOf("HandshakeReask.MismatchOutcome(softReask, requirementResend) == HandshakeMismatchOutcome.KeepInPlace", StringComparison.Ordinal);
        int note = handshake.IndexOf("RefusedJoins.Note(user.Index, failedIds, server, answered, user.PlatformId);", StringComparison.Ordinal);
        int defer = handshake.IndexOf("if (TryDeferKick(world, userEntity, user.Index, name, problems, failedIds, server, answered, clientCaps, localFixes))", StringComparison.Ordinal);
        Assert.True(ok > 0 && ok < keep, "the clean answer is accepted first");
        Assert.True(keep > 0 && keep < note && note < defer, "a server-initiated answer is kept before the refusal path and the install window");

        int end = handshake.IndexOf("return;\n            }", keep, StringComparison.Ordinal);
        string inPlace = handshake.Substring(keep, end - keep);
        int decide = inPlace.IndexOf("HandshakeInPlace.Decide(user.Index, failedIds, server, answered, InPlaceTold, HandshakeInPlaceMask.Shared)", StringComparison.Ordinal);
        int accept = inPlace.IndexOf("AcceptAnswer(inPlace: true);", StringComparison.Ordinal);
        Assert.True(decide > 0 && decide < accept, "the mask goes on before the acceptance");
        Assert.Contains("TellShortfallInPlace(user.Index, name, shortfall);", inPlace);
        Assert.DoesNotContain("Kick", inPlace.Replace("HandshakeReask.KeptAfterRequirementChangeLine", ""), StringComparison.Ordinal);
        Assert.DoesNotContain("Disconnect", inPlace, StringComparison.Ordinal);
        Assert.DoesNotContain("SessionMemory.Forget", inPlace, StringComparison.Ordinal);

        string tell = Between(handshake, "private static void TellShortfallInPlace(", "private static bool StillConnected(");
        Assert.Contains("Notice = RecheckNoticeKind.Warn", tell);
        Assert.DoesNotContain("Disconnect", tell.Replace("RecheckNoticeKind.Disconnect", ""), StringComparison.Ordinal);

        // Told and mask are forgotten at a join and a leave and cleared by a clean answer; let-in users are remembered
        // before the tables go, through the game-free helper.
        Assert.Contains("InPlaceTold.Forget(ev.UserIndex);\n            HandshakeInPlaceMask.Shared.Clear(ev.UserIndex);", handshake);
        Assert.Contains("InPlaceTold.Forget(ev.User.Index);\n        HandshakeInPlaceMask.Shared.Clear(ev.User.Index);", handshake);
        Assert.Contains("InPlaceTold.Clear(user.Index);\n                    HandshakeInPlaceMask.Shared.Clear(user.Index);", handshake);
        Assert.Contains("RememberLetInBeforeSwap();\n        AnswerWatch.Clear();", handshake);
        Assert.Contains("int kept = HandshakeReask.RememberLetIn(users, SessionMemory);", handshake);

        // The server's send gates and peer view read the mask.
        string compat = Source("src/Progenitor.Core/Networking/ModCompatNet.cs");
        string exchange = Source("src/Progenitor.Core/Networking/ExchangeNet.cs");
        string plugin = Source("src/Progenitor.Core/Plugins/PluginCompat.cs");
        Assert.Contains("listsMod: HandshakeInPlaceMask.ListsMod(table, user.Index, modGuid));", compat);
        Assert.Contains("if (_server && HandshakeInPlaceMask.Shared.IsMasked(peer, modGuid)) return null;", compat);
        Assert.Contains("listsMod: HandshakeInPlaceMask.ListsMod(table, userIndex, modGuid));", exchange);
        Assert.Contains("masked: g => HandshakeInPlaceMask.Shared.IsMasked(index, g)", plugin);
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
