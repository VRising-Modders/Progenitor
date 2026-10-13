using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// UnlockArgs is the debug-bridge <c>unlock</c> policy: argument parse, what an unlock or a lock
/// writes (planned against the game's own <c>HasUnlockedProgression</c> rule), and the record that
/// lets <c>revert</c> remove exactly what the op added. UnlockOps applies it to a live player.
/// </summary>
public class DevUnlockArgsTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static UnlockArgs.State State(int[] progression, int[]? blueprintsOn = null, int[]? blueprintsOff = null,
        int[]? recipesOn = null, int[]? recipesOff = null, int[]? shapeshifts = null)
    {
        var s = new UnlockArgs.State();
        s.Progression.AddRange(progression);
        foreach (var g in blueprintsOn ?? Array.Empty<int>()) s.BlueprintsOn.Add(g);
        foreach (var g in blueprintsOff ?? Array.Empty<int>()) s.BlueprintsContentLocked.Add(g);
        foreach (var g in recipesOn ?? Array.Empty<int>()) s.RecipesOn.Add(g);
        foreach (var g in recipesOff ?? Array.Empty<int>()) s.RecipesContentLocked.Add(g);
        foreach (var g in shapeshifts ?? Array.Empty<int>()) s.Shapeshifts.Add(g);
        return s;
    }

    // ---- parse ----------------------------------------------------------------------------------

    [Fact]
    public void Parse_defaults_to_status_for_a_named_user()
    {
        var r = UnlockArgs.Parse(Json("{\"user\":\"  TestAdmin \"}"));
        Assert.Equal(UnlockArgs.UnlockAction.Status, r.Action);
        Assert.Equal("TestAdmin", r.User.Name);
        Assert.Null(r.User.Index);
        Assert.False(r.DryRun);
        Assert.Equal(UnlockArgs.DefaultLimit, r.Limit);
        Assert.False(UnlockArgs.Writes(r.Action));
    }

    [Fact]
    public void Parse_numeric_user_is_a_user_index()
    {
        var r = UnlockArgs.Parse(Json("{\"user\":3,\"action\":\"all\",\"dryRun\":true}"));
        Assert.Equal(3, r.User.Index);
        Assert.Null(r.User.Name);
        Assert.Equal(UnlockArgs.UnlockAction.All, r.Action);
        Assert.True(r.DryRun);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"user\":\"\"}")]
    [InlineData("{\"user\":-1}")]
    [InlineData("{\"user\":true}")]
    [InlineData("[]")]
    public void Parse_refuses_a_missing_or_bad_user(string json)
    {
        Assert.Throws<ArgumentException>(() => UnlockArgs.Parse(Json(json)));
    }

    [Fact]
    public void Parse_reads_a_mixed_prefab_list()
    {
        var r = UnlockArgs.Parse(Json("{\"user\":\"A\",\"action\":\"prefabs\",\"prefabs\":[\"TM_Workstation_Furnace\",-123,\"456\"]}"));
        Assert.Equal(3, r.Prefabs.Count);
        Assert.False(r.Prefabs[0].IsHash);
        Assert.Equal("TM_Workstation_Furnace", r.Prefabs[0].Name);
        Assert.True(r.Prefabs[1].IsHash);
        Assert.Equal(-123, r.Prefabs[1].Hash);
        Assert.Equal(456, r.Prefabs[2].Hash);
    }

    [Theory]
    [InlineData("{\"user\":\"A\",\"action\":\"prefabs\"}")]
    [InlineData("{\"user\":\"A\",\"action\":\"lock\"}")]
    [InlineData("{\"user\":\"A\",\"action\":\"prefabs\",\"prefabs\":[]}")]
    [InlineData("{\"user\":\"A\",\"action\":\"prefabs\",\"prefabs\":[0]}")]
    [InlineData("{\"user\":\"A\",\"action\":\"prefabs\",\"prefabs\":[\" \"]}")]
    [InlineData("{\"user\":\"A\",\"action\":\"prefabs\",\"prefabs\":[{}]}")]
    [InlineData("{\"user\":\"A\",\"action\":\"prefabs\",\"prefabs\":\"x\"}")]
    [InlineData("{\"user\":\"A\",\"action\":\"all\",\"prefabs\":[1]}")]
    [InlineData("{\"user\":\"A\",\"action\":\"revert\",\"prefabs\":[1]}")]
    [InlineData("{\"user\":\"A\",\"action\":\"everything\"}")]
    [InlineData("{\"user\":\"A\",\"action\":1}")]
    [InlineData("{\"user\":\"A\",\"dryRun\":\"yes\"}")]
    [InlineData("{\"user\":\"A\",\"limit\":-1}")]
    [InlineData("{\"user\":\"A\",\"limit\":100000}")]
    public void Parse_refuses_inconsistent_arguments(string json)
    {
        Assert.Throws<ArgumentException>(() => UnlockArgs.Parse(Json(json)));
    }

    [Fact]
    public void Parse_caps_the_prefab_list()
    {
        var big = "[" + string.Join(",", Enumerable.Range(1, UnlockArgs.MaxPrefabs + 1)) + "]";
        Assert.Throws<ArgumentException>(() => UnlockArgs.Parse(Json("{\"user\":\"A\",\"action\":\"lock\",\"prefabs\":" + big + "}")));
    }

    // ---- the game's unlocked test ---------------------------------------------------------------

    [Fact]
    public void IsUnlocked_matches_HasUnlockedProgression()
    {
        var s = State(new[] { 1 }, blueprintsOn: new[] { 2 }, blueprintsOff: new[] { 3 }, recipesOn: new[] { 4 }, recipesOff: new[] { 5 }, shapeshifts: new[] { 6 });
        Assert.True(s.IsUnlocked(1));
        Assert.True(s.IsUnlocked(2));
        Assert.False(s.IsUnlocked(3)); // content flag false does not count
        Assert.True(s.IsUnlocked(4));
        Assert.False(s.IsUnlocked(5));
        Assert.True(s.IsUnlocked(6));
        Assert.False(s.IsUnlocked(7));
        Assert.True(s.IsContentLocked(3));
        Assert.True(s.IsContentLocked(5));
        Assert.False(s.IsContentLocked(2));
    }

    // ---- unlock plan ----------------------------------------------------------------------------

    [Fact]
    public void PlanUnlock_skips_unlocked_duplicates_zero_and_content_locked()
    {
        var s = State(new[] { 10, 11 }, blueprintsOn: new[] { 20 }, blueprintsOff: new[] { 30 });
        var plan = UnlockArgs.PlanUnlock(s, new[] { 10, 20, 30, 0, 40, 41, 40, 42 });
        Assert.Equal(new[] { 40, 41, 42 }, plan.New);
        Assert.Equal(2, plan.AlreadyUnlocked);
        Assert.Equal(new[] { 30 }, plan.ContentLocked);
        Assert.Equal(42, plan.Trigger);
        // Bulk write: the current list plus every new GUID but the trigger.
        Assert.Equal(new[] { 10, 11, 40, 41 }, plan.BulkWrite);
    }

    [Fact]
    public void PlanUnlock_one_new_guid_is_one_event_and_no_raw_write()
    {
        var plan = UnlockArgs.PlanUnlock(State(new[] { 1 }), new[] { 1, 2 });
        Assert.Equal(new[] { 2 }, plan.New);
        Assert.Equal(2, plan.Trigger);
        Assert.Null(plan.BulkWrite);
    }

    [Fact]
    public void PlanUnlock_nothing_new_writes_nothing()
    {
        var plan = UnlockArgs.PlanUnlock(State(new[] { 1 }, recipesOn: new[] { 2 }), new[] { 1, 2 });
        Assert.Empty(plan.New);
        Assert.Null(plan.Trigger);
        Assert.Null(plan.BulkWrite);
    }

    [Fact]
    public void PlanUnlock_trigger_is_never_in_the_bulk_write()
    {
        var s = State(new[] { 5 });
        var plan = UnlockArgs.PlanUnlock(s, Enumerable.Range(100, 50));
        Assert.DoesNotContain(plan.Trigger!.Value, plan.BulkWrite!);
        Assert.False(s.IsUnlocked(plan.Trigger.Value));
        Assert.Equal(s.Progression.Count + plan.New.Count - 1, plan.BulkWrite!.Count);
    }

    // ---- lock plan ------------------------------------------------------------------------------

    [Fact]
    public void PlanLock_removes_targets_and_picks_a_kept_trigger_outside_derived_buffers()
    {
        // 1 = tech (no derived row), 2 = blueprint listed directly (derived on), 3 and 4 = targets.
        var s = State(new[] { 1, 3, 2, 4 }, blueprintsOn: new[] { 2, 3, 4 });
        var plan = UnlockArgs.PlanLock(s, new[] { 3, 4, 99 });
        Assert.Null(plan.Refusal);
        Assert.Equal(new[] { 3, 4 }, plan.Removed);
        Assert.Equal(new[] { 99 }, plan.NotInProgression);
        Assert.Equal(1, plan.Trigger); // 2 is a derived blueprint row, so the handler would skip it
        Assert.Equal(new[] { 2 }, plan.Write);
    }

    [Fact]
    public void PlanLock_prefers_the_last_eligible_kept_row()
    {
        var s = State(new[] { 7, 8, 9, 5 });
        var plan = UnlockArgs.PlanLock(s, new[] { 5 });
        Assert.Equal(9, plan.Trigger);
        Assert.Equal(new[] { 7, 8 }, plan.Write);
    }

    [Fact]
    public void PlanLock_refuses_when_no_kept_row_can_trigger_the_rebuild()
    {
        var s = State(new[] { 2, 3 }, recipesOn: new[] { 2 });
        var plan = UnlockArgs.PlanLock(s, new[] { 3 });
        Assert.NotNull(plan.Refusal);
        Assert.Null(plan.Write);
        Assert.Null(plan.Trigger);

        var empty = UnlockArgs.PlanLock(State(new[] { 3 }), new[] { 3 });
        Assert.NotNull(empty.Refusal);
    }

    [Fact]
    public void PlanLock_targets_absent_from_the_list_write_nothing()
    {
        var plan = UnlockArgs.PlanLock(State(new[] { 1 }, blueprintsOn: new[] { 50 }), new[] { 50, 0 });
        Assert.Empty(plan.Removed);
        Assert.Equal(new[] { 50 }, plan.NotInProgression);
        Assert.Null(plan.Write);
        Assert.Null(plan.Trigger);
        Assert.Null(plan.Refusal);
    }

    [Fact]
    public void Unlock_then_revert_plans_restore_the_original_list()
    {
        var before = State(new[] { 1, 2 });
        var unlock = UnlockArgs.PlanUnlock(before, new[] { 10, 11, 12 });
        // After the handler: bulk rows + the trigger appended, derived buffers rebuilt with them.
        var after = State(unlock.BulkWrite!.Concat(new[] { unlock.Trigger!.Value }).ToArray(), blueprintsOn: new[] { 10, 11, 12 });
        var record = UnlockArgs.Merge(null, "A", 0, unlock.New, DateTime.UtcNow);
        var revert = UnlockArgs.PlanLock(after, record.Guids);
        Assert.Equal(new[] { 10, 11, 12 }, revert.Removed);
        Assert.Equal(2, revert.Trigger);
        // The trigger is raised again and re-added by the handler: the list ends as before.
        Assert.Equal(new[] { 1, 2 }, revert.Write!.Concat(new[] { revert.Trigger!.Value }));
    }

    // ---- record ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("TestAdmin", "progenitor-unlock-testadmin.json")]
    [InlineData("Head One", "progenitor-unlock-head_one.json")]
    [InlineData("a/b\\c..", "progenitor-unlock-a_b_c__.json")]
    [InlineData("x-y_z9", "progenitor-unlock-x-y_z9.json")]
    public void RecordFileName_is_sanitized(string name, string expected)
    {
        Assert.Equal(expected, UnlockArgs.RecordFileName(name));
    }

    [Fact]
    public void RecordFileName_refuses_an_empty_name()
    {
        Assert.Throws<ArgumentException>(() => UnlockArgs.RecordFileName(" "));
    }

    [Fact]
    public void Merge_appends_new_guids_once_in_order()
    {
        var first = UnlockArgs.Merge(null, "A", 4, new[] { 3, 1, 3, 0 }, new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new[] { 3, 1 }, first.Guids);
        Assert.Equal("2026-10-04T12:00:00Z", first.UpdatedUtc);
        var second = UnlockArgs.Merge(first, "a", 4, new[] { 1, 2 }, DateTime.UtcNow);
        Assert.Equal(new[] { 3, 1, 2 }, second.Guids);
    }

    [Fact]
    public void Merge_refuses_another_users_record()
    {
        var r = UnlockArgs.Merge(null, "A", 0, new[] { 1 }, DateTime.UtcNow);
        Assert.Throws<InvalidOperationException>(() => UnlockArgs.Merge(r, "B", 1, new[] { 2 }, DateTime.UtcNow));
    }

    [Fact]
    public void Record_round_trips()
    {
        var r = UnlockArgs.Merge(null, "TestAdmin", 7, new[] { -5, 9 }, DateTime.UtcNow);
        var back = UnlockArgs.ParseRecord(UnlockArgs.Serialize(r));
        Assert.Equal("TestAdmin", back.User);
        Assert.Equal(7, back.UserIndex);
        Assert.Equal(new[] { -5, 9 }, back.Guids);
        Assert.Equal(r.UpdatedUtc, back.UpdatedUtc);
    }

    [Fact]
    public void Record_keeps_the_mod_that_registered_each_progenitor_guid()
    {
        var crystal = new UnlockArgs.GuidOwner { Mod = "progenitor.sample", Id = "sample.bench.crystal-thread", Kind = "recipe" };
        var first = UnlockArgs.Merge(null, "TestAdmin", 7, new[] { -1276252187, 9 }, DateTime.UtcNow,
            new Dictionary<int, UnlockArgs.GuidOwner> { [-1276252187] = crystal, [404] = crystal });
        Assert.Equal(new[] { -1276252187 }, first.Owners.Keys);   // 404 is not in the record: no row

        var second = UnlockArgs.Merge(first, "TestAdmin", 7, new[] { 11 }, DateTime.UtcNow,
            new Dictionary<int, UnlockArgs.GuidOwner> { [11] = new() { Mod = "other.mod", Id = "x", Kind = "recipe" } });
        var back = UnlockArgs.ParseRecord(UnlockArgs.Serialize(second));
        Assert.Equal(new[] { -1276252187, 9, 11 }, back.Guids);
        Assert.Equal(2, back.Owners.Count);
        Assert.Equal("progenitor.sample", back.Owners[-1276252187].Mod);
        Assert.Equal("sample.bench.crystal-thread", back.Owners[-1276252187].Id);
        Assert.Equal("recipe", back.Owners[-1276252187].Kind);
        Assert.Equal("other.mod", back.Owners[11].Mod);
        Assert.False(back.Owners.ContainsKey(9));                 // vanilla GUID: no owner
    }

    [Fact]
    public void A_record_written_before_owners_still_parses()
    {
        var r = UnlockArgs.ParseRecord("{\"version\":1,\"user\":\"A\",\"userIndex\":0,\"guids\":[5]}");
        Assert.Equal(new[] { 5 }, r.Guids);
        Assert.Empty(r.Owners);
    }

    [Theory]
    [InlineData("{\"version\":1,\"user\":\"A\",\"guids\":[1],\"owners\":[]}")]
    [InlineData("{\"version\":1,\"user\":\"A\",\"guids\":[1],\"owners\":{\"x\":{}}}")]
    [InlineData("{\"version\":1,\"user\":\"A\",\"guids\":[1],\"owners\":{\"1\":\"mod\"}}")]
    public void ParseRecord_skips_malformed_owners(string json)
    {
        var r = UnlockArgs.ParseRecord(json);
        Assert.Equal(new[] { 1 }, r.Guids); // the record still works for unlock and revert
        Assert.Empty(r.Owners);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"version\":2,\"user\":\"A\",\"guids\":[]}")]
    [InlineData("{\"version\":1,\"guids\":[]}")]
    [InlineData("{\"version\":1,\"user\":\"A\"}")]
    [InlineData("{\"version\":1,\"user\":\"A\",\"guids\":[\"x\"]}")]
    public void ParseRecord_refuses_a_malformed_record(string json)
    {
        Assert.Throws<FormatException>(() => UnlockArgs.ParseRecord(json));
    }

    // ---- read-back -------------------------------------------------------------------------------

    [Fact]
    public void Mismatches_reports_what_did_not_take()
    {
        var after = State(new[] { 1 }, recipesOn: new[] { 2 });
        Assert.Equal(new[] { 3 }, UnlockArgs.Mismatches(after, new[] { 1, 2, 3 }, wantUnlocked: true));
        Assert.Equal(new[] { 1, 2 }, UnlockArgs.Mismatches(after, new[] { 1, 2, 3 }, wantUnlocked: false));
    }

    [Fact]
    public void Cap_limits_in_order()
    {
        Assert.Equal(new[] { 1, 2 }, UnlockArgs.Cap(new[] { 1, 2, 3 }, 2));
        Assert.Empty(UnlockArgs.Cap(new[] { 1 }, 0));
    }
}
