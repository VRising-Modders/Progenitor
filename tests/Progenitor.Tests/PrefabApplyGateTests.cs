using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the cloned-item apply gate (review R4 findings 1–3): WorldReady,
/// first PrfD / PrfK, PlayerConnected, load prefix, no-player fallback, and the client
/// give-up while waiting for a packet that never comes.
/// Linked through <c>PrefabApplyGate.cs</c>.
/// </summary>
public class PrefabApplyGateTests
{
    private static PrefabApplyGate Client() => new(PrefabApplyGate.Side.Client);
    private static PrefabApplyGate Server() => new(PrefabApplyGate.Side.Server);

    [Fact]
    public void Starts_closed_on_both_sides()
    {
        var client = Client();
        var server = Server();
        Assert.False(client.Ready);
        Assert.False(server.Ready);
        Assert.Equal(PrefabApplyGate.Side.Client, client.ProcessSide);
        Assert.Equal(PrefabApplyGate.Side.Server, server.ProcessSide);
        Assert.True(client.ShouldRunFrameRetry);
        Assert.False(server.ShouldRunFrameRetry);
    }

    [Fact]
    public void No_player_fallback_is_30_seconds()
    {
        Assert.Equal(30.0, PrefabApplyGate.NoPlayerFallbackSeconds);
    }

    [Fact]
    public void Client_WorldReady_closes_drops_synced_clears_tombs_and_does_not_append_placeholders()
    {
        var g = Client();
        g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.True(g.Ready);

        var plan = g.NotifyWorldReady();

        Assert.False(g.Ready);
        Assert.True(plan.CloseGate);
        Assert.True(plan.DropSyncedOwnerEntries);
        Assert.True(plan.ClearTombstones);
        Assert.True(plan.ApplyPending);
        Assert.False(plan.ApplyPlaceholders);
        Assert.False(plan.ScheduleNoPlayerFallback);
        Assert.False(PrefabApplyGate.ShouldApplyPlaceholders(g.Ready));
    }

    [Fact]
    public void Client_WorldReady_while_already_closed_still_asks_to_drop_last_join_state()
    {
        var g = Client();
        var plan = g.NotifyWorldReady();
        Assert.False(g.Ready);
        Assert.True(plan.DropSyncedOwnerEntries);
        Assert.True(plan.ClearTombstones);
        Assert.False(plan.ApplyPlaceholders);
    }

    [Fact]
    public void Server_WorldReady_does_not_open_and_schedules_no_player_fallback()
    {
        var g = Server();
        var plan = g.NotifyWorldReady();

        Assert.False(g.Ready);
        Assert.False(plan.CloseGate);
        Assert.False(plan.DropSyncedOwnerEntries);
        Assert.False(plan.ClearTombstones);
        Assert.True(plan.ApplyPending);
        Assert.False(plan.ApplyPlaceholders);
        Assert.True(plan.ScheduleNoPlayerFallback);
        Assert.False(g.ShouldRunFrameRetry);
        Assert.False(PrefabApplyGate.ShouldApplyPlaceholders(g.Ready));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void First_client_packet_opens_and_applies_pending_before_this_payload(bool placeholder)
    {
        var g = Client();
        g.NotifyWorldReady();
        Assert.False(g.Ready);

        var packet = placeholder
            ? PrefabApplyGate.ClientPacket.KeyPlaceholder
            : PrefabApplyGate.ClientPacket.Definition;
        var plan = g.NotifyClientPacket(packet);

        Assert.True(g.Ready);
        Assert.True(plan.OpenedGate);
        Assert.True(plan.ApplyPendingFirst);
        Assert.True(plan.HandleThisPacket);
        Assert.True(PrefabApplyGate.ShouldApplyPlaceholders(g.Ready));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Later_client_packet_of_the_same_join_does_not_dump_pending_again(bool firstIsPlaceholder)
    {
        var g = Client();
        g.NotifyWorldReady();
        var first = firstIsPlaceholder
            ? PrefabApplyGate.ClientPacket.KeyPlaceholder
            : PrefabApplyGate.ClientPacket.Definition;
        g.NotifyClientPacket(first);

        var second = g.NotifyClientPacket(
            firstIsPlaceholder
                ? PrefabApplyGate.ClientPacket.Definition
                : PrefabApplyGate.ClientPacket.KeyPlaceholder);

        Assert.True(g.Ready);
        Assert.False(second.OpenedGate);
        Assert.False(second.ApplyPendingFirst);
        Assert.True(second.HandleThisPacket);
    }

    [Fact]
    public void PrfK_then_PrfD_only_the_first_opens()
    {
        var g = Client();
        g.NotifyWorldReady();
        var k = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.KeyPlaceholder);
        var d = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.True(k.OpenedGate);
        Assert.False(d.OpenedGate);
        Assert.True(g.Ready);
    }

    [Fact]
    public void PrfI_opens_the_gate_but_does_not_dump_pending_in_local_order()
    {
        var g = Client();
        g.NotifyWorldReady();
        var plan = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.IndexLedger);
        Assert.True(g.Ready);
        Assert.True(plan.OpenedGate);
        Assert.False(plan.ApplyPendingFirst);
        Assert.True(plan.HandleThisPacket);

        var later = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.False(later.OpenedGate);
        Assert.False(later.ApplyPendingFirst);
    }

    [Fact]
    public void Client_rejoin_closes_then_next_packet_opens_again()
    {
        var g = Client();
        g.NotifyWorldReady();
        g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.True(g.Ready);

        var rejoin = g.NotifyWorldReady();
        Assert.False(g.Ready);
        Assert.True(rejoin.DropSyncedOwnerEntries);
        Assert.True(rejoin.ClearTombstones);
        Assert.False(rejoin.ApplyPlaceholders);

        var again = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.KeyPlaceholder);
        Assert.True(g.Ready);
        Assert.True(again.OpenedGate);
        Assert.True(again.ApplyPendingFirst);
    }

    [Fact]
    public void Client_packet_is_a_no_op_on_the_server_gate()
    {
        var g = Server();
        var plan = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.False(g.Ready);
        Assert.False(plan.OpenedGate);
        Assert.False(plan.HandleThisPacket);
    }

    [Fact]
    public void Server_PlayerConnected_opens_applies_before_send_and_sends_only_applied()
    {
        var g = Server();
        g.NotifyWorldReady();
        Assert.False(g.Ready);

        var plan = g.NotifyPlayerConnected();

        Assert.True(g.Ready);
        Assert.True(plan.OpenGate);
        Assert.True(plan.ApplyPendingBeforeSend);
        Assert.True(plan.ApplyPlaceholdersBeforeSend);
        Assert.True(plan.SendOnlyApplied);
        Assert.True(PrefabApplyGate.ShouldSendDefinition(true));
        Assert.False(PrefabApplyGate.ShouldSendDefinition(false));
        Assert.True(PrefabApplyGate.ShouldApplyPlaceholders(g.Ready));
        Assert.True(g.ShouldRunFrameRetry);
    }

    [Fact]
    public void PlayerConnected_is_a_no_op_on_the_client_gate()
    {
        var g = Client();
        var plan = g.NotifyPlayerConnected();
        Assert.False(g.Ready);
        Assert.False(plan.OpenGate);
        Assert.False(plan.ApplyPendingBeforeSend);
        Assert.False(plan.SendOnlyApplied);
    }

    [Fact]
    public void Server_load_prefix_opens_and_applies()
    {
        var g = Server();
        g.NotifyWorldReady();
        var plan = g.NotifyLoadPrefix();
        Assert.True(g.Ready);
        Assert.True(plan.Opened);
        Assert.True(plan.ApplyPending);
        Assert.True(plan.ApplyPlaceholders);
    }

    [Fact]
    public void Load_prefix_is_a_no_op_on_the_client_gate()
    {
        var g = Client();
        var plan = g.NotifyLoadPrefix();
        Assert.False(g.Ready);
        Assert.False(plan.Opened);
        Assert.False(plan.ApplyPending);
    }

    [Fact]
    public void No_player_fallback_opens_only_when_still_closed()
    {
        var g = Server();
        g.NotifyWorldReady();
        var first = g.NotifyNoPlayerFallback();
        Assert.True(first.Opened);
        Assert.True(first.ApplyPending);
        Assert.True(first.ApplyPlaceholders);
        Assert.True(g.Ready);

        var second = g.NotifyNoPlayerFallback();
        Assert.False(second.Opened);
        Assert.False(second.ApplyPending);
        Assert.True(g.Ready);
    }

    [Fact]
    public void No_player_fallback_does_not_open_after_PlayerConnected()
    {
        var g = Server();
        g.NotifyWorldReady();
        g.NotifyPlayerConnected();
        var fallback = g.NotifyNoPlayerFallback();
        Assert.False(fallback.Opened);
        Assert.True(g.Ready);
    }

    [Fact]
    public void No_player_fallback_does_not_open_after_load_prefix()
    {
        var g = Server();
        g.NotifyWorldReady();
        g.NotifyLoadPrefix();
        var fallback = g.NotifyNoPlayerFallback();
        Assert.False(fallback.Opened);
        Assert.True(g.Ready);
    }

    [Fact]
    public void No_player_fallback_is_a_no_op_on_the_client_gate()
    {
        var g = Client();
        var plan = g.NotifyNoPlayerFallback();
        Assert.False(plan.Opened);
        Assert.False(g.Ready);
    }

    [Fact]
    public void Server_WorldReady_then_PlayerConnected_then_send_only_applied()
    {
        var g = Server();
        var ready = g.NotifyWorldReady();
        Assert.False(g.Ready);
        Assert.True(ready.ScheduleNoPlayerFallback);
        Assert.False(ready.ApplyPlaceholders);

        var connect = g.NotifyPlayerConnected();
        Assert.True(g.Ready);
        Assert.True(connect.ApplyPendingBeforeSend);
        Assert.True(connect.SendOnlyApplied);
        Assert.False(PrefabApplyGate.ShouldSendDefinition(applied: false));
    }

    [Fact]
    public void Client_while_gated_counts_frames_never_applies_and_gives_up_once()
    {
        var g = Client();
        g.NotifyWorldReady();
        Assert.False(g.Ready);

        var first = g.StepFrame(0, budget: 3);
        Assert.True(first.CountThisFrame);
        Assert.Equal(1, first.FramesAfter);
        Assert.False(first.GiveUp);
        Assert.False(first.RetryApply);
        Assert.False(first.GiveUpBecauseNoServerPacket);

        var second = g.StepFrame(1, 3);
        Assert.Equal(2, second.FramesAfter);
        Assert.False(second.GiveUp);
        Assert.False(second.RetryApply);

        var last = g.StepFrame(2, 3);
        Assert.Equal(3, last.FramesAfter);
        Assert.True(last.GiveUp);
        Assert.False(last.RetryApply);
        Assert.True(last.GiveUpBecauseNoServerPacket);
        Assert.False(g.Ready);
    }

    [Fact]
    public void Client_give_up_at_production_budget_does_not_apply()
    {
        var g = Client();
        g.NotifyWorldReady();
        int budget = PrefabWire.ApplyGiveUpFrames;
        var almost = g.StepFrame(budget - 2, budget);
        Assert.False(almost.GiveUp);
        Assert.False(almost.RetryApply);

        var done = g.StepFrame(budget - 1, budget);
        Assert.True(done.GiveUp);
        Assert.True(done.GiveUpBecauseNoServerPacket);
        Assert.False(done.RetryApply);
        Assert.Equal(budget, done.FramesAfter);
    }

    [Fact]
    public void Packet_after_client_give_up_still_opens_the_gate()
    {
        var g = Client();
        g.NotifyWorldReady();
        g.StepFrame(2, 3);
        Assert.False(g.Ready);

        var plan = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.True(g.Ready);
        Assert.True(plan.OpenedGate);
        Assert.True(plan.ApplyPendingFirst);
    }

    [Fact]
    public void Server_while_gated_does_not_count_or_give_up()
    {
        var g = Server();
        g.NotifyWorldReady();
        var step = g.StepFrame(0, budget: 3);
        Assert.False(step.CountThisFrame);
        Assert.Equal(0, step.FramesAfter);
        Assert.False(step.GiveUp);
        Assert.False(step.RetryApply);
        Assert.False(g.ShouldRunFrameRetry);

        var later = g.StepFrame(3599, PrefabWire.ApplyGiveUpFrames);
        Assert.False(later.CountThisFrame);
        Assert.Equal(3599, later.FramesAfter);
        Assert.False(later.GiveUp);
    }

    [Fact]
    public void Open_gate_retries_until_budget_then_gives_up_without_the_packet_message()
    {
        var g = Server();
        g.NotifyPlayerConnected();
        Assert.True(g.Ready);

        var mid = g.StepFrame(0, budget: 2);
        Assert.True(mid.CountThisFrame);
        Assert.Equal(1, mid.FramesAfter);
        Assert.True(mid.RetryApply);
        Assert.False(mid.GiveUp);
        Assert.False(mid.GiveUpBecauseNoServerPacket);

        var last = g.StepFrame(1, 2);
        Assert.True(last.GiveUp);
        Assert.False(last.RetryApply);
        Assert.False(last.GiveUpBecauseNoServerPacket);
    }

    [Fact]
    public void Client_after_packet_retries_apply_like_the_server()
    {
        var g = Client();
        g.NotifyWorldReady();
        g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        var step = g.StepFrame(0, budget: 10);
        Assert.True(step.RetryApply);
        Assert.False(step.GiveUpBecauseNoServerPacket);
    }

    [Fact]
    public void ShouldSendDefinition_is_the_Applied_flag()
    {
        Assert.True(PrefabApplyGate.ShouldSendDefinition(true));
        Assert.False(PrefabApplyGate.ShouldSendDefinition(false));
    }

    [Fact]
    public void ShouldApplyPlaceholders_follows_the_gate()
    {
        Assert.False(PrefabApplyGate.ShouldApplyPlaceholders(false));
        Assert.True(PrefabApplyGate.ShouldApplyPlaceholders(true));
    }

    [Fact]
    public void ReachedGiveUp_is_inclusive()
    {
        Assert.False(PrefabApplyGate.ReachedGiveUp(3599, 3600));
        Assert.True(PrefabApplyGate.ReachedGiveUp(3600, 3600));
        Assert.True(PrefabApplyGate.ReachedGiveUp(3601, 3600));
    }

    [Fact]
    public void StepFrame_rejects_a_zero_budget()
    {
        var g = Client();
        Assert.Throws<ArgumentOutOfRangeException>(() => g.StepFrame(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => g.StepFrame(-1, 3));
    }

    [Fact]
    public void Full_client_join_sequence_never_appends_placeholders_at_WorldReady()
    {
        var g = Client();
        var worldReady = g.NotifyWorldReady();
        Assert.False(worldReady.ApplyPlaceholders);
        Assert.True(worldReady.DropSyncedOwnerEntries);

        var waiting = g.StepFrame(0, 5);
        Assert.False(waiting.RetryApply);

        var prfD = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        Assert.True(prfD.ApplyPendingFirst);
        var prfK = g.NotifyClientPacket(PrefabApplyGate.ClientPacket.KeyPlaceholder);
        Assert.False(prfK.ApplyPendingFirst);
        Assert.True(prfK.HandleThisPacket);
    }

    [Fact]
    public void Full_server_new_world_connect_applies_before_any_send()
    {
        var g = Server();
        var worldReady = g.NotifyWorldReady();
        Assert.False(g.Ready);
        Assert.True(worldReady.ScheduleNoPlayerFallback);
        Assert.False(worldReady.ApplyPlaceholders);

        var gated = g.StepFrame(0, 10);
        Assert.False(gated.CountThisFrame);

        var connect = g.NotifyPlayerConnected();
        Assert.True(connect.ApplyPendingBeforeSend);
        Assert.True(connect.SendOnlyApplied);
        Assert.True(g.Ready);
        Assert.False(g.NotifyNoPlayerFallback().Opened);
    }

    [Fact]
    public void Live_world_after_Core_swap_opens_a_closed_client_gate()
    {
        var g = Client();
        g.NotifyWorldReady();
        Assert.False(g.Ready);

        var plan = g.NotifyLiveWorldAfterCoreSwap();

        Assert.True(g.Ready);
        Assert.True(plan.Opened);
        Assert.True(plan.ApplyPending);
        Assert.True(plan.ApplyPlaceholders);
        Assert.True(g.ShouldRunFrameRetry);
        Assert.True(PrefabApplyGate.ShouldApplyPlaceholders(g.Ready));
    }

    [Fact]
    public void Live_world_after_Core_swap_opens_a_closed_server_gate()
    {
        var g = Server();
        g.NotifyWorldReady();
        Assert.False(g.Ready);

        var plan = g.NotifyLiveWorldAfterCoreSwap();

        Assert.True(g.Ready);
        Assert.True(plan.Opened);
        Assert.True(plan.ApplyPending);
        Assert.True(plan.ApplyPlaceholders);
        Assert.True(g.ShouldRunFrameRetry);
    }

    [Fact]
    public void Live_world_after_Core_swap_is_idempotent_when_already_open()
    {
        var client = Client();
        client.NotifyClientPacket(PrefabApplyGate.ClientPacket.Definition);
        var again = client.NotifyLiveWorldAfterCoreSwap();
        Assert.True(client.Ready);
        Assert.False(again.Opened);
        Assert.True(again.ApplyPending);

        var server = Server();
        server.NotifyPlayerConnected();
        var serverAgain = server.NotifyLiveWorldAfterCoreSwap();
        Assert.True(server.Ready);
        Assert.False(serverAgain.Opened);
        Assert.True(serverAgain.ApplyPlaceholders);
    }

    [Fact]
    public void Client_WorldReady_after_a_live_world_Core_swap_does_not_close_or_drop_join_state()
    {
        // New Core generation: gate starts closed. OpenLiveGate then, on the same
        // tick's CoreTick, EventHooks republishes WorldReady because Uninstall
        // cleared ReadyWorlds. That republish must not look like a rejoin.
        var g = Client();
        var live = g.NotifyLiveWorldAfterCoreSwap();
        Assert.True(g.Ready);
        Assert.True(live.Opened);

        var plan = g.NotifyWorldReady();

        Assert.True(g.Ready);
        Assert.False(plan.CloseGate);
        Assert.False(plan.DropSyncedOwnerEntries);
        Assert.False(plan.ClearTombstones);
        Assert.True(plan.ApplyPending);
        Assert.False(plan.ApplyPlaceholders);
        Assert.True(PrefabApplyGate.ShouldApplyPlaceholders(g.Ready));
    }

    [Fact]
    public void Client_WorldReady_after_consuming_the_Core_swap_republish_still_resets_a_new_join()
    {
        var g = Client();
        g.NotifyLiveWorldAfterCoreSwap();
        g.NotifyWorldReady();
        Assert.True(g.Ready);

        var rejoin = g.NotifyWorldReady();

        Assert.False(g.Ready);
        Assert.True(rejoin.CloseGate);
        Assert.True(rejoin.DropSyncedOwnerEntries);
        Assert.True(rejoin.ClearTombstones);
        Assert.False(rejoin.ApplyPlaceholders);
    }

    [Fact]
    public void Server_WorldReady_after_a_live_world_Core_swap_is_unchanged()
    {
        var g = Server();
        g.NotifyLiveWorldAfterCoreSwap();
        Assert.True(g.Ready);

        var plan = g.NotifyWorldReady();

        Assert.True(g.Ready);
        Assert.False(plan.CloseGate);
        Assert.False(plan.DropSyncedOwnerEntries);
        Assert.False(plan.ClearTombstones);
        Assert.True(plan.ScheduleNoPlayerFallback);
    }
}
