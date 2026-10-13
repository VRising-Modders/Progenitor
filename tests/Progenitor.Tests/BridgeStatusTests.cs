using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

[Collection("MainThreadQueue")]
public class BridgeStatusTests
{
    [Fact]
    public void Shape_is_the_documented_liveness_object()
    {
        var at = new DateTime(2026, 9, 8, 1, 18, 0, DateTimeKind.Utc);
        var drainAt = new DateTime(2026, 9, 8, 1, 17, 55, DateTimeKind.Utc);
        var shaped = BridgeStatus.Shape(
            entered: true,
            op: "ping",
            id: 1,
            enteredAtUtc: at,
            abandoned: true,
            elapsedMs: 25_000,
            queued: 1,
            delayed: 0,
            lastDrainAtUtc: drainAt,
            lastDrainMs: 4,
            frame: 12,
            heartbeatSeconds: 5,
            uiTickTiming: true);

        string json = JsonSerializer.Serialize(shaped);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("entered").GetBoolean());
        Assert.Equal("ping", root.GetProperty("op").GetString());
        Assert.Equal(1, root.GetProperty("id").GetInt64());
        Assert.Contains("2026-09-08", root.GetProperty("enteredAt").GetString());
        Assert.True(root.GetProperty("abandoned").GetBoolean());
        Assert.Equal(25_000, root.GetProperty("elapsedMs").GetInt64());
        Assert.Equal(1, root.GetProperty("queued").GetInt32());
        Assert.Equal(0, root.GetProperty("delayed").GetInt32());
        Assert.Equal(4, root.GetProperty("lastDrainMs").GetInt64());
        Assert.Equal(12, root.GetProperty("frame").GetInt32());
        Assert.Equal(5, root.GetProperty("heartbeatSeconds").GetInt32());
        Assert.True(root.GetProperty("uiTickTiming").GetBoolean());
        Assert.False(root.GetProperty("dumpInProgress").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("lastFreezeDump").ValueKind);
        Assert.False(root.TryGetProperty("ok", out _));
    }

    [Fact]
    public void ApplyToggles_sets_the_timing_flag_and_heartbeat_period()
    {
        MainThread.ResetForTests();
        try
        {
            Assert.False(MainThread.UiTickTimingEnabled);
            Assert.Equal(5, MainThread.HeartbeatSeconds);
            BridgeStatus.ApplyToggles(Json("{\"uiTickTiming\":true,\"heartbeatSeconds\":0}"));
            Assert.True(MainThread.UiTickTimingEnabled);
            Assert.Equal(0, MainThread.HeartbeatSeconds);
            BridgeStatus.ApplyToggles(Json("{\"uiTickTiming\":false}"));
            Assert.False(MainThread.UiTickTimingEnabled);
            Assert.Equal(0, MainThread.HeartbeatSeconds);
        }
        finally
        {
            MainThread.ResetForTests();
        }
    }

    [Fact]
    public void Run_does_not_take_the_gate()
    {
        MainThread.ResetForTests();
        var gate = new MainThreadOpGate();
        Assert.True(gate.TryEnter("ping", 3, out int ticket, out _));
        try
        {
            var result = BridgeStatus.Run(gate, Json("{}"));
            string json = JsonSerializer.Serialize(result);
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("entered").GetBoolean());
            Assert.Equal("ping", doc.RootElement.GetProperty("op").GetString());
            Assert.Equal(3, doc.RootElement.GetProperty("id").GetInt64());
            Assert.True(gate.IsEntered);
            Assert.False(gate.TryEnter(out _));
        }
        finally
        {
            gate.Exit(ticket);
            MainThread.ResetForTests();
        }
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}

[Collection("MainThreadQueue")]
public class BridgeGateHoldTests
{
    [Fact]
    public void Timed_out_op_never_frees_the_gate_early()
    {
        var log = new List<string>();
        MainThreadOpGate.TestLog = log.Add;
        MainThread.HeartbeatGateLabel = "free";
        var gate = new MainThreadOpGate(timeoutMs: 20);
        Assert.True(gate.TryEnter("ping", 9, out int ticket, out _));
        try
        {
            System.Threading.Thread.Sleep(40);
            gate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
            Assert.True(gate.IsAbandoned(ticket));
            Assert.True(gate.IsEntered);
            Assert.False(gate.TryEnter("mods_ui", 10, out _, out _));
            Assert.Equal("busy:ping#9", MainThread.HeartbeatGateLabel);

            Assert.Contains(log, l => l == "bridge gate enter op=ping id=9");
            Assert.Contains(log, l => l.StartsWith("bridge gate abandon op=ping id=9 waitedMs=", StringComparison.Ordinal));
            Assert.DoesNotContain(log, l => l.StartsWith("bridge gate finish ", StringComparison.Ordinal));

            gate.NoteRunFinished(ticket);
            Assert.False(gate.IsEntered);
            Assert.Equal("free", MainThread.HeartbeatGateLabel);
            Assert.Contains(log, l => l.StartsWith("bridge gate finish op=ping id=9 elapsedMs=", StringComparison.Ordinal) && l.EndsWith(" abandoned=true", StringComparison.Ordinal));
            Assert.True(gate.TryEnter(out _));
        }
        finally
        {
            MainThreadOpGate.TestLog = null;
            MainThread.HeartbeatGateLabel = "free";
        }
    }

    [Fact]
    public void Finish_after_a_live_exit_is_not_abandoned()
    {
        var log = new List<string>();
        MainThreadOpGate.TestLog = log.Add;
        var gate = new MainThreadOpGate();
        Assert.True(gate.TryEnter("logs", 2, out int ticket, out _));
        gate.Exit(ticket);
        Assert.False(gate.IsEntered);
        Assert.Contains(log, l => l.StartsWith("bridge gate finish op=logs id=2 elapsedMs=", StringComparison.Ordinal) && l.EndsWith(" abandoned=false", StringComparison.Ordinal));
        MainThreadOpGate.TestLog = null;
    }
}
