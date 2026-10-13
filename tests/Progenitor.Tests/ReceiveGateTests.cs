using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Content;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Threat model surfaces 1 and 6 (jobs EQ7, EQ8).
///
/// Surface 1: <c>CustomEvents.TryDispatchServer</c> makes exactly ONE call into the gate —
/// <see cref="ReceiveGate.Admit"/> — and then obeys the verdict. R28 finding 29 named the previous
/// shape: these tests carried a hand-written reimplementation of the dispatcher's ordering, so
/// every ordering assertion pinned the fake. There is no fake ordering here; every test below
/// drives the production decision, with only the approved-user lookup faked
/// (<see cref="FakeResolver"/>), because that one step needs game types.
///
/// Surface 6: a hostile server could push unbounded <c>PrfD</c>/<c>PrfK</c> at a client, each one
/// appending a row to <c>ItemKeyLookup</c>. <see cref="PrefabSyncBudget"/> is the per-join cap the
/// client receive handlers charge before anything is cloned or appended.
/// </summary>
public class ReceiveGateTests
{
    private const int PrhR = 0x50726852;

    /// <summary>The approved-user table, faked. Everything else under test is production code.</summary>
    private sealed class FakeResolver : IReceiveResolver
    {
        internal readonly Dictionary<long, ulong> Rows = new();
        internal int Calls;

        internal FakeResolver Approve(long connection, ulong platformId)
        {
            Rows[connection] = platformId;
            return this;
        }

        public bool TryResolve(long connection, out ulong platformId)
        {
            Calls++;
            return Rows.TryGetValue(connection, out platformId) && platformId != 0;
        }
    }

    private sealed class ThrowingResolver : IReceiveResolver
    {
        public bool TryResolve(long connection, out ulong platformId) =>
            throw new NullReferenceException("approved row has a null NetConnectionId");
    }

    private static ReceiveVerdict Admit(
        ReceiveGate gate,
        long connection,
        bool userResolved,
        double now,
        List<string> logs,
        ulong platformId = 7,
        int payloadBytes = 8)
    {
        var resolver = new FakeResolver();
        if (userResolved) resolver.Approve(connection, platformId);
        var verdict = gate.Admit(connection, PrhR, payloadBytes, now, resolver, out var log);
        if (log != null) logs.Add(log);
        return verdict;
    }

    // ---- surface 1: the dispatcher's decision --------------------------------------------------

    [Fact]
    public void A_resolved_connection_dispatches()
    {
        var gate = new ReceiveGate();
        var logs = new List<string>();
        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0x99, userResolved: true, now: 5.0, logs));
        Assert.Empty(logs);
    }

    /// <summary>
    /// The bucket follows the PLAYER, not the connection handle. R28 finding 3: the budget was
    /// keyed on the raw <c>NetConnectionId</c> pointer, a value the game guarantees nothing about
    /// (it is an abstract class with no fields; the game itself keys its own dictionaries by
    /// content-based <c>Equals</c>/<c>GetHashCode</c>). If the wrapper is ever re-boxed per
    /// callback — or simply recycled — a pointer-keyed limiter mints a fresh full bucket per
    /// packet and silently never limits. Keyed on the approved row's platform id it cannot.
    /// This test fails on the pointer-keyed gate: there, 500 distinct handles dispatch 500 times.
    /// </summary>
    [Fact]
    public void A_churning_connection_handle_cannot_mint_a_fresh_bucket_for_the_same_player()
    {
        var gate = new ReceiveGate { Burst = 10, PerSecond = 2 };
        var logs = new List<string>();
        int dispatched = 0;

        // Same player (platform id 4242), a different connection handle every single packet.
        for (int i = 0; i < 500; i++)
        {
            if (Admit(gate, 0x1000 + i, userResolved: true, now: 100.0, logs, platformId: 4242) == ReceiveVerdict.Dispatch)
                dispatched++;
        }

        Assert.Equal(10, dispatched);
        Assert.Equal(1, gate.TrackedSenders);
        Assert.Contains(logs, l => l.Contains("rate limit tripped for player 4242", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_connection_handles_for_one_player_share_one_bucket()
    {
        var gate = new ReceiveGate { Burst = 2, PerSecond = 1 };
        var logs = new List<string>();

        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0xA, true, 1.0, logs, platformId: 5));
        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0xB, true, 1.0, logs, platformId: 5));
        Assert.Equal(ReceiveVerdict.Drop, Admit(gate, 0xC, true, 1.0, logs, platformId: 5));

        // A different player still has their own budget.
        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0xD, true, 1.0, logs, platformId: 6));
        Assert.Equal(2, gate.TrackedSenders);
        Assert.Equal(4, gate.TrackedConnections);
    }

    [Fact]
    public void A_flood_is_limited_to_the_burst_and_logs_once_per_window()
    {
        var gate = new ReceiveGate { Burst = 10, PerSecond = 2 };
        var logs = new List<string>();
        int dispatched = 0;

        // All at the same instant: no refill, so exactly Burst packets get through.
        for (int i = 0; i < 500; i++)
            if (Admit(gate, 0x7, true, 100.0, logs, platformId: 11) == ReceiveVerdict.Dispatch) dispatched++;

        Assert.Equal(10, dispatched);
        Assert.Single(
            logs,
            l => l == "CustomEvents: rate limit tripped for player 11; dropping Progenitor packets "
                + "from it (2/s, burst 10). One line per 60 s.");

        // One second later the bucket has PerSecond tokens back, and no more.
        int before = logs.Count;
        for (int i = 0; i < 500; i++)
            if (Admit(gate, 0x7, true, 101.0, logs, platformId: 11) == ReceiveVerdict.Dispatch) dispatched++;
        Assert.Equal(12, dispatched);
        Assert.Equal(before, logs.Count);
    }

    /// <summary>
    /// R28 finding 21. EQ7 dropped every packet from a connection with no approved-user row.
    /// Native analysis says a real join cannot land there (<c>ServerNetworkLayer.OnDataReceived</c>
    /// only forwards to our seam in <c>UserConnectState.Connected</c>, and the transition into it
    /// fires <c>OnUserConnected</c>, itself only reachable after <c>ApproveClient</c> wrote the
    /// row) — but the hosted-child row is documented as carrying a null <c>NetConnectionId</c>,
    /// which no <c>Equals</c> can match. So the rule fails safe: hold briefly, then refuse.
    /// Fails on EQ7's gate, which had no hold state at all.
    /// </summary>
    [Fact]
    public void A_first_seen_unresolved_connection_is_held_and_only_refused_after_the_grace_window()
    {
        var gate = new ReceiveGate();
        var logs = new List<string>();

        Assert.Equal(ReceiveVerdict.Queue, Admit(gate, 0x1234, userResolved: false, now: 100.0, logs));
        Assert.Empty(logs);
        Assert.Equal(1, gate.QueuedPackets);

        // The caller releases the hold when it dispatches or expires it.
        gate.NoteDequeued(0x1234, 8);
        Assert.Equal(0, gate.QueuedPackets);

        double past = 100.0 + ReceiveGate.UnresolvedGraceSeconds + 1.0;
        Assert.Equal(ReceiveVerdict.Drop, Admit(gate, 0x1234, userResolved: false, now: past, logs));
        Assert.Single(logs);
        Assert.Contains("refusing event PrhR from unresolved connection 0x1234", logs[0], StringComparison.Ordinal);
        Assert.Contains("no handler ran", logs[0], StringComparison.Ordinal);

        // One line per window, never one per packet.
        for (int i = 0; i < 20; i++)
            Assert.Equal(ReceiveVerdict.Drop, Admit(gate, 0x1234, userResolved: false, now: past + i * 0.01, logs));
        Assert.Single(logs);
        Assert.Equal(ReceiveVerdict.Drop, Admit(gate, 0x1234, false, past + ReceiveGate.LogWindowSeconds + 1, logs));
        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public void The_hold_queue_is_bounded_per_connection_and_in_total()
    {
        var gate = new ReceiveGate();
        var logs = new List<string>();

        for (int i = 0; i < ReceiveGate.MaxQueuedPerConnection; i++)
            Assert.Equal(ReceiveVerdict.Queue, Admit(gate, 0x55, false, 10.0, logs));
        Assert.Equal(ReceiveVerdict.Drop, Admit(gate, 0x55, false, 10.0, logs));
        Assert.Single(logs);
        Assert.Contains("hold queue full", logs[0], StringComparison.Ordinal);

        // Total cap, across connections.
        var wide = new ReceiveGate();
        var wideLogs = new List<string>();
        int queued = 0;
        for (int i = 0; i < ReceiveGate.MaxQueuedPackets * 2; i++)
            if (Admit(wide, 0x9000 + i, false, 10.0, wideLogs) == ReceiveVerdict.Queue) queued++;
        Assert.Equal(ReceiveGate.MaxQueuedPackets, queued);
        Assert.Equal(ReceiveGate.MaxQueuedPackets, wide.QueuedPackets);

        // Byte cap.
        var fat = new ReceiveGate();
        var fatLogs = new List<string>();
        Assert.Equal(ReceiveVerdict.Queue, Admit(fat, 0x1, false, 10.0, fatLogs, payloadBytes: ReceiveGate.MaxQueuedBytes));
        Assert.Equal(ReceiveVerdict.Drop, Admit(fat, 0x2, false, 10.0, fatLogs, payloadBytes: 1));
    }

    /// <summary>
    /// A resolver that throws — the shape a hosted child's null <c>NetConnectionId</c> produces
    /// inside <c>TryFindUser</c> — must be treated as "unresolved", never propagate.
    /// </summary>
    [Fact]
    public void A_throwing_resolver_is_treated_as_unresolved()
    {
        var gate = new ReceiveGate();
        var verdict = gate.Admit(0x77, PrhR, 8, 1.0, new ThrowingResolver(), out var log);
        Assert.Equal(ReceiveVerdict.Queue, verdict);
        Assert.Null(log);
    }

    /// <summary>
    /// R29 finding 3, and the test that used to assert the bug. EQ8 charged one server-wide bucket
    /// FIRST, for every packet, before the per-sender limiter. This same scenario — 200 distinct,
    /// approved, well-behaved senders each sending ONE packet — then dispatched exactly
    /// <c>burst x GlobalFactor</c> = 32 of them and dropped the other 168 as "server-wide budget
    /// exhausted", and the old test asserted that number as correct. Nothing here misbehaves and
    /// nothing here may be dropped: the backstop is sized per connected player, so the honest
    /// population always fits inside it.
    ///
    /// <para>Fails on the EQ8 gate: 32 dispatched, 168 legitimate senders dropped, and the
    /// server-wide refusal line present.</para>
    /// </summary>
    [Fact]
    public void The_global_backstop_never_drops_a_sender_inside_its_own_allowance()
    {
        var gate = new ReceiveGate { Burst = 2, PerSecond = 1 };
        var logs = new List<string>();
        int dispatched = 0;
        int resolverCalls = 0;

        for (int i = 0; i < 200; i++)
        {
            var resolver = new FakeResolver();
            resolver.Approve(0x2000 + i, (ulong)(1000 + i));
            var verdict = gate.Admit(0x2000 + i, PrhR, 8, 500.0, resolver, out var log);
            if (log != null) logs.Add(log);
            resolverCalls += resolver.Calls;
            if (verdict == ReceiveVerdict.Dispatch) dispatched++;
        }

        Assert.Equal(200, dispatched);
        Assert.Equal(200, resolverCalls);
        Assert.DoesNotContain(logs, l => l.Contains("server-wide Progenitor receive budget exhausted", StringComparison.Ordinal));
        Assert.Empty(logs);
    }

    /// <summary>
    /// R29 finding 3, the hostile half. One client at line rate used to drain the shared global
    /// bucket before its own per-sender bucket was ever consulted, so every other player's packets
    /// were dropped at stage 1 with a log line that named nobody — and the per-sender limiter, the
    /// entire point of the gate, never ran. Fair share: the flooder is dropped against its OWN
    /// allowance first, so its flood never charges the backstop and cannot take an honest player's
    /// packets with it.
    ///
    /// <para>Fails on the EQ8 gate: with burst 2 the global bucket held 32 tokens, the flooder's
    /// first 32 packets emptied it, and the honest player got <c>Drop</c> with the server-wide
    /// line instead of <c>Dispatch</c>.</para>
    /// </summary>
    [Fact]
    public void One_flooder_cannot_take_an_honest_players_packets_with_it()
    {
        var gate = new ReceiveGate { Burst = 2, PerSecond = 1 };
        var logs = new List<string>();

        int flooderDispatched = 0;
        for (int i = 0; i < 500; i++)
            if (Admit(gate, 0xF10D, true, 900.0, logs, platformId: 66) == ReceiveVerdict.Dispatch)
                flooderDispatched++;

        // The flooder gets exactly its own burst and not one packet more.
        Assert.Equal(2, flooderDispatched);
        Assert.Contains(logs, l => l.Contains("rate limit tripped for player 66", StringComparison.Ordinal));

        // ...and the honest player, arriving after 498 refusals, still gets its whole allowance.
        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0x900D, true, 900.0, logs, platformId: 77));
        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0x900D, true, 900.0, logs, platformId: 77));
        Assert.DoesNotContain(logs, l => l.Contains("server-wide Progenitor receive budget exhausted", StringComparison.Ordinal));
    }

    /// <summary>
    /// The backstop is sized so the honest population can never exhaust it: every tracked player
    /// contributes <c>GlobalPerPlayer</c> whole per-sender budgets while it can spend at most one.
    /// That inequality is the fair-share rule, so it is asserted as an inequality rather than as
    /// the arithmetic restated (R29 finding 22.2).
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(60, 0)]
    [InlineData(60, 1)]
    [InlineData(60, 40)]
    [InlineData(60, 256)]
    public void The_backstop_is_sized_per_connected_player(int burst, int players)
    {
        int capacity = ReceiveGate.GlobalCapacity(burst, players);
        int refill = ReceiveGate.GlobalRefill(burst, players);

        // Strictly more than the whole population's own budgets, with a floor when nobody is on.
        Assert.True(capacity > burst * players, "capacity " + capacity + " must exceed " + (burst * players));
        Assert.True(refill > burst * players);
        Assert.True(capacity >= burst * ReceiveGate.GlobalFloorPlayers);
        // And it really does grow with the population, rather than being a fixed multiple — up to
        // the tracked-table cap, past which there are no more players to size for.
        if (players < ReceiveGate.MaxTrackedConnections)
            Assert.True(ReceiveGate.GlobalCapacity(burst, players + 1) > capacity);
        else
            Assert.Equal(capacity, ReceiveGate.GlobalCapacity(burst, players + 1));
    }

    /// <summary>
    /// The backstop is not decorative. Unidentified connections churning handles get a fresh
    /// pre-approval bucket each time, so nothing per-sender stops them: this is the traffic the
    /// backstop exists for, and it trips with its own line.
    /// </summary>
    [Fact]
    public void The_backstop_still_stops_traffic_no_per_sender_bucket_accounts_for()
    {
        var gate = new ReceiveGate { Burst = 1, PerSecond = 1 };
        var logs = new List<string>();
        int refused = 0;

        // 300 distinct, never-resolving handles at one instant: no player bucket ever applies.
        for (int i = 0; i < 300; i++)
            if (Admit(gate, 0x30000 + i, userResolved: false, now: 800.0, logs) == ReceiveVerdict.Drop) refused++;

        Assert.True(refused > 0);
        Assert.Contains(logs, l => l.Contains("server-wide Progenitor receive budget exhausted", StringComparison.Ordinal));
        Assert.Single(logs, l => l.Contains("server-wide Progenitor receive budget exhausted", StringComparison.Ordinal));
    }

    /// <summary>
    /// R29 finding 21.1. The "one drop line per 60 s per sender" budget was stored on the
    /// CONNECTION ROW, so it was really one line per connection handle: a peer that re-boxes its
    /// handle bought a fresh log allowance every packet, and the log is itself a DoS vector (threat
    /// model section 14). It lives on the bucket now, which on the resolved path is the player's.
    ///
    /// <para>Fails on the EQ8 gate: 499 rate-limit lines instead of one.</para>
    /// </summary>
    [Fact]
    public void The_rate_limit_log_budget_follows_the_player_not_the_handle()
    {
        // Burst 40 keeps the backstop out of it (its capacity is 40 x 18 with one player tracked),
        // so the only thing that can drop these 500 packets is the player's own bucket.
        var gate = new ReceiveGate { Burst = 40, PerSecond = 1 };
        var logs = new List<string>();

        for (int i = 0; i < 500; i++)
            Admit(gate, 0x40000 + i, userResolved: true, now: 700.0, logs, platformId: 4242);

        Assert.Single(logs);
        Assert.Contains("rate limit tripped for player 4242", logs[0], StringComparison.Ordinal);
        Assert.DoesNotContain(logs, l => l.Contains("server-wide", StringComparison.Ordinal));
    }

    /// <summary>
    /// R29 finding 21.3. <c>NoteDequeued</c> subtracted whatever byte count the caller passed. The
    /// production callers happen to pass the right number, but nothing enforced it, and a caller
    /// that passed the wire length instead of the copy length would drift <c>_queuedBytes</c>
    /// upward until the hold queue was permanently "full" — the same wedge as finding 14, reached
    /// by arithmetic. The gate releases one of the charges IT made.
    ///
    /// <para>Fails on the EQ8 gate at both asserts: the short release leaves 992 bytes charged
    /// forever, and <c>Forget</c> released the packet count but never the bytes.</para>
    /// </summary>
    [Fact]
    public void The_hold_byte_budget_only_moves_by_amounts_the_gate_itself_charged()
    {
        var gate = new ReceiveGate();
        var logs = new List<string>();

        Assert.Equal(ReceiveVerdict.Queue, Admit(gate, 0xB1, false, 10.0, logs, payloadBytes: 1000));
        Assert.Equal(1000, gate.QueuedBytes);

        // A caller that under-reports must not leak the difference.
        gate.NoteDequeued(0xB1, 8);
        Assert.Equal(0, gate.QueuedBytes);
        Assert.Equal(0, gate.QueuedPackets);

        // A row dropped whole releases its bytes too, and a release for a row that is already gone
        // does not subtract a second time.
        Assert.Equal(ReceiveVerdict.Queue, Admit(gate, 0xB2, false, 10.0, logs, payloadBytes: 1000));
        Assert.Equal(1000, gate.QueuedBytes);
        gate.Forget(0xB2);
        Assert.Equal(0, gate.QueuedBytes);
        Assert.Equal(0, gate.QueuedPackets);
        gate.NoteDequeued(0xB2, 1000);
        Assert.Equal(0, gate.QueuedBytes);

        // The budget is intact: a full-size hold still fits.
        Assert.Equal(
            ReceiveVerdict.Queue,
            Admit(gate, 0xB3, false, 10.0, logs, payloadBytes: ReceiveGate.MaxQueuedBytes));
    }

    /// <summary>
    /// R28 finding 3: a limiter that never limits, or a table churning to its cap, has to be
    /// visible. Nothing in EQ7 wrote a line at any threshold.
    /// </summary>
    [Fact]
    public void The_metric_line_reports_tracked_connections_and_drops_once_per_window()
    {
        var gate = new ReceiveGate { Burst = 1, PerSecond = 1 };
        var logs = new List<string>();

        Assert.False(gate.TryTakeMetricLine(0.0, out _));

        Admit(gate, 0xA1, true, 1000.0, logs, platformId: 1);
        Admit(gate, 0xA1, true, 1000.0, logs, platformId: 1); // dropped: burst is 1
        Admit(gate, 0xA2, true, 1000.0, logs, platformId: 2);

        Assert.False(gate.TryTakeMetricLine(1000.0, out _));
        Assert.True(gate.TryTakeMetricLine(1000.0 + ReceiveGate.MetricWindowSeconds, out var line));
        Assert.Equal("ReceiveGate: 2 connections tracked, 1 dropped this window", line);
        Assert.Equal(ReceiveGate.MetricLine(2, 1), line);

        // Counters reset with the window; a window with no packets writes nothing.
        Assert.False(gate.TryTakeMetricLine(1000.0 + ReceiveGate.MetricWindowSeconds * 3, out _));
    }

    /// <summary>
    /// R28 finding 28.3: <c>Forget</c> had no production caller, so a disconnecting player's
    /// bucket survived until the idle sweep or an eviction. The bucket is keyed on the platform id
    /// now and <c>ModManifest.OnPlayerDisconnected</c> retires it.
    /// </summary>
    [Fact]
    public void ForgetPlatform_retires_the_players_bucket_and_its_connection_rows()
    {
        var gate = new ReceiveGate { Burst = 1, PerSecond = 1 };
        var logs = new List<string>();

        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0xE1, true, 1.0, logs, platformId: 900));
        Assert.Equal(ReceiveVerdict.Drop, Admit(gate, 0xE1, true, 1.0, logs, platformId: 900));
        Assert.Equal(1, gate.TrackedSenders);

        gate.ForgetPlatform(900);
        Assert.Equal(0, gate.TrackedSenders);
        Assert.Equal(0, gate.TrackedConnections);

        // A reconnect starts fresh instead of inheriting the tail of the last session.
        Assert.Equal(ReceiveVerdict.Dispatch, Admit(gate, 0xE2, true, 1.0, logs, platformId: 900));
    }

    [Fact]
    public void The_connection_table_is_bounded_and_not_merely_empty()
    {
        var gate = new ReceiveGate();
        var logs = new List<string>();
        for (int i = 0; i < ReceiveGate.MaxTrackedConnections * 2; i++)
            Admit(gate, i + 1, true, 1.0, logs, platformId: (ulong)(i + 1));

        Assert.True(gate.TrackedConnections > 0);
        Assert.True(gate.TrackedConnections <= ReceiveGate.MaxTrackedConnections);
        Assert.True(gate.TrackedSenders > 0);
        Assert.True(gate.TrackedSenders <= ReceiveGate.MaxTrackedConnections);

        gate.Clear();
        Assert.Equal(0, gate.TrackedConnections);
        Assert.Equal(0, gate.TrackedSenders);
    }

    [Fact]
    public void Burst_and_rate_clamp_to_at_least_one()
    {
        var gate = new ReceiveGate { Burst = 0, PerSecond = -5 };
        Assert.Equal(1, gate.Burst);
        Assert.Equal(1, gate.PerSecond);
    }

    [Fact]
    public void HandlerMustRefuse_is_true_for_a_null_user_entity()
    {
        // default(User) is Index 0 with a null entity: that is the shape surface 1 described.
        Assert.True(ReceiveGate.HandlerMustRefuse(userEntityIsNull: true, userIndex: 0));
        Assert.True(ReceiveGate.HandlerMustRefuse(userEntityIsNull: true, userIndex: 7));
        Assert.True(ReceiveGate.HandlerMustRefuse(userEntityIsNull: false, userIndex: -1));
        Assert.False(ReceiveGate.HandlerMustRefuse(userEntityIsNull: false, userIndex: 0));
        Assert.Equal(
            "Handshake: PrhR from an unresolved connection (no user entity); ignoring.",
            ReceiveGate.HandlerRefusedLog("Handshake", ReceiveGate.Fourcc(PrhR)));
    }

    [Fact]
    public void Fourcc_renders_the_wire_tag()
    {
        Assert.Equal("PrhR", ReceiveGate.Fourcc(0x50726852));
        Assert.Equal("PrmR", ReceiveGate.Fourcc(0x50726D52));
        Assert.Equal("PrfD", ReceiveGate.Fourcc(0x50726644));
        Assert.Equal("0x00000001", ReceiveGate.Fourcc(1));
    }

    /// <summary>
    /// The two server-side handlers live inside <c>#if !PROGENITOR_TESTS</c> (they take game
    /// types), so the guard call itself is pinned by source. R27 finding 15: a source grep is only
    /// worth anything against a file that is really the shipped one — both paths below are read
    /// from <c>src/</c> at test time, not from a copy.
    /// </summary>
    [Theory]
    [InlineData("src/Progenitor.Core/Networking/Handshake.cs", "OnResponse")]
    public void Every_server_receive_handler_refuses_an_unresolved_sender(string relativePath, string handler)
    {
        var text = File.ReadAllText(RepoFile(relativePath));
        int at = text.IndexOf(" " + handler + "(", StringComparison.Ordinal);
        Assert.True(at > 0, handler + " not found in " + relativePath);
        // The guard must be inside the handler, before anything else: look at the first 1200
        // characters of its body.
        var body = text.Substring(at, Math.Min(1200, text.Length - at));
        SourcePin.AssertUncommented(body, "ReceiveGate.HandlerMustRefuse(");
        SourcePin.AssertUncommented(body, "ReceiveGate.HandlerRefusedLog(");
    }

    // ---- the hold queue's own policy (R29 findings 14 and 22.4) --------------------------------

    private sealed class FakeHeld
    {
        internal int Id;
    }

    /// <summary>
    /// R29 finding 14. <c>DrainHeld</c> snapshots the hold list, clears it, and re-holds anything
    /// still unresolved and inside its grace window. A held packet's handler can trigger a Core
    /// reload, so <c>UninstallServerReceive</c> — and therefore the detach — is reachable from the
    /// middle of that loop. Nothing coordinated the two: the detach cleared an already-emptied list
    /// and the loop's remaining iterations re-added their packets afterwards, into a list with no
    /// per-frame subscription behind it. Those packets were never drained and their gate accounting
    /// was never released, so <c>_queuedPackets</c> stuck at its cap (every genuinely first-seen
    /// connection refused for the life of the process) and their rows, carrying
    /// <c>Queued &gt; 0</c>, were skipped by both passes of <c>PruneConnections</c> and could never
    /// be evicted either.
    ///
    /// <para>R29 finding 22.4 is the reason this is testable at all: the list, the attach flag and
    /// the generation were three statics inside <c>CustomEvents</c>, which is <c>unsafe</c> and
    /// needs game types. They are <see cref="ReceiveHoldQueue{T}"/> now.</para>
    ///
    /// <para>Fails on the EQ8 shape, which had no generation at all: the re-hold always succeeded,
    /// so <c>Count</c> here would be 2 with nothing subscribed to drain them.</para>
    /// </summary>
    [Fact]
    public void A_drain_detached_mid_pass_drops_its_remaining_packets_instead_of_re_holding_them()
    {
        var held = new ReceiveHoldQueue<FakeHeld>();
        Assert.True(held.TryAttach());
        Assert.False(held.TryAttach()); // never double-subscribes
        held.Add(new FakeHeld { Id = 1 });
        held.Add(new FakeHeld { Id = 2 });
        held.Add(new FakeHeld { Id = 3 });

        var pending = held.BeginDrain(out int generation);
        Assert.Equal(3, pending.Length);
        Assert.Equal(0, held.Count);

        // Packet 1's handler triggers a Core reload: the drain is detached under this pass.
        var stranded = held.Detach(out bool wasAttached);
        Assert.True(wasAttached);
        Assert.Empty(stranded); // the pass already took them
        Assert.False(held.Attached);

        // The rest of the pass must not park its packets in a list nothing drains.
        Assert.False(held.TryRequeue(pending[1], generation));
        Assert.False(held.TryRequeue(pending[2], generation));
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public void A_drain_that_was_not_detached_re_holds_normally()
    {
        var held = new ReceiveHoldQueue<FakeHeld>();
        held.TryAttach();
        held.Add(new FakeHeld { Id = 1 });

        var pending = held.BeginDrain(out int generation);
        Assert.Single(pending);
        Assert.True(held.TryRequeue(pending[0], generation));
        Assert.Equal(1, held.Count);
        Assert.True(held.Attached);
    }

    [Fact]
    public void Detach_hands_back_what_it_was_holding_so_the_caller_can_release_it()
    {
        var held = new ReceiveHoldQueue<FakeHeld>();
        held.TryAttach();
        held.Add(new FakeHeld { Id = 1 });
        held.Add(new FakeHeld { Id = 2 });

        var stranded = held.Detach(out bool wasAttached);
        Assert.True(wasAttached);
        Assert.Equal(2, stranded.Length);
        Assert.Equal(0, held.Count);
        Assert.False(held.Attached);

        // A detach with nothing subscribed says so, and still bumps the generation.
        int generation = held.Generation;
        Assert.Empty(held.Detach(out bool second));
        Assert.False(second);
        Assert.NotEqual(generation, held.Generation);
    }

    /// <summary>
    /// A Core swap that re-installs immediately must not let a pass from the PREVIOUS generation
    /// hand its packets to the new drain: their gate accounting belongs to the old one.
    /// </summary>
    [Fact]
    public void A_reattached_drain_does_not_adopt_the_previous_passes_packets()
    {
        var held = new ReceiveHoldQueue<FakeHeld>();
        held.TryAttach();
        held.Add(new FakeHeld { Id = 1 });
        var pending = held.BeginDrain(out int generation);

        held.Detach(out _);
        Assert.True(held.TryAttach());

        Assert.False(held.TryRequeue(pending[0], generation));
        Assert.Equal(0, held.Count);
    }

    [Fact]
    public void The_detached_drop_is_one_line_for_the_batch()
    {
        Assert.Equal(
            "CustomEvents: dropping 4 held packet(s); the receive drain was detached "
            + "(Core swap or uninstall).",
            ReceiveHoldQueue<FakeHeld>.DetachedDropLog(4));
    }

    /// <summary>
    /// R29 finding 13. The dispatcher did <c>new NetConnectionId(ptr)</c> and
    /// <c>new ServerBootstrapSystem(self)</c> unconditionally, on every packet with a registered
    /// fourcc, BEFORE <c>Admit</c> — two managed allocations plus two IL2CPP GC-handle
    /// registrations on the game main thread, whatever the verdict. That is exactly the work the
    /// gate exists to bound. <c>TryDispatchServer</c> is <c>unsafe</c> and takes game types, so
    /// this is a source pin and says so; it is worth having because the property it names — that
    /// nothing is wrapped ahead of the decision — has no other guard.
    /// </summary>
    [Fact]
    public void No_il2cpp_wrapper_is_allocated_before_the_gate_decides()
    {
        var text = File.ReadAllText(RepoFile("src/Progenitor.Core/Networking/CustomEvents.cs"));
        int at = text.IndexOf("private static bool TryDispatchServer(", StringComparison.Ordinal);
        Assert.True(at > 0);
        int admit = text.IndexOf("ServerReceiveGate.Admit(", at, StringComparison.Ordinal);
        Assert.True(admit > at);

        var beforeTheDecision = text.Substring(at, admit - at);
        Assert.DoesNotContain("new NetConnectionId(", beforeTheDecision, StringComparison.Ordinal);
        Assert.DoesNotContain("new ServerBootstrapSystem(", beforeTheDecision, StringComparison.Ordinal);
        // Raw pointers are what the resolver is handed; it wraps them itself, at stage 3.
        SourcePin.AssertStatement(beforeTheDecision, "Resolver.SelfPtr = self;");
        SourcePin.AssertStatement(beforeTheDecision, "Resolver.ConnectionPtr = netConnectionId;");
        // And the bootstrap wrapper is built once per world, not once per packet.
        SourcePin.AssertStatement(text, "private static ServerBootstrapSystem? CachedBootstrap(IntPtr self)");
    }

    /// <summary>
    /// R28 finding 29: the dispatcher must not carry an ordering of its own for these tests to
    /// pin. It takes the gate's verdict and does one of three things; every rule lives in
    /// <see cref="ReceiveGate.Admit"/>, which the tests above call directly. This grep therefore
    /// asserts the ABSENCE of a second decision path, not the presence of one — R29 finding 22.5
    /// is right that it cannot fail behaviorally, and the behavioral coverage it was standing in
    /// for (the hold list and its drain policy) now exists above, against
    /// <see cref="ReceiveHoldQueue{T}"/>.
    /// </summary>
    [Fact]
    public void The_server_dispatcher_makes_exactly_one_gate_decision()
    {
        var text = File.ReadAllText(RepoFile("src/Progenitor.Core/Networking/CustomEvents.cs"));
        int at = text.IndexOf("private static bool TryDispatchServer(", StringComparison.Ordinal);
        Assert.True(at > 0);
        var body = text.Substring(at);
        int end = body.IndexOf("\n    }", StringComparison.Ordinal);
        Assert.True(end > 0);
        body = body.Substring(0, end);

        Assert.Equal(1, CountOf(body, "ServerReceiveGate.Admit("));
        Assert.DoesNotContain("ServerReceiveGate.TryTake(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerReceiveGate.RefuseUnresolved(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EventHooks.TryFindUser(", body, StringComparison.Ordinal);
        SourcePin.AssertUncommented(body, "ReceiveVerdict.Drop");
        SourcePin.AssertUncommented(body, "ReceiveVerdict.Queue");
        SourcePin.AssertUncommented(body, "TryTakeMetricLine(");

        // The only resolution path is the resolver the gate calls.
        var whole = text;
        SourcePin.AssertUncommented(whole, "Events.EventHooks.TryFindUser(bootstrap, id, out var entity, out var user)");
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        int at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            n++;
            at += needle.Length;
        }
        return n;
    }

    // ---- surface 6: per-join prefab cap -------------------------------------------------------

    /// <summary>
    /// R28 finding 29.7: <c>Assert.Equal(256, DefaultMaxPerJoin)</c> asserted a constant against
    /// itself. What matters is that the default is a real, bounded, effective cap.
    /// </summary>
    [Fact]
    public void The_default_cap_is_bounded_and_actually_caps()
    {
        Assert.InRange(PrefabSyncBudget.DefaultMaxPerJoin, 1, 4096);
        PrefabSyncBudget.Cap = PrefabSyncBudget.DefaultMaxPerJoin;
        PrefabSyncBudget.BeginJoin(out _);
        try
        {
            int accepted = 0;
            for (int i = 0; i < PrefabSyncBudget.DefaultMaxPerJoin * 2; i++)
                if (PrefabSyncBudget.TryAdmitDefinition(i, out _)) accepted++;
            Assert.Equal(PrefabSyncBudget.DefaultMaxPerJoin, accepted);
        }
        finally
        {
            PrefabSyncBudget.Cap = PrefabSyncBudget.DefaultMaxPerJoin;
            PrefabSyncBudget.Reset();
        }
    }

    [Fact]
    public void Definitions_and_placeholders_share_one_per_join_cap_and_log_once()
    {
        PrefabSyncBudget.Cap = 4;
        PrefabSyncBudget.BeginJoin(out _);
        try
        {
            var logs = new List<string>();
            int accepted = 0;
            for (int i = 0; i < 1000; i++)
            {
                bool ok = (i % 2 == 0)
                    ? PrefabSyncBudget.TryAdmitDefinition(i, out var log)
                    : PrefabSyncBudget.TryAdmitPlaceholder(i, out log);
                if (!ok && log != null) logs.Add(log);
                if (ok) accepted++;
            }

            Assert.Equal(4, accepted);
            Assert.Equal(4, PrefabSyncBudget.Accepted);
            Assert.Equal(2, PrefabSyncBudget.Definitions);
            Assert.Equal(2, PrefabSyncBudget.Placeholders);
            Assert.Single(logs);
            Assert.Equal(
                "PrefabRegistry: server exceeded the per-join cap of 4 synced prefab row(s) "
                + "(2 definition(s), 2 key placeholder(s) accepted); dropping new prefab identities "
                + "until the next join. Raise Networking.MaxSyncedPrefabsPerJoin only for a server you trust.",
                logs[0]);
        }
        finally
        {
            PrefabSyncBudget.Cap = PrefabSyncBudget.DefaultMaxPerJoin;
            PrefabSyncBudget.Reset();
        }
    }

    [Fact]
    public void A_new_join_restores_the_budget_and_reports_the_previous_one()
    {
        PrefabSyncBudget.Cap = 2;
        PrefabSyncBudget.BeginJoin(out _);
        try
        {
            Assert.True(PrefabSyncBudget.TryAdmitDefinition(1, out _));
            Assert.True(PrefabSyncBudget.TryAdmitPlaceholder(2, out _));
            Assert.False(PrefabSyncBudget.TryAdmitDefinition(3, out var capLog));
            Assert.NotNull(capLog);

            PrefabSyncBudget.BeginJoin(out var summary);
            Assert.Equal(
                "PrefabRegistry: server added 1 definition(s) and 1 key placeholder(s) in the previous join.",
                summary);
            Assert.Equal(0, PrefabSyncBudget.Accepted);

            // The refusal line is armed again for the new join.
            Assert.True(PrefabSyncBudget.TryAdmitDefinition(1, out _));
            Assert.True(PrefabSyncBudget.TryAdmitDefinition(2, out _));
            Assert.False(PrefabSyncBudget.TryAdmitDefinition(3, out var second));
            Assert.NotNull(second);

            PrefabSyncBudget.BeginJoin(out var none);
            Assert.NotNull(none);
            PrefabSyncBudget.BeginJoin(out var empty);
            Assert.Null(empty);
        }
        finally
        {
            PrefabSyncBudget.Cap = PrefabSyncBudget.DefaultMaxPerJoin;
            PrefabSyncBudget.Reset();
        }
    }

    [Fact]
    public void At_capacity_retirement_and_redefinition_reuse_identity_but_new_ids_do_not()
    {
        PrefabSyncBudget.Cap = 256;
        PrefabSyncBudget.BeginJoin(out _);
        try
        {
            for (int i = 0; i < 255; i++)
                Assert.True(PrefabSyncBudget.TryAdmitDefinition(i, out _));
            Assert.True(PrefabSyncBudget.TryAdmitPlaceholder(255, out _));
            for (int i = 0; i < 247; i++)
            {
                Assert.True(PrefabSyncBudget.TryAdmitPlaceholder(i, out _));
                Assert.True(PrefabSyncBudget.TryAdmitDefinition(i, out _));
            }
            Assert.Equal(256, PrefabSyncBudget.Accepted);
            Assert.False(PrefabSyncBudget.TryAdmitDefinition(256, out var log));
            Assert.NotNull(log);
            // A new-id refusal does not poison updates to already admitted rows.
            Assert.True(PrefabSyncBudget.TryAdmitDefinition(255, out _));
            Assert.Equal(256, PrefabSyncBudget.Accepted);
            PrefabSyncBudget.BeginJoin(out _);
            Assert.True(PrefabSyncBudget.TryAdmitDefinition(256, out _));
        }
        finally
        {
            PrefabSyncBudget.Cap = PrefabSyncBudget.DefaultMaxPerJoin;
            PrefabSyncBudget.Reset();
        }
    }

    [Fact]
    public void The_cap_clamps_to_at_least_one()
    {
        try
        {
            PrefabSyncBudget.Cap = 0;
            Assert.Equal(1, PrefabSyncBudget.Cap);
        }
        finally
        {
            PrefabSyncBudget.Cap = PrefabSyncBudget.DefaultMaxPerJoin;
            PrefabSyncBudget.Reset();
        }
    }

    /// <summary>
    /// The client receive handlers must charge the budget and validate the id before anything is
    /// cloned or appended. Those handlers take game types and are not in the test assembly.
    /// </summary>
    [Theory]
    [InlineData("OnClientDefinition", "TryAdmitDefinition")]
    [InlineData("OnClientKeyPlaceholder", "TryAdmitPlaceholder")]
    public void The_client_prefab_handlers_validate_and_charge_the_budget(string handler, string admit)
    {
        var text = File.ReadAllText(RepoFile("src/Progenitor.Core/Content/PrefabRegistry.cs"));
        int at = text.IndexOf(" " + handler + "(int eventId", StringComparison.Ordinal);
        Assert.True(at > 0, handler + " not found");
        var body = text.Substring(at, Math.Min(3000, text.Length - at));
        SourcePin.AssertUncommented(body, "PrefabWire.TryValidateAscii(");
        SourcePin.AssertUncommented(body, "PrefabGUID.CalculateGuidHash(");
        SourcePin.AssertUncommented(body, "PrefabSyncBudget." + admit + "(");
        SourcePin.AssertStatement(body, "BeginSyncBudgetJoin();");
    }

    private static string RepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && dir != null; i++)
        {
            var candidate = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("could not locate " + relative + " above " + AppContext.BaseDirectory);
    }
}
