using System;
using System.IO;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemQuarantineJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-quarantine-" + Guid.NewGuid().ToString("N"));
    private string PathName => ItemQuarantineJournal.FileName(_root, "world-one");
    private static ItemQuarantineRecord Row(string id = "instance-one") => new("world-one", id, "mod", "item", 123,
        "{\"amount\":2,\"properties\":{\"durability\":0.3}}", new("player-bag", "player-1", "", "", "player-1", 4));
    private ItemQuarantineJournal Captured()
    {
        var journal = ItemQuarantineJournal.Load(PathName, "world-one");
        Assert.True(journal.Capture(PathName, Row())); return journal;
    }
    private ItemQuarantineJournal Quarantined()
    {
        var journal = Captured();
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.Captured, ItemQuarantineState.RemovalPending));
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.RemovalPending, ItemQuarantineState.Quarantined));
        return journal;
    }

    [Fact]
    public void Snapshot_identity_and_explicit_decision_roundtrip()
    {
        Quarantined();
        var journal = ItemQuarantineJournal.Load(PathName, "world-one");
        Assert.Equal(Row().SnapshotJson, Assert.Single(journal.Rows).SnapshotJson);
        Assert.Empty(journal.RestoreCandidates("mod"));
        Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.Single(ItemQuarantineJournal.Load(PathName, "world-one").RestoreCandidates("mod"));
    }

    [Fact]
    public void Recapture_cannot_duplicate_or_overwrite_instance()
    {
        var journal = Quarantined();
        Assert.True(journal.Capture(PathName, Row()));
        Assert.False(journal.Capture(PathName, Row() with { SnapshotJson = "{\"amount\":99}" }));
        Assert.Single(journal.Rows);
        Assert.Equal(ItemQuarantineState.Quarantined, journal.Rows[0].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Crash_claims_never_replay(bool restoring)
    {
        var journal = restoring ? Quarantined() : Captured();
        if (restoring) Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.True(journal.Transition(PathName, Row().InstanceId,
            restoring ? ItemQuarantineState.RestorePending : ItemQuarantineState.Captured,
            restoring ? ItemQuarantineState.Restoring : ItemQuarantineState.RemovalPending));
        journal = ItemQuarantineJournal.Load(PathName, "world-one");
        Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.Empty(journal.RestoreCandidates("mod"));
        Assert.False(journal.Transition(PathName, Row().InstanceId, journal.Rows[0].State, ItemQuarantineState.RestorePending));
    }

    [Fact]
    public void Full_destination_pending_and_missing_destination_retained_for_explicit_retry()
    {
        var journal = Quarantined(); Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.Single(journal.RestoreCandidates("mod")); // No claim until a destination has room.
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.RestorePending, ItemQuarantineState.Unavailable, "owner changed"));
        Assert.Empty(journal.RestoreCandidates("mod"));
        Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.Equal(Row().Destination, Assert.Single(journal.RestoreCandidates("mod")).Destination);
    }

    [Fact]
    public void Completion_is_terminal_and_invalid_transition_rejected()
    {
        var journal = Quarantined();
        Assert.False(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.Quarantined, ItemQuarantineState.Restored));
        Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.RestorePending, ItemQuarantineState.Restoring));
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.Restoring, ItemQuarantineState.Restored));
        Assert.True(journal.RequestRestore(PathName, "mod")); Assert.Empty(journal.RestoreCandidates("mod"));
    }

    [Fact]
    public void Stale_or_failed_write_rolls_back_in_memory()
    {
        Captured();
        var stale = ItemQuarantineJournal.Load(PathName, "world-one");
        var current = ItemQuarantineJournal.Load(PathName, "world-one");
        Assert.True(current.Capture(PathName, Row("other-instance")));
        Assert.False(stale.Transition(PathName, Row().InstanceId, ItemQuarantineState.Captured, ItemQuarantineState.RemovalPending));
        Assert.Equal(ItemQuarantineState.Captured, Assert.Single(stale.Rows).State);
        Assert.False(stale.Capture(PathName, Row("third-instance"))); Assert.Single(stale.Rows);
        Assert.Equal(2, ItemQuarantineJournal.Load(PathName, "world-one").Rows.Count);
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(PathName)!, "*.tmp"));
    }

    [Fact]
    public void Corruption_and_cross_world_records_fail_closed()
    {
        Captured(); var contents = File.ReadAllText(PathName);
        var wrongWorld = ItemQuarantineJournal.Load(PathName, "world-two");
        Assert.False(wrongWorld.IsValid); Assert.False(wrongWorld.RequestRestore(PathName, "mod"));
        Assert.Equal(contents, File.ReadAllText(PathName));
        File.WriteAllText(PathName, "{broken");
        var corrupt = ItemQuarantineJournal.Load(PathName, "world-one");
        Assert.False(corrupt.IsValid); Assert.False(corrupt.Capture(PathName, Row()));
        Assert.Equal("{broken", File.ReadAllText(PathName));
    }

    [Fact]
    public void Capture_rejects_foreign_world_invalid_payload_and_conflicting_destination()
    {
        var journal = Captured();
        Assert.False(journal.Capture(PathName, Row("new") with { WorldId = "world-two" }));
        Assert.False(journal.Capture(PathName, Row("new") with { SnapshotJson = "[]" }));
        Assert.False(journal.Capture(PathName, Row() with { Destination = Row().Destination with { OwnerId = "new-owner" } }));
    }

    [Fact]
    public void Explicit_discard_retains_tombstone_and_never_reinstalls()
    {
        var journal = Quarantined(); Assert.True(journal.Discard(PathName, "mod"));
        journal = ItemQuarantineJournal.Load(PathName, "world-one");
        Assert.Equal(ItemQuarantineState.Discarded, Assert.Single(journal.Rows).State);
        Assert.Equal(Row().SnapshotJson, journal.Rows[0].SnapshotJson);
        Assert.True(journal.RequestRestore(PathName, "mod")); Assert.Empty(journal.RestoreCandidates("mod"));
    }

    [Fact]
    public void Proved_no_attempt_can_release_claim_but_keep_never_authorizes_restore()
    {
        var journal = Quarantined();
        Assert.True(journal.Keep(PathName, "mod"));
        Assert.False(ItemQuarantineJournal.Load(PathName, "world-one").Rows[0].RestoreDecisionPending);
        Assert.Empty(journal.RestoreCandidates("mod"));
        Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.RestorePending, ItemQuarantineState.Restoring));
        Assert.True(journal.RestoreNotAttempted(PathName, Row().InstanceId, false, "bag full"));
        Assert.Single(journal.RestoreCandidates("mod"));
        Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.RestorePending, ItemQuarantineState.Restoring));
        Assert.True(journal.RestoreNotAttempted(PathName, Row().InstanceId, true, "owner changed"));
        Assert.Empty(journal.RestoreCandidates("mod"));
    }

    [Fact]
    public void Pending_explanation_roundtrips_without_changing_claim_or_rewriting_identical_reason()
    {
        var journal = Quarantined(); Assert.True(journal.RequestRestore(PathName, "mod"));
        Assert.True(journal.Annotate(PathName, Row().InstanceId, "Original bag is full."));
        var row = Assert.Single(ItemQuarantineJournal.Load(PathName, "world-one").Rows);
        Assert.Equal(ItemQuarantineState.RestorePending, row.State); Assert.Equal("Original bag is full.", row.Reason);
        using (var held = new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(journal.Annotate(PathName, Row().InstanceId, "Original bag is full."));
            Assert.False(journal.Annotate(PathName, Row().InstanceId, "Changed reason."));
        }
        Assert.Equal("Original bag is full.", journal.Rows[0].Reason);
    }

    [Theory]
    [InlineData((int)ItemQuarantineState.RestorePending, (int)ItemQuarantineState.Quarantined)]
    [InlineData((int)ItemQuarantineState.Unavailable, (int)ItemQuarantineState.Unavailable)]
    [InlineData((int)ItemQuarantineState.Restoring, (int)ItemQuarantineState.Restoring)]
    public void Keep_cancels_pending_restoration_but_does_not_release_ambiguous_claims(int initialState, int expectedState)
    {
        var journal = Quarantined(); Assert.True(journal.RequestRestore(PathName, "mod"));
        if ((ItemQuarantineState)initialState != ItemQuarantineState.RestorePending)
            Assert.True(journal.Transition(PathName, Row().InstanceId, ItemQuarantineState.RestorePending, (ItemQuarantineState)initialState));
        Assert.True(journal.Keep(PathName, "mod"));
        var row = Assert.Single(ItemQuarantineJournal.Load(PathName, "world-one").Rows);
        Assert.Equal((ItemQuarantineState)expectedState, row.State);
        Assert.False(row.RestoreDecisionPending); Assert.Empty(journal.RestoreCandidates("mod"));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
