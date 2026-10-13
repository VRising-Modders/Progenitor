using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// TeleportArgs is the debug-bridge <c>teleport</c> policy: which subject and which of the
/// three destination forms a request names, the arrival tolerance on the deferred read-back,
/// the log line and the JSON result. TeleportOps applies it against a live character; these
/// tests cover the parts that do not need the game.
/// </summary>
public class DevTeleportArgsTests
{
    [Fact]
    public void Parse_user_to_a_point()
    {
        var r = TeleportArgs.Parse(Json("{\"user\":\"TestAdmin\",\"x\":10,\"y\":5,\"z\":-4}"));
        Assert.Equal(TeleportArgs.SubjectKind.UserName, r.SubjectKind);
        Assert.Equal("TestAdmin", r.Subject);
        Assert.Equal(TeleportArgs.DestinationKind.Point, r.DestinationKind);
        Assert.Equal(10f, r.X);
        Assert.Equal(5f, r.Y);
        Assert.Equal(-4f, r.Z);
    }

    [Fact]
    public void Parse_user_to_another_user()
    {
        var r = TeleportArgs.Parse(Json("{\"user\":\"TestAdmin\",\"to\":\"Ghost1\"}"));
        Assert.Equal(TeleportArgs.DestinationKind.UserName, r.DestinationKind);
        Assert.Equal("Ghost1", r.Destination);
    }

    [Fact]
    public void Parse_trims_names()
    {
        var r = TeleportArgs.Parse(Json("{\"user\":\"  TestAdmin \",\"to\":\"  Ghost1  \"}"));
        Assert.Equal("TestAdmin", r.Subject);
        Assert.Equal("Ghost1", r.Destination);
    }

    [Fact]
    public void Parse_entity_wins_over_user_for_the_subject()
    {
        var r = TeleportArgs.Parse(Json("{\"user\":\"TestAdmin\",\"entity\":\"45:1\",\"x\":0,\"y\":0,\"z\":0}"));
        Assert.Equal(TeleportArgs.SubjectKind.Entity, r.SubjectKind);
        Assert.Equal("45:1", r.Subject);
    }

    [Fact]
    public void Parse_toEntity_wins_over_to_which_wins_over_a_point()
    {
        var all = TeleportArgs.Parse(Json("{\"user\":\"A\",\"toEntity\":\"9:2\",\"to\":\"B\",\"x\":1,\"y\":2,\"z\":3}"));
        Assert.Equal(TeleportArgs.DestinationKind.Entity, all.DestinationKind);
        Assert.Equal("9:2", all.Destination);

        var toAndPoint = TeleportArgs.Parse(Json("{\"user\":\"A\",\"to\":\"B\",\"x\":1,\"y\":2,\"z\":3}"));
        Assert.Equal(TeleportArgs.DestinationKind.UserName, toAndPoint.DestinationKind);
    }

    [Fact]
    public void Parse_requires_a_subject()
    {
        var ex = Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"x\":1,\"y\":2,\"z\":3}")));
        Assert.Contains("args.user or args.entity is required", ex.Message);
        Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(default));
        Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"user\":\"  \",\"x\":1,\"y\":2,\"z\":3}")));
    }

    [Fact]
    public void Parse_requires_a_destination_and_never_defaults_to_the_origin()
    {
        var ex = Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"user\":\"TestAdmin\"}")));
        Assert.Contains("a destination is required", ex.Message);
        Assert.Contains("args.to", ex.Message);
        Assert.Contains("args.toEntity", ex.Message);
    }

    [Fact]
    public void Parse_refuses_a_partial_point()
    {
        var ex = Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"user\":\"TestAdmin\",\"x\":1,\"y\":2}")));
        Assert.Contains("args.x, args.y and args.z are all required", ex.Message);
        Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"user\":\"TestAdmin\",\"z\":2}")));
    }

    [Fact]
    public void Parse_refuses_a_non_numeric_coordinate()
    {
        var ex = Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"user\":\"A\",\"x\":\"east\",\"y\":0,\"z\":0}")));
        Assert.Contains("args.x must be a number", ex.Message);
    }

    [Fact]
    public void Parse_accepts_numeric_strings_for_coordinates()
    {
        var r = TeleportArgs.Parse(Json("{\"user\":\"A\",\"x\":\"-2125.5\",\"y\":\"5\",\"z\":\"-3167.6\"}"));
        Assert.Equal(-2125.5f, r.X, 3);
        Assert.Equal(5f, r.Y);
        Assert.Equal(-3167.6f, r.Z, 3);
    }

    [Fact]
    public void Parse_refuses_a_subject_or_destination_entity_that_is_not_index_version()
    {
        Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"entity\":\"45\",\"x\":0,\"y\":0,\"z\":0}")));
        var ex = Assert.Throws<ArgumentException>(() => TeleportArgs.Parse(Json("{\"user\":\"A\",\"toEntity\":\"45\"}")));
        Assert.Contains("args.toEntity must be 'index:version'", ex.Message);
    }

    [Fact]
    public void IsArrived_uses_the_epsilon_sphere()
    {
        Assert.True(TeleportArgs.IsArrived(10, 5, -4, 10, 5, -4));
        Assert.True(TeleportArgs.IsArrived(10, 5, -4, 10.5f, 5.2f, -4.1f));
        Assert.False(TeleportArgs.IsArrived(10, 5, -4, 40, 5, -4));
    }

    [Fact]
    public void IsArrived_is_false_without_a_read_back()
    {
        Assert.False(TeleportArgs.IsArrived(0, 0, 0, null, null, null));
        Assert.False(TeleportArgs.IsArrived(0, 0, 0, 0, null, 0));
    }

    [Fact]
    public void LogLine_names_the_game_path_that_moved_the_character()
    {
        Assert.Equal(
            "Dev: teleport TestAdmin (1, 2, 3) -> (10, 5, -4) via TeleportDebugEvent",
            TeleportArgs.LogLine("TestAdmin", 1, 2, 3, 10, 5, -4, "TeleportDebugEvent"));
    }

    [Fact]
    public void Result_reports_from_to_after_and_arrived()
    {
        var result = TeleportArgs.Result("45:1", "TestAdmin", "TeleportDebugEvent", 1, 2, 3, 10, 5, -4, 10, 5, -4, null, null);
        Assert.Equal("45:1", result["entity"]);
        Assert.Equal("TestAdmin", result["name"]);
        Assert.Equal("TeleportDebugEvent", result["method"]);
        Assert.Equal(true, result["arrived"]);
        Assert.Null(result["destination"]);
        Assert.NotNull(result["after"]);
    }

    [Fact]
    public void Result_reports_not_arrived_when_the_read_back_is_missing()
    {
        var result = TeleportArgs.Result("45:1", null, "Translation write", 1, 2, 3, 10, 5, -4, null, null, null, "9:2", "Ghost1");
        Assert.Equal(false, result["arrived"]);
        Assert.Null(result["after"]);
        Assert.NotNull(result["destination"]);
    }

    // ---- fallback write guard (R30 finding 12, R31 finding 16) ----------------------------------
    //
    // The fakes are the four numbers the live op reads off the component: the raw ECS TypeSize
    // (what RawWriteGuard is written against), the interop Marshal.SizeOf (what the FieldOffset
    // metadata describes), and the offset and size of the component's float3 field.

    [Fact]
    public void PositionWriteRefusal_allows_a_float3_at_offset_0_in_a_big_enough_component()
    {
        // Translation on this build: TypeSize 0, interop sizeof 12, float3 Value @0. TypeSize 0
        // is the documented "poke fields, do not memcpy" case, so the guard is not consulted and
        // the interop pair carries the write.
        Assert.Null(TeleportArgs.PositionWriteRefusal(
            "Translation", "Unity.Transforms.Translation", ecsTypeSize: 0, interopSize: 12, positionOffset: 0, positionFieldSize: 12));

        // A component with more after the float3 is still fine: the poke writes 12 of N.
        Assert.Null(TeleportArgs.PositionWriteRefusal(
            "PlayerLastValidPosition", "ProjectM.PlayerLastValidPosition", 0, 32, 0, 12));

        // And a build that does report a real TypeSize passes it through the guard.
        Assert.Null(TeleportArgs.PositionWriteRefusal(
            "LastTranslation", "ProjectM.LastTranslation", ecsTypeSize: 12, interopSize: 12, positionOffset: 0, positionFieldSize: 12));
    }

    [Fact]
    public void PositionWriteRefusal_refuses_a_component_that_became_a_tag()
    {
        // The finding's scenario: HasComponent still passes, the pointer is valid, and 12 bytes
        // would land on the next component in the chunk.
        var refusal = TeleportArgs.PositionWriteRefusal(
            "PlayerLastValidPosition", "ProjectM.PlayerLastValidPosition", ecsTypeSize: 0, interopSize: 0, positionOffset: -1, positionFieldSize: 0);

        Assert.NotNull(refusal);
        Assert.Contains("has no float3 field in this build", refusal);
    }

    [Fact]
    public void PositionWriteRefusal_refuses_a_float3_that_moved_off_offset_0()
    {
        var refusal = TeleportArgs.PositionWriteRefusal(
            "LastTranslation", "ProjectM.LastTranslation", ecsTypeSize: 0, interopSize: 16, positionOffset: 4, positionFieldSize: 12);

        Assert.NotNull(refusal);
        Assert.Contains("offset 4", refusal);
        Assert.Contains("not 0", refusal);
    }

    [Fact]
    public void PositionWriteRefusal_refuses_a_component_smaller_than_the_write()
    {
        // TypeSize still 0, so this is the named interop-extent check, not the guard.
        var refusal = TeleportArgs.PositionWriteRefusal(
            "LastTranslation", "ProjectM.LastTranslation", ecsTypeSize: 0, interopSize: 8, positionOffset: 0, positionFieldSize: 12);

        Assert.NotNull(refusal);
        Assert.Contains("8 interop bytes", refusal);
        Assert.Contains("12 bytes this write needs", refusal);
    }

    [Fact]
    public void PositionWriteRefusal_refuses_when_the_component_has_no_size_at_all()
    {
        var refusal = TeleportArgs.PositionWriteRefusal(
            "Translation", "Unity.Transforms.Translation", ecsTypeSize: 0, interopSize: 0, positionOffset: 0, positionFieldSize: 12);

        Assert.NotNull(refusal);
        Assert.Contains("0 interop bytes", refusal);
    }

    [Fact]
    public void PositionWriteRefusal_gets_the_real_TypeSize_not_the_interop_size()
    {
        // R31 finding 16: the guard used to be handed ComponentDump.SizeOf, which on this build
        // is the interop size, so "copySize > budget" could never be true. A build that reports a
        // TypeSize smaller than the write must now be refused by RawWriteGuard itself even though
        // the interop struct still measures a comfortable 32 bytes.
        var refusal = TeleportArgs.PositionWriteRefusal(
            "LastTranslation", "ProjectM.LastTranslation", ecsTypeSize: 8, interopSize: 32, positionOffset: 0, positionFieldSize: 12);

        Assert.NotNull(refusal);
        Assert.Contains("exceeds TypeSize 8", refusal);

        // A negative TypeSize is garbage and has no budget at all.
        var negative = TeleportArgs.PositionWriteRefusal(
            "LastTranslation", "ProjectM.LastTranslation", ecsTypeSize: -1, interopSize: 32, positionOffset: 0, positionFieldSize: 12);

        Assert.NotNull(negative);
        Assert.Contains("no memcpy budget", negative);
    }

    [Fact]
    public void PositionWriteRefusal_refuses_a_field_that_is_not_12_bytes()
    {
        var refusal = TeleportArgs.PositionWriteRefusal(
            "Translation", "Unity.Transforms.Translation", ecsTypeSize: 0, interopSize: 24, positionOffset: 0, positionFieldSize: 8);

        Assert.NotNull(refusal);
        Assert.Contains("8-byte field", refusal);
        Assert.Contains("12 bytes", refusal);
    }

    [Fact]
    public void PositionWriteRefusal_names_the_component_in_every_refusal()
    {
        foreach (var refusal in new[]
        {
            TeleportArgs.PositionWriteRefusal("LastTranslation", "ProjectM.LastTranslation", 0, 0, -1, 0),
            TeleportArgs.PositionWriteRefusal("LastTranslation", "ProjectM.LastTranslation", 0, 16, 4, 12),
            TeleportArgs.PositionWriteRefusal("LastTranslation", "ProjectM.LastTranslation", 0, 8, 0, 12),
            TeleportArgs.PositionWriteRefusal("LastTranslation", "ProjectM.LastTranslation", 8, 32, 0, 12),
        })
        {
            Assert.NotNull(refusal);
            Assert.StartsWith("LastTranslation:", refusal);
        }
    }

    [Fact]
    public void FallbackRefusalMessage_lists_every_reason_and_points_at_the_game_path()
    {
        var message = TeleportArgs.FallbackRefusalMessage("45:1", new[] { "Translation: a", "LastTranslation: b" });
        Assert.Contains("45:1", message);
        Assert.Contains("2 component(s)", message);
        Assert.Contains("Translation: a", message);
        Assert.Contains("LastTranslation: b", message);
        Assert.Contains("TeleportUtilityServer.Teleport", message);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
