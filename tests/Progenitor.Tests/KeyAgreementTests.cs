using System;
using System.Collections.Generic;
using Progenitor.Bootstrap;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of M2.12's narrow client/server item-key contract.
/// Two ledgers built from the same items in different registration orders disagree
/// on index; <see cref="KeyAgreement.Plan"/> rebuilds the client in the server order
/// and refuses missing, conflicting and unknown entries instead of misassigning them.
/// </summary>
public class ItemKeyAgreementTests
{
    private const int Base = 1083;
    private const int Capacity = 2048;
    private const int Ember = 480926629;
    private const int Cinder = -650563057;
    private const int Other = 111111111;

    [Fact]
    public void PrfI_opens_the_client_gate_without_dumping_local_registration_order()
    {
        var g = new PrefabApplyGate(PrefabApplyGate.Side.Client);
        g.NotifyWorldReady();
        var plan = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.IndexLedger);
        Assert.True(g.Ready);
        Assert.True(plan.OpenedGate);
        Assert.False(plan.ApplyPendingFirst);
        var later = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.False(later.OpenedGate);
        Assert.False(later.ApplyPendingFirst);
    }

    [Fact]
    public void Index_is_baseLength_plus_position_and_is_not_a_saved_identity()
    {
        Assert.Equal(1083, KeyIndexPlan.Index(Base, 0));
        Assert.Equal(1084, KeyIndexPlan.Index(Base, 1));
        Assert.Equal(-1, KeyIndexPlan.Index(-1, 0));
        Assert.True(KeyIndexPlan.MatchesPredicted(1083, 1083));
        Assert.False(KeyIndexPlan.MatchesPredicted(1083, 659));
    }

    [Fact]
    public void Ledger_appends_in_registration_order_and_refuses_a_second_claimant_for_one_guid()
    {
        var ledger = new KeyLedger();
        Assert.True(ledger.TryAdd(Ember, "sample.ember", "mod.a", out _));
        Assert.True(ledger.TryAdd(Cinder, "sample.cinder", "mod.b", out _));
        Assert.Equal(0, ledger.PositionOf(Ember));
        Assert.Equal(1, ledger.PositionOf(Cinder));
        Assert.False(ledger.TryAdd(Ember, "sample.ember.alias", "mod.c", out var error));
        Assert.Contains("already held", error);
        Assert.Equal(2, ledger.Count);
        Assert.Equal(0, ledger.PositionOf(Ember));
    }

    [Fact]
    public void Differing_registration_order_assigns_different_indices_to_the_same_item()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        Assert.True(server.TryAdd(Cinder, "sample.cinder", "mod.b", out _));

        var client = new KeyLedger();
        Assert.True(client.TryAdd(Cinder, "sample.cinder", "mod.b", out _));
        Assert.True(client.TryAdd(Ember, "sample.ember", "mod.a", out _));

        int serverEmber = KeyIndexPlan.Index(Base, server.PositionOf(Ember));
        int clientEmber = KeyIndexPlan.Index(Base, client.PositionOf(Ember));
        Assert.Equal(1083, serverEmber);
        Assert.Equal(1084, clientEmber);
        Assert.NotEqual(serverEmber, clientEmber);
        Assert.NotEqual(
            KeyIndexPlan.Index(Base, server.PositionOf(Cinder)),
            KeyIndexPlan.Index(Base, client.PositionOf(Cinder)));
    }

    [Fact]
    public void Plan_rebuilds_the_client_in_server_order_so_the_same_item_shares_an_index()
    {
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Cinder, "sample.cinder"),
            new KeyLocalEntry(Ember, "sample.ember"),
        };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);

        Assert.True(plan.Accepted);
        Assert.True(plan.TryIndexOf(Ember, out int emberIndex));
        Assert.True(plan.TryIndexOf(Cinder, out int cinderIndex));
        Assert.Equal(1083, emberIndex);
        Assert.Equal(1084, cinderIndex);
        Assert.True(plan.IsUsable(Ember));
        Assert.True(plan.IsUsable(Cinder));
        Assert.Equal(KeyAgreementKind.Reorder, plan.Rows[0].Kind);
        Assert.Equal(KeyAgreementKind.Reorder, plan.Rows[1].Kind);
        Assert.Contains("reordered=2", plan.Reason);
    }

    [Fact]
    public void Same_order_is_Agree_and_usable()
    {
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Ember, "sample.ember"),
            new KeyLocalEntry(Cinder, "sample.cinder"),
        };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        Assert.True(plan.Accepted);
        Assert.Equal(KeyAgreementKind.Agree, plan.Rows[0].Kind);
        Assert.Equal(KeyAgreementKind.Agree, plan.Rows[1].Kind);
        Assert.Empty(plan.Unknown);
    }

    [Fact]
    public void Missing_entry_occupies_a_reserved_row_and_stays_unusable()
    {
        var server = ServerEmberThenCinder();
        var local = new[] { new KeyLocalEntry(Ember, "sample.ember") };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);

        Assert.True(plan.Accepted);
        Assert.True(plan.IsUsable(Ember));
        Assert.False(plan.IsUsable(Cinder));
        Assert.Equal(KeyAgreementKind.Missing, plan.Rows[1].Kind);
        Assert.True(plan.Rows[1].OccupiesRow);
        Assert.Equal(1084, plan.Rows[1].Index);
        Assert.Contains("missing on this side", plan.Rows[1].Reason);
        Assert.Contains("unusable", plan.Rows[1].Reason);
    }

    [Fact]
    public void Conflicting_guid_for_the_same_id_is_refused_and_unusable()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        var local = new[] { new KeyLocalEntry(Other, "sample.ember") };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);

        Assert.True(plan.Accepted);
        Assert.Equal(KeyAgreementKind.Conflicting, plan.Rows[0].Kind);
        Assert.False(plan.Rows[0].Usable);
        Assert.True(plan.Rows[0].OccupiesRow);
        Assert.Contains("conflicts with local", plan.Rows[0].Reason);
        Assert.True(plan.IsUnknown(Other));
        Assert.Contains("unknown to the server", plan.Unknown[0].Reason);
    }

    [Fact]
    public void Conflicting_id_for_the_same_guid_is_refused_and_unusable()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        var local = new[] { new KeyLocalEntry(Ember, "sample.other") };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);

        Assert.True(plan.Accepted);
        Assert.Equal(KeyAgreementKind.Conflicting, plan.Rows[0].Kind);
        Assert.False(plan.IsUsable(Ember));
        Assert.Contains("conflicts with local", plan.Rows[0].Reason);
    }

    [Fact]
    public void Unknown_local_extra_is_not_assigned_an_index()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        var local = new[]
        {
            new KeyLocalEntry(Ember, "sample.ember"),
            new KeyLocalEntry(Cinder, "sample.cinder"),
        };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);

        Assert.True(plan.Accepted);
        Assert.True(plan.IsUsable(Ember));
        Assert.False(plan.TryIndexOf(Cinder, out _));
        Assert.True(plan.IsUnknown(Cinder));
        Assert.Equal(-1, plan.Unknown[0].Index);
        Assert.False(plan.Unknown[0].OccupiesRow);
    }

    [Fact]
    public void Client_vanilla_length_mismatch_refuses_the_whole_plan()
    {
        var server = ServerEmberThenCinder();
        var local = new[] { new KeyLocalEntry(Ember, "sample.ember") };

        var plan = KeyAgreement.Plan(Base, clientBaseLength: 659, Capacity, server.Rows, local);

        Assert.False(plan.Accepted);
        Assert.Empty(plan.Rows);
        Assert.Contains("vanilla length 659 != server 1083", plan.Reason);
        Assert.Contains("unusable", plan.Reason);
    }

    [Fact]
    public void Duplicate_server_guid_refuses_the_whole_plan()
    {
        var rows = new List<KeyLedgerRow>
        {
            new KeyLedgerRow(Ember, "sample.ember", "mod.a", false),
            new KeyLedgerRow(Ember, "sample.ember.dup", "mod.b", false),
        };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, rows, localEntries: System.Array.Empty<KeyLocalEntry>());

        Assert.False(plan.Accepted);
        Assert.Contains("already held", plan.Reason);
    }

    [Fact]
    public void Exhausted_headroom_refuses_the_whole_plan()
    {
        var server = ServerEmberThenCinder();
        var plan = KeyAgreement.Plan(2047, 2047, Capacity, server.Rows, System.Array.Empty<KeyLocalEntry>());
        Assert.False(plan.Accepted);
        Assert.Contains("would exceed capacity", plan.Reason);
    }

    [Fact]
    public void Dropped_PrfI_then_PrfD_cannot_resolve_an_index_as_the_wrong_item()
    {
        // Server: ember then cinder → ember at 1083. Client local order is the reverse, so a
        // PrfD-first dump would put cinder at 1083 (the Hermit's-Shawl / wrong-item case).
        var localOrder = new[] { Cinder, Ember };
        var localIds = new[] { "sample.cinder", "sample.ember" };
        Assert.Equal(1084, KeyJoinSession.NaiveLocalIndex(Base, localOrder, Ember));
        Assert.Equal(Cinder, KeyJoinSession.NaiveDecodeIndex(Base, localOrder, 1083));

        var session = new KeyJoinSession();
        Assert.True(session.NoteDefinitionPacket(out var reason));
        Assert.Contains("no PrfI", reason);
        Assert.Contains("unusable", reason);

        // Production path: ApplyOne → ShouldSkipItemAppend → no TryAppend. The naive
        // local-order index must not decode as cinder (or ember). Deleting
        // `if (refused) return true` from ShouldSkipItemAppend appends cinder at 1083.
        Assert.True(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Item, session.Refused, session.Plan, Ember, "sample.ember", out var skipWhy));
        Assert.Contains("refused this join", skipWhy);
        Assert.False(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Buff, session.Refused, session.Plan, Ember, "sample.ember", out _));

        var tail = ApplyPendingItems(session, localOrder, localIds);
        Assert.Empty(tail);
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
        Assert.NotEqual(Ember, GuidAt(tail, 1083));
        Assert.Equal(-1, IndexOf(tail, Ember));
        Assert.Equal(-1, IndexOf(tail, Cinder));
    }

    [Fact]
    public void Refused_join_stays_refused_across_a_ContentMemory_Core_swap()
    {
        var localOrder = new[] { Cinder, Ember };
        var localIds = new[] { "sample.cinder", "sample.ember" };
        var session = new KeyJoinSession();
        Assert.True(session.NoteDefinitionPacket(out _));

        var mem = new ContentMemory();
        SwapThroughContentMemory(session, mem);

        var after = new KeyJoinSession();
        RestoreFromContentMemory(after, mem);

        var tail = ApplyPendingItems(after, localOrder, localIds);
        Assert.Empty(tail);
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
        Assert.NotEqual(Ember, GuidAt(tail, 1083));
        Assert.Equal(-1, IndexOf(tail, Cinder));
        Assert.Equal(-1, IndexOf(tail, Ember));
        Assert.True(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Item, after.Refused, after.Plan, Cinder, "sample.cinder", out _));
    }

    [Fact]
    public void Bootstrap_join_session_handoff_methods_resolve_on_ContentMemory()
    {
        var t = typeof(ContentMemory);
        Assert.NotNull(KeyJoinHandoff.FindMethod(t, KeyJoinHandoff.ReplaceMethodName));
        Assert.NotNull(KeyJoinHandoff.FindMethod(t, KeyJoinHandoff.TakeMethodName));
        Assert.NotNull(KeyJoinHandoff.FindMethod(t, KeyJoinHandoff.ClearMethodName));
    }

    [Fact]
    public void Accepted_ledger_survives_a_ContentMemory_Core_swap_in_server_order()
    {
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Cinder, "sample.cinder"),
            new KeyLocalEntry(Ember, "sample.ember"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.TryAcceptLedger(plan, out _));

        var mem = new ContentMemory();
        SwapThroughContentMemory(session, mem);

        var after = new KeyJoinSession();
        RestoreFromContentMemory(after, mem);

        var tail = ApplyPendingItems(
            after, new[] { Cinder, Ember }, new[] { "sample.cinder", "sample.ember" },
            requireAgreedOrder: true);
        Assert.Equal(2, tail.Count);
        Assert.Equal(Ember, GuidAt(tail, 1083));
        Assert.Equal(Cinder, GuidAt(tail, 1084));
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
        Assert.Equal(1083, IndexOf(tail, Ember));
        Assert.False(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Item, after.Refused, after.Plan, Ember, "sample.ember",
            requireAgreedOrder: true, out _));
    }

    [Fact]
    public void Accepted_ledger_survives_the_live_Core_swap_WorldReady_republish()
    {
        // Live sequence that the ContentMemory-only swap tests did not model:
        // accepted PrfI, Swap-path export, Uninstall Reset, new-generation
        // TryRestoreInto, OpenLiveGate, then EventHooks WorldReady republish
        // (ReadyWorlds cleared on Uninstall). Treating that WorldReady as a
        // rejoin Reset the session to idle; FIX4 then refused at apply, and a
        // later PrfD logged MissingLedgerReason. Ember stayed unusable.
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Cinder, "sample.cinder"),
            new KeyLocalEntry(Ember, "sample.ember"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.False(session.NoteDefinitionPacket(out _));
        Assert.True(session.TryResolveGuid(Ember, out int before));
        Assert.Equal(1083, before);

        var mem = new ContentMemory();
        Assert.NotNull(KeyJoinHandoff.FindMethod(typeof(ContentMemory), KeyJoinHandoff.ReplaceMethodName));
        SwapThroughContentMemory(session, mem);
        Assert.False(session.HasAgreedOrder);

        var after = new KeyJoinSession();
        Assert.True(KeyJoinHandoff.TryRestoreInto(
            mem,
            KeyJoinHandoff.FindMethod,
            after,
            failClosed: true,
            liveWorld: true,
            out var restoreLog));
        Assert.Contains("accepted=true", restoreLog, StringComparison.Ordinal);
        Assert.True(after.HasAgreedOrder);
        Assert.False(after.Refused);

        var gate = new PrefabApplyGate(PrefabApplyGate.Side.Client);
        gate.NotifyLiveWorldAfterCoreSwap();
        Assert.True(gate.Ready);
        var worldReady = gate.NotifyWorldReady();
        Assert.True(gate.Ready);
        Assert.False(worldReady.DropSyncedOwnerEntries);
        Assert.False(worldReady.ClearTombstones);
        Assert.False(worldReady.CloseGate);

        var localOrder = new[] { Cinder, Ember };
        var localIds = new[] { "sample.cinder", "sample.ember" };
        Assert.Equal(Cinder, KeyJoinSession.NaiveDecodeIndex(Base, localOrder, 1083));
        var tail = ApplyPendingItems(after, localOrder, localIds, requireAgreedOrder: true);
        Assert.Equal(2, tail.Count);
        Assert.Equal(Ember, GuidAt(tail, 1083));
        Assert.Equal(Cinder, GuidAt(tail, 1084));
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
        Assert.Equal(1083, IndexOf(tail, Ember));
        Assert.True(after.TryResolveGuid(Ember, out int emberIndex));
        Assert.Equal(1083, emberIndex);
        Assert.False(after.NoteDefinitionPacket(out _));
        Assert.True(after.HasAgreedOrder);
        Assert.False(after.Refused);
    }

    [Fact]
    public void Missing_Bootstrap_TakeJoinSession_refuses_and_cannot_resolve_an_index_as_the_wrong_item()
    {
        // R88 finding 1: GetMethod("TakeJoinSession") returning null used to leave the
        // new generation idle, then OpenLiveGate appended in local order. Fail closed.
        var localOrder = new[] { Cinder, Ember };
        var localIds = new[] { "sample.cinder", "sample.ember" };
        Assert.Equal(Cinder, KeyJoinSession.NaiveDecodeIndex(Base, localOrder, 1083));
        Assert.Null(KeyJoinHandoff.FindMethod(typeof(object), KeyJoinHandoff.TakeMethodName));

        var session = new KeyJoinSession();
        Assert.False(KeyJoinHandoff.TryRestoreInto(
            new object(),
            KeyJoinHandoff.FindMethod,
            session,
            failClosed: true,
            liveWorld: true,
            out var log));
        Assert.True(session.Refused);
        Assert.False(session.HasAgreedOrder);
        Assert.Contains("TakeJoinSession", log, StringComparison.Ordinal);
        Assert.Contains("restarted", log, StringComparison.Ordinal);

        // Production skip after a refused restore: even without the idle-gate guard,
        // refused must empty the tail. Deleting RefusePacket from TryRestoreInto
        // puts cinder at 1083.
        var tail = ApplyPendingItems(session, localOrder, localIds, requireAgreedOrder: false);
        Assert.Empty(tail);
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
        Assert.NotEqual(Ember, GuidAt(tail, 1083));
        Assert.Equal(-1, IndexOf(tail, Ember));
        Assert.Equal(-1, IndexOf(tail, Cinder));
    }

    [Fact]
    public void Missing_Bootstrap_TakeJoinSession_lookup_returning_null_refuses_not_idle()
    {
        var session = new KeyJoinSession();
        Assert.False(KeyJoinHandoff.TryRestoreInto(
            new ContentMemory(),
            (Type _, string _) => null,
            session,
            failClosed: true,
            liveWorld: true,
            out var log));
        Assert.True(session.Refused);
        Assert.False(session.HasAgreedOrder);
        Assert.Null(session.Plan);
        Assert.Contains(KeyJoinHandoff.TakeMethodName, log, StringComparison.Ordinal);
        Assert.Contains("unavailable until the process is restarted", log, StringComparison.Ordinal);

        var localOrder = new[] { Cinder, Ember };
        var tail = ApplyPendingItems(
            session, localOrder, new[] { "sample.cinder", "sample.ember" }, requireAgreedOrder: false);
        Assert.Empty(tail);
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
    }

    [Fact]
    public void Restore_invoke_throw_or_unpack_failure_refuses_and_does_not_assign_a_local_index()
    {
        var localOrder = new[] { Cinder, Ember };
        var localIds = new[] { "sample.cinder", "sample.ember" };

        var thrown = new KeyJoinSession();
        Assert.False(KeyJoinHandoff.TryRestoreInto(
            new ThrowingJoinMemory(),
            KeyJoinHandoff.FindMethod,
            thrown,
            failClosed: true,
            liveWorld: true,
            out var throwLog));
        Assert.True(thrown.Refused);
        Assert.Contains("handoff exploded", throwLog, StringComparison.Ordinal);
        var thrownTail = ApplyPendingItems(thrown, localOrder, localIds, requireAgreedOrder: false);
        Assert.Empty(thrownTail);
        Assert.NotEqual(Cinder, GuidAt(thrownTail, 1083));

        var bad = new KeyJoinSession();
        Assert.False(KeyJoinHandoff.TryRestoreInto(
            new BadHeaderJoinMemory(),
            KeyJoinHandoff.FindMethod,
            bad,
            failClosed: true,
            liveWorld: true,
            out var unpackLog));
        Assert.True(bad.Refused);
        Assert.Contains("handoff arrays disagree", unpackLog, StringComparison.Ordinal);
        var badTail = ApplyPendingItems(bad, localOrder, localIds, requireAgreedOrder: false);
        Assert.Empty(badTail);
        Assert.NotEqual(Cinder, GuidAt(badTail, 1083));
    }

    [Fact]
    public void Idle_session_after_a_Core_swap_cannot_resolve_an_index_as_the_wrong_item()
    {
        // R88 finding 2: Capture/Restore of idle (join in progress, or PrfI dropped in
        // the empty-slot window) used to open the live-world gate and append in local
        // order. Idle is not permission to append.
        var localOrder = new[] { Cinder, Ember };
        var localIds = new[] { "sample.cinder", "sample.ember" };
        Assert.Equal(Cinder, KeyJoinSession.NaiveDecodeIndex(Base, localOrder, 1083));
        Assert.Equal(1084, KeyJoinSession.NaiveLocalIndex(Base, localOrder, Ember));

        var session = new KeyJoinSession();
        Assert.False(session.Refused);
        Assert.False(session.HasAgreedOrder);

        var mem = new ContentMemory();
        SwapThroughContentMemory(session, mem);
        var after = new KeyJoinSession();
        RestoreFromContentMemory(after, mem);
        Assert.False(after.Refused);
        Assert.False(after.HasAgreedOrder);

        Assert.True(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Item, after.Refused, after.Plan, Cinder, "sample.cinder",
            requireAgreedOrder: true, out var skipWhy));
        Assert.Contains("no PrfI", skipWhy);
        Assert.False(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Buff, after.Refused, after.Plan, Cinder, "sample.cinder",
            requireAgreedOrder: true, out _));

        var tail = ApplyPendingItems(after, localOrder, localIds, requireAgreedOrder: true);
        Assert.Empty(tail);
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
        Assert.NotEqual(Ember, GuidAt(tail, 1083));
        Assert.Equal(-1, IndexOf(tail, Ember));
        Assert.Equal(-1, IndexOf(tail, Cinder));
    }

    [Fact]
    public void Idle_client_still_accepts_a_later_PrfI_in_server_order()
    {
        var session = new KeyJoinSession();
        var mem = new ContentMemory();
        SwapThroughContentMemory(session, mem);
        var after = new KeyJoinSession();
        RestoreFromContentMemory(after, mem);
        Assert.False(after.Refused);
        Assert.Empty(ApplyPendingItems(
            after, new[] { Cinder, Ember }, new[] { "sample.cinder", "sample.ember" },
            requireAgreedOrder: true));

        var plan = KeyAgreement.Plan(
            Base, Base, Capacity, ServerEmberThenCinder().Rows,
            new[]
            {
                new KeyLocalEntry(Cinder, "sample.cinder"),
                new KeyLocalEntry(Ember, "sample.ember"),
            });
        Assert.True(after.TryAcceptLedger(plan, out _));
        var tail = ApplyPendingItems(
            after, new[] { Cinder, Ember }, new[] { "sample.cinder", "sample.ember" },
            requireAgreedOrder: true);
        Assert.Equal(Ember, GuidAt(tail, 1083));
        Assert.Equal(Cinder, GuidAt(tail, 1084));
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
    }

    [Fact]
    public void Server_and_host_idle_session_still_appends_in_its_own_order()
    {
        // Server process (dedicated or hosted/single-player) is the authority: no PrfI,
        // requireAgreedOrder is false, idle appends at list->Length in registration order.
        var session = new KeyJoinSession();
        Assert.False(session.Refused);
        Assert.False(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Item, session.Refused, session.Plan, Ember, "sample.ember",
            requireAgreedOrder: false, out _));
        var tail = ApplyPendingItems(
            session, new[] { Ember, Cinder }, new[] { "sample.ember", "sample.cinder" },
            requireAgreedOrder: false);
        Assert.Equal(Ember, GuidAt(tail, 1083));
        Assert.Equal(Cinder, GuidAt(tail, 1084));
        Assert.Equal(1083, IndexOf(tail, Ember));
    }

    [Fact]
    public void WorldReady_closed_gate_does_not_treat_idle_as_refused()
    {
        var session = new KeyJoinSession();
        Assert.False(KeyJoinSession.ShouldSkipItemAppend(
            PrefabKind.Item, session.Refused, session.Plan, Ember, "sample.ember",
            requireAgreedOrder: false, out _));
    }

    [Fact]
    public void Server_missing_TakeJoinSession_stays_idle_not_refused()
    {
        var session = new KeyJoinSession();
        Assert.False(KeyJoinHandoff.TryRestoreInto(
            new object(),
            KeyJoinHandoff.FindMethod,
            session,
            failClosed: false,
            liveWorld: true,
            out _));
        Assert.False(session.Refused);
        Assert.False(session.HasAgreedOrder);
        var tail = ApplyPendingItems(
            session, new[] { Ember, Cinder }, new[] { "sample.ember", "sample.cinder" },
            requireAgreedOrder: false);
        Assert.Equal(Ember, GuidAt(tail, 1083));
        Assert.NotEqual(Cinder, GuidAt(tail, 1083));
    }

    [Fact]
    public void Client_cold_start_empty_store_stays_idle_so_a_later_PrfI_can_accept()
    {
        var session = new KeyJoinSession();
        Assert.False(KeyJoinHandoff.TryRestoreInto(
            new ContentMemory(),
            KeyJoinHandoff.FindMethod,
            session,
            failClosed: true,
            liveWorld: false,
            out _));
        Assert.False(session.Refused);
        var plan = KeyAgreement.Plan(
            Base, Base, Capacity, ServerEmberThenCinder().Rows,
            new[] { new KeyLocalEntry(Ember, "sample.ember") });
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.True(session.TryResolveGuid(Ember, out int index));
        Assert.Equal(1083, index);
    }

    [Fact]
    public void A_fresh_join_after_reset_is_not_stuck_refused()
    {
        var session = new KeyJoinSession();
        Assert.True(session.NoteDefinitionPacket(out _));
        var mem = new ContentMemory();
        SwapThroughContentMemory(session, mem);
        RestoreFromContentMemory(session, mem);
        Assert.True(session.Refused);

        session.Reset();
        Assert.Equal(ContentMemoryStore.Cleared, mem.ClearJoinSession());
        var idle = new KeyJoinSession();
        mem.TakeJoinSession(
            out var header, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _);
        Assert.Empty(header);
        Assert.False(idle.Refused);

        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Ember, "sample.ember"),
            new KeyLocalEntry(Cinder, "sample.cinder"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        Assert.True(idle.TryAcceptLedger(plan, out _));
        var tail = ApplyPendingItems(idle, new[] { Ember, Cinder }, new[] { "sample.ember", "sample.cinder" });
        Assert.Equal(Ember, GuidAt(tail, 1083));
        Assert.Equal(Cinder, GuidAt(tail, 1084));
    }

    [Fact]
    public void Accepted_PrfI_resolves_the_server_index_even_when_local_order_differs()
    {
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Cinder, "sample.cinder"),
            new KeyLocalEntry(Ember, "sample.ember"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.False(session.NoteDefinitionPacket(out _));
        Assert.False(session.ShouldSkipItem);
        Assert.True(session.TryResolveGuid(Ember, out int emberIndex));
        Assert.Equal(1083, emberIndex);
        Assert.True(session.TryDecodeIndex(1083, out int decoded));
        Assert.Equal(Ember, decoded);
        Assert.NotEqual(Cinder, decoded);
    }

    [Fact]
    public void Second_PrfI_is_ignored_and_does_not_rebuild_a_good_first_order()
    {
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Ember, "sample.ember"),
            new KeyLocalEntry(Cinder, "sample.cinder"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.False(session.TryAcceptLedger(plan, out var duplicate));
        Assert.Contains("second PrfI", duplicate);
        Assert.False(session.Refused);
        Assert.True(session.TryResolveGuid(Ember, out int emberIndex));
        Assert.Equal(1083, emberIndex);
    }

    [Fact]
    public void PrfI_after_PrfD_refuses_and_does_not_assign_an_index()
    {
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Cinder, "sample.cinder"),
            new KeyLocalEntry(Ember, "sample.ember"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.NoteDefinitionPacket(out _));
        Assert.False(session.TryAcceptLedger(plan, out var late));
        Assert.Contains("after PrfD", late);
        Assert.True(session.Refused);
        Assert.True(session.ShouldSkipItem);
        Assert.False(session.TryResolveGuid(Ember, out _));
        Assert.False(session.TryDecodeIndex(1083, out int decoded));
        Assert.NotEqual(Cinder, decoded);
    }

    [Fact]
    public void Corrupt_PrfI_refuses_the_join_and_a_later_definition_cannot_append()
    {
        var session = new KeyJoinSession();
        session.RefusePacket(KeyAgreement.PacketRefusedReason("payload truncated at row 0."));
        Assert.True(session.Refused);
        Assert.True(session.SawLedger);
        Assert.False(session.NoteDefinitionPacket(out _));
        Assert.True(session.ShouldSkipItem);
        Assert.False(session.TryResolveGuid(Ember, out _));
    }

    [Fact]
    public void Missing_guid_is_unusable_at_its_reserved_index()
    {
        var server = ServerEmberThenCinder();
        var local = new[] { new KeyLocalEntry(Ember, "sample.ember") };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.True(session.TryResolveGuid(Ember, out _));
        Assert.False(session.TryResolveGuid(Cinder, out int cinderIndex));
        Assert.Equal(-1, cinderIndex);
        Assert.False(session.TryDecodeIndex(1084, out int decoded));
        Assert.NotEqual(Cinder, decoded);
        Assert.NotEqual(Ember, decoded);
    }

    [Fact]
    public void Conflicting_guid_does_not_decode_as_the_local_extra()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        var local = new[] { new KeyLocalEntry(Other, "sample.ember") };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        var session = new KeyJoinSession();
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.False(session.TryResolveGuid(Ember, out _));
        Assert.False(session.TryResolveGuid(Other, out _));
        Assert.False(session.TryDecodeIndex(1083, out int decoded));
        Assert.NotEqual(Other, decoded);
        Assert.NotEqual(Ember, decoded);
    }

    [Fact]
    public void Reset_allows_a_later_join_to_accept_a_new_ledger()
    {
        var session = new KeyJoinSession();
        Assert.True(session.NoteDefinitionPacket(out _));
        session.Reset();
        var server = ServerEmberThenCinder();
        var local = new[]
        {
            new KeyLocalEntry(Ember, "sample.ember"),
            new KeyLocalEntry(Cinder, "sample.cinder"),
        };
        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);
        Assert.True(session.TryAcceptLedger(plan, out _));
        Assert.False(session.Refused);
        Assert.True(session.TryResolveGuid(Ember, out int emberIndex));
        Assert.Equal(1083, emberIndex);
    }

    [Fact]
    public void Tombstone_row_occupies_its_index_but_is_not_usable()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        Assert.True(server.TryMarkTombstone(Ember));
        var local = new[] { new KeyLocalEntry(Ember, "sample.ember") };

        var plan = KeyAgreement.Plan(Base, Base, Capacity, server.Rows, local);

        Assert.True(plan.Accepted);
        Assert.True(plan.TryIndexOf(Ember, out int index));
        Assert.Equal(1083, index);
        Assert.False(plan.IsUsable(Ember));
    }

    private static KeyLedger ServerEmberThenCinder()
    {
        var server = new KeyLedger();
        Assert.True(server.TryAdd(Ember, "sample.ember", "mod.a", out _));
        Assert.True(server.TryAdd(Cinder, "sample.cinder", "mod.b", out _));
        return server;
    }

    /// <summary>
    /// Game-free <c>PrefabRegistry.ApplyPending</c> for custom items. An accepted plan
    /// occupies the server tail; otherwise local registration order, skipping rows the
    /// production skip predicate refuses. Index = <see cref="Base"/> + position.
    /// <paramref name="requireAgreedOrder"/> is the client live-world gate.
    /// </summary>
    private static List<int> ApplyPendingItems(
        KeyJoinSession session,
        IReadOnlyList<int> localOrder,
        IReadOnlyList<string> localIds,
        bool requireAgreedOrder = false)
    {
        var tail = new List<int>();
        var plan = session.Plan;
        if (plan != null && plan.Accepted)
        {
            for (int i = 0; i < plan.Rows.Count; i++)
            {
                var row = plan.Rows[i];
                if (!row.Usable) continue;
                if (!ContainsGuid(localOrder, row.GuidHash)) continue;
                if (tail.Contains(row.GuidHash)) continue;
                tail.Add(row.GuidHash);
            }
            return tail;
        }

        for (int i = 0; i < localOrder.Count; i++)
        {
            string id = i < localIds.Count ? localIds[i] : "";
            if (KeyJoinSession.ShouldSkipItemAppend(
                PrefabKind.Item, session.Refused, session.Plan, localOrder[i], id,
                requireAgreedOrder, out _))
                continue;
            if (tail.Contains(localOrder[i])) continue;
            tail.Add(localOrder[i]);
        }
        return tail;
    }

    private static bool ContainsGuid(IReadOnlyList<int> order, int guid)
    {
        for (int i = 0; i < order.Count; i++)
        {
            if (order[i] == guid) return true;
        }
        return false;
    }

    private static int IndexOf(List<int> tail, int guid)
    {
        int pos = tail.IndexOf(guid);
        return pos < 0 ? -1 : Base + pos;
    }

    private static int GuidAt(List<int> tail, int index)
    {
        int pos = index - Base;
        if (pos < 0 || pos >= tail.Count) return 0;
        return tail[pos];
    }

    private static void SwapThroughContentMemory(KeyJoinSession session, ContentMemory mem)
    {
        var snap = session.Capture();
        snap.Pack(
            out var header,
            out var rowGuids,
            out var rowIds,
            out var rowIndices,
            out var rowKinds,
            out var rowUsable,
            out var rowReasons,
            out var unknownGuids,
            out var unknownIds,
            out var unknownReasons,
            out var reason);
        Assert.Equal(ContentMemoryStore.Replaced, mem.ReplaceJoinSession(
            header, rowGuids, rowIds, rowIndices, rowKinds, rowUsable, rowReasons,
            unknownGuids, unknownIds, unknownReasons, reason));
        session.Reset();
    }

    private static void RestoreFromContentMemory(KeyJoinSession session, ContentMemory mem)
    {
        mem.TakeJoinSession(
            out var header,
            out var rowGuids,
            out var rowIds,
            out var rowIndices,
            out var rowKinds,
            out var rowUsable,
            out var rowReasons,
            out var unknownGuids,
            out var unknownIds,
            out var unknownReasons,
            out var reason);
        Assert.True(KeyJoinSnapshot.TryUnpack(
            header, rowGuids, rowIds, rowIndices, rowKinds, rowUsable, rowReasons,
            unknownGuids, unknownIds, unknownReasons, reason, out var snap));
        session.Restore(snap);
    }

    /// <summary>Bootstrap-shaped store whose TakeJoinSession throws.</summary>
    private sealed class ThrowingJoinMemory
    {
        public void TakeJoinSession(
            out int[] header,
            out int[] rowGuids,
            out string[] rowIds,
            out int[] rowIndices,
            out int[] rowKinds,
            out int[] rowUsable,
            out string[] rowReasons,
            out int[] unknownGuids,
            out string[] unknownIds,
            out string[] unknownReasons,
            out string reason)
        {
            header = Array.Empty<int>();
            rowGuids = Array.Empty<int>();
            rowIds = Array.Empty<string>();
            rowIndices = Array.Empty<int>();
            rowKinds = Array.Empty<int>();
            rowUsable = Array.Empty<int>();
            rowReasons = Array.Empty<string>();
            unknownGuids = Array.Empty<int>();
            unknownIds = Array.Empty<string>();
            unknownReasons = Array.Empty<string>();
            reason = "";
            throw new InvalidOperationException("handoff exploded");
        }
    }

    /// <summary>Bootstrap-shaped store whose TakeJoinSession returns an unpackable header.</summary>
    private sealed class BadHeaderJoinMemory
    {
        public void TakeJoinSession(
            out int[] header,
            out int[] rowGuids,
            out string[] rowIds,
            out int[] rowIndices,
            out int[] rowKinds,
            out int[] rowUsable,
            out string[] rowReasons,
            out int[] unknownGuids,
            out string[] unknownIds,
            out string[] unknownReasons,
            out string reason)
        {
            header = new[] { 999 };
            rowGuids = Array.Empty<int>();
            rowIds = Array.Empty<string>();
            rowIndices = Array.Empty<int>();
            rowKinds = Array.Empty<int>();
            rowUsable = Array.Empty<int>();
            rowReasons = Array.Empty<string>();
            unknownGuids = Array.Empty<int>();
            unknownIds = Array.Empty<string>();
            unknownReasons = Array.Empty<string>();
            reason = "";
        }
    }
}
