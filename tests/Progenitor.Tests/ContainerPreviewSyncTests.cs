using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the container preview's inventory sync (<c>ContainerPreviewSyncPolicy.cs</c>): which entities
/// the preview holds a copy of the user on as items enter and leave, adopting the game's own copy on an entering item,
/// the multiset edits and their read-back checks, the save suspension clock, the persisted records and the startup
/// sweep decision, the client's grace for a missing inventory, the wire's default, and source pins that every end path
/// cleans up. The buffer writes themselves are server runtime code, not live-verified (E1b).
/// </summary>
public class ContainerPreviewSyncTests
{
    private static readonly long Inv = ContainerPreviewSyncLedger.Key(100, 1);
    private static readonly long Sword = ContainerPreviewSyncLedger.Key(200, 3);
    private static readonly long Axe = ContainerPreviewSyncLedger.Key(201, 1);
    private static readonly long Me = ContainerPreviewSyncLedger.Key(7, 2);
    private static readonly long Other = ContainerPreviewSyncLedger.Key(8, 1);

    private static void Apply(ContainerPreviewSyncLedger l, (List<long> Take, List<long> Release) plan)
    {
        foreach (long e in plan.Take) l.MarkHeld(e);
        foreach (long e in plan.Release) l.MarkReleased(e);
    }

    // ---- the ledger -----------------------------------------------------------------------------

    [Fact]
    public void The_first_refresh_takes_the_inventory_and_its_items()
    {
        var l = new ContainerPreviewSyncLedger();
        var plan = l.Plan(new[] { Inv, Sword });
        Assert.Equal(new[] { Inv, Sword }, plan.Take);
        Assert.Empty(plan.Release);
        Apply(l, plan);
        var again = l.Plan(new[] { Sword, Inv });
        Assert.Empty(again.Take);
        Assert.Empty(again.Release);
    }

    [Fact]
    public void An_item_that_enters_is_taken_and_one_that_leaves_is_released()
    {
        var l = new ContainerPreviewSyncLedger();
        Apply(l, l.Plan(new[] { Inv, Sword }));
        var entered = l.Plan(new[] { Inv, Sword, Axe });
        Assert.Equal(new[] { Axe }, entered.Take);
        Apply(l, entered);
        var left = l.Plan(new[] { Inv, Axe });
        Assert.Equal(new[] { Sword }, left.Release);
        Apply(l, left);
        Assert.False(l.Holds(Sword));
        Assert.True(l.Holds(Axe));
    }

    [Fact]
    public void A_release_that_failed_stays_held_and_is_planned_again()
    {
        var l = new ContainerPreviewSyncLedger();
        Apply(l, l.Plan(new[] { Inv, Sword }));
        var left = l.Plan(new[] { Inv });
        Assert.Equal(new[] { Sword }, left.Release);
        // The removal did not read back: the caller does not mark it released.
        Assert.True(l.Holds(Sword));
        Assert.Equal(new[] { Sword }, l.Plan(new[] { Inv }).Release);
        Assert.Equal(new[] { Inv, Sword }, l.Snapshot());
    }

    [Fact]
    public void An_entering_item_with_the_games_copy_is_adopted_but_the_first_refresh_always_appends()
    {
        Assert.Equal(SyncEntryAction.Append, ContainerPreviewSyncLedger.ActionFor(firstRefresh: true, copiesOfUserNow: 0));
        // At the lease start a copy there belongs to a real interaction: the preview adds its own.
        Assert.Equal(SyncEntryAction.Append, ContainerPreviewSyncLedger.ActionFor(firstRefresh: true, copiesOfUserNow: 1));
        Assert.Equal(SyncEntryAction.Append, ContainerPreviewSyncLedger.ActionFor(firstRefresh: false, copiesOfUserNow: 0));
        // Later, the game's item sync copied the inventory's list onto the entering item: take that copy over.
        Assert.Equal(SyncEntryAction.Adopt, ContainerPreviewSyncLedger.ActionFor(firstRefresh: false, copiesOfUserNow: 1));
    }

    [Fact]
    public void Keys_round_trip_entities()
    {
        Assert.Equal((123456, 7), ContainerPreviewSyncLedger.Unkey(ContainerPreviewSyncLedger.Key(123456, 7)));
        Assert.Equal((int.MaxValue, int.MaxValue), ContainerPreviewSyncLedger.Unkey(ContainerPreviewSyncLedger.Key(int.MaxValue, int.MaxValue)));
    }

    // ---- the multiset and its read-back ------------------------------------------------------------

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
        long[] list = { Me, Other, Me };
        var after = SyncToUserBufferEdit.RemoveOne(list, Me);
        Assert.Equal(new[] { Me, Other }, after);
        Assert.Equal(new[] { Other }, SyncToUserBufferEdit.RemoveOne(after!, Me));
        // The only entry: a zero-row list (the write the read-back must confirm).
        Assert.Empty(SyncToUserBufferEdit.RemoveOne(new[] { Me }, Me)!);
    }

    [Fact]
    public void Remove_never_clears_and_does_nothing_without_a_copy()
    {
        Assert.Null(SyncToUserBufferEdit.RemoveOne(new[] { Other, Other }, Me));
        Assert.Null(SyncToUserBufferEdit.RemoveOne(Array.Empty<long>(), Me));
        Assert.Equal(2, SyncToUserBufferEdit.LastIndexOf(new[] { Me, Other, Me }, Me));
        Assert.Equal(-1, SyncToUserBufferEdit.LastIndexOf(new[] { Other }, Me));
    }

    [Fact]
    public void A_write_is_good_only_when_it_reads_back_exactly()
    {
        Assert.True(SyncToUserBufferEdit.Same(new[] { Other, Me }, new[] { Other, Me }));
        Assert.False(SyncToUserBufferEdit.Same(new[] { Other, Me }, new[] { Me, Other }));
        Assert.False(SyncToUserBufferEdit.Same(new[] { Other }, new[] { Other, Me }));
        Assert.True(SyncToUserBufferEdit.Same(Array.Empty<long>(), Array.Empty<long>()));
    }

    [Fact]
    public void An_append_that_threw_after_landing_is_not_retried()
    {
        long[] before = { Other };
        Assert.True(SyncToUserBufferEdit.AppendLanded(before, new[] { Other, Me }, Me));    // landed: no masked retry
        Assert.False(SyncToUserBufferEdit.AppendLanded(before, new[] { Other }, Me));       // did not land: retry
        Assert.False(SyncToUserBufferEdit.AppendLanded(before, new[] { Other, Me, Me }, Me)); // doubled: a failure
    }

    // ---- saves ----------------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Entries_go_back_once_the_save_file_is_written_and_settled()
    {
        var s = new SyncSaveSuspension();
        Assert.False(s.Active);
        s.Begin(now: 100, stampAtTrigger: T0);
        Assert.True(s.Active);
        Assert.False(s.ShouldResume(101, T0));                       // not written yet
        Assert.False(s.ShouldResume(102, T0.AddSeconds(2)));         // written: settle starts
        Assert.False(s.ShouldResume(102.5, T0.AddSeconds(2)));
        Assert.True(s.ShouldResume(103, T0.AddSeconds(2)));          // settled
        Assert.False(s.TimedOut);
        Assert.False(s.Active);
    }

    [Fact]
    public void A_stamp_that_keeps_moving_restarts_the_settle()
    {
        var s = new SyncSaveSuspension();
        s.Begin(0, T0);
        Assert.False(s.ShouldResume(1, T0.AddSeconds(1)));
        Assert.False(s.ShouldResume(1.9, T0.AddSeconds(2)));         // still being written
        Assert.False(s.ShouldResume(2.5, T0.AddSeconds(2)));
        Assert.True(s.ShouldResume(2.9, T0.AddSeconds(2)));
    }

    [Fact]
    public void A_second_save_during_the_suspension_extends_it()
    {
        var s = new SyncSaveSuspension();
        s.Begin(0, T0);
        Assert.False(s.ShouldResume(1, T0.AddSeconds(1)));           // the first save landed, settling
        s.Begin(1.5, T0.AddSeconds(1));                              // a second save: wait for its own write
        Assert.False(s.ShouldResume(3, T0.AddSeconds(1)));
        Assert.False(s.ShouldResume(4, T0.AddSeconds(4)));
        Assert.True(s.ShouldResume(5, T0.AddSeconds(4)));
    }

    [Fact]
    public void A_save_that_never_lands_resumes_on_the_timeout()
    {
        var s = new SyncSaveSuspension();
        s.Begin(0, DateTime.MinValue);
        Assert.False(s.ShouldResume(SyncSaveSuspension.TimeoutSeconds - 1, DateTime.MinValue));
        Assert.True(s.ShouldResume(SyncSaveSuspension.TimeoutSeconds, DateTime.MinValue));
        Assert.True(s.TimedOut);
    }

    [Fact]
    public void Only_world_saves_suspend_the_entries_through_one_shared_guard()
    {
        string modSave = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "ModSave.cs");
        string trigger = SourcePin.MethodBody(modSave, "private static void SlotTriggerSave(");
        Assert.Contains("RaiseBeforeWorldSave();", trigger);
        Assert.DoesNotContain("RaiseBeforeWorldSave", SourcePin.MethodBody(modSave, "public static void SaveAll("));
        string guard = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "WorldSaveGuard.cs");
        Assert.Contains("ModSave.BeforeWorldSave += OnBeforeWorldSave;", guard);
        Assert.Contains("ModSave.BeforeWorldSave -= OnBeforeWorldSave;", guard);
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        Assert.Contains("Persistence.WorldSaveGuard.Suspend += OnSaveSuspend;", server);
        Assert.Contains("Persistence.WorldSaveGuard.Resume += OnSaveResume;", server);
        Assert.DoesNotContain("ModSave.BeforeSave +=", server);
        Assert.DoesNotContain("ModSave.BeforeWorldSave +=", server);
    }

    [Fact]
    public void The_suspension_is_one_server_clock_and_a_new_sync_starts_inside_it()
    {
        string guard = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "WorldSaveGuard.cs");
        Assert.Contains("private static readonly SyncSaveSuspension Suspension = new();", guard);
        Assert.Contains("Suspension.Begin(Now, SaveStamp());", SourcePin.MethodBody(guard, "private static void OnBeforeWorldSave("));
        Assert.Contains("Raise(Resume, \"resume\");", SourcePin.MethodBody(guard, "private static void Tick("));
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        Assert.Contains("holder.Sync?.Resume();", SourcePin.MethodBody(server, "private static void OnSaveResume("));
        string open = SourcePin.MethodBody(server, "private static void Open(");
        Assert.True(open.IndexOf("if (SaveSuspended) holder.Sync.Suspend();", StringComparison.Ordinal)
            < open.IndexOf("holder.Sync.Refresh();", StringComparison.Ordinal));
        string sync = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewSync.cs");
        Assert.DoesNotContain("SyncSaveSuspension", sync);
    }

    [Fact]
    public void Without_the_world_save_hook_a_synced_preview_is_refused_before_any_lease()
    {
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        string open = SourcePin.MethodBody(server, "private static void Open(");
        int refuse = open.IndexOf("if (request.SyncInventory && !Persistence.ModSave.WorldSaveHookArmed)", StringComparison.Ordinal);
        Assert.True(refuse > 0);
        Assert.True(refuse < open.IndexOf("SharedObservation.Acquire(", StringComparison.Ordinal));
        Assert.Contains("world-save hook is not in place", ContainerPreviewAuthorization.SaveHookRefusal);
        string modSave = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "ModSave.cs");
        Assert.Contains("if (site == HarmonySlotHandoff.TriggerSave) WorldSaveHookArmed = true;", modSave);
        Assert.Contains("if (site == HarmonySlotHandoff.TriggerSave) WorldSaveHookArmed = armed;", modSave);
    }

    [Fact]
    public void The_bridges_direct_save_event_raises_the_same_notification()
    {
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "WorldSaveOps.cs");
        int create = ops.IndexOf("if (!TryCreateSaveEvent(em, reason, diskName, compress, out var createNote))", StringComparison.Ordinal);
        int notify = ops.IndexOf("ModSave.NotifyWorldSaveQueued();", StringComparison.Ordinal);
        Assert.True(create > 0 && notify > create);
    }

    [Fact]
    public void The_guard_turns_itself_back_on_after_one_save_or_five_minutes()
    {
        string guard = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "WorldSaveGuard.cs");
        string before = SourcePin.MethodBody(guard, "private static void OnBeforeWorldSave(");
        Assert.True(before.IndexOf("Enabled = true;", StringComparison.Ordinal) < before.IndexOf("Suspension.Begin", StringComparison.Ordinal));
        Assert.Contains("now - _offSince >= OffMaxSeconds", SourcePin.MethodBody(guard, "private static void Tick("));
    }

    // ---- the streaming origin (User.ProxySyncPosition is saved with the world) -------------------------

    private static Progenitor.Cameras.ObservationLeaseRecord Lease(ulong platform = 7) => new()
    { Platform = platform, X = -2116.75f, Y = 0.0245f, Z = 24.25f };

    [Fact]
    public void An_origin_left_at_a_recorded_lease_point_with_no_lease_is_restored()
    {
        var r = Lease();
        Assert.True(Progenitor.Cameras.ObservationProxyRules.AtPoint(-2116.75f, 0.0245f, 24.25f, r));
        Assert.True(Progenitor.Cameras.ObservationProxyRules.AtPoint(-2116.745f, 0.0245f, 24.25f, r));
        Assert.False(Progenitor.Cameras.ObservationProxyRules.AtPoint(-2116.6f, 0.0245f, 24.25f, r));
        Assert.False(Progenitor.Cameras.ObservationProxyRules.AtPoint(0, 0, 0, r));
        Assert.Equal(Progenitor.Cameras.ObservationProxyRepair.Restore, Progenitor.Cameras.ObservationProxyRules.Decide(leaseRunning: false, originAtRecordedPoint: true));
        Assert.Equal(Progenitor.Cameras.ObservationProxyRepair.Leave, Progenitor.Cameras.ObservationProxyRules.Decide(leaseRunning: true, originAtRecordedPoint: true));
        Assert.Equal(Progenitor.Cameras.ObservationProxyRepair.Leave, Progenitor.Cameras.ObservationProxyRules.Decide(leaseRunning: false, originAtRecordedPoint: false));
        Assert.True(Progenitor.Cameras.ObservationProxyRules.IsZero(0, 0, 0));
    }

    [Fact]
    public void Leases_end_before_their_records_are_put_away_on_unload()
    {
        string plugin = SourcePin.ReadFile("src", "Progenitor.Core", "Plugin.cs");
        int endAll = plugin.IndexOf("Cameras.SharedObservation.EndAll();", StringComparison.Ordinal);
        int guard = plugin.IndexOf("Cameras.ObservationGuard.Uninstall();", StringComparison.Ordinal);
        Assert.True(endAll > 0 && guard > endAll, "EndAll must restore the origins and settle the records before the record store goes away");
    }

    [Fact]
    public void Lease_records_are_one_per_user()
    {
        var doc = new Progenitor.Cameras.ObservationLeaseRecords();
        doc.Put(Lease(7));
        doc.Put(new Progenitor.Cameras.ObservationLeaseRecord { Platform = 7, X = 1 });
        doc.Put(Lease(8));
        Assert.Equal(2, doc.Records.Count);
        Assert.Equal(1, doc.For(7)!.X);
        Assert.True(doc.Remove(7));
        Assert.Null(doc.For(7));
        Assert.False(doc.Remove(7));
    }

    [Fact]
    public void Every_lease_restores_the_origin_for_a_save_and_keeps_a_record_while_it_runs()
    {
        string obs = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "CameraObservation.cs");
        Assert.Contains("else if (writeProxy && !_proxySuspended) WriteProxy(_point);", obs);
        Assert.Contains("WriteProxy(_originalProxy.Value);", SourcePin.MethodBody(obs, "internal void SuspendProxy("));
        string start = SourcePin.MethodBody(obs, "private void Start(");
        Assert.Contains("_proxySuspended = Persistence.WorldSaveGuard.Active;", start);
        Assert.Contains("ObservationGuard.Put(Platform, _point, _originalProxy.Value);", start);
        string close = SourcePin.MethodBody(obs, "private void Close(");
        Assert.Contains("if (settled) { ObservationGuard.Remove(Platform);", close);
        Assert.Contains("settled = !math.all(em.GetComponentData<User>(_user).ProxySyncPosition.Value == _point);", close);
        string guardFile = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "ObservationGuard.cs");
        Assert.Contains("(_store as ModStore)?.SaveToDisk();", SourcePin.MethodBody(guardFile, "private static void Save("));
        // A lease start that finds the origin at a recorded point repairs it instead of refusing.
        string acquire = SourcePin.MethodBody(obs, "internal static SharedObservation Acquire(");
        Assert.True(acquire.IndexOf("ObservationGuard.TryRepair(world, user)", StringComparison.Ordinal)
            < acquire.IndexOf("ObservationShareRules.Decide(", StringComparison.Ordinal));
        string guard = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "ObservationGuard.cs");
        Assert.Contains("WorldSaveGuard.Suspend += OnSuspend;", guard);
        Assert.Contains("lease.SuspendProxy();", guard);
        Assert.Contains("lease.ResumeProxy();", guard);
        Assert.Contains("if (!_loaded) return;", SourcePin.MethodBody(guard, "private static void Save("));
        Assert.Contains("SharedObservation.WriteProxyRaw(world, user, original);", guard);
        Assert.DoesNotContain("GetComponentData<User>(user).ProxySyncPosition = ", guard);
    }

    // ---- records and the startup sweep --------------------------------------------------------------

    [Fact]
    public void Records_survive_a_given_up_release_and_wait_for_the_store()
    {
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        Assert.Contains("if (!sync.GaveUp) { Unrecord(container, platform); return; }", SourcePin.MethodBody(server, "private static void Released("));
        Assert.Contains("if (!_storeLoaded) return;", SourcePin.MethodBody(server, "private static void SaveRecords("));
        string sweep = SourcePin.MethodBody(server, "private static void Sweep(");
        Assert.Contains("UnrecordRaw(r.Container, r.Platform);", sweep);
        Assert.Contains("if (tries < MaxSweepAttempts) continue;", sweep);
        string uninstall = SourcePin.MethodBody(server, "internal static void Uninstall(");
        Assert.Contains("if (sync.Done && PendingRecord.TryGetValue(sync, out var rec)) Released(sync, rec.Container, rec.Platform);", uninstall);
    }

    [Fact]
    public void An_append_whose_read_back_failed_still_holds_a_copy_that_landed()
    {
        string sync = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewSync.cs");
        string append = SourcePin.MethodBody(sync, "private void AppendOne(");
        Assert.Contains("> SyncToUserBufferEdit.CountOf(before, _userKey))", append);
        Assert.Contains("_ledger.MarkHeld(key);", append);
        Assert.Contains("throw;", append);
    }

    [Fact]
    public void Records_are_one_per_chest_and_user()
    {
        var doc = new ContainerPreviewSyncRecords();
        Assert.True(doc.Add("c-a", 1));
        Assert.False(doc.Add("c-a", 1));
        Assert.True(doc.Add("c-a", 2));
        Assert.True(doc.Remove("c-a", 1));
        Assert.False(doc.Contains("c-a", 1));
        Assert.True(doc.Contains("c-a", 2));
        Assert.False(doc.Remove("c-b", 2));
    }

    [Theory]
    [InlineData(1, 0, 0)]       // Live: sweep
    [InlineData(2, 10, 1)]      // Unbound: wait
    [InlineData(2, 120, 2)]     // Unbound past the wait: drop
    [InlineData(3, 0, 2)]       // Gone: drop
    [InlineData(0, 0, 2)]       // Unknown: drop
    public void The_sweep_waits_for_a_chest_to_bind_and_drops_what_is_gone(int status, double since, int step) =>
        Assert.Equal((SyncSweepStep)step, ContainerPreviewSyncSweep.Decide((ContainerIdStatus)status, since));

    [Fact]
    public void The_sweep_only_removes_a_copy_that_is_there()
    {
        string sync = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewSync.cs");
        string sweep = SourcePin.MethodBody(sync, "internal static int SweepOnce(");
        Assert.Contains("CountOf(ReadUsers(em, e), sweep._userKey) == 0) continue;", sweep);
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        Assert.Contains("bool online = user != Entity.Null && em.GetComponentData<User>(user).IsConnected;", server);
        Assert.Contains("if (IsActive(r))", server);
    }

    // ---- the client's missing inventory -------------------------------------------------------------

    private static ContainerPreviewMachine OpenMachine()
    {
        var m = new ContainerPreviewMachine(new ContainerPreviewOptions { KeepOpen = true }, 0);
        m.OnReply(0, true, "");
        m.Step(0.1, new ContainerPreviewSample { SameWorld = true, TargetFound = true, InventoryReady = true });
        m.Step(0.2, new ContainerPreviewSample { SameWorld = true, TargetFound = true, InventoryReady = true, WindowIsOurs = true });
        Assert.Equal(ContainerPreviewState.Open, m.State);
        return m;
    }

    [Fact]
    public void A_missing_inventory_holds_the_window_then_closes_after_the_grace()
    {
        var m = OpenMachine();
        var missing = new ContainerPreviewSample { SameWorld = true, TargetFound = true, InventoryReady = false, WindowIsOurs = true, ContainerMenuOpen = true };
        Assert.Equal(ContainerPreviewStep.None, m.Step(1, missing));
        Assert.True(m.InventoryMissing);
        Assert.Equal(ContainerPreviewStep.None, m.Step(1 + ContainerPreviewMachine.InventoryGraceSeconds - 0.1, missing));
        Assert.Equal(ContainerPreviewStep.Closed, m.Step(1 + ContainerPreviewMachine.InventoryGraceSeconds, missing));
        Assert.Equal(ContainerPreviewCloseReason.TargetGone, m.CloseReason);
        Assert.Equal("The container's contents stopped arriving.", m.Message);
    }

    [Fact]
    public void An_inventory_that_comes_back_within_the_grace_keeps_the_preview()
    {
        var m = OpenMachine();
        var missing = new ContainerPreviewSample { SameWorld = true, TargetFound = true, InventoryReady = false, WindowIsOurs = true, ContainerMenuOpen = true };
        var back = new ContainerPreviewSample { SameWorld = true, TargetFound = true, InventoryReady = true, WindowIsOurs = true, ContainerMenuOpen = true };
        m.Step(1, missing);
        m.Step(10, back);
        Assert.False(m.InventoryMissing);
        Assert.Equal(ContainerPreviewStep.None, m.Step(1 + ContainerPreviewMachine.InventoryGraceSeconds + 5, missing));
        Assert.Equal(ContainerPreviewState.Open, m.State);
        Assert.True(ContainerPreviewMachine.InventoryGraceSeconds > SyncSaveSuspension.SettleSeconds + 5);
    }

    [Fact]
    public void The_mapper_is_held_while_the_inventory_is_missing()
    {
        string client = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewClient.cs");
        string mapper = SourcePin.MethodBody(client, "private void BeforeMapper(");
        // STATIONPREVIEW1: every inventory instance, not only the first.
        Assert.Contains("_inventorySeen && !ContainerPreviewInventories.Ready(_inventories.Count, AllPresent(em))) c.Skip = true;", mapper);
        Assert.Contains("if (em.Exists(_windowTarget))", mapper);
        Assert.DoesNotContain("Close(", mapper);   // a chest that left during a save may come back: the machine decides
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
    public void Every_end_of_a_server_preview_releases_its_entries_and_retries_failures()
    {
        string server = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        string ended = SourcePin.MethodBody(server, "public void LeaseEnded(");
        Assert.Contains("Sync.End(); Finished(Sync, Container, Platform);", ended);
        Assert.Contains("RetryReleases(now);", SourcePin.MethodBody(server, "private static void Tick("));
        Assert.Contains("holder.Sync?.Suspend();", server);
        Assert.Contains("holder.Sync?.Tick(now);", server);
    }

    [Fact]
    public void An_entry_is_released_only_after_its_removal_read_back()
    {
        string sync = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewSync.cs");
        string release = SourcePin.MethodBody(sync, "private void TryRelease(");
        Assert.True(release.IndexOf("RemoveOne(em, e);", StringComparison.Ordinal) < release.IndexOf("_ledger.MarkReleased(key);", StringComparison.Ordinal));
        string remove = SourcePin.MethodBody(sync, "private void RemoveOne(");
        Assert.Contains("if (!SyncToUserBufferEdit.Same(next, ReadUsers(em, e)))", remove);
        string append = SourcePin.MethodBody(sync, "private void AppendRaw(");
        Assert.Contains("if (SyncToUserBufferEdit.AppendLanded(before, ReadUsers(em, e), _userKey)) return;", append);
        Assert.Contains("Layout.SyncToUserBufferStride", sync);
        Assert.DoesNotContain("GetBuffer<SyncToUserBuffer>", sync);
        Assert.DoesNotContain(".Clear()", remove);
    }
}
