using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.WorldState;
using Xunit;

namespace Progenitor.Tests;

public sealed class HandshakeRefreshTests
{
    private static readonly byte[] Request = { 1 };

    [Fact]
    public void Only_connected_identity_with_ok_handshake_and_exact_protocol_can_queue()
    {
        var gate = new HandshakeRefreshGate();
        Assert.False(gate.TryAdmit(-1, 22, true, Request, 0, out _));
        Assert.False(gate.TryAdmit(7, 0, true, Request, 0, out _));
        Assert.False(gate.TryAdmit(7, 22, false, Request, 0, out _));
        Assert.False(gate.TryAdmit(7, 22, true, Array.Empty<byte>(), 0, out _));
        Assert.False(gate.TryAdmit(7, 22, true, new byte[] { 2 }, 0, out _));
        Assert.False(gate.TryAdmit(7, 22, true, new byte[] { 1, 1 }, 0, out _));
        Assert.False(gate.TryAdmit(7, 22, true, Request, double.NaN, out _));
        Assert.True(gate.TryAdmit(7, 22, true, Request, 0, out _));
    }

    [Fact]
    public void Pending_and_cooldown_bound_each_peer_independently()
    {
        var gate = new HandshakeRefreshGate();
        Assert.True(gate.TryAdmit(7, 22, true, Request, 0, out var ticket));
        Assert.False(gate.TryAdmit(7, 22, true, Request, 100, out _)); // Pending is not a time window.
        Assert.True(gate.TryAdmit(8, 23, true, Request, 0, out _));
        Assert.True(gate.Finish(7, 22, ticket));
        Assert.False(gate.TryAdmit(7, 22, true, Request, 4.9, out _));
        Assert.True(gate.TryAdmit(7, 22, true, Request, 5, out _));
    }

    [Fact]
    public void Old_connection_or_queued_callback_cannot_complete_new_request()
    {
        var gate = new HandshakeRefreshGate();
        Assert.True(gate.TryAdmit(7, 22, true, Request, 0, out var first));
        Assert.False(gate.Finish(7, 99, first));
        Assert.False(gate.TryAdmit(7, 99, true, Request, 9, out _));
        gate.Forget(7);
        Assert.True(gate.TryAdmit(7, 99, true, Request, 1, out var second));
        Assert.False(gate.Finish(7, 22, first));
        Assert.False(gate.Finish(7, 99, first));
        Assert.True(gate.Finish(7, 99, second));
    }

    [Fact]
    public void Unload_and_peer_capacity_release_all_retained_work()
    {
        var gate = new HandshakeRefreshGate();
        for (int i = 0; i < HandshakeRefreshGate.MaxPeers; i++)
            Assert.True(gate.TryAdmit(i, (ulong)i + 1, true, Request, 0, out _));
        Assert.False(gate.TryAdmit(999, 999, true, Request, 0, out _));
        gate.Forget(0);
        Assert.True(gate.TryAdmit(999, 999, true, Request, 0, out var ticket));
        gate.Clear();
        Assert.False(gate.Finish(999, 999, ticket));
        Assert.True(gate.TryAdmit(999, 999, true, Request, 0, out _));
    }

    [Fact]
    public void Dispatch_delay_allows_existing_post_ok_snapshot_budget_to_expire()
    {
        double now = 0;
        var reanswers = new HandshakeReAnswer(() => now);
        Assert.True(reanswers.TryAdmit(7));
        Assert.False(reanswers.TryAdmit(7));
        now = HandshakeRefreshGate.DispatchDelaySeconds;
        Assert.True(reanswers.TryAdmit(7));
    }

    [Fact]
    public void Server_context_cleared_is_raised_on_each_owned_edge_and_not_on_a_core_only_refresh()
    {
        Handshake.ResetTables();
        Handshake.UninstallClientServerContextWatch();
        WorldEpoch.ResetForTests();
        var reasons = new List<string>();
        Handshake.ServerContextCleared += reasons.Add;
        try
        {
            Handshake.InstallClientServerContextWatch();
            WorldEpoch.Advance(ClientWorldEpoch.CreatedSource);
            Assert.Empty(reasons);

            WorldEpoch.Advance(ClientWorldEpoch.EndedSource);
            Assert.Equal(new[] { Handshake.WorkspaceCloseWorldEnded }, reasons);

            Handshake.NotifyClientConnectionEnded();
            Assert.Equal(Handshake.WorkspaceCloseServerContextCleared, reasons[1]);

            Handshake.NotifyHandshakeEpochChanged();
            Assert.Equal(Handshake.WorkspaceCloseServerContextCleared, reasons[2]);
            Assert.Equal(3, reasons.Count);

            Handshake.InvalidateClientServerContext("disconnect");
            Assert.Equal(3, reasons.Count);
        }
        finally
        {
            Handshake.ServerContextCleared -= reasons.Add;
            Handshake.UninstallClientServerContextWatch();
            Handshake.ResetTables();
            WorldEpoch.ResetForTests();
        }
    }
}
