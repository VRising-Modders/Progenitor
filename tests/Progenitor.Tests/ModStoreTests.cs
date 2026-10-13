using System;
using System.IO;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Pending-store contract: writes before the save folder is known stay in memory, never land
/// in %TEMP%, and merge with disk (dirty keys win) once a path is assigned.
/// Shares <c>PluginLogger</c> with EventBusTests because both assign the process-wide
/// <see cref="Plugin.Logger"/> shim.
/// </summary>
[Collection("PluginLogger")]
public class ModStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-modstore", Guid.NewGuid().ToString("N"));
    private readonly TestLogSource _log = new();

    public ModStoreTests()
    {
        Directory.CreateDirectory(_root);
        Plugin.Logger = _log;
        ModStore.ResetSaveSkippedWarning();
    }

    public void Dispose()
    {
        Plugin.Logger = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Pending_set_overlays_disk_and_keeps_other_keys()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"state":{"Loads":5},"other":1}""");

        var store = new ModStore("sample.mod", filePath: "");
        Assert.True(store.IsPending);
        store.Set("state", new LoadBook { Loads = 6 });

        store.Relocate(path);
        store.LoadFromDisk();

        Assert.False(store.IsPending);
        Assert.Equal(6, store.Get("state", new LoadBook()).Loads);
        Assert.Equal(1, store.Get("other", 0));
        Assert.True(store.IsDirty);
    }

    [Fact]
    public void Pending_remove_drops_a_key_that_only_existed_on_disk()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"keep":1,"drop":2}""");

        var store = new ModStore("sample.mod", filePath: "");
        Assert.False(store.Remove("drop"));

        store.Relocate(path);
        store.LoadFromDisk();

        Assert.Equal(1, store.Get("keep", 0));
        Assert.Equal(0, store.Get("drop", 0));
        Assert.DoesNotContain("drop", store.Keys);
    }

    [Fact]
    public void SaveToDisk_while_pending_is_a_no_op_and_does_not_write_temp()
    {
        var store = new ModStore("sample.mod", filePath: "");
        store.Set("state", new LoadBook { Loads = 1 });

        Assert.False(store.SaveToDisk());
        Assert.True(store.IsPending);
        Assert.Contains(_log.Warnings, w => w.Contains("%TEMP%", StringComparison.Ordinal));

        var unsaved = Path.Combine(Path.GetTempPath(), "progenitor-unsaved");
        var leaked = Path.Combine(unsaved, "sample.mod.json");
        Assert.False(File.Exists(leaked));
        Assert.Empty(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void LoadFromDisk_is_not_skipped_because_the_store_is_dirty()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"fromDisk":true,"n":1}""");

        var store = new ModStore("sample.mod", path);
        store.Set("n", 2);
        Assert.True(store.IsDirty);

        store.LoadFromDisk();

        Assert.True(store.Get("fromDisk", false));
        Assert.Equal(2, store.Get("n", 0));
    }

    [Fact]
    public void Relocate_moves_the_written_file_when_the_destination_does_not_exist()
    {
        var oldDir = Path.Combine(_root, "old");
        var newDir = Path.Combine(_root, "new");
        var oldPath = SavePaths.StoreFile(oldDir, "sample.mod");
        var newPath = SavePaths.StoreFile(newDir, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, """{"n":1}""");

        var store = new ModStore("sample.mod", oldPath);
        store.LoadFromDisk();
        store.Relocate(newPath);

        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(newPath));
        Assert.Equal(newPath, store.FilePath);
    }

    [Fact]
    public void Relocate_keeps_an_existing_destination_and_merges_dirty_keys_onto_it()
    {
        var oldDir = Path.Combine(_root, "old");
        var newDir = Path.Combine(_root, "new");
        var oldPath = SavePaths.StoreFile(oldDir, "sample.mod");
        var newPath = SavePaths.StoreFile(newDir, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.WriteAllText(oldPath, """{"fromOld":1,"n":1}""");
        File.WriteAllText(newPath, """{"fromNew":1,"n":0}""");

        var store = new ModStore("sample.mod", oldPath);
        store.LoadFromDisk();
        store.Set("n", 2);
        store.Relocate(newPath);
        store.LoadFromDisk();

        Assert.False(File.Exists(oldPath));
        var sidecars = Directory.GetFiles(Path.GetDirectoryName(oldPath)!, "sample.mod.merged-*.json");
        Assert.Single(sidecars);
        Assert.Equal("""{"fromOld":1,"n":1}""", File.ReadAllText(sidecars[0]));
        Assert.Equal("""{"fromNew":1,"n":0}""", File.ReadAllText(newPath));
        Assert.Equal(1, store.Get("fromNew", 0));
        Assert.Equal(2, store.Get("n", 0));
        Assert.Equal(1, store.Get("fromOld", 0));
        Assert.Contains(_log.Infos, w => w.Contains("old < dest < dirty", StringComparison.Ordinal)
            && w.Contains("fromOld", StringComparison.Ordinal)
            && w.Contains("renamed", StringComparison.Ordinal));
    }

    [Fact]
    public void Relocate_existing_dest_merges_old_then_dest_then_dirty()
    {
        var oldDir = Path.Combine(_root, "old");
        var newDir = Path.Combine(_root, "new");
        var oldPath = SavePaths.StoreFile(oldDir, "sample.mod");
        var newPath = SavePaths.StoreFile(newDir, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.WriteAllText(oldPath, """{"onlyOld":1,"both":10,"destAndDirty":1,"allThree":1}""");
        File.WriteAllText(newPath, """{"onlyDest":2,"both":20,"destAndDirty":2,"allThree":2}""");

        var store = new ModStore("sample.mod", oldPath);
        store.LoadFromDisk();
        store.Set("destAndDirty", 3);
        store.Set("allThree", 3);
        store.Set("onlyDirty", 4);
        store.Relocate(newPath);
        store.LoadFromDisk();

        Assert.Equal(1, store.Get("onlyOld", 0));
        Assert.Equal(2, store.Get("onlyDest", 0));
        Assert.Equal(20, store.Get("both", 0));
        Assert.Equal(3, store.Get("destAndDirty", 0));
        Assert.Equal(3, store.Get("allThree", 0));
        Assert.Equal(4, store.Get("onlyDirty", 0));

        // A second dest load must not drop old-only keys (they stay dirty until save).
        store.LoadFromDisk();
        Assert.Equal(1, store.Get("onlyOld", 0));
        Assert.Equal(20, store.Get("both", 0));
        Assert.Equal(3, store.Get("allThree", 0));

        Assert.True(store.SaveToDisk());
        Assert.False(File.Exists(oldPath));
        var written = File.ReadAllText(newPath);
        Assert.Contains("onlyOld", written, StringComparison.Ordinal);
        Assert.Contains("onlyDest", written, StringComparison.Ordinal);
        Assert.Contains("onlyDirty", written, StringComparison.Ordinal);
        Assert.Contains("20", written, StringComparison.Ordinal);
        Assert.DoesNotContain("\"both\": 10", written, StringComparison.Ordinal);
        var sidecars = Directory.GetFiles(Path.GetDirectoryName(oldPath)!, "sample.mod.merged-*.json");
        Assert.Single(sidecars);
        Assert.Contains(_log.Infos, w => w.Contains("kept from old: onlyOld", StringComparison.Ordinal)
            && w.Contains("dest overrode: both", StringComparison.Ordinal));
    }

    [Fact]
    public void After_merge_SaveToDisk_writes_the_combined_object()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"keep":1}""");

        var store = new ModStore("sample.mod", "");
        store.Set("extra", 2);
        store.Relocate(path);
        store.LoadFromDisk();
        Assert.True(store.SaveToDisk());

        var text = File.ReadAllText(path);
        Assert.Contains("keep", text, StringComparison.Ordinal);
        Assert.Contains("extra", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Relocate_keeps_the_old_path_when_move_fails_and_dest_is_missing()
    {
        var oldPath = SavePaths.StoreFile(Path.Combine(_root, "old"), "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, """{"keep":1,"n":2}""");

        var store = new ModStore("sample.mod", oldPath);
        store.LoadFromDisk();
        Assert.False(store.IsDirty);

        var blocker = Path.Combine(_root, "not-a-directory");
        File.WriteAllText(blocker, "x");
        var newPath = Path.Combine(blocker, "progenitor", "sample.mod.json");

        Assert.False(store.Relocate(newPath));
        Assert.Equal(oldPath, store.FilePath);
        Assert.True(File.Exists(oldPath));
        Assert.False(File.Exists(newPath));
        Assert.Equal(1, store.Get("keep", 0));
        Assert.Equal(2, store.Get("n", 0));
        Assert.Contains(_log.Warnings, w => w.Contains("keeping the old path", StringComparison.Ordinal));

        // A second failure on the same store must not log again.
        Assert.False(store.Relocate(newPath));
        Assert.Single(_log.Warnings, w => w.Contains("keeping the old path", StringComparison.Ordinal));

        // Save still lands on the populated file, not an empty dest we never created.
        store.Set("n", 3);
        Assert.True(store.SaveToDisk());
        Assert.False(File.Exists(newPath));
        var text = File.ReadAllText(oldPath);
        Assert.Contains("keep", text, StringComparison.Ordinal);
        Assert.Contains("3", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Relocate_pending_store_takes_the_new_path_even_when_dest_is_missing()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        var store = new ModStore("sample.mod", filePath: "");
        store.Set("n", 1);

        Assert.True(store.Relocate(path));
        Assert.Equal(path, store.FilePath);
        Assert.True(store.IsPending);

        store.LoadFromDisk();
        Assert.False(store.IsPending);
        Assert.Equal(1, store.Get("n", 0));
    }

    [Fact]
    public void LoadFromDisk_missing_file_keeps_already_loaded_non_dirty_keys()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"keep":1,"n":2}""");

        var store = new ModStore("sample.mod", path);
        store.LoadFromDisk();
        Assert.False(store.IsDirty);
        File.Delete(path);

        store.LoadFromDisk();

        Assert.Equal(1, store.Get("keep", 0));
        Assert.Equal(2, store.Get("n", 0));
        Assert.Contains("keep", store.Keys);
        Assert.False(store.IsDirty);
    }

    [Fact]
    public void LoadFromDisk_missing_file_keeps_non_dirty_keys_and_in_memory_dirty_overlay()
    {
        var path = SavePaths.StoreFile(_root, "sample.mod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"keep":1,"n":2}""");

        var store = new ModStore("sample.mod", path);
        store.LoadFromDisk();
        store.Set("n", 9);
        File.Delete(path);

        store.LoadFromDisk();

        Assert.Equal(1, store.Get("keep", 0));
        Assert.Equal(9, store.Get("n", 0));
        Assert.True(store.IsDirty);
    }

    private sealed class LoadBook
    {
        public int Loads { get; set; }
    }
}
