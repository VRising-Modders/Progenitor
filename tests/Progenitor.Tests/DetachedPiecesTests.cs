using System;
using System.Linq;
using Progenitor.Debugging;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>.debug build detached [remove [all|&lt;id&gt;]]</c>: the parse, which pieces count as detached,
/// which a remove takes, and the reply lines. Game-free; the world reads and the destroy are
/// <c>DetachedPiecesRuntime</c>.
/// </summary>
public sealed class DetachedPiecesTests
{
    private const byte Attach = 1;   // CastleAreaRequirementType.AttachToOwnedArea
    private const byte Ignore = 0;   // CastleAreaRequirementType.IgnoreCastleAreas

    [Theory]
    [InlineData("detached", "BuildDetached", "", -1)]
    [InlineData(" Detached ", "BuildDetached", "", -1)]
    [InlineData("detached remove", "BuildDetachedRemove", "", -1)]
    [InlineData("detached remove 12", "BuildDetachedRemove", "", 12)]
    [InlineData("detached  remove   ALL", "BuildDetachedRemoveAll", "", -1)]
    [InlineData("detached remove all 17", "BuildDetachedRemoveAll", "", 17)]
    [InlineData("detached remove 321132:132", "BuildDetachedRemove", "321132:132", -1)]
    public void Detached_forms_parse(string tail, string action, string piece, int confirm)
    {
        Assert.True(DebugCommandParse.TryParse("build", tail, out var request, out var error));
        Assert.Equal("", error);
        Assert.Equal(Enum.Parse<DebugAction>(action), request.Action);
        Assert.Equal(piece, request.Piece);
        Assert.Equal(confirm, request.Confirm);
        Assert.True(request.IsBuild);
        Assert.True(request.IsDetached);
    }

    [Theory]
    [InlineData("detached remove Alaric")]
    [InlineData("detached remove 0")]
    [InlineData("detached remove all 0")]
    [InlineData("detached remove all x")]
    [InlineData("detached remove all 3 4")]
    [InlineData("detached remove -1:2")]
    [InlineData("detached remove 1:2 3:4")]
    [InlineData("detached list")]
    public void Bad_detached_forms_are_refused(string tail)
    {
        Assert.False(DebugCommandParse.TryParse("build", tail, out _, out var error));
        Assert.Equal(DebugCommandParse.BuildUsageReply, error);
    }

    [Fact]
    public void Only_an_owned_piece_that_should_join_a_castle_and_did_not_is_detached()
    {
        Assert.True(DetachedPieces.IsDetached(Row("1:1", Attach, heart: false, owner: true)));
        Assert.False(DetachedPieces.IsDetached(Row("1:1", Attach, heart: true, owner: true)));   // joined its castle
        Assert.False(DetachedPieces.IsDetached(Row("1:1", Attach, heart: false, owner: false)));  // world piece, no owner
        Assert.False(DetachedPieces.IsDetached(Row("1:1", Ignore, heart: false, owner: true)));   // may stand outside castles
        Assert.Equal(1, Layout.CastleAreaRequirementTypeAttachToOwnedArea);
    }

    [Fact]
    public void A_remove_takes_the_callers_own_by_default_every_one_with_all_or_one_by_id()
    {
        var rows = new[]
        {
            Row("10:1", Attach, false, true, "7:1"),
            Row("11:1", Attach, false, true, "8:1"),
            Row("12:1", Attach, true, true, "7:1"),   // attached: never taken
        };
        Assert.Equal(new[] { "10:1" }, DetachedPieces.ToRemove(rows, "7:1", all: false).Select(r => r.Id));
        Assert.Equal(new[] { "10:1", "11:1" }, DetachedPieces.ToRemove(rows, "7:1", all: true).Select(r => r.Id));
        Assert.Equal(new[] { "11:1" }, DetachedPieces.ToRemove(rows, "7:1", all: false, piece: "11:1").Select(r => r.Id));
        Assert.Empty(DetachedPieces.ToRemove(rows, "7:1", all: false, piece: "12:1"));
    }

    [Fact]
    public void The_list_names_owners_says_the_cause_is_probable_and_caps_the_rows()
    {
        Assert.Equal(new[] { "No detached pieces: every castle piece on this server belongs to a castle." },
            DetachedPieces.ListLines(Array.Empty<DetachedPieces.Row>(), "7:1"));

        var rows = Enumerable.Range(0, DetachedPieces.ListLimit + 3)
            .Select(i => Row(i + ":1", Attach, false, true, i == 0 ? "8:1" : "7:1", i == 0 ? "Other" : "Admin", items: i == 1))
            .ToList();
        var lines = DetachedPieces.ListLines(rows, "7:1");
        Assert.StartsWith((DetachedPieces.ListLimit + 3) + " detached piece(s) belong to no castle (" + (DetachedPieces.ListLimit + 2) + " yours). They were probably built while", lines[0], StringComparison.Ordinal);
        Assert.Contains("could not damage such a piece in testing", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Nobody can damage", lines[0], StringComparison.Ordinal);
        Assert.Equal("By owner: Other 1, Admin " + (DetachedPieces.ListLimit + 2) + ".", lines[1]);
        Assert.Equal("TM_Castle_Wall at -1815, 5, -1707.5, owner Other (0:1)", lines[2]);
        Assert.Equal("TM_Castle_Wall at -1815, 5, -1707.5, owner Admin (1:1), may hold items", lines[3]);
        Assert.Contains("... and 3 more.", lines);
        Assert.Contains("1 of them may hold items and will not be removed; empty them first.", lines);
        Assert.Contains(lines, l => l.Contains("remove all <count>", StringComparison.Ordinal) && l.Contains("No refund", StringComparison.Ordinal));
    }

    [Fact]
    public void Removing_more_than_one_needs_the_previewed_count()
    {
        // remove <id>: never a count.
        Assert.Equal("", DetachedPieces.ConfirmRefusal(1, -1, all: false, piece: "5:5"));
        // own remove: one piece goes without a count; more need the exact count.
        Assert.Equal("", DetachedPieces.ConfirmRefusal(1, -1, all: false, piece: ""));
        Assert.Equal("", DetachedPieces.ConfirmRefusal(3, 3, all: false, piece: ""));
        string preview = DetachedPieces.ConfirmRefusal(3, -1, all: false, piece: "");
        Assert.Equal("This would remove 3 detached piece(s) of yours. There is no refund. To confirm, run .debug build detached remove 3.", preview);
        Assert.StartsWith("The count 2 does not match: this would remove 3", DetachedPieces.ConfirmRefusal(3, 2, all: false, piece: ""), StringComparison.Ordinal);
        Assert.StartsWith("The count 2 does not match", DetachedPieces.ConfirmRefusal(1, 2, all: false, piece: ""), StringComparison.Ordinal);
        // remove all: always the count, even for one.
        Assert.Equal("This would remove 1 detached piece(s) on the whole server. There is no refund. To confirm, run .debug build detached remove all 1.",
            DetachedPieces.ConfirmRefusal(1, -1, all: true, piece: ""));
        Assert.Equal("", DetachedPieces.ConfirmRefusal(17, 17, all: true, piece: ""));
        Assert.Contains("Nothing was removed.", DetachedPieces.ConfirmRefusal(17, 18, all: true, piece: ""), StringComparison.Ordinal);
        // nothing to remove: the remove reply says so instead.
        Assert.Equal("", DetachedPieces.ConfirmRefusal(0, -1, all: true, piece: ""));
    }

    [Fact]
    public void Remove_replies_say_what_happened()
    {
        var none = Array.Empty<DetachedPieces.Row>();
        Assert.Equal("No detached pieces to remove.", DetachedPieces.RemoveLines(none, none, 0, 0, all: false)[0]);
        Assert.StartsWith("None of the 4 detached piece(s) are yours", DetachedPieces.RemoveLines(none, none, 0, 4, all: false)[0], StringComparison.Ordinal);
        Assert.StartsWith("No detached piece 5:5:", DetachedPieces.RemoveLines(none, none, 0, 4, all: false, piece: "5:5")[0], StringComparison.Ordinal);

        var removed = new[] { Row("10:1", Attach, false, true, "7:1", "Admin") };
        var holding = new[] { Row("11:1", Attach, false, true, "7:1", "Admin", items: true) };
        var lines = DetachedPieces.RemoveLines(removed, holding, 1, 3, all: false);
        Assert.Equal("Removed 1 detached piece(s) of yours; 1 left because they may hold items; 1 could not be removed (see the server log). No refund.", lines[0]);
        Assert.Equal("TM_Castle_Wall at -1815, 5, -1707.5, owner Admin (10:1)", lines[1]);
        Assert.Equal("Has items; empty it first: TM_Castle_Wall at -1815, 5, -1707.5, owner Admin (11:1), may hold items", lines[2]);
    }

    [Fact]
    public void The_runtime_rechecks_logs_skips_items_and_destroys_through_the_games_path()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DetachedPiecesRuntime.cs");
        string run = SourcePin.MethodBody(runtime, "internal static void Run(World world, Entity caller, DebugAction action, string piece, int confirm, string by, Action<string> reply)");
        int confirm = run.IndexOf("DetachedPieces.ConfirmRefusal(", StringComparison.Ordinal);
        int recheck = run.IndexOf("IsStillDetached(em, e)", StringComparison.Ordinal);
        int logged = run.IndexOf("not removed: \" + why", StringComparison.Ordinal);
        int items = run.IndexOf("Progenitor.Items.Items.MayHoldItems(em, e)", StringComparison.Ordinal);
        int destroy = run.IndexOf("DestroyUtility.Destroy(em, e, DestroyDebugReason.DestroyTileModels);", StringComparison.Ordinal);
        Assert.True(confirm > 0 && recheck > confirm, "the count is confirmed before anything is touched");
        Assert.True(logged > recheck, "a failed re-check is logged");
        Assert.True(items > recheck && destroy > items, "a piece that may hold items is skipped before the destroy");
        Assert.DoesNotContain("AddComponent", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("AiRuntime.RW<", runtime, StringComparison.Ordinal);
    }

    private static DetachedPieces.Row Row(string id, byte requirement, bool heart, bool owner, string ownerKey = "7:1", string ownerName = "", bool items = false) =>
        new(id, "TM_Castle_Wall", -1815f, 5f, -1707.5f, requirement, heart, owner, ownerKey, ownerName, items);
}
