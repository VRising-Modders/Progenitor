using System;
using System.IO;
using System.Linq;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// WORLDRESTORE3 (live re-test 2026-10-07, step 21): the game must never start with the save folder missing while restore
/// debris or an unreadable journal is there. The world in the one <c>.outgoing-*</c> folder is put back, or the start is
/// refused (Bootstrap exits with code 75). Also the hand-written journal of the docs (plain Windows paths) is readable, and
/// the command line's save folder is known even while it is missing. Game-free.
/// </summary>
public sealed class WorldRestoreGuardTests
{
    private static WorldRestoreApplyTests.Fixture Fix() => new();

    /// <summary>The state of docs/world-restore.md step 20: the world moved to .outgoing-test, the server stopped.</summary>
    private static string Strand(WorldRestoreApplyTests.Fixture f)
    {
        string outgoing = Path.Combine(f.BackupsRoot, ".outgoing-test");
        f.Fs.MoveDirectory(f.Save, outgoing);
        return outgoing;
    }

    /// <summary>The journal exactly as the docs show it, written by hand: single backslashes, no BOM.</summary>
    private static void HandJournal(WorldRestoreApplyTests.Fixture f, string outgoing) =>
        f.Fs.Put(f.Journal, "{\"schema\":1,\"requestId\":\"test\",\"phase\":\"swapping\",\"saveFolder\":\"" + f.Save + "\",\"backupsRoot\":\""
            + f.BackupsRoot + "\",\"outgoing\":\"" + outgoing + "\",\"incoming\":\"\",\"sourceName\":\"test\"}", DateTime.UtcNow);

    private static void Lock(WorldRestoreApplyTests.Fixture f, string folder) =>
        f.Fs.Deny = (op, path) => op == "movedir" && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_documented_hand_written_journal_is_read_and_the_world_is_renamed_back()
    {
        var f = Fix();
        var save = f.Fs.Snapshot(f.Save);
        string outgoing = Strand(f);
        HandJournal(f, outgoing);
        if (Path.DirectorySeparatorChar == '\\') Assert.Equal(f.Save, RestoreJournal.Parse(f.Fs.Bytes(f.Journal))!.SaveFolder);

        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.Empty(f.Stops);
        Assert.Contains("put back", r.Reason);
        // The hand-written journal has no start time: the recovery reports its own (here 0 ms), never a span from year 1.
        Assert.Equal(" in 0 ms", r.Elapsed());
        Assert.Equal("", new RestoreResult { FinishedUtc = DateTime.UtcNow }.Elapsed());
        Assert.Equal(save, f.Fs.Snapshot(f.Save));
        Assert.False(f.Fs.Has(f.Journal));
    }

    [Theory]
    [InlineData(@"D:\Games\Server\Saves\v4\restore1")]
    [InlineData(@"D:\new\test\Saves\v4\restore1")]
    [InlineData(@"D:\Games\_testserver\restore-save-data\Saves\v4\restore1")]
    public void Plain_windows_paths_in_a_hand_written_journal_are_read_literally(string path)
    {
        string text = "{\"requestId\":\"test\",\"phase\":\"swapping\",\"saveFolder\":\"" + path + "\",\"outgoing\":\"" + path + "x\"}";
        var j = RestoreJournal.Parse(System.Text.Encoding.UTF8.GetBytes(text))!;
        Assert.Equal(path, j.SaveFolder);
        Assert.Equal(path + "x", j.Outgoing);
        // Escaped as JSON says, the same.
        string escaped = text.Replace(@"\", @"\\");
        Assert.Equal(path, RestoreJournal.Parse(System.Text.Encoding.UTF8.GetBytes(escaped))!.SaveFolder);
    }

    [Fact]
    public void The_documented_journal_with_the_world_locked_in_outgoing_stops_the_server()
    {
        var f = Fix();
        string outgoing = Strand(f);
        HandJournal(f, outgoing);
        Lock(f, outgoing);
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.Single(f.Stops);
        Assert.True(r.Stranded);
        Assert.False(f.Fs.HasDir(f.Save), "no world takes the save folder's place");
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("deleted")]
    public void An_unreadable_or_deleted_journal_with_the_world_in_outgoing_puts_it_back_or_stops(string journal)
    {
        foreach (bool locked in new[] { false, true })
        {
            var f = Fix();
            var save = f.Fs.Snapshot(f.Save);
            string outgoing = Strand(f);
            if (journal == "garbage") f.Fs.Put(f.Journal, "{ this is not a journal", DateTime.UtcNow);
            if (locked) Lock(f, outgoing);
            var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env());
            if (locked)
            {
                Assert.Single(f.Stops);
                Assert.True(r!.Stranded);
                Assert.Contains("could not be renamed back", r.Reason);
                Assert.False(f.Fs.HasDir(f.Save));
            }
            else
            {
                Assert.Empty(f.Stops);
                Assert.Equal(save, f.Fs.Snapshot(f.Save));
                // The world is in place again, so an unreadable journal is set aside for the operator.
                Assert.False(f.Fs.Has(f.Journal));
            }
        }
    }

    [Fact]
    public void Two_outgoing_worlds_or_only_partial_copies_beside_a_missing_save_stop_the_server()
    {
        var f = Fix();
        string outgoing = Strand(f);
        f.Fs.Put(Path.Combine(f.BackupsRoot, ".outgoing-other", "AutoSave_1.save.gz"), FakeWorldFs.Gzip("x"), DateTime.UtcNow);
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!;
        Assert.Single(f.Stops);
        Assert.Contains("2 .outgoing folders", r.Reason);
        Assert.True(f.Fs.HasDir(outgoing));

        var g = Fix();
        g.Fs.DeleteDirectory(g.Save);
        g.Fs.Put(Path.Combine(g.BackupsRoot, "20261007-093000-before-restore.partial", "AutoSave_1.save.gz"), "x", DateTime.UtcNow);
        Assert.True(WorldRestoreMarker.ApplyAtStart(g.Fs, g.BepInEx, g.Env())!.Stranded);
        Assert.Single(g.Stops);
    }

    [Fact]
    public void A_missing_save_folder_without_debris_is_a_first_start_and_starts()
    {
        var f = Fix();
        f.Fs.DeleteDirectory(f.Save);
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env()));
        Assert.Empty(f.Stops);
    }

    [Fact]
    public void An_unreadable_journal_without_a_known_save_folder_stops_the_server_and_one_beside_a_present_world_is_set_aside()
    {
        var f = Fix();
        f.Fs.Put(f.Journal, "nope", DateTime.UtcNow);
        var env = f.Env();
        env.Args = new[] { "VRisingServer.exe" };
        var r = WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, env)!;
        Assert.Single(f.Stops);
        Assert.Contains("does not name its save folder", r.Reason);

        var g = Fix();
        g.Fs.Put(g.Journal, "nope", DateTime.UtcNow);
        Assert.Null(WorldRestoreMarker.ApplyAtStart(g.Fs, g.BepInEx, g.Env()));
        Assert.Empty(g.Stops);
        Assert.True(g.Fs.Has(g.Journal + ".unreadable"));
        Assert.False(g.Fs.Has(g.Journal));
    }

    [Fact]
    public void A_request_waits_while_the_journal_is_unreadable()
    {
        var f = Fix();
        WorldRestoreMarker.Write(f.Fs, f.BepInEx, f.Request());
        f.Fs.Put(f.Journal, "nope", DateTime.UtcNow);
        var save = f.Fs.Snapshot(f.Save);
        Assert.Null(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env()));
        Assert.Equal(save, f.Fs.Snapshot(f.Save));
        Assert.True(f.Fs.Has(WorldRestoreMarker.RequestPath(f.BepInEx)));
        // Set aside now: the next start runs it.
        Assert.True(WorldRestoreMarker.ApplyAtStart(f.Fs, f.BepInEx, f.Env())!.Ok);
    }

    [Fact]
    public void The_command_line_names_the_save_folder_even_while_it_is_missing()
    {
        var f = Fix();
        var args = new[] { "VRisingServer.exe", "-persistentDataPath", f.Persist, "-saveName", "world1" };
        Assert.Equal(f.Save, WorldRestorePaths.SaveFolderFromArgs(f.Fs, args));
        f.Fs.DeleteDirectory(f.Save);
        Assert.Equal(f.Save, WorldRestorePaths.SaveFolderFromArgs(f.Fs, args));
        Assert.Equal(Path.Combine(FakeWorldFs.Root, "none", "Saves", "v4", "w"),
            WorldRestorePaths.SaveFolderFromArgs(f.Fs, new[] { "x", "-persistentDataPath", Path.Combine(FakeWorldFs.Root, "none"), "-saveName", "w" }));
        Assert.Null(WorldRestorePaths.SaveFolderFromArgs(f.Fs, new[] { "x", "-saveName", "w" }));
    }
}
