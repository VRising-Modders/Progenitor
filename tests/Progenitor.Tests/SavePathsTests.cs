using System;
using System.IO;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Save-folder layout: look for <c>Saves\v*\&lt;name&gt;</c>, never invent
/// <c>Saves\&lt;name&gt;</c> without a version folder, and move JSON when the resolved
/// directory changes. F2 finding 3.
/// </summary>
public class SavePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-savepaths", Guid.NewGuid().ToString("N"));

    public SavePathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void StoreFile_is_under_the_progenitor_subfolder()
    {
        var path = SavePaths.StoreFile(Path.Combine(_root, "world"), "mod.guid");
        Assert.Equal(Path.Combine(_root, "world", "progenitor", "mod.guid.json"), path);
    }

    [Fact]
    public void FindSaveFolder_returns_existing_versioned_save()
    {
        var expected = Path.Combine(_root, "Saves", "v4", "test1");
        Directory.CreateDirectory(expected);

        var found = SavePaths.FindSaveFolder(_root, "test1");

        Assert.Equal(expected, found);
    }

    [Fact]
    public void FindSaveFolder_prefers_the_highest_version_folder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Saves", "v3", "test1"));
        var v10 = Path.Combine(_root, "Saves", "v10", "test1");
        Directory.CreateDirectory(v10);
        Directory.CreateDirectory(Path.Combine(_root, "Saves", "v4", "test1"));

        var found = SavePaths.FindSaveFolder(_root, "test1");

        Assert.Equal(v10, found);
    }

    [Fact]
    public void FindSaveFolder_ignores_a_save_sitting_directly_under_Saves()
    {
        var unversioned = Path.Combine(_root, "Saves", "test1");
        Directory.CreateDirectory(unversioned);

        var found = SavePaths.FindSaveFolder(_root, "test1");

        Assert.Equal(Path.Combine(_root, "Saves", SavePaths.DefaultVersionFolder, "test1"), found);
        Assert.NotEqual(unversioned, found);
    }

    [Fact]
    public void FindSaveFolder_finds_cloud_saves_under_a_version_folder()
    {
        var expected = Path.Combine(_root, "CloudSaves", "user1", "v4", "test1");
        Directory.CreateDirectory(expected);

        var found = SavePaths.FindSaveFolder(_root, "test1");

        Assert.Equal(expected, found);
    }

    [Fact]
    public void FindSaveFolder_invents_under_newest_existing_version_not_Saves_name()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Saves", "v5"));

        var found = SavePaths.FindSaveFolder(_root, "brand-new");

        Assert.Equal(Path.Combine(_root, "Saves", "v5", "brand-new"), found);
    }

    [Fact]
    public void FindSaveFolder_invents_v4_when_no_version_folder_exists()
    {
        var found = SavePaths.FindSaveFolder(_root, "brand-new");

        Assert.Equal(Path.Combine(_root, "Saves", "v4", "brand-new"), found);
    }

    [Fact]
    public void CombineSaveFolder_returns_directory_when_it_already_ends_with_the_save_name()
    {
        var dir = Path.Combine(_root, "Saves", "v4", "test1");
        Assert.Equal(dir, SavePaths.CombineSaveFolder(dir, "test1"));
    }

    [Fact]
    public void CombineSaveFolder_appends_the_save_name_when_the_leaf_does_not_exist()
    {
        var dir = Path.Combine(_root, "Saves", "v4");
        Directory.CreateDirectory(dir);

        Assert.Equal(Path.Combine(dir, "test1"), SavePaths.CombineSaveFolder(dir, "test1"));
    }

    [Fact]
    public void CombineSaveFolder_returns_directory_when_it_already_holds_gzip_saves()
    {
        var dir = Path.Combine(_root, "Saves", "v4", "test1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AutoSave_0.save.gz"), "x");

        Assert.Equal(dir, SavePaths.CombineSaveFolder(dir, "other-name"));
    }

    [Fact]
    public void CombineSaveFolder_returns_directory_when_it_already_has_a_progenitor_folder()
    {
        var dir = Path.Combine(_root, "Saves", "v4", "test1");
        Directory.CreateDirectory(Path.Combine(dir, SavePaths.FolderName));

        Assert.Equal(dir, SavePaths.CombineSaveFolder(dir, "other-name"));
    }

    [Fact]
    public void CombineSaveFolder_null_directory_is_null()
    {
        Assert.Null(SavePaths.CombineSaveFolder(null, "test1"));
        Assert.Null(SavePaths.CombineSaveFolder("", "test1"));
    }

    [Fact]
    public void TryMoveStoreFile_moves_when_destination_is_missing()
    {
        var srcDir = Path.Combine(_root, "old", "progenitor");
        var dstDir = Path.Combine(_root, "new", "progenitor");
        Directory.CreateDirectory(srcDir);
        var src = Path.Combine(srcDir, "mod.json");
        var dst = Path.Combine(dstDir, "mod.json");
        File.WriteAllText(src, """{"a":1}""");

        Assert.True(SavePaths.TryMoveStoreFile(src, dst));
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(dst));
        Assert.Equal("""{"a":1}""", File.ReadAllText(dst));
    }

    [Fact]
    public void TryArchiveMergedStoreFile_renames_beside_the_original()
    {
        var dir = Path.Combine(_root, "progenitor");
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "sample.mod.json");
        File.WriteAllText(src, """{"fromOld":1}""");

        var sidecar = SavePaths.TryArchiveMergedStoreFile(src);

        Assert.False(string.IsNullOrEmpty(sidecar));
        Assert.False(File.Exists(src));
        Assert.True(File.Exists(sidecar));
        Assert.Equal("""{"fromOld":1}""", File.ReadAllText(sidecar!));
        Assert.True(SavePaths.IsMergedArchive(sidecar));
        Assert.StartsWith("sample.mod.merged-", Path.GetFileName(sidecar), StringComparison.Ordinal);
        Assert.EndsWith(".json", Path.GetFileName(sidecar), StringComparison.Ordinal);
    }

    [Fact]
    public void TryArchiveMergedStoreFile_returns_null_when_the_old_file_is_missing()
    {
        var src = Path.Combine(_root, "missing.json");
        Assert.Null(SavePaths.TryArchiveMergedStoreFile(src));
        Assert.Null(SavePaths.TryArchiveMergedStoreFile(null));
        Assert.Null(SavePaths.TryArchiveMergedStoreFile(""));
    }

    [Fact]
    public void IsMergedArchive_detects_sidecar_names_and_rejects_live_stores()
    {
        Assert.True(SavePaths.IsMergedArchive(Path.Combine(_root, "sample.mod.merged-20260902T211923000Z.json")));
        Assert.True(SavePaths.IsMergedArchive("sample.mod.merged-1.json"));
        Assert.False(SavePaths.IsMergedArchive(Path.Combine(_root, "sample.mod.json")));
        Assert.False(SavePaths.IsMergedArchive("merged-only.json"));
        Assert.False(SavePaths.IsMergedArchive(""));
        Assert.False(SavePaths.IsMergedArchive(null));
    }

    [Fact]
    public void MergedArchivePath_inserts_the_marker_before_the_extension()
    {
        var src = Path.Combine(_root, "progenitor", "sample.mod.json");
        var utc = new DateTime(2026, 9, 2, 21, 19, 23, 123, DateTimeKind.Utc);

        var path = SavePaths.MergedArchivePath(src, utc);

        Assert.Equal(Path.Combine(_root, "progenitor", "sample.mod.merged-20260902T211923123Z.json"), path);
        Assert.True(SavePaths.IsMergedArchive(path));
        Assert.Equal(
            Path.Combine(_root, "progenitor", "sample.mod.merged-20260902T211923123Z-2.json"),
            SavePaths.MergedArchivePath(src, utc, attempt: 2));
    }

    [Fact]
    public void TryMoveStoreFile_does_not_overwrite_an_existing_destination()
    {
        var src = Path.Combine(_root, "old.json");
        var dst = Path.Combine(_root, "new.json");
        File.WriteAllText(src, "old");
        File.WriteAllText(dst, "new");

        Assert.False(SavePaths.TryMoveStoreFile(src, dst));
        Assert.True(File.Exists(src));
        Assert.Equal("new", File.ReadAllText(dst));
    }

    [Fact]
    public void TryMoveStoreFile_is_a_no_op_when_the_old_path_is_empty()
    {
        var dst = Path.Combine(_root, "new.json");
        Assert.False(SavePaths.TryMoveStoreFile(null, dst));
        Assert.False(SavePaths.TryMoveStoreFile("", dst));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void TryMoveStoreFile_returns_false_when_the_destination_cannot_be_created()
    {
        var src = Path.Combine(_root, "old.json");
        File.WriteAllText(src, """{"a":1}""");
        // Parent of dest is a file, so CreateDirectory / File.Move throw. Same outcome as a
        // locked source or an unwritable dest dir, without depending on Windows share modes.
        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var dst = Path.Combine(blocker, "progenitor", "mod.json");

        Assert.False(SavePaths.TryMoveStoreFile(src, dst));
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
        Assert.Equal("""{"a":1}""", File.ReadAllText(src));
    }

    [Theory]
    [InlineData("v4", 4)]
    [InlineData("v10", 10)]
    [InlineData("V4", 4)]
    public void TryParseVersionFolder_accepts_v_plus_digits(string name, int version)
    {
        Assert.True(SavePaths.TryParseVersionFolder(name, out var parsed));
        Assert.Equal(version, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("test1")]
    [InlineData("v4beta")]
    [InlineData("version4")]
    public void TryParseVersionFolder_rejects_non_version_names(string? name)
    {
        Assert.False(SavePaths.TryParseVersionFolder(name, out _));
    }
}
