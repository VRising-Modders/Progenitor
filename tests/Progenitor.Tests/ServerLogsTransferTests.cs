using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Progenitor.Support;
using Xunit;

namespace Progenitor.Tests;

/// <summary>SUPPORT1 remote server logs: the 'PrSL' wire, the server's paced sender and the client's fetch. Game-free fakes.</summary>
public sealed class ServerLogsTransferTests : IDisposable
{
    private const ulong Admin = 76561198000000042;
    private const int UserIndex = 7;

    private readonly string _base;
    private readonly string _root;

    public ServerLogsTransferTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "progenitor-serverlogs-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "game", "BepInEx");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SupportActions.ResetForTests();
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static byte[] Random(int length, int seed = 3)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Runs one whole transfer over a fake link: the server's pump, the client's packets and acknowledgements and the
    /// client's tick, on a fake clock. <paramref name="refuse"/> makes the transport refuse a server packet (not sent);
    /// <paramref name="lose"/> makes a sent server packet vanish; <paramref name="loseAck"/> makes a client
    /// acknowledgement vanish; <paramref name="tamper"/> changes a packet on the way.
    /// </summary>
    private static ServerLogsOutcome? Run(ServerLogsServer server, ServerLogsClient client, byte[] zip, ref double now,
        Func<byte[], byte[]>? tamper = null, List<int>? packetsPerPump = null, Func<byte[], bool>? refuse = null,
        Func<byte[], bool>? lose = null, Func<byte[], bool>? loseAck = null, double maxSeconds = 300)
    {
        ServerLogsOutcome? outcome = null;
        double t = now;
        byte[] request = client.Begin(t, o => outcome = o)!;
        Assert.True(ServerLogsWire.TryDecode(request, out var req));
        Assert.Equal(ServerLogsWire.KindRequest, req.Kind);

        byte[] answer = server.Begin(Admin, UserIndex, req.TransferId, isAdmin: true, t)!;
        client.Accept(answer, t);
        Assert.Null(server.Ready(Admin, req.TransferId, zip, t));

        void Deliver(byte[]? reply, double at)
        {
            if (reply == null || (loseAck != null && loseAck(reply))) return;
            if (ServerLogsWire.TryDecode(reply, out var r) && r.Kind == ServerLogsWire.KindAck)
                server.Ack(Admin, r.TransferId, r.Index, at);
        }

        double nextTick = t;
        while ((outcome == null || client.NeedsTicks || server.ActiveCount > 0) && t - now < maxSeconds)
        {
            t += ServerLogsServer.PumpSeconds;
            int count = server.Pump(t, (_, _) => true, (index, packet) =>
            {
                Assert.Equal(UserIndex, index);
                if (refuse != null && refuse(packet)) return false;
                if (lose != null && lose(packet)) return true;
                byte[] p = tamper != null ? tamper(packet) : packet;
                Deliver(client.Accept(p, t), t);
                return true;
            });
            packetsPerPump?.Add(count);
            if (t >= nextTick)
            {
                Deliver(client.Tick(t, connected: true), t);
                nextTick = t + 0.5;
            }
        }
        now = t;
        return outcome;
    }

    private static bool IsChunk(byte[] packet, int index) =>
        ServerLogsWire.TryDecode(packet, out var p) && p.Kind == ServerLogsWire.KindChunk && p.Index == index;

    [Fact]
    public void Every_packet_round_trips_and_malformed_ones_are_refused()
    {
        var sha = ServerLogsWire.Sha256Of(new byte[] { 1, 2, 3 });
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeRequest(5), out var p) && p.Kind == ServerLogsWire.KindRequest && p.TransferId == 5);
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeAck(5, 9), out p) && p.Kind == ServerLogsWire.KindAck && p.Index == 9);
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeCancel(-2), out p) && p.Kind == ServerLogsWire.KindCancel && p.TransferId == -2);
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeAccepted(5), out p) && p.Kind == ServerLogsWire.KindAccepted);
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeStart(5, 3, 1, sha), out p));
        Assert.Equal((3, 1), (p.Total, p.Chunks));
        Assert.Equal(sha, p.Sha256);
        byte[] zip = Random(ServerLogsWire.MaxChunkBody + 10);
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeChunk(5, 1, zip), out p));
        Assert.Equal(1, p.Index);
        Assert.Equal(zip.Skip(ServerLogsWire.MaxChunkBody).ToArray(), p.Body);
        Assert.True(ServerLogsWire.EncodeChunk(5, 0, zip).Length <= ServerLogsWire.MaxPacketBytes);
        Assert.True(ServerLogsWire.TryDecode(ServerLogsWire.EncodeRefuse(5, ServerLogsRefusal.NotAdmin, "nope"), out p));
        Assert.Equal((ServerLogsRefusal.NotAdmin, "nope"), (p.Refusal, p.Message));
        Assert.True(ServerLogsWire.EncodeRefuse(5, ServerLogsRefusal.Failed, new string('é', 1000)).Length <= ServerLogsWire.HeaderBytes + 1 + ServerLogsWire.MaxMessageBytes);

        var bad = ServerLogsWire.EncodeRequest(1);
        bad[0] = 2; // another version
        Assert.False(ServerLogsWire.TryDecode(bad, out _));
        Assert.False(ServerLogsWire.TryDecode(new byte[] { 1, 99, 0, 0, 0, 0 }, out _)); // unknown kind
        Assert.False(ServerLogsWire.TryDecode(ServerLogsWire.EncodeAck(1, 1).Take(8).ToArray(), out _)); // short
        Assert.False(ServerLogsWire.TryDecode(new byte[ServerLogsWire.MaxPacketBytes + 1], out _));
        var refuse = ServerLogsWire.EncodeRefuse(1, ServerLogsRefusal.Failed, "x");
        refuse[^1] = 0xFF; // not UTF-8
        Assert.False(ServerLogsWire.TryDecode(refuse, out _));
        Assert.Throws<ArgumentException>(() => ServerLogsWire.EncodeStart(1, 1, 1, new byte[3]));
    }

    [Fact]
    public void A_bundle_reassembles_exactly_paced_and_windowed()
    {
        var server = new ServerLogsServer();
        var client = new ServerLogsClient();
        byte[] zip = Random(ServerLogsWire.MaxChunkBody * 50 + 123);
        var perPump = new List<int>();
        double now = 0;
        var outcome = Run(server, client, zip, ref now, packetsPerPump: perPump);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Ok, outcome.Reason);
        Assert.True(outcome.Verify());
        Assert.Equal(zip, outcome.Zip);
        Assert.Equal(0, server.ActiveCount);
        Assert.False(client.Busy);
        Assert.All(perPump, n => Assert.True(n <= ServerLogsServer.PacketsPerPump));
    }

    [Fact]
    public void A_packet_the_transport_refuses_is_sent_again_on_the_next_pump()
    {
        var server = new ServerLogsServer();
        var client = new ServerLogsClient();
        byte[] zip = Random(ServerLogsWire.MaxChunkBody * 10);
        int refusals = 0;
        double now = 0;
        var outcome = Run(server, client, zip, ref now, refuse: p =>
        {
            if (IsChunk(p, 3) && refusals < 2) { refusals++; return true; }
            return ServerLogsWire.TryDecode(p, out var s) && s.Kind == ServerLogsWire.KindStart && refusals++ == 0;
        });
        Assert.True(outcome!.Ok, outcome.Reason);
        Assert.True(outcome.Verify());
        Assert.Equal(zip, outcome.Zip);
        Assert.Equal(0, server.ActiveCount);
    }

    [Fact]
    public void A_lost_chunk_is_resent_from_the_last_acknowledgement()
    {
        var log = new List<string>();
        var server = new ServerLogsServer { Log = log.Add };
        var client = new ServerLogsClient();
        byte[] zip = Random(ServerLogsWire.MaxChunkBody * 60);
        bool lost = false;
        double now = 0;
        var outcome = Run(server, client, zip, ref now, lose: p =>
        {
            if (!lost && IsChunk(p, 5)) { lost = true; return true; }
            return false;
        });
        Assert.True(lost);
        Assert.True(outcome!.Ok, outcome.Reason);
        Assert.True(outcome.Verify());
        Assert.Equal(zip, outcome.Zip);
        Assert.Contains(log, l => l.Contains(" sent: ") && l.Contains("resend"));
        Assert.InRange(now, 0, ServerLogsServer.StallSeconds); // recovered well before the stall limit
    }

    [Fact]
    public void A_lost_acknowledgement_is_said_again_and_the_transfer_completes()
    {
        var server = new ServerLogsServer();
        var client = new ServerLogsClient();
        byte[] zip = Random(ServerLogsWire.MaxChunkBody * 60);
        int acks = 0;
        double now = 0;
        // Lose the first five acknowledgements (the window fills), then the first final one.
        var outcome = Run(server, client, zip, ref now, loseAck: a =>
        {
            if (!ServerLogsWire.TryDecode(a, out var p) || p.Kind != ServerLogsWire.KindAck) return false;
            acks++;
            return acks <= 5 || p.Index == 60 && acks < 100 && !_finalSeen && (_finalSeen = true);
        });
        Assert.True(outcome!.Ok, outcome.Reason);
        Assert.Equal(zip, outcome.Zip);
        Assert.Equal(0, server.ActiveCount); // completed from a repeated final acknowledgement
        Assert.InRange(now, 0, ServerLogsServer.StallSeconds);
    }

    private bool _finalSeen;

    [Fact]
    public void The_final_acknowledgement_is_repeated_three_times()
    {
        var client = new ServerLogsClient();
        var req = client.Begin(0, _ => { })!;
        ServerLogsWire.TryDecode(req, out var r);
        byte[] zip = Random(100);
        client.Accept(ServerLogsWire.EncodeStart(r.TransferId, zip.Length, 1, ServerLogsWire.Sha256Of(zip)), 0);
        var first = client.Accept(ServerLogsWire.EncodeChunk(r.TransferId, 0, zip), 0);
        Assert.True(ServerLogsWire.TryDecode(first!, out var ack) && ack.Kind == ServerLogsWire.KindAck && ack.Index == 1);
        Assert.False(client.Busy);
        Assert.True(client.NeedsTicks);
        Assert.Null(client.Tick(0.2, true));
        Assert.NotNull(client.Tick(0.5, true));
        Assert.NotNull(client.Tick(1.0, true));
        Assert.Null(client.Tick(1.5, true));
        Assert.False(client.NeedsTicks);
    }

    [Fact]
    public void Without_acknowledgements_the_server_stops_a_window_ahead_and_then_drops_the_stalled_transfer()
    {
        var log = new List<string>();
        var server = new ServerLogsServer { Log = log.Add };
        Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin, UserIndex, 1, true, 0)!, out var a) && a.Kind == ServerLogsWire.KindAccepted);
        server.Ready(Admin, 1, Random(ServerLogsWire.MaxChunkBody * 100), 0);
        var highest = -1;
        double t = 0;
        for (int i = 0; i < 30; i++) // 3 s: the window fills, and a go-back-N resend never goes past it either
        {
            t += ServerLogsServer.PumpSeconds;
            server.Pump(t, (_, _) => true, (_, p) =>
            {
                if (ServerLogsWire.TryDecode(p, out var c) && c.Kind == ServerLogsWire.KindChunk) highest = Math.Max(highest, c.Index);
                return true;
            });
        }
        Assert.Equal(ServerLogsServer.WindowChunks - 1, highest); // never past the window without an acknowledgement
        server.Pump(ServerLogsServer.StallSeconds + 1, (_, _) => true, (_, _) => true);
        Assert.Equal(0, server.ActiveCount);
        Assert.Contains(log, l => l.Contains("dropped: no acknowledgement"));
        Assert.DoesNotContain(log, l => l.Contains(Admin.ToString()));
        Assert.Contains(log, l => l.Contains("admin …0042"));
    }

    [Fact]
    public void A_tampered_chunk_fails_the_SHA_256_check()
    {
        var server = new ServerLogsServer();
        var client = new ServerLogsClient();
        byte[] zip = Random(ServerLogsWire.MaxChunkBody * 3);
        double now = 0;
        var outcome = Run(server, client, zip, ref now, tamper: packet =>
        {
            if (IsChunk(packet, 1))
            {
                var copy = (byte[])packet.Clone();
                copy[^1] ^= 0x55;
                return copy;
            }
            return packet;
        });
        Assert.NotNull(outcome);
        Assert.False(outcome!.Verify());
        Assert.False(outcome.Ok);
        Assert.Null(outcome.Zip);
        Assert.Contains("SHA-256", outcome.Reason);
    }

    [Fact]
    public void The_build_drops_a_server_zip_whose_hash_does_not_match()
    {
        var request = Request();
        request.ServerZip = ServerZip();
        request.ServerZipSha256 = new byte[32];
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.False(result.ServerPartIncluded);
        Assert.Contains("SHA-256", result.ServerPartMissing);
        Assert.DoesNotContain(Read(result.Path).Keys, k => k.StartsWith("server/", StringComparison.Ordinal));

        var good = Request();
        good.NowLocal = good.NowLocal.AddSeconds(1);
        good.ServerZip = ServerZip();
        good.ServerZipSha256 = ServerLogsWire.Sha256Of(good.ServerZip);
        Assert.True(SupportBundle.Build(good).ServerPartIncluded);
    }

    [Fact]
    public void A_chunk_of_the_wrong_length_ends_the_fetch_with_a_cancel()
    {
        var client = new ServerLogsClient();
        ServerLogsOutcome? outcome = null;
        var req = client.Begin(0, o => outcome = o)!;
        ServerLogsWire.TryDecode(req, out var r);
        byte[] zip = Random(100);
        client.Accept(ServerLogsWire.EncodeStart(r.TransferId, zip.Length, 1, ServerLogsWire.Sha256Of(zip)), 0);
        var reply = client.Accept(ServerLogsWire.EncodeChunk(r.TransferId, 0, Random(50)), 0);
        Assert.True(ServerLogsWire.TryDecode(reply!, out var cancel) && cancel.Kind == ServerLogsWire.KindCancel);
        Assert.False(outcome!.Ok);
    }

    [Fact]
    public void A_player_who_is_not_an_admin_is_refused_with_the_reason()
    {
        var log = new List<string>();
        var server = new ServerLogsServer { Log = log.Add };
        var client = new ServerLogsClient();
        ServerLogsOutcome? outcome = null;
        var req = client.Begin(0, o => outcome = o)!;
        ServerLogsWire.TryDecode(req, out var r);
        var answer = server.Begin(Admin, UserIndex, r.TransferId, isAdmin: false, 0)!;
        client.Accept(answer, 0);
        Assert.False(outcome!.Ok);
        Assert.Equal("you are not an admin of this server", outcome.Reason);
        Assert.Equal(0, server.ActiveCount);
        Assert.Contains(log, l => l.Contains("refused: NotAdmin"));
    }

    [Fact]
    public void Refusals_are_rate_limited_per_sender_and_counted()
    {
        var log = new List<string>();
        var server = new ServerLogsServer { Log = log.Add };
        Assert.NotNull(server.Begin(Admin, UserIndex, 1, isAdmin: false, 0));
        // A spamming client still gets every refusal (so it can say why), but only the first one is logged.
        for (int i = 0; i < 50; i++)
        {
            Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin, UserIndex, 2 + i, isAdmin: false, 0.05 * i), out var held));
            Assert.Equal(ServerLogsRefusal.NotAdmin, held.Refusal);
        }
        Assert.Single(log);
        // Another sender has its own allowance.
        Assert.NotNull(server.Begin(Admin + 1, UserIndex + 1, 99, isAdmin: false, 1));
        // After the window, one more reply, and the line counts what was held back.
        Assert.NotNull(server.Begin(Admin, UserIndex, 100, isAdmin: false, ServerLogsServer.RefusalWindowSeconds + 1));
        Assert.Contains(log, l => l.Contains("refused: NotAdmin (50 more refused since the last report)"));
    }

    [Fact]
    public void One_transfer_per_admin_and_requests_are_spaced()
    {
        var server = new ServerLogsServer();
        Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin, UserIndex, 1, true, 0)!, out var first) && first.Kind == ServerLogsWire.KindAccepted);
        Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin, UserIndex, 2, true, 1)!, out var busy));
        Assert.Equal(ServerLogsRefusal.Busy, busy.Refusal);
        // Another admin is not blocked by the first.
        Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin + 1, UserIndex + 1, 3, true, 1)!, out var other) && other.Kind == ServerLogsWire.KindAccepted);
        server.Cancel(Admin, 1, "test");
        // Too soon after the last request: refused (answered; its log line is held back within the minute).
        Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin, UserIndex, 4, true, 2), out var soonHeld));
        Assert.Equal(ServerLogsRefusal.RateLimited, soonHeld.Refusal);
        Assert.True(ServerLogsWire.TryDecode(server.Begin(Admin, UserIndex, 5, true, 2 + ServerLogsServer.MinSecondsBetweenRequests)!, out var later));
        Assert.Equal(ServerLogsWire.KindAccepted, later.Kind);
        var fresh = new ServerLogsServer();
        fresh.Begin(Admin, UserIndex, 1, true, 0);
        fresh.Cancel(Admin, 1, "test");
        Assert.True(ServerLogsWire.TryDecode(fresh.Begin(Admin, UserIndex, 2, true, 1)!, out var soon));
        Assert.Equal(ServerLogsRefusal.RateLimited, soon.Refusal);
    }

    [Fact]
    public void Bundles_over_20_MB_are_refused_on_both_sides()
    {
        var server = new ServerLogsServer();
        server.Begin(Admin, UserIndex, 1, true, 0);
        var refuse = server.Ready(Admin, 1, new byte[ServerLogsWire.MaxBundleBytes + 1], 0);
        Assert.True(ServerLogsWire.TryDecode(refuse!, out var p));
        Assert.Equal(ServerLogsRefusal.TooLarge, p.Refusal);
        Assert.Equal(0, server.ActiveCount);

        var client = new ServerLogsClient();
        ServerLogsOutcome? outcome = null;
        var req = client.Begin(0, o => outcome = o)!;
        ServerLogsWire.TryDecode(req, out var r);
        int total = ServerLogsWire.MaxBundleBytes + 1;
        var reply = client.Accept(ServerLogsWire.EncodeStart(r.TransferId, total, ServerLogsWire.ChunkCount(total), new byte[32]), 0);
        Assert.True(ServerLogsWire.TryDecode(reply!, out var cancel) && cancel.Kind == ServerLogsWire.KindCancel);
        Assert.False(outcome!.Ok);
        Assert.Contains("breaks the limits", outcome.Reason);
    }

    [Fact]
    public void Cancel_disconnect_stall_and_unload_end_everything_exactly_once()
    {
        // Client cancel on Core unload: the outcome says so.
        var client = new ServerLogsClient();
        int calls = 0;
        ServerLogsOutcome? outcome = null;
        client.Begin(0, o => { calls++; outcome = o; });
        Assert.NotNull(client.Cancel("Progenitor is unloading", unloading: true));
        Assert.Null(client.Cancel("again"));
        Assert.Equal(1, calls);
        Assert.True(outcome!.Unloading);

        // The client left the server.
        outcome = null;
        client.Begin(0, o => outcome = o);
        client.Tick(1, connected: false);
        Assert.Equal("this client left the server", outcome!.Reason);
        Assert.False(outcome.Unloading);

        // Receiving, then no chunk for longer than the stall limit (with a re-acknowledgement each second meanwhile).
        outcome = null;
        var req = client.Begin(0, o => outcome = o)!;
        ServerLogsWire.TryDecode(req, out var r);
        byte[] zip = Random(ServerLogsWire.MaxChunkBody * 2);
        client.Accept(ServerLogsWire.EncodeStart(r.TransferId, zip.Length, 2, ServerLogsWire.Sha256Of(zip)), 0);
        client.Accept(ServerLogsWire.EncodeChunk(r.TransferId, 0, zip), 0);
        Assert.StartsWith("Getting server logs... 0.0 / 0.0 MB", client.ProgressLine);
        var reAck = client.Tick(1.5, true);
        Assert.True(ServerLogsWire.TryDecode(reAck!, out var ra) && ra.Kind == ServerLogsWire.KindAck && ra.Index == 1);
        var cancel = client.Tick(ServerLogsClient.StallSeconds + 1, true);
        Assert.True(ServerLogsWire.TryDecode(cancel!, out var c) && c.Kind == ServerLogsWire.KindCancel);
        Assert.Contains("stalled", outcome!.Reason);

        // Server: the admin disconnects mid-transfer; then Core unloads with a transfer running.
        var log = new List<string>();
        var server = new ServerLogsServer { Log = log.Add };
        server.Begin(Admin, UserIndex, 1, true, 0);
        server.Ready(Admin, 1, zip, 0);
        Assert.Equal(0, server.Pump(0.1, (_, _) => false, (_, _) => true));
        Assert.Equal(0, server.ActiveCount);
        Assert.Contains(log, l => l.Contains("the admin disconnected"));
        server.Begin(Admin + 1, UserIndex, 2, true, 0);
        server.Clear();
        Assert.Equal(0, server.ActiveCount);
        Assert.Contains(log, l => l.Contains("unloading"));
    }

    [Fact]
    public void A_server_that_never_answers_reads_as_too_old()
    {
        var client = new ServerLogsClient();
        ServerLogsOutcome? outcome = null;
        client.Begin(0, o => outcome = o);
        Assert.Equal("Asking the server for its logs...", client.ProgressLine);
        client.Tick(ServerLogsClient.AnswerSeconds - 1, true);
        Assert.Null(outcome);
        client.Tick(ServerLogsClient.AnswerSeconds + 1, true);
        Assert.Contains("too old", outcome!.Reason);
        Assert.False(client.Busy);
    }

    [Fact]
    public void An_unloading_fetch_starts_no_build()
    {
        bool built = false;
        SupportActions.Gather = () => Request();
        SupportActions.ServerLogsWanted = () => true;
        SupportActions.FetchServerLogs = done => done(new ServerLogsOutcome { Ok = false, Unloading = true, Reason = "Progenitor is unloading" });
        SupportActions.Builder = _ => { built = true; return new SupportBundleResult { Ok = true }; };
        SupportActions.Collect();
        Assert.False(built);
        Assert.False(SupportActions.Running);
        Assert.Equal("Collect logs stopped: Progenitor is unloading.", SupportActions.Status);
    }

    [Fact]
    public void A_server_zip_with_too_many_entries_or_bytes_is_cut_with_a_note()
    {
        using var many = new MemoryStream();
        using (var zip = new ZipArchive(many, ZipArchiveMode.Create, leaveOpen: true))
            for (int i = 0; i < SupportBundle.MaxServerEntries + 100; i++)
            {
                using var w = new StreamWriter(zip.CreateEntry("f" + i + ".txt").Open());
                w.Write("x");
            }
        var request = Request();
        request.ServerZip = many.ToArray();
        var result = SupportBundle.Build(request);
        Assert.True(result.ServerPartIncluded);
        var entries = Read(result.Path);
        Assert.Equal(SupportBundle.MaxServerEntries, entries.Keys.Count(k => k.StartsWith("server/", StringComparison.Ordinal)));
        Assert.Contains("stopped at 500 files", entries["summary.txt"]);

        using var big = new MemoryStream();
        using (var zip = new ZipArchive(big, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = zip.CreateEntry("small.txt").Open()) w.Write(new byte[] { 1 }, 0, 1);
            using var s = zip.CreateEntry("huge.log").Open();
            var block = new byte[1024 * 1024];
            for (int i = 0; i < 81; i++) s.Write(block, 0, block.Length); // zeros compress to almost nothing
        }
        var second = Request();
        second.NowLocal = second.NowLocal.AddSeconds(1);
        second.ServerZip = big.ToArray();
        var cut = SupportBundle.Build(second);
        var cutEntries = Read(cut.Path);
        Assert.Contains("server/small.txt", cutEntries.Keys);
        Assert.DoesNotContain("server/huge.log", cutEntries.Keys);
        Assert.Contains("stopped at 80.0 MB uncompressed", cutEntries["summary.txt"]);
    }

    [Fact]
    public void Builds_at_the_same_second_never_collide()
    {
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), "x\n");
        var results = new SupportBundleResult[4];
        Parallel.For(0, 4, i =>
        {
            var r = Request();
            r.Keep = 100;
            results[i] = SupportBundle.Build(r);
        });
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(4, results.Select(r => r.Path).Distinct().Count());
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "progenitor-support"), "*.tmp"));

        // A name taken between the check and the move goes to the next number.
        string folder = Path.Combine(_base, "move");
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, "a.tmp");
        File.WriteAllText(temp, "z");
        File.WriteAllText(Path.Combine(folder, "vrising-mod-logs-client-20261010-142233.zip"), "taken");
        string name = SupportBundle.MoveIntoPlace(temp, folder, "client", new DateTime(2026, 10, 10, 14, 22, 33), out string final);
        Assert.Equal("vrising-mod-logs-client-20261010-142233-2.zip", name);
        Assert.True(File.Exists(final));
    }

    [Fact]
    public void A_transfer_the_server_drops_tells_the_client_why()
    {
        var server = new ServerLogsServer();
        var client = new ServerLogsClient();
        ServerLogsOutcome? outcome = null;
        var req = client.Begin(0, o => outcome = o)!;
        ServerLogsWire.TryDecode(req, out var r);
        client.Accept(server.Begin(Admin, UserIndex, r.TransferId, true, 0), 0);
        server.Ready(Admin, r.TransferId, Random(ServerLogsWire.MaxChunkBody * 100), 0);
        var toClient = new List<byte[]>();
        // Packets the client never acknowledges (they are lost), then the stall limit.
        for (double t = 0.1; t < 1; t += 0.1) server.Pump(t, (_, _) => true, (_, _) => true);
        server.Pump(ServerLogsServer.StallSeconds + 1, (_, _) => true, (_, p) => { toClient.Add(p); return true; });
        Assert.Equal(0, server.ActiveCount);
        var cancel = Assert.Single(toClient);
        client.Accept(cancel, ServerLogsServer.StallSeconds + 1);
        Assert.False(outcome!.Ok);
        Assert.Equal("the server stopped the transfer: no acknowledgement for 30 s", outcome.Reason);

        // Building too long, and Core unloading on the server, say so too.
        var building = new ServerLogsServer();
        building.Begin(Admin, UserIndex, 7, true, 0);
        var sent = new List<byte[]>();
        building.Pump(ServerLogsServer.BuildSeconds + 1, (_, _) => true, (_, p) => { sent.Add(p); return true; });
        Assert.True(ServerLogsWire.TryDecode(Assert.Single(sent), out var b) && b.Refusal == ServerLogsRefusal.Cancelled);
        Assert.Contains("building took longer", b.Message);

        var unloading = new ServerLogsServer();
        unloading.Begin(Admin, UserIndex, 8, true, 0);
        sent.Clear();
        unloading.Clear((_, p) => { sent.Add(p); return true; });
        Assert.True(ServerLogsWire.TryDecode(Assert.Single(sent), out var u) && u.Refusal == ServerLogsRefusal.Cancelled);
        Assert.Contains("unloading", u.Message);

        // A disconnected admin is not sent anything.
        var gone = new ServerLogsServer();
        gone.Begin(Admin, UserIndex, 9, true, 0);
        gone.Ready(Admin, 9, Random(10), 0);
        sent.Clear();
        gone.Pump(0.1, (_, _) => false, (_, p) => { sent.Add(p); return true; });
        Assert.Empty(sent);
    }

    // ---- merging into the client's bundle ----------------------------------------------------------------------

    private static byte[] ServerZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in new[] { ("README.txt", "server readme"), ("bepinex/LogOutput.log", "server log [steam-1]") })
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
        }
        return ms.ToArray();
    }

    private SupportBundleRequest Request() => new()
    {
        Side = "client",
        BepInExRoot = _root,
        NowLocal = new DateTime(2026, 10, 10, 14, 22, 33),
    };

    private static Dictionary<string, string> Read(string path)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        using var archive = ZipFile.OpenRead(path);
        foreach (var e in archive.Entries)
        {
            using var r = new StreamReader(e.Open(), Encoding.UTF8);
            map[e.FullName] = r.ReadToEnd();
        }
        return map;
    }

    [Fact]
    public void The_servers_bundle_is_merged_under_server_and_named_in_the_summary()
    {
        File.WriteAllText(Path.Combine(_root, "LogOutput.log"), "client log\n");
        var request = Request();
        request.ServerZip = ServerZip();
        var result = SupportBundle.Build(request);
        Assert.True(result.Ok, result.Error);
        Assert.True(result.ServerPartIncluded);
        var entries = Read(result.Path);
        Assert.Equal("server log [steam-1]", entries["server/bepinex/LogOutput.log"]);
        Assert.Equal("server readme", entries["server/README.txt"]);
        Assert.Equal("client log\n", entries["bepinex/LogOutput.log"]);
        Assert.Contains("Server logs: included under server/ (2 files", entries["summary.txt"]);
    }

    [Fact]
    public void A_missing_server_part_is_named_in_the_summary_and_an_unreadable_one_is_left_out()
    {
        var request = Request();
        request.ServerPartNote = "this server's Progenitor is too old to send its logs";
        var result = SupportBundle.Build(request);
        Assert.Contains("Server logs: not included: this server's Progenitor is too old to send its logs", Read(result.Path)["summary.txt"]);
        Assert.Equal(request.ServerPartNote, result.ServerPartMissing);

        var broken = Request();
        broken.ServerZip = Random(500);
        var second = SupportBundle.Build(broken);
        Assert.True(second.Ok, second.Error);
        Assert.False(second.ServerPartIncluded);
        Assert.Contains("could not be read", second.ServerPartMissing);
        Assert.DoesNotContain(Read(second.Path).Keys, k => k.StartsWith("server/", StringComparison.Ordinal));
    }

    [Fact]
    public void Collect_logs_fetches_the_server_part_first_and_reports_it()
    {
        Action<ServerLogsOutcome>? pending = null;
        string progress = "Getting server logs... 1.2 / 3.4 MB";
        SupportActions.Gather = () => Request();
        SupportActions.ServerLogsWanted = () => true;
        SupportActions.FetchServerLogs = done => pending = done;
        SupportActions.ServerLogsProgress = () => progress;

        SupportActions.Collect();
        Assert.True(SupportActions.Running);
        Assert.Equal("Getting server logs... 1.2 / 3.4 MB", Ui.ProgenitorPage.LastBundleLine());
        progress = "";
        var serverZip = ServerZip();
        pending!(new ServerLogsOutcome { Ok = true, Zip = serverZip, ExpectedSha256 = ServerLogsWire.Sha256Of(serverZip) });
        Assert.False(SupportActions.Running);
        Assert.True(SupportActions.LastResult!.ServerPartIncluded);
        Assert.EndsWith("The server's logs are inside, under server/.", SupportActions.Status);

        SupportActions.FetchServerLogs = done => done(new ServerLogsOutcome { Ok = false, Reason = "you are not an admin of this server" });
        SupportActions.Gather = () =>
        {
            var r = Request();
            r.NowLocal = r.NowLocal.AddSeconds(1);
            return r;
        };
        SupportActions.Collect();
        Assert.True(SupportActions.LastResult!.Ok);
        Assert.EndsWith("Server logs not included: you are not an admin of this server.", SupportActions.Status);
    }

    [Fact]
    public void Collect_logs_without_the_server_part_never_asks()
    {
        bool asked = false;
        SupportActions.Gather = () => Request();
        SupportActions.ServerLogsWanted = () => false;
        SupportActions.FetchServerLogs = _ => asked = true;
        SupportActions.Collect();
        Assert.False(asked);
        Assert.True(SupportActions.LastResult!.Ok);
        Assert.DoesNotContain("Server logs", SupportActions.Status);
    }

    [Fact]
    public void The_capability_note_follows_the_join()
    {
        ServerLogsCaps.NoteServerSendsLogs(true);
        Assert.True(ServerLogsCaps.ServerSendsLogs);
        ServerLogsCaps.NoteServerSendsLogs(false);
        Assert.False(ServerLogsCaps.ServerSendsLogs);
        Assert.True(Progenitor.Networking.CustomEventPolicy.IsReserved(ServerLogsWire.EventId));
    }
}
