using System;
using System.IO;
using Progenitor.Dev;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The server-level store (<c>ServerSave</c>, <c>ctx.ServerSave</c>): its folder is outside every
/// world folder, so the game's wipe (which archives the world folder) and a world restore (which
/// replaces it) leave its keys alone; it loads, writes atomically, merges a pending overlay, runs
/// <c>WhenReady</c>, migrates old ids, and drops callbacks on unload. Drives the game-free
/// <see cref="ServerStoreSet"/> over a temp persistent data root.
/// </summary>
[Collection("PluginLogger")]
public class ServerSaveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-serversave", Guid.NewGuid().ToString("N"));
    private readonly TestLogSource _log = new();

    public ServerSaveTests()
    {
        Directory.CreateDirectory(_root);
        Plugin.Logger = _log;
        MainThread.ResetForTests();
    }

    public void Dispose()
    {
        MainThread.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class Season
    {
        public int Number { get; set; }
        public string Champion { get; set; } = "";
    }

    [Fact]
    public void Store_file_is_under_progenitor_server_beside_Saves()
    {
        string file = ServerSavePaths.StoreFile(_root, "progenitor.sample");
        Assert.Equal(Path.Combine(_root, "progenitor-server", "progenitor.sample.json"), file);

        string world = Path.Combine(_root, "Saves", "v4", "world1");
        Assert.False(ServerSavePaths.IsInside(file, world));
        Assert.False(ServerSavePaths.IsInside(file, Path.Combine(_root, "Saves")));
        // ...and a world store is inside its world folder, which is what a wipe or restore moves.
        Assert.True(ServerSavePaths.IsInside(SavePaths.StoreFile(world, "progenitor.sample"), world));
    }

    [Theory]
    [InlineData(@"C:\data\Saves\v4\world1", @"C:\data")]
    [InlineData(@"C:\data\Saves\v4\world1\", @"C:\data")]
    [InlineData(@"C:\data\CloudSaves\12345\v4\world1", @"C:\data")]
    [InlineData(@"C:\data\Saves\world1", null)]
    [InlineData(@"C:\data\world1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Root_is_derived_from_the_two_world_folder_shapes_only(string? saveFolder, string? expected)
    {
        if (!OperatingSystem.IsWindows() && saveFolder != null && saveFolder.Contains('\\')) return;
        Assert.Equal(expected, ServerSavePaths.RootFromSaveFolder(saveFolder));
    }

    [Fact]
    public void Values_round_trip_to_disk_and_back_through_a_new_set()
    {
        var set = new ServerStoreSet();
        Assert.True(set.SetRoot(_root));
        var store = set.Store("progenitor.sample");
        store.Set("season", new Season { Number = 3, Champion = "Ash" });
        Assert.Equal(1, set.SaveAll());

        string file = ServerSavePaths.StoreFile(_root, "progenitor.sample");
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".tmp"));

        // A new Core generation (a hot swap, a restart) starts with an empty set.
        var next = new ServerStoreSet();
        next.SetRoot(_root);
        var season = next.Store("progenitor.sample").Get("season", new Season());
        Assert.Equal(3, season.Number);
        Assert.Equal("Ash", season.Champion);
    }

    [Fact]
    public void A_wipe_or_restore_of_the_world_folder_leaves_the_server_store_alone()
    {
        // World and server data written the way Core writes them.
        string world = Path.Combine(_root, "Saves", "v4", "world1");
        var worldStore = new ModStore("progenitor.sample", SavePaths.StoreFile(world, "progenitor.sample"));
        worldStore.LoadFromDisk();
        worldStore.Set("castleCount", 4);
        Assert.True(worldStore.SaveToDisk());

        var set = new ServerStoreSet();
        set.SetRoot(_root);
        set.Store("progenitor.sample").Set("seasonWins", 7);
        set.SaveAll();

        // The game's wipe: the world folder is archived as <save>_<ticks> and a fresh one starts.
        Directory.Move(world, world + "_638000000000000000");
        Directory.CreateDirectory(world);
        // A restore: the world folder is replaced by an earlier copy (here: an empty one).
        Directory.Delete(world, recursive: true);
        Directory.CreateDirectory(world);

        var fresh = new ModStore("progenitor.sample", SavePaths.StoreFile(world, "progenitor.sample"));
        fresh.LoadFromDisk();
        Assert.Equal(0, fresh.Get("castleCount", 0));

        var next = new ServerStoreSet();
        next.SetRoot(_root);
        Assert.Equal(7, next.Store("progenitor.sample").Get("seasonWins", 0));
    }

    [Fact]
    public void Only_changed_stores_are_written()
    {
        var set = new ServerStoreSet();
        set.SetRoot(_root);
        var a = set.Store("mod.a");
        set.Store("mod.b");
        a.Set("k", 1);

        Assert.Equal(1, set.SaveAll());
        Assert.False(File.Exists(ServerSavePaths.StoreFile(_root, "mod.b")));
        Assert.Equal(0, set.SaveAll());
    }

    [Fact]
    public void A_store_handed_out_before_the_root_is_pending_and_merges_with_disk()
    {
        string file = ServerSavePaths.StoreFile(_root, "progenitor.sample");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"bans":["a"],"other":1}""");

        var set = new ServerStoreSet();
        var store = set.Store("progenitor.sample");
        int seen = -1;
        store.WhenReady(s => seen = s.Get("other", 0));
        store.Set("bans", new[] { "a", "b" });
        Assert.Equal(0, set.SaveAll());      // no root: nothing is written
        Assert.Equal(-1, seen);

        Assert.True(set.SetRoot(_root));
        MainThread.Drain();

        Assert.Equal(1, seen);
        Assert.Equal(new[] { "a", "b" }, store.Get<string[]>("bans"));
        Assert.Equal(1, store.Get("other", 0));
        Assert.Equal(1, set.SaveAll());
    }

    [Fact]
    public void WhenReady_runs_in_line_when_the_root_is_known()
    {
        MainThread.Drain(); // claim this thread as the main thread, as OnLoad runs on it
        var set = new ServerStoreSet();
        set.SetRoot(_root);
        int calls = 0;
        set.Store("progenitor.sample").WhenReady(_ => calls++);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_same_id_returns_the_same_store_and_a_path_id_is_refused()
    {
        var set = new ServerStoreSet();
        set.SetRoot(_root);
        Assert.Same(set.Store("progenitor.sample"), set.Store("PROGENITOR.SAMPLE"));
        Assert.Throws<ArgumentException>(() => set.Store(@"..\escape"));
        Assert.Throws<ArgumentException>(() => set.Store("a/b"));
        Assert.Throws<ArgumentException>(() => set.Store(" "));
    }

    [Fact]
    public void A_second_different_root_is_refused_and_the_first_kept()
    {
        var set = new ServerStoreSet();
        Assert.True(set.SetRoot(_root));
        Assert.False(set.SetRoot(Path.Combine(_root, "elsewhere")));
        Assert.Equal(_root, set.Root);
        Assert.Contains(_log.Warnings, w => w.Contains("keeping the first", StringComparison.Ordinal));
    }

    [Fact]
    public void A_store_written_under_an_old_id_is_renamed_when_the_root_is_set()
    {
        string folder = ServerSavePaths.StoreFolder(_root);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "acme.progenitor.sample.json"), """{"wins":5}""");

        var set = new ServerStoreSet();
        set.SetRoot(_root);

        Assert.Equal(5, set.Store("progenitor.sample").Get("wins", 0));
        Assert.True(File.Exists(Path.Combine(folder, "acme.progenitor.sample.json.bak")));
    }

    [Fact]
    public void Unload_drops_only_the_owners_pending_callbacks()
    {
        var set = new ServerStoreSet();
        var store = (ModStore)set.Store("progenitor.sample");
        ((ModStore)store).WhenReadyAs(_ => { }, "ctx-a|mvid|Shared");
        ((ModStore)store).WhenReadyAs(_ => { }, "ctx-b|mvid|Shared");

        Assert.Equal(1, set.DropReadyCallbacks(new[] { "ctx-a|mvid|Shared" }));
        Assert.Equal(1, store.PendingReadyCallbacks);
        Assert.Equal(1, set.DropReadyCallbacks(null));
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    [Fact]
    public void Server_store_log_lines_name_ServerSave()
    {
        string file = ServerSavePaths.StoreFile(_root, "progenitor.sample");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "not json");

        var set = new ServerStoreSet();
        set.SetRoot(_root);
        set.Store("progenitor.sample");

        Assert.Contains(_log.Warnings, w => w.StartsWith("ServerSave: ", StringComparison.Ordinal));
    }
}
