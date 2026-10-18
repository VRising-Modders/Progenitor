using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public sealed class ScopedSettingsClientTests
{
    private const string Id = "mod/Section/Receive";
    private readonly List<ScopedSettingsPacket> _sent = new();
    private readonly List<(string Id, string Value)> _changes = new();
    private readonly ScopedSettingsClient _client;
    private double _now = 10;

    public ScopedSettingsClientTests()
    {
        _client = new ScopedSettingsClient(bytes =>
        {
            Assert.True(ScopedSettingsWire.TryDecode(bytes, false, out var packet));
            _sent.Add(packet);
            return true;
        });
        _client.ValueChanged += (id, value) => _changes.Add((id, value));
    }

    private static ScopedSettingRecord Row(string id, string value) => new()
    {
        Id = id, PluginId = "mod", ModName = "Mod", Version = "1.0", Section = "Section", Key = id.Split('/')[2],
        Type = "System.Boolean", Default = "true", Value = value, Personal = true, Editable = true,
    };

    private void Begin(Guid generation, int count) =>
        _client.Accept(new ScopedSettingsPacket { Kind = 1, Generation = generation, Count = count, ServerName = "Server" }, _now);

    private void RowPacket(Guid generation, ScopedSettingRecord row) =>
        _client.Accept(new ScopedSettingsPacket { Kind = 2, Generation = generation, Row = row }, _now);

    private void End(Guid generation, int count) =>
        _client.Accept(new ScopedSettingsPacket { Kind = 3, Generation = generation, Count = count }, _now);

    private Guid Snapshot(params (string Id, string Value)[] rows)
    {
        var generation = Guid.NewGuid();
        Begin(generation, rows.Length);
        foreach (var row in rows) RowPacket(generation, Row(row.Id, row.Value));
        End(generation, rows.Length);
        return generation;
    }

    private void Result(Guid generation, Guid request, string value, bool success = true, string error = "") =>
        _client.Accept(new ScopedSettingsPacket
        {
            Kind = 6, Generation = generation, RequestId = request, Id = Id, Value = value, Success = success, Error = error, Personal = true,
        }, _now);

    [Fact]
    public void Snapshots_notify_only_rows_whose_value_changed()
    {
        Snapshot((Id, "true"), ("mod/Section/Other", "1"));
        Assert.Equal(2, _changes.Count); // first provided: both are new values
        _changes.Clear();

        Snapshot((Id, "true"), ("mod/Section/Other", "1"));
        Assert.Empty(_changes);

        Snapshot((Id, "false"), ("mod/Section/Other", "1"));
        Assert.Equal(new[] { (Id, "false") }, _changes);
        _changes.Clear();

        var generation = _client.Generation;
        _client.Accept(new ScopedSettingsPacket { Kind = 4, Generation = generation, Id = Id, Value = "false", Personal = true }, _now);
        Assert.Empty(_changes);
        _client.Accept(new ScopedSettingsPacket { Kind = 4, Generation = generation, Id = Id, Value = "true", Personal = true }, _now);
        Assert.Equal(new[] { (Id, "true") }, _changes);
    }

    [Fact]
    public void An_accepted_write_answered_after_a_resnapshot_completes_as_accepted()
    {
        var first = Snapshot((Id, "true"));
        ServerSettingResult? result = null;
        _client.Request(Id, "false", "true", r => result = r, _now);
        var write = Assert.Single(_sent);
        Assert.Equal(first, write.Generation);

        var second = Snapshot((Id, "true")); // re-snapshot built before the server applied the write
        Assert.NotEqual(first, second);
        Result(first, write.RequestId, "false");

        Assert.True(result!.Value.Success);
        Assert.Equal("false", _client.Read(Id));
        Assert.Equal(0, _client.PendingCount);
    }

    [Fact]
    public void A_write_requested_while_a_snapshot_arrives_waits_and_uses_the_new_generation()
    {
        Snapshot((Id, "true"));
        var next = Guid.NewGuid();
        Begin(next, 1);
        ServerSettingResult? result = null;
        _client.Request(Id, "false", null, r => result = r, _now);
        Assert.Empty(_sent);
        Assert.Null(result);

        RowPacket(next, Row(Id, "true"));
        End(next, 1);
        var write = Assert.Single(_sent);
        Assert.Equal(next, write.Generation);
        Assert.Equal("true", write.Expected);
        Result(next, write.RequestId, "false");
        Assert.True(result!.Value.Success);
    }

    [Fact]
    public void A_queued_write_is_sent_when_the_awaited_snapshot_is_abandoned()
    {
        var first = Snapshot((Id, "true"));
        Begin(Guid.NewGuid(), 1);
        _client.Request(Id, "false", null, _ => { }, _now);
        Assert.Empty(_sent);
        _now += ScopedSettingsSnapshot.TimeoutSeconds + 1;
        _client.Tick(true, _now);
        Assert.Equal(first, Assert.Single(_sent).Generation);
    }

    [Fact]
    public void A_lost_answer_reports_success_when_the_server_already_shows_the_value()
    {
        var generation = Snapshot((Id, "true"));
        ServerSettingResult? result = null;
        _client.Request(Id, "false", null, r => result = r, _now);
        _client.Accept(new ScopedSettingsPacket { Kind = 4, Generation = generation, Id = Id, Value = "false", Personal = true }, _now);
        _now += ScopedSettingsClient.RequestTimeoutSeconds + 1;
        _client.Tick(true, _now);
        Assert.True(result!.Value.Success);

        ServerSettingResult? late = null;
        _client.Request(Id, "true", null, r => late = r, _now);
        _now += ScopedSettingsClient.RequestTimeoutSeconds + 1;
        _client.Tick(true, _now);
        Assert.False(late!.Value.Success);
        Assert.Equal("The server did not answer in time.", late.Value.Error);
    }

    [Fact]
    public void Disconnect_clears_once_and_ends_pending_writes()
    {
        Snapshot((Id, "true"));
        ServerSettingResult? result = null;
        _client.Request(Id, "false", null, r => result = r, _now);
        int revision = _client.Revision;
        _client.Tick(false, _now);
        Assert.False(result!.Value.Success);
        Assert.False(_client.Ready);
        Assert.Empty(_client.Rows);
        Assert.Equal(revision + 1, _client.Revision);
        for (int i = 0; i < 100; i++) _client.Tick(false, _now + i);
        Assert.Equal(revision + 1, _client.Revision);
    }

    [Fact]
    public void Tick_does_not_allocate_while_connected_or_disconnected()
    {
        Snapshot((Id, "true"));
        _client.Request(Id, "false", null, _ => { }, _now); // one pending write, not yet due
        for (int i = 0; i < 10; i++) _client.Tick(true, _now); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) _client.Tick(true, _now + i * 0.001);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());

        _client.Tick(false, _now);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) _client.Tick(false, _now);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public void Answers_for_unknown_or_mismatched_requests_are_ignored()
    {
        var generation = Snapshot((Id, "true"));
        ServerSettingResult? result = null;
        _client.Request(Id, "false", null, r => result = r, _now);
        Result(generation, Guid.NewGuid(), "false");
        _client.Accept(new ScopedSettingsPacket
        { Kind = 6, Generation = generation, RequestId = _sent[0].RequestId, Id = "mod/Section/Other", Value = "false", Success = true }, _now);
        Assert.Null(result);
        Assert.Equal("true", _client.Read(Id));
    }
}
