using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FMAN18: the deferred kick and its install window.
///
/// <para>Until this job a client that lacked a <c>ServerMustHave</c> mod was disconnected at its
/// first failing answer even when the server had that very package on offer — the contradiction
/// evidence 172 found, and the one FMAN12 could only describe in the kick text. The window makes
/// the offer usable inside the join that needs it: the server holds the kick, says what is
/// missing, sends the same signed descriptor the deny path sends, and re-evaluates the second
/// <c>DeclareSelf</c> down the same code path as the first.</para>
///
/// <para>Everything here is game-free. The window's own clock is a fake, exactly as
/// <see cref="HandshakeAnswerWatch"/>'s is, so the 60 s lapse is one line rather than a wait.</para>
/// </summary>
[Collection("Handshake")]
public class HandshakeInstallGraceTests : IDisposable
{
    private const string Sample = "progenitor.sample";
    private const string Other = "other.mod";

    private double _now;
    private readonly HandshakeInstallGrace _grace;

    public HandshakeInstallGraceTests()
    {
        Handshake.ResetTables();
        _grace = new HandshakeInstallGrace(() => _now);
    }

    public void Dispose() => Handshake.ResetTables();

    // ---- the decision -------------------------------------------------------------------------

    [Fact]
    public void Default_window_is_60_seconds_and_zero_turns_the_feature_off()
    {
        Assert.Equal(60, HandshakeInstallGrace.DefaultGraceSeconds);
        Assert.Equal(60, Handshake.DefaultInstallGraceSeconds);
        Assert.Equal(
            HandshakeInstallGrace.Grant.Disabled,
            _grace.Begin(1, "TestPlayer", new[] { Sample }, 0).Verdict);
        Assert.False(_grace.IsAwaiting(1));
    }

    /// <summary>
    /// The case the whole job exists for: one required row, the client lacks it, the server has it
    /// on offer. No kick — a window.
    /// </summary>
    [Fact]
    public void An_offered_only_miss_is_deferred_rather_than_kicked()
    {
        var server = new[] { Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        var verdict = Handshake.Compare.DecideMiss(
            problems, failedIds, server, client, _ => MissFix.Thunderstore, graceAvailable: true);

        Assert.True(verdict.Defer);
        Assert.Equal(new[] { Sample }, verdict.Ids);
        Assert.Equal("", verdict.Why);
    }

    // ---- the client capability (job FMAN20, R50 finding 2) --------------------------------------

    /// <summary>
    /// The defect this gate closes. FMAN18 shipped the window <b>on</b> and every client alive
    /// then routed <c>Ok = 2</c> into its read-only deny report, which forecloses the consent panel
    /// the offer needs — so a deferrable join cost such a player sixty seconds of a connecting
    /// screen, a sentence telling them to install something, no control anywhere that could, and
    /// then the same kick. A client says it can act on the window by declaring
    /// <see cref="Handshake.Capability.InstallWindow"/>; saying nothing is what every older Core
    /// does, so nothing is the safe reading and it kicks immediately as before.
    /// </summary>
    [Fact]
    public void A_client_that_did_not_declare_the_capability_is_kicked_immediately()
    {
        var server = new[] { Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        var without = Handshake.Compare.DecideMiss(
            problems, failedIds, server, client, _ => MissFix.Thunderstore,
            graceAvailable: true, clientCanInstall: false);

        Assert.False(without.Defer);
        Assert.Empty(without.Ids);
        Assert.Equal(Handshake.Compare.NoInstallWindowCapability, without.Why);

        var with = Handshake.Compare.DecideMiss(
            problems, failedIds, server, client, _ => MissFix.Thunderstore,
            graceAvailable: true, clientCanInstall: true);

        Assert.True(with.Defer);
        Assert.Equal(new[] { Sample }, with.Ids);
    }

    /// <summary>
    /// The operator's switch is still the operator's: <c>Networking.InstallGraceSeconds = 0</c>
    /// turns the feature off for every client, capable or not, and that reason is reported rather
    /// than the capability one.
    /// </summary>
    [Fact]
    public void The_operator_switch_still_wins_over_a_capable_client()
    {
        var server = new[] { Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        var verdict = Handshake.Compare.DecideMiss(
            problems, failedIds, server, client, _ => MissFix.Thunderstore,
            graceAvailable: false, clientCanInstall: true);

        Assert.False(verdict.Defer);
        Assert.Equal("no install window is available for this user", verdict.Why);
    }

    /// <summary>
    /// A capability travels as a reserved row, like the metadata and overflow rows: never compared
    /// as a plugin, ignored by a v1 peer, and read back as what was declared.
    /// </summary>
    [Fact]
    public void The_capability_row_round_trips_and_is_never_a_plugin()
    {
        var rows = new List<Handshake.ModRecord>
        {
            Handshake.ModRecord.Undeclared(Sample, "0.1.0"),
        };

        Assert.True(Handshake.Compare.AppendCapabilityRow(rows, Handshake.SelfCapabilities));
        Assert.Equal(2, rows.Count);
        Assert.Equal(Handshake.CapabilityGuid, rows[1].Guid);
        Assert.True(Handshake.Compare.IsReservedGuid(Handshake.CapabilityGuid));

        Assert.Equal(Handshake.SelfCapabilities, Handshake.Compare.ReadCapabilities(rows));

        // Stripped before any comparison: the server never sees it as a mod the client claims.
        var wire = Handshake.Compare.MaterializeWire(rows);
        Assert.Equal(new[] { Sample }, Names(wire));
        Assert.Empty(Handshake.Compare.Evaluate(
            new[] { Handshake.ModRecord.Undeclared(Sample, "0.1.0") }, rows));
    }

    /// <summary>
    /// This Core declares the install window, and the flag word is what the reader parses back —
    /// so a capability added later costs no wire bytes and an unknown bit is simply not acted on.
    /// </summary>
    [Fact]
    public void This_core_declares_the_install_window_and_unknown_bits_are_carried_not_believed()
    {
        Assert.True((Handshake.SelfCapabilities & Handshake.Capability.InstallWindow) != 0);

        var future = (Handshake.Capability)0x81u;
        var row = Handshake.Compare.CreateCapabilityRecord(future);
        var read = Handshake.Compare.ReadCapabilityBlob(row.Version);

        Assert.Equal(future, read);
        Assert.True((read & Handshake.Capability.InstallWindow) != 0);
    }

    /// <summary>
    /// Anything that is not a well-formed blob of this format reads as
    /// <see cref="Handshake.Capability.None"/>. Disbelief is free: it costs the peer only the
    /// immediate kick it would have had before FMAN18.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("2ff")]
    [InlineData("1zz")]
    [InlineData("1123456789")]
    [InlineData("0.1.0")]
    public void A_malformed_capability_blob_declares_nothing(string version)
    {
        Assert.Equal(Handshake.Capability.None, Handshake.Compare.ReadCapabilityBlob(version));
        Assert.Equal(
            Handshake.Capability.None,
            Handshake.Compare.ReadCapabilities(new[]
            {
                Handshake.ModRecord.Undeclared(Handshake.CapabilityGuid, version),
            }));
    }

    /// <summary>
    /// A reply with no room left declares nothing, and no asked row is dropped to make room:
    /// answering the server's question is worth more than declaring a capability, and the client
    /// that loses the declaration gets the pre-FMAN18 immediate kick, which is the safe direction.
    /// </summary>
    [Fact]
    public void A_full_reply_declares_nothing_rather_than_dropping_a_row()
    {
        var rows = new List<Handshake.ModRecord>();
        for (int i = 0; i < Handshake.MaxMods; i++)
            rows.Add(Handshake.ModRecord.Undeclared("mod" + i.ToString(CultureInfo.InvariantCulture), "1.0"));

        Assert.False(Handshake.Compare.AppendCapabilityRow(rows, Handshake.SelfCapabilities));
        Assert.Equal(Handshake.MaxMods, rows.Count);
        Assert.Equal(Handshake.Capability.None, Handshake.Compare.ReadCapabilities(rows));
    }

    /// <summary>Nothing to declare is no row at all, so absence and None cannot diverge.</summary>
    [Fact]
    public void Declaring_nothing_puts_no_row_on_the_wire()
    {
        var rows = new List<Handshake.ModRecord> { Handshake.ModRecord.Undeclared(Sample, "0.1.0") };

        Assert.False(Handshake.Compare.AppendCapabilityRow(rows, Handshake.Capability.None));
        Assert.Single(rows);
        Assert.Equal(Handshake.Capability.None, Handshake.Compare.ReadCapabilities(rows));
    }

    /// <summary>
    /// The row is appended <b>last</b> and replaces any earlier one, so a reader that lifts it out
    /// — which the server does, before <c>ApplyMetaRecord</c> — shifts no in-slot metadata index.
    /// </summary>
    [Fact]
    public void The_capability_row_goes_last_and_never_twice()
    {
        var rows = new List<Handshake.ModRecord>
        {
            Handshake.ModRecord.Undeclared(Sample, "0.1.0"),
            Handshake.Compare.CreateMetaRecord(new List<Handshake.ModRecord>()),
        };

        Assert.True(Handshake.Compare.AppendCapabilityRow(rows, Handshake.SelfCapabilities));
        Assert.True(Handshake.Compare.AppendCapabilityRow(rows, Handshake.SelfCapabilities));

        Assert.Equal(3, rows.Count);
        Assert.Equal(Handshake.CapabilityGuid, rows[rows.Count - 1].Guid);
        Assert.Equal(Handshake.MetaRecordGuid, rows[1].Guid);
    }

    /// <summary>
    /// Job FMAN20: the client half really does declare it. The reply build is behind
    /// <c>!PROGENITOR_TESTS</c> (it writes a fixed-buffer <c>ModList</c>), so the seam is pinned in
    /// the source: <c>SendReply</c> appends the capability row before it fills the packet.
    /// </summary>
    [Fact]
    public void The_client_reply_declares_the_capability()
    {
        string body = SourcePin.MethodBody(HandshakeSource(), "private static bool SendReply(");

        SourcePin.AssertStatement(body, "var rows = new List<ModRecord>(packed.Rows);");
        int append = body.IndexOf("Compare.AppendCapabilityRow(rows, CapabilitiesForPrefabLimit(Progenitor.Content.PrefabSyncBudget.Cap))", StringComparison.Ordinal);
        int fill = body.IndexOf("var reply = new ModList();", StringComparison.Ordinal);
        Assert.True(append >= 0 && fill >= 0 && append < fill,
            "the capability row must be appended before the packet is filled");
    }

    /// <summary>
    /// And the server reads it out of the answer beside the overflow row, before <c>answered</c>
    /// exists — so nothing downstream can mistake a reserved row for a plugin — and hands it to
    /// the deferral decision.
    /// </summary>
    [Fact]
    public void The_server_reads_the_capability_out_of_the_answer()
    {
        // Compat C3: the judgment moved into JudgeAnswer (it may run after the feature table hold).
        string body = SourcePin.MethodBody(HandshakeSource(), "private static unsafe void OnResponse(")
            + SourcePin.MethodBody(HandshakeSource(), "private static void JudgeAnswer(");

        SourcePin.AssertStatement(body, "var clientCaps = Capability.None;");
        SourcePin.AssertStatement(body, "clientCaps |= Compare.ReadCapabilityBlob(rec.Version);");
        Assert.Contains(
            "TryDeferKick(world, userEntity, user.Index, name, problems, failedIds, server, answered, clientCaps, localFixes)",
            body,
            StringComparison.Ordinal);
    }

    private static string[] Names(IReadOnlyList<Handshake.ModRecord> rows)
    {
        var names = new string[rows.Count];
        for (int i = 0; i < rows.Count; i++) names[i] = rows[i].Guid;
        return names;
    }

    /// <summary>
    /// Nothing to wait for: the server refused the client over a row it does not have a package
    /// for, so the window could not fix the join however long it ran. That kicks now, exactly as
    /// it did before FMAN18.
    /// </summary>
    [Fact]
    public void A_row_the_server_does_not_offer_still_kicks_immediately()
    {
        var server = new[]
        {
            Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave),
            Declared(Other, "2.0", NetworkCompatibilityLevel.ServerMustHave),
        };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        var verdict = Handshake.Compare.DecideMiss(
            problems,
            failedIds,
            server,
            client,
            f => string.Equals(f.Id, Sample, StringComparison.Ordinal) ? MissFix.Thunderstore : MissFix.None,
            graceAvailable: true);

        Assert.False(verdict.Defer);
        Assert.Empty(verdict.Ids);
        Assert.Contains(Other, verdict.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reverse walk: a row the <em>client</em> declares <c>ServerMustHave</c> that this server
    /// does not run. No package of ours can ever satisfy it, so a window would be a minute of
    /// waiting and then the same kick — even if some offer table answered "yes" for the id.
    /// </summary>
    [Fact]
    public void A_row_only_the_client_requires_is_never_deferred()
    {
        var server = Array.Empty<Handshake.ModRecord>();
        var client = new[] { Declared("client.only", "1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        Assert.NotEmpty(failedIds);
        Assert.False(Handshake.Compare
            .DecideMiss(problems, failedIds, server, client, _ => MissFix.Thunderstore, graceAvailable: true)
            .Defer);
    }

    /// <summary>
    /// The handshake cap. <c>OnResponse</c> inserts the overflow string into <c>problems</c> with
    /// no id behind it, and no download fixes a protocol failure — so a problem count that does
    /// not match the failing-id count refuses the deferral outright.
    /// </summary>
    [Fact]
    public void A_problem_with_no_mod_id_behind_it_refuses_the_deferral()
    {
        var server = new[] { Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);
        problems.Insert(0, Handshake.Compare.CapExceededReason(1));

        var verdict = Handshake.Compare.DecideMiss(
            problems, failedIds, server, client, _ => MissFix.Thunderstore, graceAvailable: true);

        Assert.False(verdict.Defer);
        Assert.Contains("nothing on the client fixes", verdict.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// Fail-closed, like every other decoration on this path: a throwing offer table costs the
    /// window, never the disconnect.
    /// </summary>
    [Fact]
    public void A_throwing_offer_table_refuses_the_deferral_instead_of_escaping()
    {
        var server = new[] { Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var client = Array.Empty<Handshake.ModRecord>();
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        Assert.False(Handshake.Compare.DecideMiss(
            problems,
            failedIds,
            server,
            client,
            _ => throw new InvalidOperationException("boom"),
            graceAvailable: true).Defer);
    }

    /// <summary>
    /// The second answer, still wrong. The grace is spent — the window it opened is what the
    /// client just spent — so there is no second deferral, and the kick is the ordinary readable
    /// mismatch reason this path has always produced, not the lapse text.
    /// </summary>
    [Fact]
    public void A_re_answer_that_still_fails_kicks_with_the_mismatch_reason()
    {
        var server = new[] { Declared(Sample, "0.1.0", NetworkCompatibilityLevel.ServerMustHave) };
        var client = new[] { Handshake.ModRecord.Undeclared(Sample, "0.0.9") };
        var problems = Handshake.Compare.Evaluate(server, client, out var failedIds);

        // First answer: deferred. Second: the grace is gone, so the same inputs kick.
        Assert.True(Handshake.Compare
            .DecideMiss(problems, failedIds, server, client, _ => MissFix.Thunderstore, graceAvailable: true).Defer);
        var second = Handshake.Compare
            .DecideMiss(problems, failedIds, server, client, _ => MissFix.Thunderstore, graceAvailable: false);
        Assert.False(second.Defer);

        string reason = Handshake.Compare.MismatchKickReason(problems);
        Assert.StartsWith(Handshake.Compare.MismatchPrefix, reason, StringComparison.Ordinal);
        Assert.Contains("expected 0.1.0, got 0.0.9", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not installed within", reason, StringComparison.Ordinal);
    }

    // ---- the state machine --------------------------------------------------------------------

    [Fact]
    public void One_window_per_join_and_it_is_never_extended()
    {
        var opened = _grace.Begin(1, "TestPlayer", new[] { Sample }, 60);
        Assert.True(opened.Opened);
        Assert.True(_grace.IsAwaiting(1));
        Assert.True(_grace.HasSpentGrace(1));

        // A second Begin while the first is still open does not re-arm it...
        Assert.Equal(HandshakeInstallGrace.Grant.AlreadyUsed, _grace.Begin(1, "TestPlayer", new[] { Sample }, 60).Verdict);

        // ...and neither does one after it is settled. That is what "one per join" means: a client
        // that keeps re-answering badly cannot hold a slot open for as long as it likes.
        Assert.True(_grace.Settle(1));
        Assert.Equal(HandshakeInstallGrace.Grant.AlreadyUsed, _grace.Begin(1, "TestPlayer", new[] { Sample }, 60).Verdict);
        Assert.False(_grace.IsAwaiting(1));

        // The window that was open really was fixed at 60 s from when it opened: re-Begin at
        // t = 59 could not have pushed it forward, because it never took.
        _now = 59;
        Assert.Equal(HandshakeInstallGrace.Grant.AlreadyUsed, _grace.Begin(1, "TestPlayer", new[] { Sample }, 60).Verdict);
    }

    [Fact]
    public void A_window_that_lapses_kicks_with_a_reason_that_names_it()
    {
        var opened = _grace.Begin(4, "TestPlayer", new[] { Sample }, 60);

        _now = 59.9;
        Assert.False(_grace.TryExpire(4, opened.Generation, out _));

        _now = 60;
        Assert.True(_grace.TryExpire(4, opened.Generation, out var due));
        Assert.Equal("TestPlayer", due.Name);
        Assert.Equal(new[] { Sample }, due.Ids);
        Assert.Contains(Sample, due.Reason, StringComparison.Ordinal);
        Assert.Contains("was not confirmed within 60 s", due.Reason, StringComparison.Ordinal);
        Assert.StartsWith(Handshake.Compare.MismatchPrefix, due.Reason, StringComparison.Ordinal);

        // Once expired it is gone: the delayed post cannot fire twice.
        Assert.False(_grace.TryExpire(4, opened.Generation, out _));
        Assert.False(_grace.IsAwaiting(4));
    }

    [Fact]
    public void A_settled_window_never_expires()
    {
        var opened = _grace.Begin(4, "TestPlayer", new[] { Sample }, 60);
        Assert.True(_grace.Settle(4));

        _now = 600;
        Assert.False(_grace.TryExpire(4, opened.Generation, out _));
    }

    [Fact]
    public void A_stale_delayed_post_cannot_expire_someone_elses_window()
    {
        var first = _grace.Begin(4, "TestPlayer", new[] { Sample }, 60);
        _grace.OnDisconnect(4);
        var second = _grace.Begin(4, "Rowan", new[] { Sample }, 60);
        Assert.NotEqual(first.Generation, second.Generation);

        _now = 600;
        Assert.False(_grace.TryExpire(4, first.Generation, out _));
        Assert.True(_grace.TryExpire(4, second.Generation, out var due));
        Assert.Equal("Rowan", due.Name);
    }

    /// <summary>
    /// Disconnect drops the window <em>and</em> the spent-grace mark, so a delayed post cannot
    /// kick a new occupant of the same <c>User.Index</c> and a genuine reconnect gets its own.
    /// </summary>
    [Fact]
    public void Disconnect_clears_the_state_including_the_spent_grace()
    {
        _grace.Begin(2, "TestPlayer", new[] { Sample }, 60);

        _grace.OnDisconnect(2);

        Assert.False(_grace.IsAwaiting(2));
        Assert.False(_grace.HasSpentGrace(2));
        Assert.Equal(0, _grace.AwaitingCount);
        Assert.True(_grace.Begin(2, "TestPlayer", new[] { Sample }, 60).Opened);
    }

    [Fact]
    public void Clear_drops_every_window()
    {
        _grace.Begin(1, "A", new[] { Sample }, 60);
        _grace.Begin(2, "B", new[] { Sample }, 60);
        Assert.Equal(2, _grace.AwaitingCount);

        _grace.Clear();

        Assert.Equal(0, _grace.AwaitingCount);
        Assert.False(_grace.HasSpentGrace(1));
    }

    [Fact]
    public void A_window_with_no_ids_is_refused()
    {
        Assert.Equal(
            HandshakeInstallGrace.Grant.NothingToWaitFor,
            _grace.Begin(1, "TestPlayer", Array.Empty<string>(), 60).Verdict);
        Assert.Equal(
            HandshakeInstallGrace.Grant.NothingToWaitFor,
            _grace.Begin(1, "TestPlayer", new[] { "", null! }, 60).Verdict);
    }

    // ---- what the window waits for -------------------------------------------------------------

    [Fact]
    public void The_window_remembers_the_ids_it_was_opened_for()
    {
        _grace.Begin(3, "TestPlayer", new[] { Sample }, 60);

        Assert.Equal(new[] { Sample }, _grace.IdsOf(3));
        Assert.Empty(_grace.IdsOf(9));
    }

    // ---- the watch token and the answer deadline -----------------------------------------------

    [Fact]
    public void The_users_op_reports_awaiting_install()
    {
        var watch = new HandshakeAnswerWatch(() => 0);
        watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, requiredList: "x");
        Assert.Equal("asked", watch.Token(1));

        watch.OnAwaitingInstall(1);

        Assert.Equal(HandshakeAnswerWatch.Status.AwaitingInstall, watch.GetStatus(1));
        Assert.Equal("awaiting-install", watch.Token(1));
        Assert.Equal("awaiting-install", HandshakeAnswerWatch.Token(HandshakeAnswerWatch.Status.AwaitingInstall));

        // Not an outcome — the answer that matters has not arrived — but it does cancel the
        // pending answer deadline, because TryExpire only fires on a Pending row.
        Assert.False(watch.HasAnswered(1));
        Assert.Equal(0, watch.PendingCount);
    }

    /// <summary>
    /// The defect this guards: a Core-generation re-ask during the window would <c>Arm</c> a fresh
    /// 20 s answer deadline over the row and kick the client a third of the way into its own 60 s
    /// window, with "no handshake answer" — for a client that had already answered.
    /// </summary>
    [Fact]
    public void A_re_ask_during_the_window_does_not_replace_it_with_a_20_second_deadline()
    {
        double now = 0;
        var watch = new HandshakeAnswerWatch(() => now);
        watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, requiredList: "x");
        watch.OnAwaitingInstall(1);

        var rearm = watch.Arm(1, "TestPlayer", 20, skipUser: false, serverRequiresAnswer: true, requiredList: "x");

        Assert.False(rearm.Scheduled);
        Assert.Equal(HandshakeAnswerWatch.Status.AwaitingInstall, watch.GetStatus(1));
        now = 100;
        Assert.False(watch.TryExpire(1, rearm.Generation, out _));
        Assert.Equal(0, watch.PendingCount);
    }

    /// <summary>
    /// An awaiting row is not exempt from the kick paths — the window's own lapse uses the ordinary
    /// mismatch route — but it is not on the could-not-ask route either, which belongs to a client
    /// that was never successfully asked.
    /// </summary>
    [Fact]
    public void An_awaiting_row_kicks_like_a_mismatch_and_never_like_a_could_not_ask()
    {
        Assert.True(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.AwaitingInstall, timeoutKick: false));
        Assert.False(Handshake.Compare.ShouldProceedWithKick(
            HandshakeAnswerWatch.Status.AwaitingInstall, timeoutKick: true));
        Assert.False(Handshake.Compare.ShouldScheduleCouldNotAskKick(
            HandshakeAnswerWatch.Status.AwaitingInstall));
        Assert.False(Handshake.Compare.ShouldDisconnectAfterCouldNotAskResult(
            HandshakeAnswerWatch.Status.AwaitingInstall, deadlineArmed: false));
    }

    // ---- the wire ------------------------------------------------------------------------------

    /// <summary>
    /// The backward-compatibility argument, held by a test rather than a comment. A client running
    /// today's Core reads the <see cref="Handshake.Result"/> packet as <c>r.Ok == 1</c> and
    /// nothing else, so <see cref="Handshake.ResultDeferred"/> lands in the existing else branch:
    /// it logs the message, opens its deny report, and is disconnected at the end of the window
    /// with the readable reason — which is what it does today, one minute later, and strictly more
    /// informative than the immediate kick it gets now.
    /// </summary>
    [Fact]
    public void A_deferred_result_is_not_ok_to_a_client_that_has_never_heard_of_it()
    {
        Assert.Equal(2, Handshake.ResultDeferred);
        Assert.NotEqual(1, Handshake.ResultDeferred);
        Assert.NotEqual(0, Handshake.ResultDeferred);

        // And the protocol version does not move: the packet, its size and its event id are all
        // unchanged, so no peer is kicked for a version it cannot have.
        Assert.Equal(2, Handshake.ProtocolVersion);
        Assert.Equal(0x50726851, Handshake.ResultEventId);
    }

    [Fact]
    public void The_deferred_message_says_what_is_needed_where_to_get_it_and_how_long()
    {
        string one = Handshake.Compare.DeferredResultMessage(new[] { Sample }, 60);

        Assert.Equal("Mods needed: " + Sample + ". Install from the join screens within 60 s, or this join is refused.", one);
        Assert.Equal(one, Handshake.Compare.TruncateReason(one));
        // Servers no longer send mod files: nothing in the sentence may promise a server copy.
        Assert.DoesNotContain("send", one, StringComparison.OrdinalIgnoreCase);

        string many = Handshake.Compare.DeferredResultMessage(new[] { Sample, Other }, 45);
        Assert.Contains(Sample + ", " + Other, many, StringComparison.Ordinal);
        Assert.Contains("45 s", many, StringComparison.Ordinal);
    }

    /// <summary>
    /// Operator-chosen ids again: both FMAN18 texts must fit the 479-character wire whatever they
    /// are called, and the sentence that matters must survive the cut.
    /// </summary>
    [Fact]
    public void Both_new_texts_fit_the_wire_for_any_ids()
    {
        var ids = new List<string>();
        for (int i = 0; i < 8; i++) ids.Add(new string((char)('a' + i), 63) + i);

        foreach (string text in new[]
        {
            Handshake.Compare.DeferredResultMessage(ids, 60),
            Handshake.Compare.NotInstalledReason(ids, 60),
        })
        {
            Assert.Equal(text, Handshake.Compare.TruncateReason(text));
            Assert.Contains("and 5 more", text, StringComparison.Ordinal);

            // Job FMAN20 (R50 finding 4). "Fits the wire" now means the encoder, not the managed
            // string's Length: build the bytes Result.Message actually carries and read them back.
            Assert.Equal(text, Handshake.Compare.WireMessageText(Handshake.Compare.WireMessageBytes(text)));
        }
    }

    /// <summary>
    /// Job FMAN20 (R50 finding 4). The deferred sentence shipped with a U+2014 em dash and
    /// <c>Encoding.ASCII</c>'s replacement fallback is <c>?</c>, so the one message a deferred
    /// player reads said "can send it now ? you have 60 s". Five assertions covered that string
    /// and every one of them stopped short of the character, because none of them reached the
    /// encoder — <c>Result</c> is a fixed-buffer struct compiled out of this build.
    ///
    /// <para>This is the general form of the check, not the specific one: any non-ASCII character
    /// in either text fails the round trip, whatever it is and wherever it lands.</para>
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    public void Every_handshake_wire_string_survives_the_encoder_byte_for_byte(int idCount)
    {
        var ids = new List<string>();
        for (int i = 0; i < idCount; i++) ids.Add("mod" + i.ToString(CultureInfo.InvariantCulture));

        foreach (string text in new[]
        {
            Handshake.Compare.DeferredResultMessage(ids, 60),
            Handshake.Compare.NotInstalledReason(ids, 60),
            Handshake.Compare.NoInstallWindowCapability,
        })
        {
            var bytes = Handshake.Compare.WireMessageBytes(text);
            Assert.Equal(text, Handshake.Compare.WireMessageText(bytes));
            Assert.DoesNotContain((byte)'?', bytes);
            for (int i = 0; i < bytes.Length; i++) Assert.InRange(bytes[i], (byte)0x20, (byte)0x7E);
        }
    }

    /// <summary>
    /// And the truncation the encoder inherits still holds: an over-long message is cut with the
    /// ellipsis and what comes back is exactly what went on the wire, not the original.
    /// </summary>
    [Fact]
    public void An_over_long_message_round_trips_as_its_truncated_self()
    {
        string huge = new string('x', Handshake.ResultMessageMaxChars + 40);
        var bytes = Handshake.Compare.WireMessageBytes(huge);

        Assert.Equal(Handshake.ResultMessageMaxChars, bytes.Length);
        Assert.Equal(Handshake.Compare.TruncateReason(huge), Handshake.Compare.WireMessageText(bytes));
        Assert.EndsWith(Handshake.ReasonEllipsis, Handshake.Compare.WireMessageText(bytes), StringComparison.Ordinal);
    }

    // ---- one grace per join, across a Core swap (job FMAN20, R50 finding 11) --------------------

    /// <summary>
    /// The predicate named "can arm a deadline" answered <b>yes</b> for the one status whose whole
    /// purpose is that no deadline may be armed over it. Inert only because
    /// <c>HandshakeAnswerWatch.Arm</c> is the real gate and <em>was</em> updated — two places
    /// encoding one rule with one of them updated, and the un-updated one is the one whose name
    /// implies it decides.
    /// </summary>
    [Fact]
    public void No_deadline_may_be_armed_over_an_awaiting_install_status()
    {
        Assert.False(Handshake.Compare.CanArmDeadline(HandshakeAnswerWatch.Status.AwaitingInstall));
        Assert.False(Handshake.Compare.CanArmDeadline(HandshakeAnswerWatch.Status.Ok));
        Assert.False(Handshake.Compare.CanArmDeadline(HandshakeAnswerWatch.Status.Mismatch));
        Assert.True(Handshake.Compare.CanArmDeadline(HandshakeAnswerWatch.Status.None));
        Assert.True(Handshake.Compare.CanArmDeadline(HandshakeAnswerWatch.Status.Pending));
    }

    /// <summary>
    /// A Core swap must not hand a still-connected player a second sixty seconds. The table itself
    /// cannot survive — it is a Core static and the incoming generation builds a fresh one — so the
    /// mark is re-derived from the connection: a user that was already connected when this
    /// generation installed brought its join with it and its one window is already spent.
    /// </summary>
    [Fact]
    public void A_join_carried_into_a_new_core_generation_has_already_spent_its_window()
    {
        // Generation 1: the player fails, gets a window, and the operator swaps Core.
        Assert.True(_grace.Begin(4, "TestPlayer", new[] { Sample }, 60).Opened);
        Assert.True(_grace.HasSpentGrace(4));

        var next = new HandshakeInstallGrace(() => _now);
        Assert.False(next.HasSpentGrace(4));

        // Install walks the connected users and spends their carried-over joins.
        Assert.True(next.NoteCarriedOverJoin(4));
        Assert.True(next.HasSpentGrace(4));
        Assert.False(next.IsAwaiting(4));
        Assert.Equal(
            HandshakeInstallGrace.Grant.AlreadyUsed,
            next.Begin(4, "TestPlayer", new[] { Sample }, 60).Verdict);

        // Idempotent, and only for the users that were there: a fresh joiner still gets its one.
        Assert.False(next.NoteCarriedOverJoin(4));
        Assert.True(next.Begin(5, "Newcomer", new[] { Sample }, 60).Opened);

        // And a real disconnect clears it, so the same index reused by someone else is not punished.
        next.OnDisconnect(4);
        Assert.False(next.HasSpentGrace(4));
    }

    /// <summary>
    /// The wiring, pinned in the source because it walks the server world: <c>Install</c> spends
    /// the carried-over joins <em>before</em> it re-asks anyone, so the re-ask already sees the
    /// mark.
    /// </summary>
    [Fact]
    public void Install_spends_carried_over_windows_before_it_re_asks()
    {
        string body = SourcePin.MethodBody(HandshakeSource(), "internal static unsafe void Install(");

        int mark = body.IndexOf("MarkCarriedOverInstallGrace();", StringComparison.Ordinal);
        int rearm = body.IndexOf("RearmConnectedUnanswered();", StringComparison.Ordinal);
        Assert.True(mark >= 0, "Install must spend carried-over install windows");
        Assert.True(mark < rearm, "the mark must be in place before the re-ask it applies to");

        string marker = SourcePin.MethodBody(HandshakeSource(), "private static void MarkCarriedOverInstallGrace(");
        SourcePin.AssertStatement(marker, "if (!user.IsConnected) continue;");
        Assert.Contains("InstallGrace.NoteCarriedOverJoin(user.Index)", marker, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lapse_reason_is_plural_aware_and_never_empty()
    {
        Assert.Contains(" was not confirmed within 60 s (",
            Handshake.Compare.NotInstalledReason(new[] { Sample }, 60), StringComparison.Ordinal);
        Assert.Contains(" were not confirmed within 30 s (",
            Handshake.Compare.NotInstalledReason(new[] { Sample, Other }, 30), StringComparison.Ordinal);
        Assert.Contains("the missing mods",
            Handshake.Compare.NotInstalledReason(Array.Empty<string>(), 60), StringComparison.Ordinal);
    }

    // ---- source pins: the call site really takes the new branch --------------------------------

    /// <summary>
    /// The FMAN15 lesson, applied to this job. <c>OnResponse</c> needs an <c>Entity</c> and a
    /// <c>ProjectM.Network.User</c>, so no test here can call it — and a suite that stays green
    /// with the fix reverted has not verified the fix. These pin the two things the call site
    /// alone owns: the deferral is consulted, and it is consulted <em>before</em> the kick reason
    /// is composed. <c>AssertStatement</c> rules out a commented-out or ternary-buried call.
    /// </summary>
    [Fact]
    public void OnResponse_asks_about_the_window_before_it_composes_a_kick()
    {
        // Compat C3: the judgment moved into JudgeAnswer (it may run after the feature table hold).
        string body = SourcePin.MethodBody(HandshakeSource(), "private static unsafe void OnResponse(")
            + SourcePin.MethodBody(HandshakeSource(), "private static void JudgeAnswer(");

        SourcePin.AssertStatement(body, "if (TryDeferKick(");
        SourcePin.AssertStatement(body, "bool wasAwaitingInstall = InstallGrace.Settle(user.Index);");

        int defer = body.IndexOf("TryDeferKick(", StringComparison.Ordinal);
        int compose = body.IndexOf("Compare.MismatchKickReason(", StringComparison.Ordinal);
        Assert.True(defer >= 0 && compose >= 0 && defer < compose,
            "the deferral must be consulted before the kick reason is composed");
    }

    /// <summary>
    /// The window is opened by one function that owns the whole rule, and the caller supplies only
    /// the four values it alone has. Same shape as FMAN14's answer to R45 finding 13.
    /// </summary>
    [Fact]
    public void The_deferral_decision_lives_in_Compare_and_not_at_the_call_site()
    {
        string body = SourcePin.MethodBody(HandshakeSource(), "private static bool TryDeferKick(");

        SourcePin.AssertStatement(body, "var verdict = Compare.DecideMiss(");
        Assert.Contains("return OpenInstallWindow(", body, StringComparison.Ordinal);

        // The window is never opened without the catalog that makes it usable.
        string open = SourcePin.MethodBody(HandshakeSource(), "private static bool OpenInstallWindow(");
        SourcePin.AssertStatement(open, "AnswerWatch.OnAwaitingInstall(userIndex);");
        Assert.Contains(
            "if (!ModManifest.TryBuildInstallDescriptor(userIndex, ids, joinFlowClient, out var descriptor) || descriptor == null)",
            open,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FMAN20 (R50 finding 8). <b>Sendability is decided before anything is sent.</b> The
    /// deferred <c>Result</c> used to go out first, so the three reachable ways
    /// <c>TryOpenInstallOffer</c> (now <c>TryBuildInstallOffer</c>) could fail — not server-side, the operator's deny-descriptor
    /// switch off, no rows after narrowing — each sent the client "you have 60 s to install it"
    /// and then the mismatch kick, in the same tick, and published two deny reports from them.
    ///
    /// <para>Pinned in the source because <c>Result</c> and <c>CustomEvents.SendToUser</c> are
    /// behind <c>!PROGENITOR_TESTS</c> and this ordering cannot be driven from here. Three facts:
    /// the build comes before the only <c>Result</c> send, the send of the catalog comes after
    /// it, and the failure path opens no window (no <c>InstallGrace.Begin</c> above it, so nothing
    /// to <c>Settle</c> and no grace spent).</para>
    /// </summary>
    [Fact]
    public void Nothing_is_sent_before_the_catalogue_is_known_to_be_sendable()
    {
        string body = SourcePin.MethodBody(HandshakeSource(), "private static bool OpenInstallWindow(");

        int build = body.IndexOf("ModManifest.TryBuildInstallDescriptor(", StringComparison.Ordinal);
        int begin = body.IndexOf("InstallGrace.Begin(", StringComparison.Ordinal);
        int result = body.IndexOf("Result.CreateDeferred(", StringComparison.Ordinal);
        int send = body.IndexOf("ModManifest.SendInstallDescriptor(", StringComparison.Ordinal);

        Assert.True(build >= 0, "the descriptor must be built");
        Assert.True(build < begin, "no window may be opened before the descriptor is known to exist");
        Assert.True(begin < result, "the Result must follow the grant it announces");
        Assert.True(result < send, "the Result still leads the catalog, as the client needs");

        // Exactly one Result leaves this method, and the failure path settles nothing because it
        // opened nothing.
        Assert.Equal(result, body.LastIndexOf("Result.CreateDeferred(", StringComparison.Ordinal));
        Assert.DoesNotContain("InstallGrace.Settle(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FMAN19 closes the loop across the two halves: the byte this server puts on a deferred
    /// <c>Result</c> is the byte the client reads as <em>deferred</em>. The two are written in
    /// different files a job apart, and nothing else in the suite stands them next to each other —
    /// so a change to either that did not change the other would land silently as "the client is
    /// kicked immediately again", which is exactly the defect FMAN18 was written to remove.
    /// </summary>
    [Fact]
    public void The_byte_this_server_defers_with_is_the_byte_the_client_reads_as_deferred()
    {
        Assert.Equal(HandshakeResultKind.Deferred, ModInstallWindowPolicy.Classify(Handshake.ResultDeferred));
        Assert.Equal(HandshakeResultKind.Ok, ModInstallWindowPolicy.Classify(1));
        Assert.Equal(HandshakeResultKind.Denied, ModInstallWindowPolicy.Classify(0));
    }

    /// <summary>
    /// The routing itself, pinned in the source, because the packet reader it lives in is compiled
    /// out of the test build (<c>Result</c> is a fixed-buffer struct behind
    /// <c>!PROGENITOR_TESTS</c>) and cannot be driven from here.
    ///
    /// <para>What must hold is that <c>OnResult</c> branches on the <em>kind</em> rather than on
    /// <c>r.Ok == 1</c>, and that the deferred branch does not open the deny report: that report's
    /// whole claim is that nothing the client does on this connection changes the outcome, which is
    /// precisely false during an install window, and it is also the flag
    /// <c>ModManifest.AcceptEnvelope</c> reads to decide whether the descriptor that follows goes
    /// to the read-only report or to the consent panel.</para>
    /// </summary>
    [Fact]
    public void The_deferred_branch_does_not_open_the_deny_report()
    {
        string src = HandshakeSource();
        int result = src.IndexOf("private static unsafe void OnResult(", StringComparison.Ordinal);
        Assert.True(result > 0, "OnResult moved; this pin needs updating");
        int end = src.IndexOf("private static void WarnMalformedOnce(", result, StringComparison.Ordinal);
        Assert.True(end > result, "OnResult's end moved; this pin needs updating");
        string body = src.Substring(result, end - result);

        Assert.Contains("ClientInstallWindow.Classify(r.Ok)", body);
        Assert.Contains("HandshakeResultKind.Deferred", body);
        Assert.Contains("ClientInstallWindow.OnResult(r.Ok, r.Text)", body);

        // One OnJoinDenied, and it is in the denied branch: after the deferred branch's own call.
        int deferred = body.IndexOf("ClientInstallWindow.OnResult(", StringComparison.Ordinal);
        int denied = body.IndexOf("ModManifestDeny.OnJoinDenied(", StringComparison.Ordinal);
        Assert.True(denied > deferred, "the deny report must not be opened on the deferred branch");
        Assert.Equal(
            denied,
            body.LastIndexOf("ModManifestDeny.OnJoinDenied(", StringComparison.Ordinal));
    }

    private static string HandshakeSource() => File.ReadAllText(Path.Combine(
        SourcePin.RepoRoot(), "src", "Progenitor.Core", "Networking", "Handshake.cs"));

    private static Handshake.ModRecord Declared(string id, string version, NetworkCompatibilityLevel level) =>
        Handshake.ModRecord.CreateDeclared(id, version, level, NetworkVersionRule.Exact);
}

/// <summary>
/// Job FMAN18, retargeted 2026-10-05: the half that touches <c>ModManifest</c>'s process-wide state. The window sends
/// the signed descriptor (so the client's join screens can name the Thunderstore packages), never a file: servers no
/// longer send mod files, and the window grants no fetch of any kind.
/// </summary>
[Collection("ModManifest")]
public sealed class InstallGraceDescriptorTests : IDisposable
{
    private const string Id = "progenitor.sample";

    private readonly string _dir;

    public InstallGraceDescriptorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-install-grace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        Handshake.InstallGrace.Clear();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        Handshake.InstallGrace.Clear();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// The descriptor really is sent: the same signed descriptor the deny path sends, and the same narrowing (the rows
    /// this client failed, not the operator's inventory).
    /// </summary>
    [Fact]
    public void Opening_the_window_sends_the_signed_descriptor()
    {
        ServeCatalog();

        Assert.True(ModManifest.TryBuildInstallDescriptor(5, new[] { Id }, false, out var descriptor));
        ModManifest.SendInstallDescriptor(5, descriptor);

        Assert.Equal(new[] { 5 }, ModManifest.TestDenyUsers);
        var envelope = Assert.IsType<ManifestEnvelope>(ModManifest.TestDenyEnvelope);
        Assert.Equal(Id, Assert.Single(envelope.Body.Mods).Id);
        Assert.Contains(
            Log().Infos,
            line => line.StartsWith("ModManifest: install window descriptor to user 5", StringComparison.Ordinal));
    }

    /// <summary>
    /// With the operator switch off there is no catalog, so the join screens could not name a package and the window
    /// must not open: <c>Handshake</c> reads the false and kicks now.
    /// </summary>
    [Fact]
    public void With_the_descriptor_switched_off_no_window_is_opened()
    {
        ServeCatalog();
        ModManifest.TestDenyDescriptorEnabled = false;
        try
        {
            Assert.False(ModManifest.TryBuildInstallDescriptor(5, new[] { Id }, false, out var descriptor));
            Assert.Null(descriptor);
            Assert.Empty(ModManifest.TestDenyUsers);
        }
        finally
        {
            ModManifest.TestDenyDescriptorEnabled = ModManifestDenyPolicy.DefaultEnabled;
        }
    }

    /// <summary>
    /// The window ends when the join is decided. After the passing re-answer the ok path runs once, and it is the
    /// ordinary path, reached exactly once because the deferral never ran it.
    /// </summary>
    [Fact]
    public void A_passing_re_answer_runs_the_ok_path_once()
    {
        ServeCatalog();
        Handshake.InstallGrace.Begin(8, "TestPlayer", new[] { Id }, 60);
        Assert.True(ModManifest.TryBuildInstallDescriptor(8, new[] { Id }, false, out var descriptor));
        ModManifest.SendInstallDescriptor(8, descriptor);
        Assert.Empty(ModManifest.TestOkHookUsers);

        Assert.True(Handshake.InstallGrace.Settle(8));
        Handshake.NotifyHandshakeOk(8);

        Assert.Equal(new[] { 8 }, ModManifest.TestOkHookUsers);
        Assert.Equal(8, ModManifest.TestLastHandshakeOkUser);
        Assert.Equal(new[] { "handshake", "config", "manifest" }, Handshake.TestOkHookOrder);
        Assert.False(Handshake.InstallGrace.IsAwaiting(8));
    }

    private void ServeCatalog()
    {
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo(Id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        ModManifest.TestIsServer = true;
        _ = ModManifest.Describe();
    }

    private static TestLogSource Log() => (TestLogSource)Plugin.Logger!;
}

/// <summary>
/// Job FMAN21 (R52 finding 3): a re-answer from a user that is already <c>ok</c> is an event.
///
/// <para>FMAN20 hung the "this client may have lost its layers" resend on an edge into the server's
/// own answer status. A <b>client-side</b> Core swap cannot produce that edge: the re-arm is
/// server-only, <c>Arm</c> returns early for a user already at <c>Ok</c>, and <c>OnAnswer</c>
/// writes <c>Ok</c> over <c>Ok</c>. The observable that does happen is the second
/// <c>DeclareSelf</c> itself, and this is the bound on acting on it — re-answering costs the client
/// a packet and costs the server a full layer walk per prefab, so a floor per user is the whole
/// difference between a repair and a lever.</para>
/// </summary>
public class HandshakeReAnswerTests
{
    [Fact]
    public void A_first_re_answer_from_a_user_is_admitted()
    {
        double now = 100;
        var limit = new HandshakeReAnswer(() => now);

        Assert.True(limit.TryAdmit(7));
    }

    [Fact]
    public void A_second_re_answer_inside_the_floor_is_dropped()
    {
        double now = 100;
        var limit = new HandshakeReAnswer(() => now);

        Assert.True(limit.TryAdmit(7));
        now += HandshakeReAnswer.MinSecondsBetween - 0.01;
        Assert.False(limit.TryAdmit(7));
    }

    /// <summary>
    /// And a refused attempt does not push the next chance forward, so a client hammering the
    /// socket still gets exactly one run per floor rather than none for ever.
    /// </summary>
    [Fact]
    public void A_refused_attempt_does_not_extend_the_floor()
    {
        double now = 100;
        var limit = new HandshakeReAnswer(() => now);

        Assert.True(limit.TryAdmit(7));
        for (int i = 0; i < 20; i++)
        {
            now += 0.1;
            Assert.False(limit.TryAdmit(7));
        }

        now = 100 + HandshakeReAnswer.MinSecondsBetween;
        Assert.True(limit.TryAdmit(7));
    }

    [Fact]
    public void The_floor_is_per_user_index()
    {
        double now = 100;
        var limit = new HandshakeReAnswer(() => now);

        Assert.True(limit.TryAdmit(7));
        Assert.True(limit.TryAdmit(8));
        Assert.False(limit.TryAdmit(7));
    }

    /// <summary>A reconnecting player must not wait out the previous occupant of its index.</summary>
    [Fact]
    public void A_disconnect_frees_the_index()
    {
        double now = 100;
        var limit = new HandshakeReAnswer(() => now);

        Assert.True(limit.TryAdmit(7));
        limit.OnDisconnect(7);
        Assert.True(limit.TryAdmit(7));
    }
}

/// <summary>
/// The composition above the bound: <c>Handshake.RunPostOkEffects</c>, which is the whole of what
/// an accepted answer does after the <c>Result</c> packet.
///
/// <para>Job FMAN22 (R53 finding 3). FMAN21 put the floor on the third of the three effects — the
/// edit-layer resend, which was also the only one already deduped per user per payload hash — and
/// left the synced-config snapshot and the signed catalog running once per inbound
/// <c>DeclareSelf</c> on the receive thread. These tests count all three: the resend through the
/// hook the production build fills with <c>PrefabRegistry.ResendEditLayersTo</c> (it lives in
/// <c>Content</c> and is not in this assembly), the config snapshot through
/// <c>ConfigSync.TestHandshakeOkCalls</c>, and the catalog through
/// <c>ModManifest.TestOkHookUsers</c>.</para>
/// </summary>
[Collection("Handshake")]
public class HandshakeReAnswerHookTests : IDisposable
{
    private readonly List<int> _resent = new();
    private double _now = 100;

    public HandshakeReAnswerHookTests()
    {
        Handshake.ResetTables();
        ModManifest.ResetForTests();
        ConfigSync.TestHandshakeOkCalls = 0;
        Plugin.Logger = new TestLogSource();
        Handshake.TestResendEditLayers = _resent.Add;
        Handshake.TestClock = () => _now;
        // The catalog push is server-side only, so without this the third effect is a no-op and
        // the test would be counting nothing.
        ModManifest.TestIsServer = true;
    }

    public void Dispose()
    {
        Handshake.ResetTables();
        ModManifest.TestIsServer = false;
        ModManifest.ResetForTests();
        ConfigSync.TestHandshakeOkCalls = 0;
        Plugin.Logger = null;
    }

    /// <summary>
    /// The packet pair the live call site composes, in the same order: the synced-config snapshot
    /// (counted by the config-sync fake) and then the signed catalog (counted by
    /// <c>ModManifest.TestOkHookUsers</c>).
    /// </summary>
    private static Action PostOkPackets(int userIndex) => () =>
    {
        ConfigSync.NotifyHandshakeOkForTests();
        ModManifest.OnHandshakeOk(userIndex);
    };

    private static int Catalogues => ModManifest.TestOkHookUsers.Count;

    [Fact]
    public void A_first_answer_runs_the_config_snapshot_and_the_catalogue_and_no_resend()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: false, PostOkPackets(3)));

        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);
        Assert.Equal(1, Catalogues);
        Assert.Empty(_resent);

        // And it did not spend the floor: a re-answer at the very same instant is still admitted.
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        Assert.Equal(new[] { 3 }, _resent);
    }

    [Fact]
    public void A_re_answer_from_an_ok_user_runs_all_three_effects_once()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));

        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);
        Assert.Equal(1, Catalogues);
        Assert.Equal(new[] { 3 }, _resent);
    }

    /// <summary>
    /// The finding itself: a second re-answer inside the floor must push no synced-config
    /// snapshot, which is the expensive one and the one FMAN21 left unbounded.
    /// </summary>
    [Fact]
    public void A_second_re_answer_inside_the_floor_pushes_nothing()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);

        for (int i = 0; i < 20; i++)
        {
            _now += 0.1;
            Assert.False(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        }

        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);
        Assert.Equal(1, Catalogues);
        Assert.Equal(new[] { 3 }, _resent);
    }

    [Fact]
    public void A_re_answer_after_the_floor_pushes_exactly_one_more()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        _now += 1;
        Assert.False(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));

        _now = 100 + HandshakeReAnswer.MinSecondsBetween;
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));

        Assert.Equal(2, ConfigSync.TestHandshakeOkCalls);
        Assert.Equal(2, Catalogues);
        Assert.Equal(new[] { 3, 3 }, _resent);
    }

    /// <summary>
    /// A join must never be made to wait for its own config because someone else's re-answer, or
    /// its own earlier one, spent a floor. The first answer neither consults the bound nor spends
    /// it.
    /// </summary>
    [Fact]
    public void The_first_answer_still_pushes_its_snapshot_with_the_bound_spent()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        _now += 0.5;
        Assert.False(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);

        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: false, PostOkPackets(3)));

        Assert.Equal(2, ConfigSync.TestHandshakeOkCalls);
        Assert.Equal(2, Catalogues);
    }

    [Fact]
    public void The_floor_is_per_user_index()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        Assert.True(Handshake.RunPostOkEffects(4, wasAlreadyOk: true, PostOkPackets(4)));
        Assert.False(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));

        Assert.Equal(2, ConfigSync.TestHandshakeOkCalls);
        Assert.Equal(new[] { 3, 4 }, _resent);
    }

    /// <summary>
    /// The bound is on a repeated <c>ok</c>, not on a change of state. A client whose rows changed
    /// answers into the mismatch branch, which never reaches this method — and once its status is
    /// off <c>Ok</c> the next accepted answer comes back through here as a first answer and is not
    /// held by the floor its earlier re-answers spent.
    /// </summary>
    [Fact]
    public void A_changed_outcome_is_handled_immediately_however_spent_the_floor_is()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        _now += 0.1;
        Assert.False(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));

        // The client's rows changed: OnResponse takes the mismatch branch instead, which records
        // the outcome and kicks without asking the bound anything.
        Handshake.AnswerWatch.OnMismatch(3, "Mod mismatch: sample");
        Assert.Equal(HandshakeAnswerWatch.Status.Mismatch, Handshake.AnswerWatch.GetStatus(3));
        Assert.Equal("Mod mismatch: sample", Handshake.AnswerWatch.MismatchReasonOf(3));

        // And back: the status is no longer Ok, so the next accepted answer is a first answer.
        _now += 0.1;
        Assert.True(Handshake.RunPostOkEffects(
            3,
            wasAlreadyOk: Handshake.AnswerWatch.GetStatus(3) == HandshakeAnswerWatch.Status.Ok,
            PostOkPackets(3)));
        Assert.Equal(2, ConfigSync.TestHandshakeOkCalls);
    }

    /// <summary>A disconnect frees the index, so a reconnect does not wait out its predecessor.</summary>
    [Fact]
    public void A_disconnect_frees_the_index()
    {
        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        Handshake.ReAnswers.OnDisconnect(3);

        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
        Assert.Equal(2, ConfigSync.TestHandshakeOkCalls);
    }

    /// <summary>
    /// A throw out of the packet pair costs the pair and not the resend, and a throw out of the
    /// resend still counts as a run — the same rule FMAN21 gave the resend, now over the set.
    /// </summary>
    [Fact]
    public void A_throwing_effect_does_not_cost_the_resend()
    {
        Handshake.TestResendEditLayers = _ => throw new InvalidOperationException("resend boom");

        Assert.True(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, () =>
        {
            ConfigSync.NotifyHandshakeOkForTests();
            throw new InvalidOperationException("config boom");
        }));

        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);
        // The throw came after the snapshot and before the catalog, so the catalog is the one
        // it cost; the run itself still happened and still spent the floor.
        Assert.Equal(0, Catalogues);
        _now += 0.1;
        Assert.False(Handshake.RunPostOkEffects(3, wasAlreadyOk: true, PostOkPackets(3)));
    }
}
