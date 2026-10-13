using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// HealLogic is the debug-bridge <c>heal</c> policy: user vs entity args, Health/Blood
/// offsets from the interop stubs, the blood-max fallback, the log line, and the JSON
/// result shape. HealOps applies it against a live character; these tests cover the
/// parts that do not need the game.
/// </summary>
public class HealLogicTests
{
    [Fact]
    public void ParseTarget_user_name()
    {
        var t = HealLogic.ParseTarget(Json("{\"user\":\"TestAdmin\"}"));
        Assert.Equal(HealLogic.TargetKind.UserName, t.Kind);
        Assert.Equal("TestAdmin", t.Value);
    }

    [Fact]
    public void ParseTarget_trims_the_user_name()
    {
        var t = HealLogic.ParseTarget(Json("{\"user\":\"  TestAdmin  \"}"));
        Assert.Equal("TestAdmin", t.Value);
    }

    [Fact]
    public void ParseTarget_entity_index_version()
    {
        var t = HealLogic.ParseTarget(Json("{\"entity\":\"45:1\"}"));
        Assert.Equal(HealLogic.TargetKind.Entity, t.Kind);
        Assert.Equal("45:1", t.Value);
    }

    [Fact]
    public void ParseTarget_entity_wins_when_both_are_set()
    {
        var t = HealLogic.ParseTarget(Json("{\"user\":\"TestAdmin\",\"entity\":\"45:1\"}"));
        Assert.Equal(HealLogic.TargetKind.Entity, t.Kind);
        Assert.Equal("45:1", t.Value);
    }

    [Fact]
    public void ParseTarget_refuses_neither()
    {
        var ex = Assert.Throws<ArgumentException>(() => HealLogic.ParseTarget(Json("{}")));
        Assert.Contains("args.user or args.entity is required", ex.Message);
        Assert.Throws<ArgumentException>(() => HealLogic.ParseTarget(default));
    }

    [Fact]
    public void ParseTarget_refuses_empty_strings()
    {
        var ex = Assert.Throws<ArgumentException>(() => HealLogic.ParseTarget(Json("{\"user\":\"  \",\"entity\":\"\"}")));
        Assert.Contains("args.user or args.entity is required", ex.Message);
    }

    [Theory]
    [InlineData("45")]
    [InlineData(":1")]
    [InlineData("45:")]
    [InlineData("ab:cd")]
    [InlineData("45:1:2")]
    public void ParseTarget_refuses_a_malformed_entity_id(string entity)
    {
        var ex = Assert.Throws<ArgumentException>(() => HealLogic.ParseTarget(Json($"{{\"entity\":\"{entity}\"}}")));
        Assert.Contains("index:version", ex.Message);
        Assert.Contains(entity, ex.Message);
    }

    [Theory]
    [InlineData("45:1", 45, 1)]
    [InlineData("0:1", 0, 1)]
    [InlineData("10001:3", 10001, 3)]
    public void TryParseEntityId_accepts_index_version(string text, int index, int version)
    {
        Assert.True(HealLogic.TryParseEntityId(text, out var i, out var v));
        Assert.Equal(index, i);
        Assert.Equal(version, v);
    }

    [Theory]
    [InlineData(125f, 125f)]
    [InlineData(80.5f, 80.5f)]
    [InlineData(0f, 100f)]
    [InlineData(-1f, 100f)]
    public void BloodTarget_uses_max_when_positive_else_one_hundred(float maxBlood, float expected)
    {
        Assert.Equal(expected, HealLogic.BloodTarget(maxBlood));
    }

    [Fact]
    public void DefaultBloodWhenMaxIsZero_is_one_hundred()
    {
        Assert.Equal(100f, HealLogic.DefaultBloodWhenMaxIsZero);
    }

    [Fact]
    public void Dead_refusal_names_the_component_and_the_in_game_path()
    {
        Assert.Contains("ProjectM.Dead", HealLogic.DeadComponentRefusal);
        Assert.Contains("Space", HealLogic.DeadComponentRefusal);
    }

    [Fact]
    public void Health_and_Blood_offsets_match_the_interop_stubs()
    {
        Assert.Equal(0, HealLogic.HealthMaxOffset);
        Assert.Equal(16, HealLogic.HealthValueOffset);
        Assert.Equal(24, HealLogic.HealthIsDeadOffset);
        Assert.Equal(25, HealLogic.MinHealthBytes);
        Assert.Equal(0, HealLogic.BloodMaxOffset);
        Assert.Equal(16, HealLogic.BloodValueOffset);
        Assert.Equal(20, HealLogic.MinBloodBytes);
    }

    [Fact]
    public void LogLine_is_the_grep_line_with_invariant_numbers()
    {
        Assert.Equal(
            "Dev: heal TestAdmin health 1->125 blood 12.5->100",
            HealLogic.LogLine("TestAdmin", 1f, 125f, 12.5f, 100f));
        Assert.Equal(
            "Dev: heal 45:1 health 0->0 blood 0->100",
            HealLogic.LogLine("45:1", 0f, 0f, 0f, 100f));
    }

    [Fact]
    public void Result_shape_has_before_after_max_and_isDead()
    {
        var json = JsonSerializer.Serialize(HealLogic.Result(
            entity: "45:1",
            name: "TestAdmin",
            healthBefore: 1f,
            healthAfter: 125f,
            maxHealth: 125f,
            bloodBefore: 12.5f,
            bloodAfter: 100f,
            maxBlood: 100f,
            isDeadBefore: true,
            isDeadAfter: false,
            hasBlood: true));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("45:1", root.GetProperty("entity").GetString());
        Assert.Equal("TestAdmin", root.GetProperty("name").GetString());
        Assert.Equal(1f, root.GetProperty("health").GetProperty("before").GetSingle());
        Assert.Equal(125f, root.GetProperty("health").GetProperty("after").GetSingle());
        Assert.Equal(125f, root.GetProperty("health").GetProperty("max").GetSingle());
        Assert.Equal(12.5f, root.GetProperty("blood").GetProperty("before").GetSingle());
        Assert.Equal(100f, root.GetProperty("blood").GetProperty("after").GetSingle());
        Assert.Equal(100f, root.GetProperty("blood").GetProperty("max").GetSingle());
        Assert.True(root.GetProperty("blood").GetProperty("present").GetBoolean());
        Assert.True(root.GetProperty("isDead").GetProperty("before").GetBoolean());
        Assert.False(root.GetProperty("isDead").GetProperty("after").GetBoolean());
    }

    [Fact]
    public void Result_marks_blood_absent_when_the_character_had_none()
    {
        var json = JsonSerializer.Serialize(HealLogic.Result(
            "88:1", "Wolf", 10f, 50f, 50f, 0f, 0f, 0f, false, false, hasBlood: false));
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("blood").GetProperty("present").GetBoolean());
        Assert.Equal(0f, doc.RootElement.GetProperty("blood").GetProperty("after").GetSingle());
    }

    [Fact]
    public void Result_reports_the_downed_buff_heal_removed()
    {
        var json = JsonSerializer.Serialize(HealLogic.Result(
            "45:1", "TestAdmin", 0f, 125f, 125f, 50f, 100f, 100f, false, false, hasBlood: true,
            downed: new HealLogic.Downed(true, true, "88:1", -1234)));
        using var doc = JsonDocument.Parse(json);
        var downed = doc.RootElement.GetProperty("downed");
        Assert.True(downed.GetProperty("found").GetBoolean());
        Assert.True(downed.GetProperty("removed").GetBoolean());
        Assert.Equal("88:1", downed.GetProperty("buff").GetString());
        Assert.Equal(-1234, downed.GetProperty("guid").GetInt32());
        Assert.False(downed.GetProperty("goneNow").GetBoolean());
        Assert.EndsWith("downed buff 88:1 (guid -1234) removed",
            HealLogic.LogLine("TestAdmin", 0f, 125f, 50f, 100f, new HealLogic.Downed(true, true, "88:1", -1234)));
    }

    [Fact]
    public void Result_without_a_downed_buff_says_not_found()
    {
        var json = JsonSerializer.Serialize(HealLogic.Result(
            "45:1", "TestAdmin", 1f, 125f, 125f, 12.5f, 100f, 100f, false, false, hasBlood: true));
        using var doc = JsonDocument.Parse(json);
        var downed = doc.RootElement.GetProperty("downed");
        Assert.False(downed.GetProperty("found").GetBoolean());
        Assert.Equal(JsonValueKind.Null, downed.GetProperty("guid").ValueKind);
        Assert.Equal("Dev: heal TestAdmin health 1->125 blood 12.5->100",
            HealLogic.LogLine("TestAdmin", 1f, 125f, 12.5f, 100f, HealLogic.Downed.None));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
