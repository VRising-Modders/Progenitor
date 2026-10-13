using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// What a client keeps and what it drops when it leaves one server and joins another
/// (FSCOPE1). Server-sourced content is connection-scoped: dropped at connection end, local
/// values back. Clone/key-row bookkeeping and edit fingerprints are world-scoped: dropped when a
/// new client world replaces the old one, never on a Core swap of a surviving world.
/// </summary>
public class ClientSessionScopeTests
{
    private const int Ember = 480926629;
    private const int Nodrain = 650563057;
    private const int Crystal = -257494203;
    private const int SourceA = 111;
    private const int SourceB = 222;

    // ---- world-scoped clone / key-row ledger ------------------------------------------------------

    [Fact]
    public void A_key_row_recorded_in_the_first_world_refuses_the_same_item_in_the_next_until_forgotten()
    {
        // The live failure (client logs 2026-09-20, 2026-09-27): sample.ember took row 1083 in the
        // first join; the next join's fresh ItemKeyLookup does not have it yet (IndexOf = -1).
        var ledger = new PrefabCloneLedger();
        ledger.RememberClone(Ember);
        ledger.RememberAppend(Ember, 1083);
        ledger.RememberIdentity(Ember, SourceA, PrefabKind.Item);

        Assert.True(ledger.TryGetAppendedIndex(Ember, out int saved));
        Assert.False(PrefabCloneLedger.KeyRowStillHolds(saved, currentIndex: -1)); // "key index mismatch saved=1083 now=-1"

        Assert.Equal(1, ledger.ForgetWorld());

        Assert.False(ledger.TryGetAppendedIndex(Ember, out _));
        Assert.False(ledger.WasCloned(Ember));
        Assert.False(ledger.WasAppended(Ember));
        Assert.False(ledger.TryGetIdentity(Ember, out _, out _, out _));
        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void A_core_swap_of_a_surviving_world_still_carries_the_ledger()
    {
        var before = new PrefabCloneLedger();
        before.RememberClone(Ember);
        before.RememberAppend(Ember, 1083);
        before.RememberClone(Nodrain);
        before.RememberIdentity(Ember, SourceA, PrefabKind.Item);
        before.ExportClonesAndKeys(out var cloned, out var appended, out var indices);
        before.ExportSources(out var guids, out var sources);

        var after = new PrefabCloneLedger();
        after.ImportClonesAndKeys(cloned, appended, indices);
        after.ImportSources(guids, sources);

        Assert.True(after.WasCloned(Ember));
        Assert.True(after.WasCloned(Nodrain));
        Assert.True(after.TryGetAppendedIndex(Ember, out int idx));
        Assert.True(PrefabCloneLedger.KeyRowStillHolds(idx, currentIndex: 1083));
        Assert.True(after.TryGetIdentity(Ember, out int src, out _, out _));
        Assert.Equal(SourceA, src);
    }

    // ---- gate: connection end vs new world vs Core-swap republish --------------------------------

    [Fact]
    public void Connection_end_closes_the_client_gate_and_is_a_no_op_on_the_server()
    {
        var client = new PrefabApplyGate(PrefabApplyGate.Side.Client);
        client.NotifyWorldReady();
        client.NotifyClientPacket(PrefabApplyGate.ClientPacket.IndexLedger);
        Assert.True(client.Ready);

        Assert.True(client.NotifyClientConnectionEnded());
        Assert.False(client.Ready);
        Assert.True(client.NotifyClientConnectionEnded()); // Error then world end: idempotent

        var server = new PrefabApplyGate(PrefabApplyGate.Side.Server);
        server.NotifyPlayerConnected();
        Assert.False(server.NotifyClientConnectionEnded());
        Assert.True(server.Ready);
    }

    [Fact]
    public void A_new_client_world_forgets_world_bookkeeping_but_a_core_swap_republish_does_not()
    {
        var gate = new PrefabApplyGate(PrefabApplyGate.Side.Client);
        var fresh = gate.NotifyWorldReady();
        Assert.True(fresh.ForgetWorldBookkeeping);
        Assert.True(fresh.DropSyncedOwnerEntries);

        gate.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        gate.NotifyLiveWorldAfterCoreSwap();
        var republish = gate.NotifyWorldReady();
        Assert.False(republish.ForgetWorldBookkeeping);
        Assert.False(republish.DropSyncedOwnerEntries);
        Assert.True(gate.Ready);

        var server = new PrefabApplyGate(PrefabApplyGate.Side.Server).NotifyWorldReady();
        Assert.False(server.ForgetWorldBookkeeping);
    }

    [Fact]
    public void Connection_end_drops_a_pending_core_swap_mark_so_the_next_world_resets()
    {
        var gate = new PrefabApplyGate(PrefabApplyGate.Side.Client);
        gate.NotifyWorldReady();
        gate.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        gate.NotifyLiveWorldAfterCoreSwap(); // the republish never came before the leave

        gate.NotifyClientConnectionEnded();
        var next = gate.NotifyWorldReady();

        Assert.True(next.ForgetWorldBookkeeping);
        Assert.True(next.DropSyncedOwnerEntries);
        Assert.False(gate.Ready);
    }

    // ---- connection-scoped PrfD fill of a local definition ---------------------------------------

    [Fact]
    public void Server_a_fill_of_a_local_definition_is_reverted_so_server_b_fills_from_scratch()
    {
        var fill = new PrefabServerFill();
        var local = new PrefabFillFields(0, 0, null, "Local description", PrefabKind.Item);

        var a = fill.Apply(local, new PrefabFillFields(50, SourceA, "Server A name", "Server A description", PrefabKind.Item));
        Assert.Equal(50, a.MaxAmount);
        Assert.Equal(SourceA, a.SourceHash);
        Assert.Equal("Server A name", a.Name);
        Assert.Equal("Local description", a.Description); // a local value is never overwritten
        Assert.True(fill.NameFromServer);
        Assert.False(fill.DescriptionFromServer);

        Assert.True(fill.TryRevert(out var restored));
        Assert.Equal(0, restored.MaxAmount);
        Assert.Equal(0, restored.SourceHash);
        Assert.Null(restored.Name);
        Assert.Equal("Local description", restored.Description);
        Assert.False(fill.NameFromServer);
        Assert.False(fill.TryRevert(out _));

        var b = fill.Apply(restored, new PrefabFillFields(20, SourceB, "Server B name", null, PrefabKind.Item));
        Assert.Equal(20, b.MaxAmount);
        Assert.Equal(SourceB, b.SourceHash);
        Assert.Equal("Server B name", b.Name);
    }

    [Fact]
    public void Without_the_revert_server_a_values_would_win_over_server_b()
    {
        // The pre-fix behavior, spelled out: the fill only writes empty fields, so once A's
        // values sit on the local definition B's PrfD can no longer change them.
        var fill = new PrefabServerFill();
        var local = new PrefabFillFields(0, 0, null, null, PrefabKind.Item);
        var afterA = fill.Apply(local, new PrefabFillFields(50, SourceA, "Server A name", null, PrefabKind.Item));
        var afterB = fill.Apply(afterA, new PrefabFillFields(20, SourceB, "Server B name", null, PrefabKind.Item));
        Assert.Equal(50, afterB.MaxAmount);
        Assert.Equal("Server A name", afterB.Name);

        // A second fill in the same join keeps the first snapshot, so the revert still lands on local.
        Assert.True(fill.TryRevert(out var restored));
        Assert.Equal(0, restored.MaxAmount);
        Assert.Null(restored.Name);
    }

    [Fact]
    public void A_server_buff_promotion_is_reverted_but_a_local_buff_is_never_demoted()
    {
        var fill = new PrefabServerFill();
        var promoted = fill.Apply(
            new PrefabFillFields(0, SourceA, null, null, PrefabKind.Item),
            new PrefabFillFields(0, SourceA, null, null, PrefabKind.Buff));
        Assert.Equal(PrefabKind.Buff, promoted.Kind);
        Assert.True(fill.TryRevert(out var restored));
        Assert.Equal(PrefabKind.Item, restored.Kind);

        var kept = fill.Apply(
            new PrefabFillFields(0, SourceA, null, null, PrefabKind.Buff),
            new PrefabFillFields(0, SourceA, null, null, PrefabKind.Item));
        Assert.Equal(PrefabKind.Buff, kept.Kind);
    }

    // ---- connection-scoped PrfE layer ----------------------------------------------------------------

    [Fact]
    public void Withdrawing_every_received_layer_leaves_nothing_and_a_rejoin_applies_the_same_bytes_again()
    {
        var wire = new PrefabEditWireLayer();
        var records = new[] { new PrefabEditComponentBytes("ProjectM.LifeTime", new byte[] { 1, 2, 3, 4 }) };
        int hash = PrefabEditWire.PayloadHash(records);
        Assert.True(wire.ShouldApplyReceived(Crystal, hash));
        wire.SetReceived(Crystal, records);
        Assert.True(wire.ShouldApplyReceived(Nodrain, hash));
        wire.SetReceived(Nodrain, records);

        // What the registry does at connection end: each stored layer goes through the withdraw path.
        var guids = wire.ReceivedGuids();
        int withdrawn = 0;
        for (int i = 0; i < guids.Length; i++)
            if (wire.DropReceived(guids[i])) withdrawn++;

        Assert.Equal(2, withdrawn);
        Assert.Empty(wire.ReceivedGuids());
        Assert.False(wire.HasReceived(Crystal));
        // The applied-payload fingerprint went with the layer: rejoining the same server is not deduped away.
        Assert.True(wire.ShouldApplyReceived(Crystal, hash));
    }
}
