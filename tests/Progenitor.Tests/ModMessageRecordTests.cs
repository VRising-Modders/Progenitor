using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C5: the fixture format and the bounded recorder behind the Dev bridge's <c>messages_record</c>
/// (ModMessageFixtures.cs), its argument parsing (MessagesRecordArgs.cs), and the router's per-mod message summary the
/// mod page's developer details read.
/// </summary>
public sealed class ModMessageRecordTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-record-" + Guid.NewGuid().ToString("n"));
    private double _now = 100;

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* temp */ }
    }

    private static ModMessageRecord Rec(string mod = "example.trade", string version = "1.2.0", string message = "trade.offer", int v = 2,
        byte[]? payload = null, string direction = ModMessageFixtureFormat.ToServer) => new()
    {
        Mod = mod,
        ModVersion = version,
        Message = message,
        Version = v,
        MinReaderVersion = 1,
        Direction = direction,
        Payload = payload ?? new MessageWriter().Int(1, 7).ToArray(),
    };

    private ModMessageRecorder Recorder(string? mod = null, int seconds = 60, int messages = 10, int bytes = 1 << 20) =>
        new(_root, mod, seconds, messages, bytes, () => _now);

    [Fact]
    public void A_recorded_message_becomes_a_fixture_the_reader_reads_back()
    {
        var rec = Recorder();
        Assert.Equal("written", rec.Offer(Rec(), DateTime.UtcNow));
        string folder = Path.Combine(_root, "example.trade", "1.2.0", "trade.offer@v2");
        string sidecar = Assert.Single(Directory.GetFiles(folder, "*.json"));
        Assert.True(ModMessageFixtureFormat.TryRead(sidecar, out var back, out var error), error);
        Assert.Equal("example.trade", back!.Mod);
        Assert.Equal("1.2.0", back.ModVersion);
        Assert.Equal(2, back.Version);
        Assert.Equal(ModMessageFixtureFormat.ToServer, back.Direction);
        Assert.Equal("live", back.Source);
        Assert.Equal(Rec().Payload, back.Payload);
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
        Assert.Equal(1, doc.RootElement.GetProperty("format").GetInt32());
        Assert.Equal(64, doc.RootElement.GetProperty("sha256").GetString()!.Length);
    }

    [Fact]
    public void The_same_payload_twice_is_one_fixture()
    {
        var rec = Recorder();
        Assert.Equal("written", rec.Offer(Rec(), DateTime.UtcNow));
        Assert.Equal("duplicate", rec.Offer(Rec(direction: ModMessageFixtureFormat.ToClient), DateTime.UtcNow));
        Assert.Equal(1, rec.Written);
        Assert.Equal(1, rec.Duplicates);
        Assert.Equal(2, rec.Seen);
    }

    [Fact]
    public void A_mod_filter_records_only_that_mod()
    {
        var rec = Recorder("example.trade");
        Assert.Equal("other-mod", rec.Offer(Rec(mod: "other.mod"), DateTime.UtcNow));
        Assert.Equal("written", rec.Offer(Rec(mod: "EXAMPLE.TRADE"), DateTime.UtcNow));
        Assert.False(Directory.Exists(Path.Combine(_root, "other.mod")));
    }

    [Fact]
    public void The_message_budget_stops_the_session()
    {
        var rec = Recorder(messages: 2);
        Assert.Equal("written", rec.Offer(Rec(payload: new byte[] { 8, 1 }), DateTime.UtcNow));
        Assert.Equal("written", rec.Offer(Rec(payload: new byte[] { 8, 2 }), DateTime.UtcNow));
        Assert.False(rec.Active);
        Assert.Equal("messages", rec.StoppedBecause);
        Assert.Equal("stopped", rec.Offer(Rec(payload: new byte[] { 8, 3 }), DateTime.UtcNow));
    }

    [Fact]
    public void The_byte_budget_stops_the_session_before_writing_over_it()
    {
        var rec = Recorder(bytes: 2048);
        Assert.Equal("written", rec.Offer(Rec(payload: new byte[1000]), DateTime.UtcNow));
        Assert.Equal("stopped", rec.Offer(Rec(payload: new byte[1001]), DateTime.UtcNow));
        Assert.Equal("bytes", rec.StoppedBecause);
        Assert.Equal(1, rec.Written);
    }

    [Fact]
    public void The_time_budget_stops_the_session()
    {
        var rec = Recorder(seconds: 5);
        Assert.True(rec.Active);
        _now += 5.1;
        Assert.False(rec.Active);
        Assert.Equal("time", rec.StoppedBecause);
        Assert.Equal("stopped", rec.Offer(Rec(), DateTime.UtcNow));
    }

    [Fact]
    public void Bounds_are_capped_and_defaulted()
    {
        var rec = new ModMessageRecorder(_root, null, 0, 0, 0, () => 0);
        Assert.Equal(ModMessageRecorder.DefaultSeconds, rec.Seconds);
        Assert.Equal(ModMessageRecorder.DefaultMaxMessages, rec.MaxMessages);
        Assert.Equal(ModMessageRecorder.DefaultMaxBytes, rec.MaxBytes);
        var big = new ModMessageRecorder(_root, null, 99999, 99999, int.MaxValue, () => 0);
        Assert.Equal(ModMessageRecorder.MaxSeconds, big.Seconds);
        Assert.Equal(ModMessageRecorder.MaxMessagesCap, big.MaxMessages);
        Assert.Equal(ModMessageRecorder.MaxBytesCap, big.MaxBytes);
    }

    [Fact]
    public void An_unsafe_version_or_mod_never_leaves_the_fixture_root()
    {
        var rec = Recorder();
        Assert.Equal("written", rec.Offer(Rec(mod: "..", version: "../../evil"), DateTime.UtcNow));
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.StartsWith(Path.GetFullPath(_root), Path.GetFullPath(f)));
        Assert.Equal("_._", ModMessageFixtureFormat.Segment(".."));
        Assert.Equal("1.0_", ModMessageFixtureFormat.Segment("1.0."));
        Assert.Equal(".._.._evil", ModMessageFixtureFormat.Segment("../../evil"));
    }

    [Fact]
    public void An_invalid_record_is_an_error_not_a_throw()
    {
        var rec = Recorder();
        Assert.Equal("error", rec.Offer(Rec(message: "Bad Name"), DateTime.UtcNow));
        Assert.Single((System.Collections.Generic.List<string>)rec.Status()["errors"]!);
    }

    [Fact]
    public void A_tampered_payload_is_refused_on_read()
    {
        var rec = Recorder();
        rec.Offer(Rec(), DateTime.UtcNow);
        string sidecar = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(Path.ChangeExtension(sidecar, ".bin"), new byte[] { 8, 9 });
        Assert.False(ModMessageFixtureFormat.TryRead(sidecar, out _, out var error));
        Assert.Contains("sha256", error);
    }

    [Fact]
    public void Status_reports_the_session()
    {
        var rec = Recorder("example.trade");
        rec.Offer(Rec(), DateTime.UtcNow);
        var s = rec.Status();
        Assert.Equal(true, s["recording"]);
        Assert.Equal("example.trade", s["mod"]);
        Assert.Equal(1, s["written"]);
        rec.Stop();
        Assert.Equal("stopped", rec.Status()["stoppedBecause"]);
        Assert.Equal(false, rec.Status()["recording"]);
    }

    [Theory]
    [InlineData("{}", "status", "", 0)]
    [InlineData("{\"action\":\"on\",\"mod\":\"example.trade\",\"seconds\":30}", "on", "example.trade", 30)]
    [InlineData("{\"action\":\"start\",\"mod\":\"*\"}", "on", "*", 0)]
    [InlineData("{\"action\":\"stop\"}", "off", "", 0)]
    public void Args_parse(string json, string action, string mod, int seconds)
    {
        using var doc = JsonDocument.Parse(json);
        var req = MessagesRecordArgs.Parse(doc.RootElement);
        Assert.Equal(action, req.Action);
        Assert.Equal(mod, req.Mod);
        Assert.Equal(seconds, req.Seconds);
    }

    [Theory]
    [InlineData("{\"action\":\"record\"}")]
    [InlineData("{\"action\":\"on\",\"mod\":\"x\",\"seconds\":-1}")]
    [InlineData("{\"action\":\"on\"}")]
    [InlineData("{\"action\":\"on\",\"mod\":\"  \"}")]
    [InlineData("{\"action\":\"on\",\"maxMessages\":\"ten\"}")]
    public void Bad_args_are_refused(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => MessagesRecordArgs.Parse(doc.RootElement));
    }

    [Fact]
    public void A_session_budget_stops_at_the_folder_cap_across_sessions()
    {
        Assert.Equal(ModMessageRecorder.DefaultMaxBytes, ModMessageRecorder.BudgetFor(0, 0));
        Assert.Equal(ModMessageRecorder.MaxBytesCap, ModMessageRecorder.BudgetFor(int.MaxValue, 0));
        Assert.Equal(4096, ModMessageRecorder.BudgetFor(ModMessageRecorder.MaxBytesCap, ModMessageRecorder.DiskCapBytes - 4096));
        Assert.Equal(0, ModMessageRecorder.BudgetFor(1 << 20, ModMessageRecorder.DiskCapBytes));
        Assert.Equal(0, ModMessageRecorder.BudgetFor(1 << 20, ModMessageRecorder.DiskCapBytes - 100));
        Assert.Equal(0, ModMessageRecorder.FolderBytes(Path.Combine(_root, "missing")));
        Recorder().Offer(Rec(), DateTime.UtcNow);
        Assert.True(ModMessageRecorder.FolderBytes(_root) > 0);
    }

    [Fact]
    public void The_queue_drops_with_a_count_when_full_and_never_blocks()
    {
        var queue = new ModMessageRecordQueue(Recorder(messages: 5000), startThread: false);
        for (int i = 0; i < ModMessageRecordQueue.Capacity + 10; i++)
            queue.Enqueue(Rec(payload: new byte[] { 8, (byte)(i & 0x7F), 16, (byte)(i >> 7) }));
        Assert.Equal(10, queue.Dropped);
        Assert.Equal(ModMessageRecordQueue.Capacity, queue.Pending);
        Assert.Equal(ModMessageRecordQueue.Capacity, queue.DrainOnce());
        Assert.Equal(0, queue.Pending);
        Assert.Equal(ModMessageRecordQueue.Capacity, queue.Recorder.Written);
        queue.Complete(0);
        Assert.False(queue.Enqueue(Rec()));
        Assert.Equal(11, queue.Dropped);
    }

    [Fact]
    public void The_writer_thread_writes_what_the_main_thread_queued()
    {
        var queue = new ModMessageRecordQueue(Recorder());
        int thread = Environment.CurrentManagedThreadId;
        Assert.True(queue.Enqueue(Rec(payload: new byte[] { 8, 1 })));
        Assert.True(queue.Enqueue(Rec(payload: new byte[] { 8, 2 })));
        Assert.True(queue.Flush(5000));
        queue.Complete(5000);
        Assert.Equal(2, queue.Recorder.Written);
        Assert.Equal(2, Directory.GetFiles(_root, "*.bin", SearchOption.AllDirectories).Length);
        Assert.Equal(thread, Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void A_recorded_schema_survives_the_sidecar()
    {
        var shape = new System.Collections.Generic.List<FieldShape>
        {
            new(1, "Id", "int", false),
            new(2, "Items", "message", true) { Fields = new() { new FieldShape(1, "Tag", "string", false) } },
        };
        var rec = Recorder();
        rec.Offer(new ModMessageRecord
        {
            Mod = "example.trade", ModVersion = "1.0.0", Message = "trade.offer", Version = 1, MinReaderVersion = 1,
            Payload = new byte[] { 8, 2 }, Schema = shape,
        }, DateTime.UtcNow);
        string sidecar = Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories).Single();
        Assert.True(ModMessageFixtureFormat.TryRead(sidecar, out var back, out var error), error);
        Assert.Equal(new[] { "int", "list:message" }, back!.Schema!.Select(f => f.Describe()).ToArray());
        Assert.Equal("Tag", back.Schema![1].Fields![0].Name);
        Assert.DoesNotContain("\r", File.ReadAllText(sidecar));
    }

    [Fact]
    public void The_router_describes_handled_and_sent_messages_per_mod()
    {
        var router = new ModMessageRouter();
        Assert.Null(router.Register("example.trade", "trade.offer", 2, (_, _, _) => { }, out _));
        Assert.Null(router.Claim("example.trade", "trade.tick", out _, out _));
        router.NoteSent("example.trade", "trade.tick", 3, 2);
        router.NoteSent("example.trade", "never.claimed", 1, 1);
        var list = router.Describe("example.trade");
        Assert.Equal(new[] { "trade.offer", "trade.tick" }, list.Select(m => m.Name).ToArray());
        Assert.Equal(2, list[0].ReaderVersion);
        Assert.Equal(0, list[0].SentVersion);
        Assert.Equal(0, list[1].ReaderVersion);
        Assert.Equal(3, list[1].SentVersion);
        Assert.Equal(2, list[1].SentMinReader);
        Assert.Empty(router.Describe("other.mod"));
        router.ForgetMod("example.trade");
        Assert.Empty(router.Describe("example.trade"));
    }
}
