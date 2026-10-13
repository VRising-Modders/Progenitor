using System;
using Progenitor.Bootstrap;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of F10 (review R3 finding 3): vanilla name reuse / hash collision
/// must not stay registered, must not go out as PrfD, and must not be tombstoned.
/// Linked through <c>PrefabWire.cs</c> (<see cref="PrefabApplyBookkeeping"/>).
/// </summary>
public class PrefabApplyDecisionTests
{
    // Stand-in for a vanilla GUID that also equals CalculateGuidHash(id). The registrar
    // must refuse that id even though mapped == target.
    private const int CrystalHash = -651247173;
    private const int EmberHash = 802060347;

    [Fact]
    public void Mapped_name_equal_to_target_is_refused_when_not_cloned_here()
    {
        Assert.False(PrefabApplyBookkeeping.CanClaimMappedSpawnableName(
            CrystalHash, CrystalHash, mappedWasClonedHere: false));
    }

    [Fact]
    public void Mapped_name_equal_to_target_is_allowed_when_this_process_cloned_it()
    {
        Assert.True(PrefabApplyBookkeeping.CanClaimMappedSpawnableName(
            EmberHash, EmberHash, mappedWasClonedHere: true));
    }

    [Fact]
    public void Mapped_name_pointing_at_a_different_guid_is_refused_even_if_we_cloned_that_guid()
    {
        Assert.False(PrefabApplyBookkeeping.CanClaimMappedSpawnableName(
            EmberHash, CrystalHash, mappedWasClonedHere: true));
        Assert.False(PrefabApplyBookkeeping.CanClaimMappedSpawnableName(
            CrystalHash, EmberHash, mappedWasClonedHere: false));
    }

    [Fact]
    public void Existing_guid_in_the_entity_map_is_refused_when_not_cloned_here()
    {
        Assert.True(PrefabApplyBookkeeping.MustRefuseExistingGuid(haveExisting: true, clonedHere: false));
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(haveExisting: true, clonedHere: true));
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(haveExisting: false, clonedHere: false));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void KeepEntryAfterApply_stays_only_on_success_or_pending(bool applied, bool pending, bool keep)
    {
        Assert.Equal(keep, PrefabApplyBookkeeping.KeepEntryAfterApply(applied, pending));
    }

    [Fact]
    public void Vanilla_name_reuse_report_is_not_pending_so_the_entry_is_dropped()
    {
        const string spawnable =
            "spawnable name 'Item_Ingredient_Crystal' already maps to -651247173 (not cloned by this process)";
        const string alreadyInMap =
            "guid -651247173 already in _PrefabGuidToEntityMap and was not cloned by this process";
        const string ourOtherGuid =
            "spawnable name 'x' already maps to our guid 802060347, not -651247173";
        const string foreign =
            "spawnable name 'x' already maps to -651247173 (not ours)";

        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(spawnable));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(alreadyInMap));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(ourOtherGuid));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(foreign));
        Assert.False(PrefabApplyBookkeeping.KeepEntryAfterApply(false, PrefabApplyBookkeeping.IsApplyStillPending(spawnable)));
    }

    [Theory]
    [InlineData("source spawnable 'Item_Ingredient_GreaterEssence' not found")]
    [InlineData("PrefabCollectionSystem or GameDataSystem missing in world Server")]
    [InlineData("world missing")]
    [InlineData("item key table: singleton count 0")]
    [InlineData("item key table: null container pointers")]
    public void Source_not_ready_reports_stay_pending(string report)
    {
        Assert.True(PrefabApplyBookkeeping.IsApplyStillPending(report));
        Assert.True(PrefabApplyBookkeeping.KeepEntryAfterApply(false, true));
    }

    [Fact]
    public void Key_table_full_is_not_pending()
    {
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending("item key table full: list full (1084/1084)"));
        Assert.False(PrefabApplyBookkeeping.KeepEntryAfterApply(false, false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_apply_report_is_not_pending(string? report)
    {
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(report));
    }

    [Fact]
    public void Tombstone_only_when_this_process_appended_the_key_row()
    {
        Assert.True(PrefabApplyBookkeeping.ShouldTombstone(appendedHere: true));
        Assert.False(PrefabApplyBookkeeping.ShouldTombstone(appendedHere: false));
    }

    [Fact]
    public void Describe_shows_appended_so_a_vanilla_collision_cannot_look_registered()
    {
        Assert.Equal(
            "registered=0 tombstones=0 appended=0",
            PrefabApplyBookkeeping.FormatRegistryDescribe(0, 0, 0));
        Assert.Equal(
            "registered=1 tombstones=0 appended=1",
            PrefabApplyBookkeeping.FormatRegistryDescribe(1, 0, 1));
        Assert.Equal(
            "registered=0 tombstones=1 appended=1",
            PrefabApplyBookkeeping.FormatRegistryDescribe(0, 1, 1));
    }

    [Fact]
    public void Describe_tombstone_detail_is_guid_and_id()
    {
        Assert.Equal(
            "; tombstone guid=802060347 id='sample.ember'",
            PrefabApplyBookkeeping.FormatTombstoneDetail(802060347, "sample.ember"));
        Assert.Equal(
            "; tombstone guid=1 id=''",
            PrefabApplyBookkeeping.FormatTombstoneDetail(1, null));
    }

    [Fact]
    public void Vanilla_collision_pipeline_never_registers_sends_or_tombstones()
    {
        bool canClaim = PrefabApplyBookkeeping.CanClaimMappedSpawnableName(
            CrystalHash, CrystalHash, mappedWasClonedHere: false);
        bool refuseExisting = PrefabApplyBookkeeping.MustRefuseExistingGuid(
            haveExisting: true, clonedHere: false);
        // Exact wording PrefabApplicator emits (not interpolated from NotClonedToken).
        const string report =
            "guid -651247173 already in _PrefabGuidToEntityMap and was not cloned by this process";
        const string spawnable =
            "spawnable name 'Item_Ingredient_Crystal' already maps to -651247173 (not cloned by this process)";
        bool pending = PrefabApplyBookkeeping.IsApplyStillPending(report);
        bool keep = PrefabApplyBookkeeping.KeepEntryAfterApply(appliedOk: false, stillPending: pending);
        bool tombstone = PrefabApplyBookkeeping.ShouldTombstone(appendedHere: false);
        string describe = PrefabApplyBookkeeping.FormatRegistryDescribe(
            registered: keep ? 1 : 0, tombstones: tombstone ? 1 : 0, appended: 0);

        Assert.False(canClaim);
        Assert.True(refuseExisting);
        Assert.False(pending);
        Assert.False(keep);
        Assert.False(tombstone);
        Assert.Equal("registered=0 tombstones=0 appended=0", describe);
        Assert.Equal("not cloned by this process", PrefabApplyBookkeeping.NotClonedToken);
        Assert.Contains(PrefabApplyBookkeeping.NotClonedToken, report);
        Assert.Contains(PrefabApplyBookkeeping.NotClonedToken, spawnable);
        Assert.Equal("already maps to", PrefabApplyBookkeeping.SpawnableMappedToken);
        Assert.Contains(PrefabApplyBookkeeping.SpawnableMappedToken, spawnable);
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(spawnable));
    }

    [Fact]
    public void Item_source_requires_ItemData_not_Buff()
    {
        Assert.True(PrefabApplyBookkeeping.SourceMatchesKind(PrefabKind.Item, sourceHasItemData: true, sourceHasBuff: false));
        Assert.False(PrefabApplyBookkeeping.SourceMatchesKind(PrefabKind.Item, sourceHasItemData: false, sourceHasBuff: true));
        Assert.False(PrefabApplyBookkeeping.SourceMatchesKind(PrefabKind.Item, sourceHasItemData: false, sourceHasBuff: false));
        Assert.Equal(
            PrefabApplyBookkeeping.ItemSourceRejectedToken,
            PrefabApplyBookkeeping.SourceKindRejectedMessage(PrefabKind.Item));
        Assert.Contains("DefineItem", PrefabApplyBookkeeping.ItemSourceRejectedToken);
    }

    [Fact]
    public void Buff_source_requires_Buff_not_ItemData()
    {
        Assert.True(PrefabApplyBookkeeping.SourceMatchesKind(PrefabKind.Buff, sourceHasItemData: false, sourceHasBuff: true));
        Assert.False(PrefabApplyBookkeeping.SourceMatchesKind(PrefabKind.Buff, sourceHasItemData: true, sourceHasBuff: false));
        Assert.False(PrefabApplyBookkeeping.SourceMatchesKind(PrefabKind.Buff, sourceHasItemData: false, sourceHasBuff: false));
        Assert.Equal(
            PrefabApplyBookkeeping.BuffSourceRejectedToken,
            PrefabApplyBookkeeping.SourceKindRejectedMessage(PrefabKind.Buff));
        Assert.Contains("DefineBuff", PrefabApplyBookkeeping.BuffSourceRejectedToken);
    }

    [Fact]
    public void Wrong_kind_refuse_is_not_pending()
    {
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(PrefabApplyBookkeeping.ItemSourceRejectedToken));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(PrefabApplyBookkeeping.BuffSourceRejectedToken));
        Assert.False(PrefabApplyBookkeeping.KeepEntryAfterApply(
            false, PrefabApplyBookkeeping.IsApplyStillPending(PrefabApplyBookkeeping.BuffSourceRejectedToken)));
    }

    [Fact]
    public void Buffs_do_not_append_ItemKeyLookup_and_do_not_tombstone()
    {
        Assert.True(PrefabApplyBookkeeping.ShouldAppendItemKey(PrefabKind.Item));
        Assert.False(PrefabApplyBookkeeping.ShouldAppendItemKey(PrefabKind.Buff));
        Assert.False(PrefabApplyBookkeeping.ShouldTombstone(appendedHere: false));
    }

    [Fact]
    public void Buff_icon_apply_runs_on_the_client_only()
    {
        Assert.True(PrefabIconPath.ShouldApply(isServerProcess: false, PrefabKind.Buff, "icons/nodrain.png"));
        Assert.False(PrefabIconPath.ShouldApply(isServerProcess: true, PrefabKind.Buff, "icons/nodrain.png"));
        Assert.False(PrefabIconPath.ShouldApply(isServerProcess: false, PrefabKind.Item, "icons/nodrain.png"));
        Assert.False(PrefabIconPath.ShouldApply(isServerProcess: false, PrefabKind.Buff, null));
        Assert.False(PrefabIconPath.ShouldApply(isServerProcess: false, PrefabKind.Buff, ""));
        Assert.False(PrefabIconPath.ShouldApply(isServerProcess: true, PrefabKind.Item, "icons/nodrain.png"));
    }

    [Theory]
    [InlineData(PrefabKind.Item)]
    [InlineData(PrefabKind.Buff)]
    public void Retired_then_reregistered_same_id_updates_in_place_for_item_and_buff(PrefabKind kind)
    {
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.UpdateInPlace,
            PrefabApplyBookkeeping.DecideSameIdApply(
                kind, kind,
                existingMatchesNewKind: true,
                sourceChanged: false,
                liveInstanceCount: 0));
        Assert.True(PrefabApplyBookkeeping.ShouldRememberRetiredClone(entityKept: true, appliedHere: true));
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(haveExisting: true, clonedHere: true));
        Assert.Equal(
            "; " + PrefabApplyBookkeeping.OwnershipKeptToken,
            PrefabApplyBookkeeping.FormatRetiredOwnership(kept: true));
    }

    [Theory]
    [InlineData(PrefabKind.Item, EmberHash)]
    [InlineData(PrefabKind.Buff, 650563057)]
    public void Snapshot_carries_a_retired_clone_without_a_tombstone(PrefabKind kind, int guid)
    {
        // Plugin unload during a Core swap clears Entries before snapshot. Items may still
        // land in cloned via tombstones; buffs never tombstone, so retired-or-applied is
        // the only way they survive.
        int[] cloned = PrefabApplyBookkeeping.UnionClonedHashes(
            liveCloned: Array.Empty<int>(),
            retiredOrApplied: new[] { guid },
            tombstones: Array.Empty<int>());
        Assert.Contains(guid, cloned);
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(
            haveExisting: true, clonedHere: ContainsHash(cloned, guid)));
        if (kind == PrefabKind.Buff)
            Assert.False(PrefabApplyBookkeeping.ShouldTombstone(appendedHere: false));
    }

    [Theory]
    [InlineData(PrefabKind.Item, CrystalHash)]
    [InlineData(PrefabKind.Buff, 650563057)]
    public void Foreign_guid_not_in_the_snapshot_is_refused_for_item_and_buff(PrefabKind kind, int guid)
    {
        int[] cloned = PrefabApplyBookkeeping.UnionClonedHashes(null, null, null);
        Assert.DoesNotContain(guid, cloned);
        Assert.True(PrefabApplyBookkeeping.MustRefuseExistingGuid(haveExisting: true, clonedHere: false));
        Assert.Equal(kind == PrefabKind.Item, PrefabApplyBookkeeping.ShouldAppendItemKey(kind));
        Assert.False(PrefabApplyBookkeeping.ShouldRememberRetiredClone(entityKept: true, appliedHere: false));
        Assert.False(PrefabApplyBookkeeping.ShouldRememberRetiredClone(entityKept: false, appliedHere: true));
        Assert.Equal("", PrefabApplyBookkeeping.FormatRetiredOwnership(kept: false));
    }

    [Fact]
    public void Snapshot_union_is_live_plus_retired_plus_tombs_and_dedups()
    {
        int[] cloned = PrefabApplyBookkeeping.UnionClonedHashes(
            liveCloned: new[] { EmberHash },
            retiredOrApplied: new[] { 650563057, EmberHash },
            tombstones: new[] { EmberHash });
        Assert.Equal(2, cloned.Length);
        Assert.Contains(EmberHash, cloned);
        Assert.Contains(650563057, cloned);
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(true, ContainsHash(cloned, EmberHash)));
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(true, ContainsHash(cloned, 650563057)));
        Assert.True(PrefabApplyBookkeeping.MustRefuseExistingGuid(true, ContainsHash(cloned, CrystalHash)));
    }

    [Fact]
    public void Rehydrate_of_a_retired_buff_is_the_same_clonedHere_bit_as_an_item()
    {
        // ImportProcessMemory HashSet-adds the snapshot cloned array. The apply gate after
        // that is MustRefuseExistingGuid(haveExisting, clonedHere) for both kinds.
        int[] snap = PrefabApplyBookkeeping.UnionClonedHashes(
            liveCloned: new[] { EmberHash },
            retiredOrApplied: new[] { 650563057 },
            tombstones: new[] { EmberHash });
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(true, ContainsHash(snap, EmberHash)));
        Assert.False(PrefabApplyBookkeeping.MustRefuseExistingGuid(true, ContainsHash(snap, 650563057)));
        Assert.True(PrefabApplyBookkeeping.MustRefuseExistingGuid(true, clonedHere: false));
    }

    [Fact]
    public void Net_snapshot_header_is_prefab_guid_then_schema_type()
    {
        Assert.Equal(0, PrefabApplyBookkeeping.NetSnapshotPrefabGuidOffset);
        Assert.Equal(4, PrefabApplyBookkeeping.NetSnapshotTypeOffset);
        Assert.Equal(6, PrefabApplyBookkeeping.NetSnapshotHeaderMinBytes);
        Assert.True(PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(6));
        Assert.True(PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(56));
        Assert.False(PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(5));
        Assert.False(PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(0));
    }

    [Fact]
    public void Net_snapshot_guid_is_rewritten_when_instantiate_copied_the_source()
    {
        const int haste = 900030866;
        const int nodrain = 650563057;
        Assert.True(PrefabApplyBookkeeping.ShouldRewriteNetSnapshotGuid(haste, nodrain));
        Assert.False(PrefabApplyBookkeeping.ShouldRewriteNetSnapshotGuid(nodrain, nodrain));

        // 56-byte spawn blob (length in bytes). Element count of that blob as a 56-byte
        // struct is 1, which is not a writable header.
        const ushort schema = 42;
        byte[] buffer = new byte[56];
        WriteInt32(buffer, PrefabApplyBookkeeping.NetSnapshotPrefabGuidOffset, haste);
        WriteUInt16(buffer, PrefabApplyBookkeeping.NetSnapshotTypeOffset, schema);

        Assert.True(PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(buffer.Length));
        Assert.False(PrefabApplyBookkeeping.NetSnapshotHeaderIsWritable(1));
        Assert.False(PrefabApplyBookkeeping.TryRewriteNetSnapshotGuid(buffer, byteLength: 1, nodrain));
        Assert.Equal(haste, ReadInt32(buffer, PrefabApplyBookkeeping.NetSnapshotPrefabGuidOffset));

        Assert.True(PrefabApplyBookkeeping.TryRewriteNetSnapshotGuid(buffer, buffer.Length, nodrain));
        Assert.Equal(nodrain, ReadInt32(buffer, PrefabApplyBookkeeping.NetSnapshotPrefabGuidOffset));
        Assert.Equal(schema, ReadUInt16(buffer, PrefabApplyBookkeeping.NetSnapshotTypeOffset));
        Assert.False(PrefabApplyBookkeeping.TryRewriteNetSnapshotGuid(buffer, buffer.Length, nodrain));
        Assert.Equal(
            "net=650563057 type=42 (was 900030866)",
            PrefabApplyBookkeeping.FormatNetIdentity(nodrain, schema, haste));
    }

    [Fact]
    public void Net_identity_report_is_the_apply_log_fragment()
    {
        const int haste = 900030866;
        const int nodrain = 650563057;
        Assert.Equal("net=none", PrefabApplyBookkeeping.NetNoneToken);
        Assert.Equal("net=short (4)", PrefabApplyBookkeeping.FormatNetShort(4));
        Assert.Equal("net=error", PrefabApplyBookkeeping.NetErrorToken);
        Assert.Equal(
            "net=650563057 type=42 (was 900030866)",
            PrefabApplyBookkeeping.FormatNetIdentity(nodrain, 42, haste));
        Assert.Equal(
            "net=650563057 type=42",
            PrefabApplyBookkeeping.FormatNetIdentity(nodrain, 42, nodrain));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(
            PrefabApplyBookkeeping.FormatNetIdentity(nodrain, 42, haste)));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(PrefabApplyBookkeeping.NetNoneToken));
    }

    [Fact]
    public void Same_id_changed_CloneOf_without_instances_is_retarget()
    {
        var action = PrefabApplyBookkeeping.DecideSameIdApply(
            PrefabKind.Buff, PrefabKind.Buff,
            existingMatchesNewKind: true,
            sourceChanged: true,
            liveInstanceCount: 0);
        Assert.Equal(PrefabApplyBookkeeping.SameIdApplyAction.RetargetClone, action);
        Assert.Equal(
            "retargeted (was 111, now 222)",
            PrefabApplyBookkeeping.FormatRetargeted(111, 222));
        Assert.Equal(
            PrefabApplyBookkeeping.RetargetedToken + " (was 111, now 222)",
            PrefabApplyBookkeeping.FormatRetargeted(111, 222));
        Assert.False(PrefabApplyBookkeeping.IsFatalIdentityRefuse(
            PrefabApplyBookkeeping.FormatRetargeted(111, 222)));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(
            PrefabApplyBookkeeping.FormatRetargeted(111, 222)));
    }

    [Fact]
    public void Same_id_changed_CloneOf_with_instances_is_refused_log()
    {
        var action = PrefabApplyBookkeeping.DecideSameIdApply(
            PrefabKind.Buff, PrefabKind.Buff,
            existingMatchesNewKind: true,
            sourceChanged: true,
            liveInstanceCount: 3);
        Assert.Equal(PrefabApplyBookkeeping.SameIdApplyAction.RefuseSourceChange, action);
        string log = PrefabApplyBookkeeping.FormatSourceChangedRefuse("sample.nodrain", 111, 222, 3);
        Assert.Equal(
            "PrefabRegistry: 'sample.nodrain' source changed (was 111, now 222) but 3 live instance(s) exist; restart to retarget",
            log);
        Assert.True(PrefabApplyBookkeeping.IsFatalIdentityRefuse(log));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(log));
        Assert.False(PrefabApplyBookkeeping.KeepEntryAfterApply(false, PrefabApplyBookkeeping.IsApplyStillPending(log)));
    }

    [Theory]
    [InlineData(PrefabKind.Item, PrefabKind.Buff)]
    [InlineData(PrefabKind.Buff, PrefabKind.Item)]
    public void Same_id_Item_Buff_kind_change_is_always_refused(PrefabKind was, PrefabKind now)
    {
        var action = PrefabApplyBookkeeping.DecideSameIdApply(
            was, now,
            existingMatchesNewKind: false,
            sourceChanged: true,
            liveInstanceCount: 0);
        Assert.Equal(PrefabApplyBookkeeping.SameIdApplyAction.RefuseKindChange, action);
        string log = PrefabApplyBookkeeping.FormatKindChangedRefuse("foo", was, now);
        Assert.Equal(
            "PrefabRegistry: 'foo' kind changed (was " + was + ", now " + now + "); use a new id",
            log);
        Assert.Contains(PrefabApplyBookkeeping.KindChangedToken, log);
        Assert.Contains(PrefabApplyBookkeeping.UseNewIdToken, log);
        Assert.True(PrefabApplyBookkeeping.IsFatalIdentityRefuse(log));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(log));
        Assert.False(PrefabApplyBookkeeping.KeepEntryAfterApply(false, PrefabApplyBookkeeping.IsApplyStillPending(log)));
    }

    [Fact]
    public void Rollback_on_failed_append_pops_a_new_key_row()
    {
        Assert.True(PrefabApplyBookkeeping.ShouldRollbackKey(appliedOk: false, keyWasNew: true));
        Assert.False(PrefabApplyBookkeeping.ShouldRollbackKey(appliedOk: true, keyWasNew: true));
        Assert.False(PrefabApplyBookkeeping.ShouldRollbackKey(appliedOk: false, keyWasNew: false));
        Assert.False(PrefabApplyBookkeeping.ShouldRollbackKey(appliedOk: true, keyWasNew: false));
    }

    [Fact]
    public void Tombstone_for_a_non_item_appended_guid()
    {
        Assert.True(PrefabApplyBookkeeping.ShouldTombstone(
            appendedHere: true, hasLiveEntry: true, liveKind: PrefabKind.Buff));
        Assert.False(PrefabApplyBookkeeping.ShouldTombstone(
            appendedHere: true, hasLiveEntry: true, liveKind: PrefabKind.Item));
        Assert.True(PrefabApplyBookkeeping.ShouldTombstone(
            appendedHere: true, hasLiveEntry: false, liveKind: PrefabKind.Item));
        Assert.False(PrefabApplyBookkeeping.ShouldTombstone(
            appendedHere: false, hasLiveEntry: true, liveKind: PrefabKind.Buff));
        Assert.True(PrefabApplyBookkeeping.ShouldTombstone(appendedHere: true));
    }

    [Fact]
    public void Same_kind_same_source_updates_in_place()
    {
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.UpdateInPlace,
            PrefabApplyBookkeeping.DecideSameIdApply(
                PrefabKind.Buff, PrefabKind.Buff,
                existingMatchesNewKind: true,
                sourceChanged: false,
                liveInstanceCount: 0));
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.UpdateInPlace,
            PrefabApplyBookkeeping.DecideSameIdApply(
                PrefabKind.Item, PrefabKind.Item,
                existingMatchesNewKind: true,
                sourceChanged: false,
                liveInstanceCount: 2));
    }

    [Fact]
    public void Existing_entity_kind_mismatch_fails_that_definition()
    {
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.RefuseExistingMismatch,
            PrefabApplyBookkeeping.DecideSameIdApply(
                PrefabKind.Buff, PrefabKind.Buff,
                existingMatchesNewKind: false,
                sourceChanged: false,
                liveInstanceCount: 0));
        Assert.False(PrefabApplyBookkeeping.IsApplyStillPending(
            PrefabApplyBookkeeping.SourceKindRejectedMessage(PrefabKind.Buff)));
    }

    [Fact]
    public void Remembered_source_hash_detects_CloneOf_change()
    {
        Assert.True(PrefabApplyBookkeeping.SourceChanged(111, 222, fingerprintMatches: true));
        Assert.False(PrefabApplyBookkeeping.SourceChanged(111, 111, fingerprintMatches: false));
        Assert.True(PrefabApplyBookkeeping.SourceChanged(0, 222, fingerprintMatches: false));
        Assert.False(PrefabApplyBookkeeping.SourceChanged(0, 222, fingerprintMatches: true));
    }

    [Fact]
    public void Swap_snapshot_with_different_source_retargets_same_source_updates()
    {
        const int nodrain = 650563057;
        const int haste = 900030866;
        const int immaterial = 111;
        var snapGuids = new[] { nodrain };
        var snapSources = new[] { immaterial };

        Assert.True(PrefabApplyBookkeeping.TrySourceFromSnapshot(
            nodrain, snapGuids, snapSources, out int remembered));
        Assert.Equal(immaterial, remembered);

        // Same-schema fingerprint would miss this pair; the snapshot hash must win.
        Assert.True(PrefabApplyBookkeeping.SourceChanged(remembered, haste, fingerprintMatches: true));
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.RetargetClone,
            PrefabApplyBookkeeping.DecideSameIdApply(
                PrefabKind.Buff, PrefabKind.Buff,
                existingMatchesNewKind: true,
                sourceChanged: true,
                liveInstanceCount: 0));
        Assert.Equal(
            "retargeted (was 111, now 900030866)",
            PrefabApplyBookkeeping.FormatRetargeted(remembered, haste));

        Assert.False(PrefabApplyBookkeeping.SourceChanged(remembered, immaterial, fingerprintMatches: false));
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.UpdateInPlace,
            PrefabApplyBookkeeping.DecideSameIdApply(
                PrefabKind.Buff, PrefabKind.Buff,
                existingMatchesNewKind: true,
                sourceChanged: false,
                liveInstanceCount: 0));

        var mem = new ContentMemory();
        mem.ReplaceCloneSources(snapGuids, snapSources);
        mem.Replace(snapGuids, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<string>());
        mem.TakeCloneSources(out var storedGuids, out var storedSources);
        Assert.Equal(snapGuids, storedGuids);
        Assert.Equal(snapSources, storedSources);
        Assert.True(PrefabApplyBookkeeping.TrySourceFromSnapshot(
            nodrain, storedGuids, storedSources, out int roundTripped));
        Assert.Equal(immaterial, roundTripped);
    }

    [Fact]
    public void Live_query_throw_fails_closed_and_refuses_retarget()
    {
        Assert.Equal(1, PrefabApplyBookkeeping.LiveCountForDecision(-1));
        Assert.Equal(0, PrefabApplyBookkeeping.LiveCountForDecision(0));
        Assert.Equal(3, PrefabApplyBookkeeping.LiveCountForDecision(3));
        Assert.Equal(
            PrefabApplyBookkeeping.SameIdApplyAction.RefuseSourceChange,
            PrefabApplyBookkeeping.DecideSameIdApply(
                PrefabKind.Buff, PrefabKind.Buff,
                existingMatchesNewKind: true,
                sourceChanged: true,
                liveInstanceCount: PrefabApplyBookkeeping.LiveCountForDecision(
                    PrefabApplyBookkeeping.LiveInstanceCountUnknown)));
        Assert.Equal(
            "PrefabApplicator: live-instance count for guid 650563057 failed closed (treating as instances exist)",
            PrefabApplyBookkeeping.FormatLiveQueryFailedClosed(650563057));
    }

    [Fact]
    public void Failed_retarget_remap_rolls_back_with_one_log_line()
    {
        Assert.Equal(
            "PrefabApplicator: retarget of 'sample.nodrain' rolled back (bookkeeping rows)",
            PrefabApplyBookkeeping.FormatRetargetRemapFailed("sample.nodrain"));
    }

    [Fact]
    public void PrfD_after_retarget_log_is_the_client_rebuild_line()
    {
        Assert.Equal(
            "PrefabRegistry: sent PrfD after retarget of 'sample.nodrain' so connected clients rebuild the clone",
            PrefabApplyBookkeeping.FormatPrfDAfterRetarget("sample.nodrain"));
    }

    [Fact]
    public void Named_only_does_not_clear_UseParent()
    {
        Assert.False(PrefabApplyBookkeeping.ShouldClearUseParent(wroteHeader: true, wroteDescription: false));
        Assert.False(PrefabApplyBookkeeping.ShouldClearUseParent(wroteHeader: false, wroteDescription: true));
        Assert.True(PrefabApplyBookkeeping.ShouldClearUseParent(wroteHeader: true, wroteDescription: true));
        Assert.False(PrefabApplyBookkeeping.ShouldClearUseParent(wroteHeader: false, wroteDescription: false));
    }

    [Fact]
    public void NetSnapshot_fingerprint_detects_Immaterial_to_Haste()
    {
        Assert.False(PrefabApplyBookkeeping.CloneLooksLikeSource(
            cloneHasNetSnapshot: false, cloneSnapshotType: 0,
            sourceHasNetSnapshot: true, sourceSnapshotType: 42));
        Assert.True(PrefabApplyBookkeeping.CloneLooksLikeSource(
            cloneHasNetSnapshot: true, cloneSnapshotType: 42,
            sourceHasNetSnapshot: true, sourceSnapshotType: 42));
        Assert.False(PrefabApplyBookkeeping.CloneLooksLikeSource(
            cloneHasNetSnapshot: true, cloneSnapshotType: 7,
            sourceHasNetSnapshot: true, sourceSnapshotType: 42));
        Assert.True(PrefabApplyBookkeeping.CloneLooksLikeSource(
            cloneHasNetSnapshot: false, cloneSnapshotType: 0,
            sourceHasNetSnapshot: false, sourceSnapshotType: 0));
    }

    [Fact]
    public void Infer_kind_from_Buff_vs_ItemData()
    {
        Assert.Equal(PrefabKind.Buff, PrefabApplyBookkeeping.InferKindFromComponents(false, true));
        Assert.Equal(PrefabKind.Item, PrefabApplyBookkeeping.InferKindFromComponents(true, false));
        Assert.Equal(PrefabKind.Buff, PrefabApplyBookkeeping.InferKindFromComponents(true, true));
        Assert.Equal(PrefabKind.Item, PrefabApplyBookkeeping.InferKindFromComponents(false, false));
    }

    [Fact]
    public void Source_changed_refuse_unknown_previous_source()
    {
        Assert.Equal("unknown", PrefabApplyBookkeeping.FormatSourceHash(0));
        Assert.Equal(
            "PrefabRegistry: 'sample.nodrain' source changed (was unknown, now 900030866) but 1 live instance(s) exist; restart to retarget",
            PrefabApplyBookkeeping.FormatSourceChangedRefuse("sample.nodrain", 0, 900030866, 1));
    }

    private static bool ContainsHash(int[] hashes, int guid)
    {
        for (int i = 0; i < hashes.Length; i++)
        {
            if (hashes[i] == guid) return true;
        }
        return false;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] buffer, int offset) =>
        buffer[offset]
        | (buffer[offset + 1] << 8)
        | (buffer[offset + 2] << 16)
        | (buffer[offset + 3] << 24);

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
}
