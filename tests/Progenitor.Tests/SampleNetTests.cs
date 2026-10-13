using System;
using Progenitor.Networking;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX1 (RD6 J3). Game-free coverage of the client→server round trip: the framing constants,
/// the server's whole admission decision (<c>SampleNet.Admit</c>), and the lines both sides write.
/// The sends, the receive handlers and the F8 keybind need a live client and server and are
/// behind <c>!PROGENITOR_TESTS</c>.
/// </summary>
public class SampleNetTests
{
    [Fact]
    // Event ids must be large and distinctive - the game's own ids are hashes and a collision is
    // silent - and the two directions must not share one. The Sample's convention is the ASCII
    // word "Sm??" (SmFx, SmUi, SmMd, and these two), which is what keeps its own features from
    // colliding; the cross-file check is deliberately by convention rather than by referencing
    // the other Sample files, which the -p:SkipForeignTestFiles build drops.
    public void The_event_ids_are_distinct_ascii_words()
    {
        Assert.Equal(0x536D4E70, SampleNet.PingEventId);   // "SmNp"
        Assert.Equal(0x536D4E72, SampleNet.PongEventId);   // "SmNr"
        Assert.NotEqual(SampleNet.PingEventId, SampleNet.PongEventId);
        Assert.Equal(0x536D, SampleNet.PingEventId >> 16);
        Assert.Equal(0x536D, SampleNet.PongEventId >> 16);
    }

    [Fact]
    public void A_well_formed_packet_from_a_handshaken_user_is_accepted()
    {
        Assert.Equal(
            SampleNet.Verdict.Ok,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, counter: 1, "ok", acceptedSoFar: 0));
    }

    [Fact]
    // TryRead<T> refuses any length that is not sizeof(Ping); the size check is the first gate.
    public void A_wrong_sized_payload_is_refused_first()
    {
        Assert.Equal(
            SampleNet.Verdict.BadSize,
            SampleNet.Admit(readOk: false, SampleNet.PingMagic, counter: 1, "ok", acceptedSoFar: 0));
    }

    [Fact]
    public void A_packet_without_the_magic_word_is_refused()
    {
        Assert.Equal(
            SampleNet.Verdict.BadMagic,
            SampleNet.Admit(readOk: true, magic: 0, counter: 1, "ok", acceptedSoFar: 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(SampleNet.MaxCounter + 1)]
    [InlineData(int.MaxValue)]
    public void A_counter_outside_the_bounds_is_refused(int counter)
    {
        Assert.Equal(
            SampleNet.Verdict.BadCounter,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, counter, "ok", acceptedSoFar: 0));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("asked")]
    [InlineData("mismatch")]
    [InlineData("timeout")]
    [InlineData("unverified")]
    [InlineData("awaiting-install")]
    [InlineData(null)]
    // Handshake.UserAnswerStatus's tokens. Only "ok" gets an answer, even though the plugin's
    // ServerMustHave level should already have kicked anybody else.
    public void A_sender_that_has_not_answered_the_handshake_is_refused(string? status)
    {
        Assert.Equal(
            SampleNet.Verdict.NotHandshaken,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, counter: 1, status, acceptedSoFar: 0));
    }

    [Fact]
    public void A_user_past_the_per_generation_cap_is_refused()
    {
        Assert.Equal(
            SampleNet.Verdict.Ok,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, 1, "ok", SampleNet.MaxPacketsPerUser - 1));
        Assert.Equal(
            SampleNet.Verdict.TooMany,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, 1, "ok", SampleNet.MaxPacketsPerUser));
    }

    [Fact]
    // The order is the contract: a malformed packet from an unhandshaken peer must report the
    // malformation, not the handshake, or a refusal line names the wrong cause.
    public void The_cheapest_and_most_specific_gate_wins()
    {
        Assert.Equal(
            SampleNet.Verdict.BadSize,
            SampleNet.Admit(readOk: false, magic: 0, counter: -5, "none", acceptedSoFar: 9999));
        Assert.Equal(
            SampleNet.Verdict.BadMagic,
            SampleNet.Admit(readOk: true, magic: 1, counter: -5, "none", acceptedSoFar: 9999));
        Assert.Equal(
            SampleNet.Verdict.BadCounter,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, counter: -5, "none", acceptedSoFar: 9999));
    }

    [Fact]
    public void The_refusal_line_names_the_sender_and_the_reason()
    {
        Assert.Equal(
            "refused ping from Rowan reason=BadMagic",
            SampleNet.RefusedLine("Rowan", SampleNet.Verdict.BadMagic));
        Assert.Equal(
            "refused ping from <unknown> reason=TooMany",
            SampleNet.RefusedLine(null, SampleNet.Verdict.TooMany));
    }

    [Fact]
    public void The_accepted_line_shows_both_sends_of_the_round_trip()
    {
        Assert.Equal(
            "ping from Rowan counter=3 accepted=3 reply=True notice recipients=2",
            SampleNet.AcceptedLine("Rowan", counter: 3, accepted: 3, replySent: true, noticeRecipients: 2));
    }

    [Fact]
    public void The_client_lines_cover_a_press_a_cap_and_both_answer_kinds()
    {
        Assert.Equal("F8 press counter=1 sent=True", SampleNet.PressLine(1, true));
        Assert.Equal("F8 press counter=2 sent=False", SampleNet.PressLine(2, false));
        Assert.Equal(
            "F8 press ignored: this Core generation has already sent 500 ping(s).",
            SampleNet.PressCappedLine(SampleNet.MaxClientSends));
        Assert.Equal("reply counter=1 total=1", SampleNet.ReceivedLine(SampleNet.KindReply, 1, 1));
        Assert.Equal("notice counter=1 total=1", SampleNet.ReceivedLine(SampleNet.KindNotice, 1, 1));
        Assert.Equal("packet kind=7 counter=1 total=1", SampleNet.ReceivedLine(7, 1, 1));
    }

    [Theory]
    [InlineData(SampleNet.KindReply, true)]
    [InlineData(SampleNet.KindNotice, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    public void The_client_only_acts_on_kinds_it_knows(int kind, bool expected)
    {
        Assert.Equal(expected, SampleNet.IsKnownKind(kind));
    }

    [Fact]
    public void An_unreadable_answer_is_logged_and_dropped()
    {
        Assert.Equal(
            "ignored a server packet: payload was 3 byte(s), not one Pong",
            SampleNet.BadReplyLine("payload was 3 byte(s), not one Pong"));
    }

    [Fact]
    public void The_formatted_log_line_is_the_greppable_one()
    {
        Assert.Equal(
            "Sample net: F8 press counter=1 sent=True",
            SampleLog.Format(SampleNet.Area, SampleNet.PressLine(1, true)));
    }
}

/// <summary>
/// Job FEX2 (R56 finding 6). The per-session packet budget: the table is keyed on
/// <c>User.Index</c>, which is a connection <em>slot</em> the game recycles, so the interesting
/// tests are the ones about what happens after somebody leaves. The table, the arithmetic and the
/// scrub moved out of the file's <c>!PROGENITOR_TESTS</c> half so they could be driven here.
/// </summary>
public class SampleNetBudgetTests
{
    [Fact]
    public void A_budget_row_is_dropped_when_the_user_disconnects()
    {
        SampleNet.Reset();

        Assert.Equal(1, SampleNet.NoteAccepted(3));
        Assert.Equal(2, SampleNet.NoteAccepted(3));
        Assert.Equal(2, SampleNet.AcceptedFor(3));
        Assert.Equal(1, SampleNet.TrackedUsers);

        Assert.True(SampleNet.ForgetUser(3));
        Assert.Equal(0, SampleNet.AcceptedFor(3));
        Assert.Equal(0, SampleNet.TrackedUsers);

        // The generation-wide diagnostic count is not a budget and is deliberately not scrubbed.
        Assert.Equal(2, SampleNet.Accepted);
    }

    [Fact]
    // The handler stays quiet for the overwhelming majority of players, who never pressed F8.
    public void Forgetting_a_slot_that_never_sent_anything_reports_nothing_to_forget()
    {
        SampleNet.Reset();
        Assert.False(SampleNet.ForgetUser(11));
    }

    [Fact]
    // The defect R56 finding 6 names: without the scrub the next player in that slot is refused
    // TooMany on their first press, silently, with no client-side feedback at all.
    public void A_recycled_connection_slot_does_not_inherit_the_previous_players_spend()
    {
        SampleNet.Reset();
        for (int i = 0; i < SampleNet.MaxPacketsPerUser; i++) SampleNet.NoteAccepted(7);

        Assert.Equal(
            SampleNet.Verdict.TooMany,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, 1, "ok", SampleNet.AcceptedFor(7)));

        SampleNet.ForgetUser(7);        // the disconnect

        Assert.Equal(
            SampleNet.Verdict.Ok,
            SampleNet.Admit(readOk: true, SampleNet.PingMagic, 1, "ok", SampleNet.AcceptedFor(7)));
    }

    [Fact]
    public void Forgetting_one_slot_leaves_every_other_slot_alone()
    {
        SampleNet.Reset();
        SampleNet.NoteAccepted(1);
        SampleNet.NoteAccepted(2);
        SampleNet.NoteAccepted(2);

        Assert.True(SampleNet.ForgetUser(1));
        Assert.Equal(0, SampleNet.AcceptedFor(1));
        Assert.Equal(2, SampleNet.AcceptedFor(2));
        Assert.Equal(1, SampleNet.TrackedUsers);
    }

    [Fact]
    public void The_disconnect_line_names_the_player_the_slot_and_the_spend()
    {
        Assert.Equal(
            "forgot the ping budget of Rowan (user index 4, accepted=12): the slot is recycled, "
            + "so the budget must not be.",
            SampleNet.ForgotUserLine("Rowan", 4, 12));
        Assert.Equal(
            "forgot the ping budget of <unknown> (user index 0, accepted=0): the slot is recycled, "
            + "so the budget must not be.",
            SampleNet.ForgotUserLine(null, 0, 0));
    }

    [Fact]
    // The client half of the cap. Unlike the per-user budget this one is per Core generation on
    // purpose: it exists for a stuck key, and a key is stuck for as long as the process runs.
    public void The_client_send_ceiling_is_spent_once_per_generation()
    {
        SampleNet.Reset();
        for (int i = 1; i <= SampleNet.MaxClientSends; i++)
        {
            Assert.True(SampleNet.TryTakeClientSend(out int counter));
            Assert.Equal(i, counter);
        }

        Assert.False(SampleNet.TryTakeClientSend(out _));
        SampleNet.Reset();
        Assert.True(SampleNet.TryTakeClientSend(out int first));
        Assert.Equal(1, first);
        SampleNet.Reset();
    }
}

/// <summary>
/// Job FEX2 (R56 finding 7), amended by job FEX4 (R57 findings 3 and 4). The reserved event-id
/// range, and the refusals <c>CustomEvents</c> owes to it.
///
/// <para><b>What is driven and what is pinned.</b> The rule itself is now a game-free type,
/// <c>CustomEventPolicy</c>, compiled into this suite and exercised for real in
/// <see cref="CustomEventsPolicyTests"/> — including the refusals, which FEX2 could only pin and
/// which R57 finding 4 showed the pin did not hold. What is left here is the coverage assertion
/// (<c>ReceiveSlotPolicy</c>'s eleven Core protocol ids and the Sample's ids are both compiled, so
/// "the range covers what Core owns" is a real check against the real predicate) and the pins that
/// tie <c>src/Progenitor.Core/Networking/CustomEvents.cs</c> — <c>unsafe</c>, binding ProjectM,
/// Stunlock.Network and Unity, so never compiled here — to it: that it forwards to the policy, that
/// its registrations store only through the table, and that every send entry point carries the
/// range check.</para>
/// </summary>
public class CustomEventsReservedRangeTests
{
    private const int ReservedPrefix = 0x5072;      // ASCII "Pr"

    // The production predicate, not a copy: CustomEventPolicy is compiled into this suite.
    private static bool IsReserved(int eventId) => Progenitor.Networking.CustomEventPolicy.IsReserved(eventId);

    private static string Source() =>
        SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "CustomEvents.cs");

    [Fact]
    // The reservation is only worth writing down if it actually covers what Core owns today. All
    // eleven, from the list Bootstrap drops while the Core slot is empty.
    public void Every_core_protocol_id_is_inside_the_reserved_range()
    {
        int[] core =
        {
            Progenitor.Bootstrap.ReceiveSlotPolicy.HandshakeRequest,
            Progenitor.Bootstrap.ReceiveSlotPolicy.HandshakeResponse,
            Progenitor.Bootstrap.ReceiveSlotPolicy.HandshakeResult,
            Progenitor.Bootstrap.ReceiveSlotPolicy.PrefabDefinition,
            Progenitor.Bootstrap.ReceiveSlotPolicy.PrefabUnregister,
            Progenitor.Bootstrap.ReceiveSlotPolicy.PrefabKeyPlaceholder,
            Progenitor.Bootstrap.ReceiveSlotPolicy.PrefabEditResults,
            Progenitor.Bootstrap.ReceiveSlotPolicy.ConfigSync,
            Progenitor.Bootstrap.ReceiveSlotPolicy.ModManifest,
            Progenitor.Bootstrap.ReceiveSlotPolicy.ModManifestRequest,
            Progenitor.Bootstrap.ReceiveSlotPolicy.ModManifestChunk,
        };

        Assert.Equal(11, core.Length);
        foreach (int id in core)
            Assert.True(IsReserved(id), "0x" + id.ToString("X8") + " is outside 0x5072____");
    }

    [Fact]
    // And that a mod's own four-letter tag is not caught by it. The Sample's are "Sm**".
    public void The_samples_own_ids_are_outside_the_reserved_range()
    {
        Assert.False(IsReserved(SampleNet.PingEventId));
        Assert.False(IsReserved(SampleNet.PongEventId));
        Assert.False(IsReserved(0x4D794D64));            // "MyMd", the tutorial's literal
        Assert.True(IsReserved(0x50726851));             // "PrhQ", the handshake result
    }

    [Fact]
    // Core's public constant and predicate forward to the game-free policy, so what
    // CustomEventsPolicyTests drives is what the dispatcher applies.
    public void The_predicate_in_core_is_the_one_measured_here()
    {
        string body = Source();
        SourcePin.AssertStatement(
            body, "public const int ReservedEventIdPrefix = CustomEventPolicy.ReservedEventIdPrefix;");
        SourcePin.AssertStatement(
            body, "public static bool IsReservedEventId(int eventId) => CustomEventPolicy.IsReserved(eventId);");

        Assert.Equal(ReservedPrefix, Progenitor.Networking.CustomEventPolicy.ReservedEventIdPrefix);
    }

    [Fact]
    // Refusal one: the reserved range, from anybody but Core. Refusal two: an id a different owner
    // already holds, kept rather than replaced. Both are decided in one game-free place, which is
    // what lets CustomEventsPolicyTests drive them rather than grep for them.
    public void Both_refusals_are_decided_in_one_place()
    {
        string decide = SourcePin.MethodBody(
            SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "CustomEventPolicy.cs"),
            "public static string? RefuseRegistration(");

        SourcePin.AssertStatement(decide, "if (IsReserved(eventId) && !owner.IsCore)");
        SourcePin.AssertStatement(decide, "if (currentOwner is { } held && !held.Equals(owner))");
        SourcePin.AssertStatement(decide, "return null;");
    }

    [Theory]
    [InlineData("public static void OnReceive(int eventId, CustomEventHandler handler)", "ClientHandlers")]
    [InlineData("public static void OnReceiveFromClient(int eventId, CustomEventFromClientHandler handler)", "ServerHandlers")]
    // A refused registration must store nothing and must not reach RegisterOwnedEventId - an id in
    // Bootstrap's owned set with no handler behind it would have that fourcc dropped rather than
    // passed to the game.
    //
    // FEX2 pinned that as "the LogError line comes before the store", which R57 finding 4 measured
    // to be worth nothing: deleting both `return;` statements passed every test. The rule is now
    // structural instead. EventHandlerTable.Register is the only thing that stores a handler or an
    // owner and it returns Refused without storing (driven in CustomEventsPolicyTests), so this
    // method's only remaining job is to stop on that verdict before claiming the id. The pin: the
    // early-out is one unfoldable statement, RegisterOwnedEventId comes after it, and no other
    // statement in the method writes the table.
    public void A_refused_registration_cannot_reach_the_store_or_the_owned_set(string signature, string table)
    {
        string body = SourcePin.MethodBody(Source(), signature);

        SourcePin.AssertStatementExactly(
            body, "var result = " + table + ".Register(eventId, handler, owner, out string? log);");
        SourcePin.AssertStatementExactly(body, "if (!Accepted(result, log)) return;");
        SourcePin.AssertNoStatementContaining(body, table + "[eventId]");

        int guard = body.IndexOf("if (!Accepted(result, log)) return;", System.StringComparison.Ordinal);
        int owned = body.IndexOf("ReceiveSlotHandoff.RegisterOwnedEventId(eventId);", System.StringComparison.Ordinal);
        Assert.True(guard >= 0 && owned > guard, "RegisterOwnedEventId is not after the refusal guard in " + signature);
    }

    [Fact]
    // R57 finding 3: the range was enforced on receive and on none of the six send entry points, so
    // an author who picked a "Pr**" id got a refusal in the log while their SendToAll still fed
    // Core's protocol handlers on every peer. Each entry point evaluates GetCallingAssembly only
    // behind the range test, so an ordinary send pays a mask and a compare.
    public void Every_send_entry_point_refuses_the_reserved_range()
    {
        string body = Source();
        string[] entries =
        {
            "public static bool SendToUser(ServerBootstrapSystem bootstrap, int userIndex, int eventId, ReadOnlySpan<byte> payload)",
            "public static bool SendToUser<T>(ServerBootstrapSystem bootstrap, int userIndex, int eventId, in T value) where T : unmanaged",
            "public static bool SendToUser<T>(World server, Entity userEntity, int eventId, in T value) where T : unmanaged",
            "public static int SendToAll<T>(World server, int eventId, in T value) where T : unmanaged",
            "public static bool SendToServer(int eventId, ReadOnlySpan<byte> payload)",
            "public static bool SendToServer<T>(int eventId, in T value) where T : unmanaged",
        };

        foreach (string signature in entries)
        {
            string method = SourcePin.MethodBody(body, signature);
            bool refuses =
                method.Contains(
                    "if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return false;",
                    System.StringComparison.Ordinal)
                || method.Contains(
                    "if (IsReservedEventId(eventId) && !SendAllowed(eventId, Assembly.GetCallingAssembly())) return 0;",
                    System.StringComparison.Ordinal);
            Assert.True(refuses, "no reserved-range check in " + signature);
        }
    }

    [Fact]
    // PluginNet is Core code, so attributing a send to its immediate caller would read a plugin's
    // send through the facade as Core's. The facade therefore makes the call itself, with the
    // plugin's own assembly.
    public void The_plugin_facade_checks_the_range_with_the_plugins_own_assembly()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
        string decide = SourcePin.MethodBody(source, "private bool MaySend(int eventId)");

        SourcePin.AssertStatement(decide, "if (!CustomEvents.IsReservedEventId(eventId)) return true;");
        SourcePin.AssertStatement(decide, "return CustomEvents.SendAllowedFor(eventId, owner);");

        // All five wrappers, by the exact line each one carries. Three are expression-bodied, which
        // SourcePin.MethodBody cannot open, so these are statement pins rather than body reads.
        SourcePin.AssertStatement(
            source, "=> MaySend(eventId) && CustomEvents.SendToUser(server, userEntity, eventId, value);");
        SourcePin.AssertStatement(source, "if (!MaySend(eventId)) return false;");
        SourcePin.AssertStatement(
            source, "=> MaySend(eventId) ? CustomEvents.SendToAll(server, eventId, value) : 0;");
        SourcePin.AssertStatement(source, "if (!MaySend(eventId)) return 0;");
        SourcePin.AssertStatement(source, "=> MaySend(eventId) && CustomEvents.SendToServer(eventId, value);");
    }

    [Fact]
    // Ownership is by assembly simple name, not Assembly identity: a plugin hot reload comes back
    // from a fresh collectible assembly with the same name and must still be able to re-register
    // its own ids. (Core is the exception and is the next test: identity, never a name.)
    public void Ownership_is_by_assembly_simple_name_so_a_hot_reload_is_the_same_owner()
    {
        string body = SourcePin.MethodBody(Source(), "private static string OwnerNameOf(Assembly? assembly)");
        SourcePin.AssertStatement(body, "try { return assembly?.GetName().Name ?? \"\"; }");
    }

    [Fact]
    // R58 finding 12, job FEX6. Rule 1 used to ask the same question rule 2 asks - "is the simple
    // name 'Progenitor'?" - so a fork built from this tree with the project file unchanged, or a mod
    // shipping a private copy of Progenitor.dll, *became* Core and could take PrhQ, PrfD and the
    // rest. Core's identity must not be nameable. The behavior is driven in
    // CustomEventsPolicyTests (a fake owner named Progenitor is refused in the range); pinned here
    // is that CustomEvents establishes it by reference and that the string cannot come back.
    public void Core_is_identified_by_reference_to_its_own_assembly_and_not_by_name()
    {
        string body = Source();

        SourcePin.AssertStatementExactly(
            body, "private static readonly Assembly CoreAssembly = typeof(CustomEvents).Assembly;");
        SourcePin.AssertUncommented(body, "ReferenceEquals(assembly, CoreAssembly)");
        SourcePin.AssertUncommented(body, "ReferenceEquals(caller, CoreAssembly)");
        SourcePin.AssertUncommented(body, "EventIdOwner.ForCore(");

        // The name is carried for log lines only. Nothing compares against it, and the field that
        // used to be the Core test is gone.
        SourcePin.AssertNoStatementContaining(body, "CoreOwnerName");

        // The plugin facade is never Core, whatever the plugin is called: SendAllowedFor takes a
        // name and hands the decision a hard false.
        SourcePin.AssertStatementExactly(body, "SendDecision(eventId, ownerName, senderIsCore: false);");
    }

    [Fact]
    // R58 finding 5, job FEX6. Assembly.GetCallingAssembly() reports the caller of the frame it is
    // evaluated in, so the method that must not be inlined is the one that *asks*. FEX4 put the
    // attribute on SendAllowed, which is handed the assembly, leaving all six entry points resting
    // on incidental inline blockers (EH, a pinned local, a loop); shrink one - and
    // SendToUser<T>(World, Entity, ...) was one refactor away - and the crawl walks one frame too
    // far, returning Progenitor for any handler the framework itself invoked. The refusal would
    // then stop refusing on exactly the case it exists for, silently.
    //
    // This scans rather than lists, so a seventh entry point added later is covered on the day it
    // is written.
    public void Every_method_that_asks_who_called_it_refuses_to_be_inlined()
    {
        string[] lines = Source().Split('\n');
        var asking = new System.Collections.Generic.List<string>();
        var missing = new System.Collections.Generic.List<string>();
        int declaration = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", System.StringComparison.Ordinal)) continue;

            // A member declaration at class level: four spaces of indent and no more.
            if (line.StartsWith("    ", System.StringComparison.Ordinal)
                && !line.StartsWith("     ", System.StringComparison.Ordinal)
                && trimmed.Contains('(', System.StringComparison.Ordinal)
                && !trimmed.StartsWith("[", System.StringComparison.Ordinal)
                && !trimmed.StartsWith("///", System.StringComparison.Ordinal)
                && (trimmed.Contains("public ", System.StringComparison.Ordinal)
                    || trimmed.Contains("private ", System.StringComparison.Ordinal)
                    || trimmed.Contains("internal ", System.StringComparison.Ordinal)))
                declaration = i;

            if (!trimmed.Contains("GetCallingAssembly", System.StringComparison.Ordinal)) continue;
            Assert.True(declaration >= 0, "GetCallingAssembly outside any member: " + trimmed);

            string signature = lines[declaration].Trim();
            asking.Add(signature);
            if (!CarriesNoInlining(lines, declaration)) missing.Add(signature);
        }

        Assert.Empty(missing);

        // The six sends and all four removals - the two by-id ones and, since job FEX8 (R59
        // finding 2), the two handler-matched ones. A lower number means an entry point stopped
        // attributing its caller, which is the other way this protection disappears.
        Assert.True(asking.Count >= 10, "only " + asking.Count + " methods attribute their caller");
    }

    /// <summary>
    /// True when the declaration at <paramref name="declaration"/> is preceded by
    /// <c>[MethodImpl(MethodImplOptions.NoInlining)]</c>, walking back over other attributes, doc
    /// comments and blank lines.
    /// </summary>
    private static bool CarriesNoInlining(string[] lines, int declaration)
    {
        for (int i = declaration - 1; i >= 0; i--)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0) return false;
            if (trimmed.StartsWith("///", System.StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("//", System.StringComparison.Ordinal)) continue;
            if (!trimmed.StartsWith("[", System.StringComparison.Ordinal)) return false;
            if (trimmed.StartsWith("[MethodImpl(MethodImplOptions.NoInlining)]", System.StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    [Theory]
    [InlineData("public static bool RemoveHandler(int eventId)", "RemoveClientOwned")]
    [InlineData("public static bool RemoveClientHandler(int eventId)", "RemoveServerOwned")]
    // R58 finding 1, job FEX6. Removal was the third direction into the table and the ungated one:
    // it dropped the handler *and the owner row*, so RemoveHandler(theirId) + OnReceive(theirId)
    // walked around rule 2 in two lines, and RemoveHandler(PrfD) silenced Core's prefab-definition
    // handler for the rest of the generation - strictly worse than the foreign-payload case FEX4
    // was written to prevent, since the fourcc stays routed and the miss drops the packet in
    // silence. The decision is driven in CustomEventsPolicyTests; pinned here is that the public
    // entry point goes through it with the *caller's* identity rather than reaching the table.
    public void The_by_id_removals_go_through_the_owner_check_with_the_callers_identity(
        string signature, string helper)
    {
        string body = Source();
        SourcePin.AssertStatement(
            body, signature + " =>");
        SourcePin.AssertStatement(
            body, helper + "(eventId, CallerOwner(Assembly.GetCallingAssembly()));");

        string inner = SourcePin.MethodBody(body, "private static bool " + helper + "(int eventId, in EventIdOwner remover)");
        SourcePin.AssertUncommented(inner, ".Remove(eventId, remover, out string? log)");
        SourcePin.AssertStatementExactly(inner, "NoteRemovalRefusal(eventId, log);");
    }

    [Theory]
    [InlineData("public static bool RemoveHandler(int eventId, CustomEventHandler handler)",
        "RemoveClientMatched", "CustomEventHandler", "ClientHandlers")]
    [InlineData("public static bool RemoveClientHandler(int eventId, CustomEventFromClientHandler handler)",
        "RemoveServerMatched", "CustomEventFromClientHandler", "ServerHandlers")]
    // R59 finding 2, job FEX8. These two used to pass OwnerOf(handler) as the remover - the same
    // function that produced the recorded owner from that same delegate at registration - so the
    // owner comparison was a value against itself and no argument pair reachable through the
    // public API could be refused, while the docstring, modules.md and a test all said one could.
    // They attribute the caller now, like the by-id pair, which is also why the attribute matters:
    // if the JIT folded the entry point into a plugin's Unload, GetCallingAssembly would walk one
    // frame too far.
    public void The_handler_matched_removals_are_owner_checked_with_the_callers_identity(
        string signature, string helper, string handlerType, string table)
    {
        string body = Source();
        SourcePin.AssertStatement(body, signature);
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(body, signature),
            "return " + helper + "(eventId, handler, CallerOwner(Assembly.GetCallingAssembly()));");

        // And the remover the helper hands the table is that caller, not anything read off the
        // handler: OwnerOf must not appear on a removal path at all.
        string inner = SourcePin.MethodBody(
            body,
            "private static bool " + helper + "(int eventId, " + handlerType + " handler, in EventIdOwner remover)");
        SourcePin.AssertUncommented(inner, table + ".Remove(eventId, handler, remover, out string? log)");
        SourcePin.AssertStatementExactly(inner, "NoteRemovalRefusal(eventId, log);");
        Assert.DoesNotContain("OwnerOf", inner, System.StringComparison.Ordinal);
    }

    [Fact]
    // And there is no erasure that skips a decision, the same structural rule R57 finding 4 forced
    // on registration: the table's two Remove overloads both take a remover and both ask
    // CustomEventPolicy.RefuseRemoval, and the whole-table drop asks RefuseClear. R59 finding 8:
    // that third one used to be a bare Clear() with no decision in it, under a docstring saying
    // the two Removes were the only things that erase a row. Both its callers were Core, so
    // nothing was exploitable - the defect was a false structural sentence in the one file whose
    // claim is that its refusals are driven and not pinned.
    public void The_table_has_no_erasure_that_skips_the_owner_check()
    {
        string policy = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "CustomEventPolicy.cs");

        SourcePin.AssertStatement(policy, "public bool Remove(int eventId, in EventIdOwner remover, out string? log)");
        SourcePin.AssertStatement(
            policy, "public bool Remove(int eventId, THandler handler, in EventIdOwner remover, out string? log)");
        SourcePin.AssertStatement(
            policy, "public int ClearForUninstall(in EventIdOwner caller, out string? log)");
        SourcePin.AssertNoStatementContaining(policy, "public bool Remove(int eventId)");
        SourcePin.AssertNoStatementContaining(policy, "public bool Remove(int eventId, THandler handler)");
        SourcePin.AssertNoStatementContaining(policy, "public void Clear()");

        string decide = SourcePin.MethodBody(policy, "public static string? RefuseRemoval(");
        SourcePin.AssertStatementExactly(decide, "if (currentOwner is not { } held) return null;");
        SourcePin.AssertStatementExactly(decide, "if (remover.IsCore || held.Equals(remover)) return null;");

        string clear = SourcePin.MethodBody(policy, "public int ClearForUninstall(in EventIdOwner caller, out string? log)");
        SourcePin.AssertStatementExactly(clear, "log = CustomEventPolicy.RefuseClear(caller, _side);");
        SourcePin.AssertStatementExactly(clear, "if (log != null) return 0;");
    }

    [Fact]
    // A plugin cannot reach a clear. It cannot reach the type at all - EventHandlerTable is
    // internal to Progenitor.dll - and inside Core the only two callers are the Uninstall paths,
    // which pass an identity built by reference against Core's own assembly. The behavior (a
    // named plugin, including one named Progenitor, is refused) is driven in
    // CustomEventsPolicyTests; pinned here is that Core's two call sites are the only ones and
    // that neither of them names its own owner.
    public void The_only_callers_of_the_clear_are_cores_two_uninstall_paths()
    {
        string body = Source();

        SourcePin.AssertStatementExactly(SourcePin.MethodBody(body, "internal static void UninstallClientReceive()"),
            "ClearForUninstall(ClientHandlers);");
        SourcePin.AssertStatementExactly(SourcePin.MethodBody(body, "internal static void UninstallServerReceive()"),
            "ClearForUninstall(ServerHandlers);");

        // The Core identity is built through OwnerFor, whose Core test is ReferenceEquals against
        // CoreAssembly, so "Core" here cannot come from a string.
        SourcePin.AssertStatement(body, "private static EventIdOwner CoreOwner => OwnerFor(CoreAssembly, CoreAssembly);");
        SourcePin.AssertNoStatementContaining(body, "Handlers.Clear();");

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, @"\bClearForUninstall\(\w+Handlers\);").Count);
    }

    [Fact]
    // PluginNet's unload disposables remove through the owner-checked path, which is what makes
    // "unloading plugin A cannot silence plugin B" true of the framework's own teardown and not
    // only of the handler comparison. They are also declared in Core, which is what keeps the
    // caller-attributed rule of job FEX8 from standing between a plugin and its own handle - the
    // reason the facade, not CustomEvents, is the surface a mod should hold.
    public void The_plugin_facade_unloads_through_the_owner_checked_removal()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");

        SourcePin.AssertStatementExactly(
            source, "var d = new ActionDisposable(() => CustomEvents.RemoveHandler(eventId, handler));");
        SourcePin.AssertStatementExactly(
            source, "var d = new ActionDisposable(() => CustomEvents.RemoveClientHandler(eventId, handler));");
    }
}

/// <summary>
/// Job J19 (RD6). The reserved-event-id half of <c>SampleNet</c>: <c>.net status</c>,
/// <c>.net reserved &lt;id&gt;</c>, and the Attach line that runs Core's predicate over the
/// Sample's own two ids.
///
/// <para><c>CustomEvents.IsReservedEventId</c> is <c>unsafe</c> and game-bound, so it cannot be
/// called from here; the Sample takes its answer as a parameter and a source pin below asserts the
/// live handler really passes it. What <em>is</em> checked directly is Core's rule itself, through
/// <c>CustomEventPolicy</c> — which is game-free and linked into this suite — so the Sample's own
/// ids are measured against the real predicate and not against a copy of it.</para>
/// </summary>
public class SampleNetReservedTests
{
    [Fact]
    // The claim the Attach line makes every boot. Measured here against Core's own predicate, so
    // a future Sample id that strayed into the range fails the build rather than the server.
    public void The_samples_own_ids_are_outside_progenitors_reserved_range()
    {
        Assert.False(CustomEventPolicy.IsReserved(SampleNet.PingEventId));
        Assert.False(CustomEventPolicy.IsReserved(SampleNet.PongEventId));

        // And Core's own protocol ids are inside it, so the predicate under test is not one that
        // simply answers false.
        Assert.True(CustomEventPolicy.IsReserved(Handshake.RequestEventId));
        Assert.True(CustomEventPolicy.IsReserved(Handshake.ResultEventId));
    }

    [Fact]
    public void The_reply_text_names_the_same_prefix_core_uses()
    {
        string prefix = "0x" + CustomEventPolicy.ReservedEventIdPrefix.ToString("X4");

        Assert.Contains(prefix + "____", SampleNet.OwnIdsLine(false, false), StringComparison.Ordinal);
        Assert.Contains(prefix + "____", SampleNet.ReservedReply(0x50726848, reserved: true), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0x50726848", 0x50726848)]
    [InlineData("0X50726848", 0x50726848)]
    [InlineData("50726848", 0x50726848)]
    [InlineData("536d4e70", 0x536D4E70)]
    [InlineData("  0x1  ", 1)]
    [InlineData("FFFFFFFF", -1)]
    public void An_event_id_is_read_as_hex_with_or_without_the_prefix(string token, int expected)
    {
        Assert.True(SampleNet.TryParseEventId(token, out int id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("0x")]
    [InlineData("123456789")]      // nine digits cannot be an int32 id
    [InlineData("0x123456789")]
    [InlineData("nope")]
    [InlineData("-1")]
    [InlineData("12 34")]
    public void Anything_that_is_not_eight_or_fewer_hex_digits_is_refused(string? token)
    {
        Assert.False(SampleNet.TryParseEventId(token, out int id));
        Assert.Equal(0, id);
    }

    [Fact]
    // Decimal is refused on purpose: every id in the framework is written in hex, and guessing
    // the base would make the answer depend on how the reader typed it.
    public void A_decimal_looking_id_is_read_as_hex_and_not_as_decimal()
    {
        Assert.True(SampleNet.TryParseEventId("1000", out int id));
        Assert.Equal(0x1000, id);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("status", "")]
    [InlineData("STATUS", "")]
    public void The_status_branch_takes_no_argument(string what, string arg)
    {
        Assert.True(SampleNet.TryParseCommand(what, arg, out var kind, out int id));
        Assert.Equal(SampleNet.CommandKind.Status, kind);
        Assert.Equal(0, id);
    }

    [Fact]
    public void The_reserved_branch_carries_the_parsed_id()
    {
        Assert.True(SampleNet.TryParseCommand("reserved", "0x50726848", out var kind, out int id));
        Assert.Equal(SampleNet.CommandKind.Reserved, kind);
        Assert.Equal(0x50726848, id);

        Assert.True(SampleNet.TryParseCommand("RESERVED", "536D4E70", out kind, out id));
        Assert.Equal(SampleNet.CommandKind.Reserved, kind);
        Assert.Equal(SampleNet.PingEventId, id);
    }

    [Theory]
    [InlineData("status", "0x1")]      // status takes nothing
    [InlineData("reserved", "")]       // reserved needs an id
    [InlineData("reserved", "zzz")]
    [InlineData("free", "0x1")]
    [InlineData("ping", "")]
    public void Anything_else_is_refused_so_the_usage_line_is_what_the_caller_sees(string what, string arg)
    {
        Assert.False(SampleNet.TryParseCommand(what, arg, out _, out _));
    }

    [Fact]
    public void The_usage_line_names_both_branches()
    {
        Assert.Contains(".net [status]", SampleNet.UsageReply, StringComparison.Ordinal);
        Assert.Contains(".net reserved", SampleNet.UsageReply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reserved_answer_says_what_actually_happens_to_a_mod_that_takes_one()
    {
        string reply = SampleNet.ReservedReply(0x50726848, reserved: true);

        Assert.StartsWith("0x50726848 is RESERVED", reply, StringComparison.Ordinal);
        Assert.Contains("CustomEvents.IsReservedEventId", reply, StringComparison.Ordinal);
        // The failure mode is the point: a refused send is silence, not an error at the far end.
        Assert.Contains("silence", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_free_answer_says_what_the_other_rule_is()
    {
        string reply = SampleNet.ReservedReply(SampleNet.PingEventId, reserved: false);

        Assert.StartsWith("0x536D4E70 is free", reply, StringComparison.Ordinal);
        // The second rule of the module: one handler owns one id, process-wide.
        Assert.Contains("one handler owns one id", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void An_id_is_always_written_as_eight_hex_digits()
    {
        Assert.Equal("0x00000001", SampleNet.FormatEventId(1));
        Assert.Equal("0x536D4E70", SampleNet.FormatEventId(SampleNet.PingEventId));
        Assert.Equal("0xFFFFFFFF", SampleNet.FormatEventId(-1));
    }

    [Fact]
    public void The_attach_line_reports_both_ids_and_says_both_must_be_false()
    {
        string line = SampleNet.OwnIdsLine(pingReserved: false, pongReserved: false);

        Assert.Contains("0x536D4E70 ('SmNp')", line, StringComparison.Ordinal);
        Assert.Contains("0x536D4E72 ('SmNr')", line, StringComparison.Ordinal);
        Assert.Contains("reserved=False/False", line, StringComparison.Ordinal);
        Assert.Contains("both must be False", line, StringComparison.Ordinal);
        Assert.Equal("Sample net: " + line, SampleLog.Format(SampleNet.Area, line));
    }

    [Fact]
    public void The_attach_line_reports_a_reserved_id_rather_than_hiding_it()
    {
        Assert.Contains("reserved=True/False", SampleNet.OwnIdsLine(true, false), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_reports_the_ids_and_the_budgets()
    {
        // Deliberately does not touch the budget tables: SampleNetTests drives those and xUnit
        // runs the two classes in parallel, so a Reset() here would race a counter assertion there.
        var lines = SampleNet.StatusLines();

        Assert.Equal(2, lines.Count);
        Assert.Contains("ping=0x536D4E70", lines[0], StringComparison.Ordinal);
        Assert.Contains("pong=0x536D4E72", lines[0], StringComparison.Ordinal);
        Assert.Contains("reserved 0x5072____ range", lines[0], StringComparison.Ordinal);
        Assert.Contains("accepted=", lines[1], StringComparison.Ordinal);
        Assert.Contains("trackedUsers=", lines[1], StringComparison.Ordinal);
        Assert.Contains("maxPerUser=" + SampleNet.MaxPacketsPerUser, lines[1], StringComparison.Ordinal);
        Assert.Contains("maxClientSends=" + SampleNet.MaxClientSends, lines[1], StringComparison.Ordinal);
    }

    [Fact]
    // The pin that stops the example going stale. A Sample that re-derived the mask locally would
    // pass every one of the tests above forever while Core's range moved underneath it.
    public void The_live_handler_asks_core_rather_than_re_implementing_the_mask()
    {
        string source = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleNet.cs");
        string body = SourcePin.MethodBody(source, "internal static void HandleReserved(ICommandContext ctx, int eventId)");

        SourcePin.AssertStatementExactly(
            body, "string line = ReservedReply(eventId, CustomEvents.IsReservedEventId(eventId));");

        // And the game-free half must not carry a second copy of the rule.
        SourcePin.AssertNoStatementContaining(source, ">> 16");
        SourcePin.AssertNoStatementContaining(source, "0xFFFF");
    }

    [Fact]
    // The Attach line has to run the real predicate too, or "both must be False" is a sentence
    // the Sample writes about itself without checking.
    public void Attach_runs_the_predicate_over_the_samples_own_ids()
    {
        string source = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleNet.cs");

        SourcePin.AssertStatement(source, "SampleLog.Info(Area, OwnIdsLine(");
        SourcePin.AssertStatementExactly(source, "CustomEvents.IsReservedEventId(PingEventId),");
        SourcePin.AssertStatementExactly(source, "CustomEvents.IsReservedEventId(PongEventId)));");
    }
}
