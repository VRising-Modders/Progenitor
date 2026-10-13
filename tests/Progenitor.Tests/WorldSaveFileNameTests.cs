using System;
using System.Text;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Progenitor.Hosted;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Filename, live-server fence, and overlay-destroy rules for the debug-bridge
/// <c>world_save</c> op. Compiles <c>WorldSaveOps.cs</c> under <c>PROGENITOR_TESTS</c>
/// so these cases cannot drift from production.
/// </summary>
public class WorldSaveFileNameTests
{
    [Fact]
    public void Empty_uses_the_current_world_save_name()
    {
        Assert.Equal("AutoSave_3.save", WorldSaveOps.NormalizeSaveFileName(null, "AutoSave_3.save"));
        Assert.Equal("AutoSave_3.save", WorldSaveOps.NormalizeSaveFileName("", "AutoSave_3.save.gz"));
        Assert.Equal("AutoSave_3.save", WorldSaveOps.NormalizeSaveFileName("   ", "AutoSave_3.save"));
    }

    [Fact]
    public void Empty_without_a_current_name_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName(null));
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName(""));
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName("   ", ""));
    }

    [Theory]
    [InlineData("Manual.save", "Manual.save")]
    [InlineData("WorldSave.save", "WorldSave.save")]
    [InlineData("  AutoSave_9.save  ", "AutoSave_9.save")]
    public void Name_already_ending_in_save_is_kept(string input, string expected)
    {
        Assert.Equal(expected, WorldSaveOps.NormalizeSaveFileName(input));
    }

    [Theory]
    [InlineData("WorldSave", "WorldSave.save")]
    [InlineData("named", "named.save")]
    public void Missing_save_suffix_is_appended(string input, string expected)
    {
        Assert.Equal(expected, WorldSaveOps.NormalizeSaveFileName(input));
    }

    [Theory]
    [InlineData("WorldSave.save.gz", "WorldSave.save")]
    [InlineData("AutoSave_3.save.gz", "AutoSave_3.save")]
    public void Trailing_gz_is_stripped_because_TriggerSave_appends_it(string input, string expected)
    {
        Assert.Equal(expected, WorldSaveOps.NormalizeSaveFileName(input));
    }

    [Theory]
    [InlineData(@"C:\saves\WorldSave.save", "WorldSave.save")]
    [InlineData("folder/named.save", "named.save")]
    [InlineData(@"folder\named.save.gz", "named.save")]
    public void Directory_is_stripped_to_a_leaf(string input, string expected)
    {
        Assert.Equal(expected, WorldSaveOps.NormalizeSaveFileName(input));
    }

    [Fact]
    public void Path_that_is_only_dots_is_refused()
    {
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName(".."));
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName("."));
    }

    [Fact]
    public void Invalid_filename_characters_are_refused()
    {
        // `:` is a drive separator on Windows (GetFileName("a:b.save") => "b.save"), so use
        // characters that survive the leaf split: * and ?.
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName("a*b.save"));
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName("a?.save"));
    }

    [Fact]
    public void Name_over_FixedString128_capacity_is_refused()
    {
        var tooLong = new string('a', WorldSaveOps.MaxFileNameUtf8Bytes) + ".save";
        Assert.True(Encoding.UTF8.GetByteCount(tooLong) > WorldSaveOps.MaxFileNameUtf8Bytes);
        Assert.Throws<ArgumentException>(() => WorldSaveOps.NormalizeSaveFileName(tooLong));
    }

    [Fact]
    public void Disk_name_appends_gz_only_when_compressing()
    {
        Assert.Equal("WorldSave.save.gz", WorldSaveOps.DiskFileName("WorldSave.save", compress: true));
        Assert.Equal("WorldSave.save", WorldSaveOps.DiskFileName("WorldSave.save", compress: false));
        Assert.Equal("WorldSave.save.gz", WorldSaveOps.DiskFileName("WorldSave.save.gz", compress: true));
    }

    [Theory]
    [InlineData(null, "ManualSave")]
    [InlineData("", "ManualSave")]
    [InlineData("  ", "ManualSave")]
    [InlineData("ManualSave", "ManualSave")]
    [InlineData("manual", "ManualSave")]
    [InlineData("1", "ManualSave")]
    [InlineData("AutoSave", "AutoSave")]
    [InlineData("auto", "AutoSave")]
    [InlineData("2", "AutoSave")]
    [InlineData("QuickSave", "QuickSave")]
    [InlineData("quick", "QuickSave")]
    [InlineData("0", "QuickSave")]
    public void Reason_parses_known_aliases(string? input, string expected)
    {
        Assert.True(WorldSaveOps.TryParseReason(input, out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("3")]
    [InlineData("Manual Save")]
    public void Unknown_reason_is_refused(string input)
    {
        Assert.False(WorldSaveOps.TryParseReason(input, out _));
    }

    [Fact]
    public void Combined_path_leaf_is_the_current_save_file()
    {
        Assert.Equal(
            "AutoSave_127.save.gz",
            WorldSaveOps.LeafSaveNameFromCombinedPath(
                @"C:\GameModding\_testserver\save-data\Saves\v4\test1\AutoSave_127.save.gz"));
        Assert.Equal("Manual.save", WorldSaveOps.LeafSaveNameFromCombinedPath("Manual.save"));
        Assert.Null(WorldSaveOps.LeafSaveNameFromCombinedPath(@"C:\saves\v4\test1"));
        Assert.Null(WorldSaveOps.LeafSaveNameFromCombinedPath(""));
    }

    [Fact]
    public void Newest_AutoSave_leaf_is_the_highest_index()
    {
        Assert.Equal(
            "AutoSave_12.save.gz",
            WorldSaveOps.NewestAutoSaveLeaf(new[]
            {
                "QuickSave.save.gz",
                "AutoSave_3.save.gz",
                "AutoSave_12.save.gz",
                "AutoSave_7.save",
                "not-a-save.txt",
            }));
        Assert.Null(WorldSaveOps.NewestAutoSaveLeaf(Array.Empty<string>()));
        Assert.True(WorldSaveOps.TryParseAutoSaveIndex("AutoSave_9.save.gz", out var n));
        Assert.Equal(9, n);
        Assert.False(WorldSaveOps.TryParseAutoSaveIndex("WorldSave.save", out _));
    }

    [Fact]
    public void Live_dedicated_path_is_refused()
    {
        var liveExe = HostedPathRules.ForbiddenLiveServerRoot + @"\VRisingServer.exe";
        var liveSave = HostedPathRules.ForbiddenLiveServerRoot + @"\save-data\Saves\v4\world";
        Assert.False(WorldSaveOps.TryAllowWorldSave(liveSave, liveExe, null, out var reason));
        Assert.Contains(HostedPathRules.ForbiddenLiveServerRoot, reason);

        var cmd = @"VRisingServer.exe -persistentDataPath " + HostedPathRules.ForbiddenLiveServerRoot + @"\save-data";
        Assert.False(WorldSaveOps.TryAllowWorldSave(
            @"D:\Servers\VRising\save-data\Saves\v4\world1",
            @"D:\Servers\VRising\VRisingServer.exe",
            cmd,
            out reason));
        Assert.Contains(HostedPathRules.ForbiddenLiveServerRoot, reason);
    }

    [Fact]
    public void Test_server_path_is_allowed_for_world_save()
    {
        Assert.True(WorldSaveOps.TryAllowWorldSave(
            @"C:\GameModding\_testserver\server\save-data\Saves\v4\test1",
            @"C:\GameModding\_testserver\server\VRisingServer.exe",
            null,
            out var reason), reason);
        Assert.Equal("", reason);
    }

    [Fact]
    public void TypeSize_zero_does_not_overlay_SaveFileName()
    {
        Assert.False(WorldSaveOps.MayFieldPokeSaveEvent(0));
        Assert.False(WorldSaveOps.MayFieldPokeSaveEvent(WorldSaveOps.CompressSaveFilesOffset));
        Assert.True(WorldSaveOps.MayFieldPokeSaveEvent(WorldSaveOps.CompressSaveFilesOffset + 1));
        Assert.False(WorldSaveOps.MayMemcpySaveFileName(0));
        Assert.True(WorldSaveOps.MayMemcpySaveFileName(WorldSaveOps.SaveFileNameBytes + WorldSaveOps.SaveFileNameOffset));
        Assert.False(WorldSaveOps.UseOverlayWhenTypedWriteFails);
        Assert.Throws<InvalidOperationException>(
            () => RawWriteGuard.EnsureOverlayMemcpy(0, WorldSaveOps.SaveFileNameBytes, "ProjectM.SavePersistentDataEvent"));
    }

    [Fact]
    public void Failed_typed_write_must_destroy_the_created_entity()
    {
        Assert.True(WorldSaveOps.ShouldDestroyCreatedSaveEvent(created: true, writeOk: false));
        Assert.False(WorldSaveOps.ShouldDestroyCreatedSaveEvent(created: true, writeOk: true));
        Assert.False(WorldSaveOps.ShouldDestroyCreatedSaveEvent(created: false, writeOk: false));
    }
}

/// <summary>
/// The Core trigger's argument (Back up now, the scheduled backup, the graceful stop). Seen live on 2026-10-07: a dedicated
/// server's <c>SaveToLoad</c> is <c>AutoSave_N.save.gz</c>, and the old rule wrote <c>AutoSave_N.save.gz.save.gz</c>.
/// </summary>
public class WorldSaveTriggerFileNameTests
{
    [Theory]
    [InlineData("AutoSave_8238.save.gz", "AutoSave_8238.save")]
    [InlineData("AutoSave_8238.save", "AutoSave_8238.save")]
    [InlineData("AutoSave_8238", "AutoSave_8238.save")]
    [InlineData("  AutoSave_7.SAVE.GZ ", "AutoSave_7.SAVE")]
    [InlineData(@"C:\x\Saves\v4\w\AutoSave_7.save.gz", "AutoSave_7.save")]
    public void The_loaded_file_name_becomes_one_dot_save_name(string saveToLoad, string expected)
    {
        Assert.Equal(expected, Progenitor.Persistence.WorldSaveTrigger.TriggerFileName(saveToLoad));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_name_is_empty(string? saveToLoad)
    {
        Assert.Equal("", Progenitor.Persistence.WorldSaveTrigger.TriggerFileName(saveToLoad));
    }

    [Fact]
    public void Core_and_the_bridge_agree()
    {
        foreach (var n in new[] { "AutoSave_1.save.gz", "AutoSave_1.save", "Named" })
            Assert.Equal(WorldSaveOps.NormalizeSaveFileName(n), Progenitor.Persistence.WorldSaveTrigger.TriggerFileName(n));
    }

    private static (string, DateTime)[] Files(params (string Name, int Minute)[] f) =>
        Array.ConvertAll(f, x => (x.Name, new DateTime(2026, 10, 7, 12, x.Minute, 0, DateTimeKind.Utc)));

    [Fact]
    public void SaveToLoad_wins_over_files()
    {
        var files = Files(("AutoSave_9.save.gz", 30));
        Assert.Equal("AutoSave_3.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName("AutoSave_3.save.gz", files));
    }

    [Fact]
    public void Empty_SaveToLoad_picks_newest_by_write_time_not_highest_number()
    {
        var files = Files(("AutoSave_9.save.gz", 10), ("AutoSave_2.save.gz", 40), ("AutoSave_5.save.gz", 20));
        Assert.Equal("AutoSave_2.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName("", files));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_SaveToLoad_and_no_files_gives_the_first_autosave_name(string? saveToLoad)
    {
        Assert.Equal("AutoSave_0.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName(saveToLoad, Files()));
        Assert.Equal("AutoSave_0.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName(saveToLoad, null));
    }

    [Fact]
    public void Non_autosave_files_are_ignored()
    {
        var files = Files(("SessionId.json", 50), ("StartDate.json", 51), ("MyNamed.save", 52), ("AutoSave_1.save", 5));
        Assert.Equal("AutoSave_1.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName(null, files));
        Assert.Equal("AutoSave_0.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName(null,
            Files(("SessionId.json", 50), ("MyNamed.save", 52), ("AutoSave_1.save.bak", 53))));
    }

    [Fact]
    public void A_compressed_pick_loses_its_gz()
    {
        Assert.Equal("AutoSave_5.save", Progenitor.Persistence.WorldSaveTrigger.ChooseSaveName(null, Files(("AutoSave_5.save.gz", 10))));
    }
}
