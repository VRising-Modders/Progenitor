using System;
using System.Text;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the container preview's inventory sync (<c>ContainerPreviewSyncPolicy.cs</c>): which entities
/// the preview holds a copy of the user on as items enter and leave, the multiset edits that mirror the game's own
/// add and remove-last, the save suspension clock, the wire's default, and source pins that every end path cleans up.
/// The buffer writes themselves are server runtime code, not live-verified (E1b).
/// </summary>
public class ContainerPreviewSyncTests
{
    private static readonly long Inv = ContainerPreviewSyncLedger.Key(100, 1);
    private static readonly long Sword = ContainerPreviewSyncLedger.Key(200, 3);
    private static readonly long Axe = ContainerPreviewSyncLedger.Key(201, 1);
    private static readonly long Me = ContainerPreviewSyncLedger.Key(7, 2);
    private static readonly long Other = ContainerPreviewSyncLedger.Key(8, 1);

    private static void Apply(ContainerPreviewSyncLedger l, (System.Collections.Generic.List<long> Add, System.Collections.Generic.List<long> Remove) plan)
    {
        foreach (long e in plan.Add) l.MarkAdded(e);
        foreach (long e in plan.Remove) l.MarkRemoved(e);
    }

    [Fact]
    public void The_first_refresh_adds_the_inventory_and_its_items()
    {
        var l = new ContainerPreviewSyncLedger();
        var plan = l.Plan(new[] { Inv, Sword });
        Assert.Equal(new[] { Inv, Sword }, plan.Add);
        Assert.Empty(plan.Remove);
        Apply(l, plan);
        Assert.Equal(2, l.Count);
        var again = l.Plan(new[] { Sword, Inv });
        Assert.Empty(again.Add);
        Assert.Empty(again.Remove);
    }

    [Fact]
    public void An_item_that_enters_is_added_and_one_that_leaves_loses_only_the_preview_copy()
    {
        var l = new ContainerPreviewSyncLedger();
        Apply(l, l.Plan(new[] { Inv, Sword }));
        var entered = l.Plan(new[] { Inv, Sword, Axe });
        Assert.Equal(new[] { Axe }, entered.Add);
        Assert.Empty(entered.Remove);
        Apply(l, entered);
        var left = l.Plan(new[] { Inv, Axe });
        Assert.Empty(left.Add);
        Assert.Equal(new[] { Sword }, left.Remove);
        Apply(l, left);
        Assert.False(l.Holds(Sword));
        Assert.True(l.Holds(Axe));
    }

    [Fact]
    public void The_end_takes_every_held_entity_once_and_empties_the_ledger()
    {
        var l = new ContainerPreviewSyncLedger();
        Apply(l, l.Plan(new[] { Sword, Inv, Axe }));
        var all = l.TakeAll();
        Assert.Equal(new[] { Inv, Sword, Axe }, all);
        Assert.Equal(0, l.Count);
        Assert.Empty(l.TakeAll());
        // After a save suspension the next refresh adds everything again.
        Assert.Equal(new[] { Inv, Sword, Axe }, l.Plan(new[] { Inv, Sword, Axe }).Add);
    }

    [Fact]
    public void Keys_round_trip_entities()
    {
        var key = ContainerPreviewSyncLedger.Key(123456, 7);
        Assert.Equal((123456, 7), ContainerPreviewSyncLedger.Unkey(key));
        Assert.Equal((int.MaxValue, int.MaxValue), ContainerPreviewSyncLedger.Unkey(ContainerPreviewSyncLedger.Key(int.MaxValue, int.MaxValue)));
    }

    // ---- the multiset ----------------------------------------------------------------------------

    [Fact]
    public void Add_appends_without_a_duplicate_check()
    {
        var once = SyncToUserBufferEdit.AddOne(new[] { Other }, Me);
        Assert.Equal(new[] { Other, Me }, once);
        Assert.Equal(new[] { Other, Me, Me }, SyncToUserBufferEdit.AddOne(once, Me));
    }

    [Fact]
    public void Remove_takes_out_the_last_copy_only_and_keeps_everyone_else()
    {
        // The player is also interacting for real (an earlier copy) and another user is synced.
        long[] list = { Me, Other, Me };
        var after = SyncToUserBufferEdit.RemoveOne(list, Me);
        Assert.Equal(new[] { Me, Other }, after);
        Assert.Equal(1, SyncToUserBufferEdit.CountOf(after!, Me));
        Assert.Equal(new[] { Other }, SyncToUserBufferEdit.RemoveOne(after!, Me));
    }

    [Fact]
    public void Remove_never_clears_and_does_nothing_without_a_copy()
    {
        Assert.Null(SyncToUserBufferEdit.RemoveOne(new[] { Other, Other }, Me));
        Assert.Null(SyncToUserBufferEdit.RemoveOne(Array.Empty<long>(), Me));
        Assert.Equal(new[] { Other }, SyncToUserBufferEdit.RemoveOne(new[] { Other, Me }, Me));
        Assert.Equal(2, SyncToUserBufferEdit.LastIndexOf(new[] { Me, Other, Me }, Me));
        Assert.Equal(-1, SyncToUserBufferEdit.LastIndexOf(new[] { Other }, Me));
    }

    // ---- saves ----------------------------------------------------------------------------------

    [Fact]
    public void Entries_come_back_a_few_seconds_after_a_save()
    {
        Assert.False(ContainerPreviewSyncSave.ShouldResume(double.NaN, 100));
        Assert.False(ContainerPreviewSyncSave.ShouldResume(10, 10 + ContainerPreviewSyncSave.ResumeAfterSeconds - 0.1));
        Assert.True(ContainerPreviewSyncSave.ShouldResume(10, 10 + ContainerPreviewSyncSave.ResumeAfterSeconds));
    }

    // ---- the wire -------------------------------------------------------------------------------

    [Fact]
    public void An_open_syncs_unless_it_says_not_to()
    {
        var older = Encoding.UTF8.GetBytes("{\"Version\":1,\"Kind\":\"open\",\"Sequence\":3,\"Container\":\"c-x\",\"Seconds\":30,\"Message\":\"\"}");
        Assert.True(ContainerPreviewWire.Decode(older, expectRequest: true)!.SyncInventory);
        var off = ContainerPreviewWire.Encode(new ContainerPreviewPacket { Kind = "open", Sequence = 4, SyncInventory = false });
        Assert.False(ContainerPreviewWire.Decode(off, expectRequest: true)!.SyncInventory);
    }

    // ---- every end path cleans up -----------------------------------------------------------------

    [Fact]
    public void Every_end_of_a_server_preview_removes_its_sync_entries()
    {
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        string ended = SourcePin.MethodBody(server, "public void LeaseEnded(");
        Assert.Contains("Sync?.End();", ended);
        Assert.Contains("Persistence.ModSave.BeforeSave += OnBeforeSave;", server);
        Assert.Contains("Persistence.ModSave.BeforeSave -= OnBeforeSave;", server);
        Assert.Contains("holder.Sync?.Suspend(now);", server);
        Assert.Contains("holder.Sync?.Tick(now);", server);
    }

    [Fact]
    public void The_sync_list_is_edited_one_copy_at_a_time_through_layout_facts()
    {
        string sync = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewSync.cs");
        Assert.Contains("SyncToUserBufferEdit.RemoveOne(", sync);
        Assert.Contains("Layout.SyncToUserBufferStride", sync);
        Assert.Contains("Layout.SyncToUserBufferUserEntityOffset", sync);
        Assert.DoesNotContain(".Clear()", SourcePin.MethodBody(sync, "private bool RemoveOne("));
        Assert.DoesNotContain("GetBuffer<SyncToUserBuffer>", sync);
    }
}
