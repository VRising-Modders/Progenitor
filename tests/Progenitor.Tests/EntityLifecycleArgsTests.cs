using System;
using System.Text;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// EntityLifecycleArgs is the debug-bridge <c>entity</c> id parse and 64-entry cap.
/// EntityLifecycleOps applies it against a live world; these tests cover the parts that
/// do not need the game.
/// </summary>
public class EntityLifecycleArgsTests
{
    [Fact]
    public void TryParseId_parses_index_and_version()
    {
        Assert.True(EntityLifecycleArgs.TryParseId("12:3", out var index, out var version));
        Assert.Equal(12, index);
        Assert.Equal(3, version);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("a:b")]
    [InlineData("12:0")]
    [InlineData("-1:1")]
    public void TryParseId_rejects_unparseable_ids(string text)
    {
        Assert.False(EntityLifecycleArgs.TryParseId(text, out _, out _));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("a:b")]
    [InlineData("12:0")]
    [InlineData("-1:1")]
    public void ParseEntities_unparseable_id_is_a_per_entry_error(string id)
    {
        var parsed = EntityLifecycleArgs.ParseEntities(Json("{\"entities\":[\"" + id + "\"]}"));
        Assert.Single(parsed);
        Assert.False(parsed[0].IsValid);
        Assert.NotNull(parsed[0].Error);
        Assert.Equal(id, parsed[0].Text);
        Assert.Contains(id, parsed[0].Error);
    }

    [Fact]
    public void ParseEntities_parses_a_valid_id()
    {
        var parsed = EntityLifecycleArgs.ParseEntities(Json("{\"entities\":[\"12:3\"]}"));
        Assert.Single(parsed);
        Assert.True(parsed[0].IsValid);
        Assert.Equal(12, parsed[0].Index);
        Assert.Equal(3, parsed[0].Version);
        Assert.Null(parsed[0].Error);
    }

    [Fact]
    public void ParseEntities_keeps_a_bad_id_as_a_per_entry_error_next_to_a_valid_one()
    {
        var parsed = EntityLifecycleArgs.ParseEntities(Json("{\"entities\":[\"12:3\",\"12\"]}"));
        Assert.Equal(2, parsed.Length);
        Assert.True(parsed[0].IsValid);
        Assert.False(parsed[1].IsValid);
        Assert.NotNull(parsed[1].Error);
    }

    [Fact]
    public void ParseEntities_allows_the_cap()
    {
        var parsed = EntityLifecycleArgs.ParseEntities(Json(EntitiesJson(EntityLifecycleArgs.MaxEntities)));
        Assert.Equal(EntityLifecycleArgs.MaxEntities, parsed.Length);
        Assert.True(parsed[0].IsValid);
        Assert.True(parsed[EntityLifecycleArgs.MaxEntities - 1].IsValid);
    }

    [Fact]
    public void ParseEntities_refuses_more_than_the_cap()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EntityLifecycleArgs.ParseEntities(Json(EntitiesJson(EntityLifecycleArgs.MaxEntities + 1))));
        Assert.Contains(EntityLifecycleArgs.MaxEntities.ToString(), ex.Message);
        Assert.Contains("65", ex.Message);
    }

    [Fact]
    public void ParseEntities_requires_the_entities_array()
    {
        var ex = Assert.Throws<ArgumentException>(() => EntityLifecycleArgs.ParseEntities(Json("{\"action\":\"exists\"}")));
        Assert.Contains("args.entities", ex.Message);
        Assert.Throws<ArgumentException>(() => EntityLifecycleArgs.ParseEntities(default));
    }

    [Fact]
    public void AfterSweep_phase_buffs_never_tags_and_keeps_stale()
    {
        var incomplete = EntityLifecycleArgs.AfterSweep(
            sweepComplete: false,
            stale: 1,
            reason: "length 257 above cap");
        Assert.Equal("buffs", incomplete.Phase);
        Assert.False(incomplete.BuffSweepComplete);
        Assert.Equal(1, incomplete.Stale);
        Assert.Equal("length 257 above cap", incomplete.Reason);

        var complete = EntityLifecycleArgs.AfterSweep(true, stale: 2, reason: "ignored");
        Assert.Equal("buffs", complete.Phase);
        Assert.True(complete.BuffSweepComplete);
        Assert.Equal(2, complete.Stale);
        Assert.Null(complete.Reason);
    }

    [Fact]
    public void AfterFinalize_tagged_waiting_buffs_and_stuck()
    {
        var tagged = EntityLifecycleArgs.AfterFinalize(allGone: true, tagAddSucceeded: true);
        Assert.Equal("tagged", tagged.Phase);
        Assert.True(tagged.DestroyTagAdded);

        var taggedButMissed = EntityLifecycleArgs.AfterFinalize(allGone: true, tagAddSucceeded: false);
        Assert.Equal("tagged", taggedButMissed.Phase);
        Assert.False(taggedButMissed.DestroyTagAdded);

        var missingUnit = EntityLifecycleArgs.AfterFinalize(allGone: true, tagAddSucceeded: true, unitExists: false);
        Assert.Equal("tagged", missingUnit.Phase);
        Assert.False(missingUnit.DestroyTagAdded);

        var waiting = EntityLifecycleArgs.AfterFinalize(allGone: false, tagAddSucceeded: true);
        Assert.Equal("waiting-buffs", waiting.Phase);
        Assert.False(waiting.DestroyTagAdded);

        var waitingBuffs = EntityLifecycleArgs.AfterFinalize(
            new BuffSweep.FinalizeStep("waiting-buffs", BuffSweep.ActionNone),
            tagAddSucceeded: true);
        Assert.Equal("waiting-buffs", waitingBuffs.Phase);
        Assert.False(waitingBuffs.DestroyTagAdded);

        var buffsStep = EntityLifecycleArgs.AfterFinalize(
            new BuffSweep.FinalizeStep("buffs", BuffSweep.ActionDestroyBuffs),
            tagAddSucceeded: true);
        Assert.Equal("buffs", buffsStep.Phase);
        Assert.False(buffsStep.DestroyTagAdded);

        var taggedStep = EntityLifecycleArgs.AfterFinalize(
            new BuffSweep.FinalizeStep("tagged", BuffSweep.ActionDestroyUnit),
            tagAddSucceeded: true);
        Assert.Equal("tagged", taggedStep.Phase);
        Assert.True(taggedStep.DestroyTagAdded);

        var stuck = EntityLifecycleArgs.AfterFinalize(
            new BuffSweep.FinalizeStep("stuck", BuffSweep.ActionNone, BuffSweep.FormatStuckReason(4)),
            tagAddSucceeded: true);
        Assert.Equal("stuck", stuck.Phase);
        Assert.False(stuck.DestroyTagAdded);
    }

    [Fact]
    public void ParseBuffs_reads_ids_keyed_by_unit()
    {
        var parsed = EntityLifecycleArgs.ParseBuffs(Json(
            "{\"entities\":[\"12:3\"],\"buffs\":{\"12:3\":[\"10:1\",\"20:2\"]}}"));
        Assert.True(parsed.ContainsKey("12:3"));
        Assert.Equal(2, parsed["12:3"].Length);
        Assert.True(parsed["12:3"][0].IsValid);
        Assert.Equal(10, parsed["12:3"][0].Index);
        Assert.Equal(1, parsed["12:3"][0].Version);
        Assert.Equal(20, parsed["12:3"][1].Index);
    }

    [Fact]
    public void ParseBuffs_missing_is_empty()
    {
        var parsed = EntityLifecycleArgs.ParseBuffs(Json("{\"entities\":[\"12:3\"]}"));
        Assert.Empty(parsed);
    }

    [Fact]
    public void ParseBuffs_refuses_more_than_the_buff_cap()
    {
        var sb = new StringBuilder("{\"buffs\":{\"1:1\":[");
        int n = BuffSweep.MaxElements + 1;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("\"2:1\"");
        }
        sb.Append("]}}");
        var ex = Assert.Throws<ArgumentException>(() => EntityLifecycleArgs.ParseBuffs(Json(sb.ToString())));
        Assert.Contains(BuffSweep.MaxElements.ToString(), ex.Message);
        Assert.Contains("257", ex.Message);
    }

    private static string EntitiesJson(int count)
    {
        var sb = new StringBuilder("{\"entities\":[");
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("\"1:1\"");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
