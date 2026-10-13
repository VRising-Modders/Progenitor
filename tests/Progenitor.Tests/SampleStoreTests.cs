using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Dev;
using Progenitor.Persistence;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX3 (RD6 J4). The Sample's persistence example, driven against the <b>real</b>
/// <see cref="ModStore"/> over a temp folder: <c>Remove</c>, <c>Keys</c>, and the pending-store
/// merge the example exists to demonstrate.
///
/// The merge tests are the ones that matter, and job FEX7 fixed the fixture they were measured
/// with (R58 finding 2). The file now holds a counter as well as the join book, because a fixture
/// with no counter in it cannot tell a counter that continued from one that restarted: the whole
/// hazard is a read-modify-write across the pending boundary, where the read is the fallback and
/// the overlay then writes that answer over the file. So there are two facts here — the join book
/// survives (<c>StoreMerge</c> doing its job), and the counter reaches 18 rather than 1 because
/// the Sample defers it to <c>WhenReady</c>. <c>A_blind_RecordLoad_from_OnLoad_would_have_reset…</c>
/// pins the defect itself, so the reason for the deferral cannot quietly stop being true.
///
/// <c>WhenReady</c> dispatches through Core's main-thread queue, so a merge here is followed by
/// <c>MainThread.Drain()</c>; that is the test standing in for the frame tick.
///
/// Shares the <c>PluginLogger</c> collection with the other suites that assign the process-wide
/// <see cref="Plugin.Logger"/> shim.
/// </summary>
[Collection("PluginLogger")]
public class SampleStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-samplestore", Guid.NewGuid().ToString("N"));
    private readonly TestLogSource _log = new();

    public SampleStoreTests()
    {
        Directory.CreateDirectory(_root);
        Plugin.Logger = _log;
        ModStore.ResetSaveSkippedWarning();
    }

    public void Dispose()
    {
        Plugin.Logger = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteStoreFile(string json)
    {
        var path = SavePaths.StoreFile(_root, "progenitor.sample");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    // The expectation travels as an int because SampleStore.Kind is internal and an xUnit theory
    // method has to be public.
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("status", 0)]
    [InlineData("KEYS", 1)]
    [InlineData("Reset", 2)]
    public void TryParse_accepts_the_three_forms(string token, int expected)
    {
        Assert.True(SampleStore.TryParse(token, out var kind));
        Assert.Equal((SampleStore.Kind)expected, kind);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("remove joins")]
    [InlineData("-1")]
    public void TryParse_refuses_anything_else(string token)
    {
        Assert.False(SampleStore.TryParse(token, out _));
    }

    [Fact]
    public void The_counter_armed_in_OnLoad_continues_the_number_on_disk_and_keeps_every_other_key()
    {
        // The save already has a join book *and* a counter at 17 from earlier sessions. Both
        // halves matter: the join book is what the merge must not drop, the 17 is what the
        // increment must not overwrite (R58 finding 2 — the old fixture had no counter).
        var path = WriteStoreFile("""{"joins":{"Players":[{"PlatformId":"7","Name":"Rowan","Count":4}]},"loads":17}""");

        // OnLoad runs before the world save folder is known: the store is pending, so the Sample
        // registers the read-modify-write instead of performing it.
        var store = new ModStore("progenitor.sample", filePath: "");
        Assert.True(store.IsPending);
        var lines = new List<string>();
        using var armed = SampleStore.ArmLoadCounter(store, lines.Add);
        Assert.Empty(lines);
        Assert.Equal(0, store.Get(SampleStore.LoadsKey, 0));

        // The folder arrives; disk is the base and the held keys are laid over it.
        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.False(store.IsPending);
        Assert.Equal(18, store.Get(SampleStore.LoadsKey, 0));
        Assert.Contains(SampleStore.JoinsKey, store.Keys);
        Assert.Contains("loads=18", Assert.Single(lines), StringComparison.Ordinal);

        // And it reaches the file that way.
        Assert.True(store.SaveToDisk());
        string json = File.ReadAllText(path);
        Assert.Contains("\"loads\": 18", json, StringComparison.Ordinal);
        Assert.Contains("\"" + SampleStore.JoinsKey + "\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blind_RecordLoad_from_OnLoad_would_have_reset_the_number_on_disk()
    {
        // The defect the deferral exists for, pinned so the reason cannot quietly stop being
        // true: on a pending store the read is the fallback 0, and the overlay lays the 1 over
        // the file's 17 — permanently, on every start, with the join book still intact beside it.
        var path = WriteStoreFile("""{"joins":{"Players":[]},"loads":17}""");
        var store = new ModStore("progenitor.sample", filePath: "");

        Assert.Equal(1, SampleStore.RecordLoad(store));

        store.Relocate(path);
        store.LoadFromDisk();

        Assert.Equal(1, store.Get(SampleStore.LoadsKey, 0));
        Assert.Contains(SampleStore.JoinsKey, store.Keys);
    }

    [Fact]
    public void Arming_the_counter_on_a_store_that_is_already_real_increments_without_a_merge()
    {
        // A hot reload mid-session: OnLoad runs again, but the save folder has been known for
        // hours, so WhenReady has nothing to wait for.
        var path = WriteStoreFile("""{"loads":4}""");
        var store = new ModStore("progenitor.sample", path);
        store.LoadFromDisk();

        var lines = new List<string>();
        using var armed = SampleStore.ArmLoadCounter(store, lines.Add);
        MainThread.Drain();

        Assert.Equal(5, store.Get(SampleStore.LoadsKey, 0));
        Assert.Contains("loads=5", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void A_counter_armed_by_a_plugin_that_unloads_first_never_runs()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore("progenitor.sample", filePath: "");
        var lines = new List<string>();
        SampleStore.ArmLoadCounter(store, lines.Add);

        // What ModSave.RemoveAll(plugin) does for the assembly that wrote the callback.
        Assert.Equal(1, store.DropReadyCallbacks(ModStore.OwnerIdOf(typeof(SampleStoreTests).Assembly)));

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Empty(lines);
        Assert.Equal(17, store.Get(SampleStore.LoadsKey, 0));
    }

    [Fact]
    public void RecordLoad_continues_a_count_that_was_already_on_disk()
    {
        var path = WriteStoreFile("""{"loads":9}""");
        var store = new ModStore("progenitor.sample", path);
        store.LoadFromDisk();

        Assert.Equal(10, SampleStore.RecordLoad(store));
    }

    [Fact]
    public void Keys_lists_what_is_in_the_bag()
    {
        var path = WriteStoreFile("""{"joins":{"Players":[]},"loads":3}""");
        var store = new ModStore("progenitor.sample", path);
        store.LoadFromDisk();

        var lines = SampleStore.Run(store, SampleStore.Kind.Keys, _root, out bool removed);

        Assert.False(removed);
        string reply = Assert.Single(lines);
        Assert.Contains("Store keys (2)", reply, StringComparison.Ordinal);
        Assert.Contains(SampleStore.JoinsKey, reply, StringComparison.Ordinal);
        Assert.Contains(SampleStore.LoadsKey, reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_on_an_empty_store_says_so_rather_than_printing_an_empty_list()
    {
        var store = new ModStore("progenitor.sample", SavePaths.StoreFile(_root, "progenitor.sample"));
        Assert.Equal("The store has no keys yet.", SampleStore.KeysReply(store.Keys));
    }

    [Fact]
    public void A_long_bag_is_cut_and_says_how_many_are_left()
    {
        var keys = new List<string>();
        for (int i = 0; i < SampleStore.MaxKeysListed + 3; i++) keys.Add("k" + i);

        string reply = SampleStore.KeysReply(keys);

        Assert.Contains("Store keys (" + keys.Count + ")", reply, StringComparison.Ordinal);
        Assert.Contains("… and 3 more", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("k" + (keys.Count - 1), reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_removes_the_join_book_and_leaves_the_load_counter()
    {
        var path = WriteStoreFile("""{"joins":{"Players":[{"PlatformId":"7","Name":"Rowan","Count":4}]},"loads":3}""");
        var store = new ModStore("progenitor.sample", path);
        store.LoadFromDisk();

        var lines = SampleStore.Run(store, SampleStore.Kind.Reset, _root, out bool removed);

        Assert.True(removed);
        Assert.Contains("Removed '" + SampleStore.JoinsKey + "'", Assert.Single(lines), StringComparison.Ordinal);
        Assert.DoesNotContain(SampleStore.JoinsKey, store.Keys);
        Assert.Equal(3, store.Get(SampleStore.LoadsKey, 0));
    }

    [Fact]
    public void Reset_reaches_the_file_once_the_store_is_saved()
    {
        var path = WriteStoreFile("""{"joins":{"Players":[]},"loads":1}""");
        var store = new ModStore("progenitor.sample", path);
        store.LoadFromDisk();

        SampleStore.Run(store, SampleStore.Kind.Reset, _root, out bool removed);
        Assert.True(removed);
        Assert.True(store.SaveToDisk());

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("\"" + SampleStore.JoinsKey + "\"", json, StringComparison.Ordinal);
        Assert.Contains("\"" + SampleStore.LoadsKey + "\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_on_a_store_with_no_join_book_says_there_was_nothing_to_remove()
    {
        var store = new ModStore("progenitor.sample", SavePaths.StoreFile(_root, "progenitor.sample"));

        var lines = SampleStore.Run(store, SampleStore.Kind.Reset, _root, out bool removed);

        Assert.False(removed);
        Assert.Contains("Nothing to remove", Assert.Single(lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_reports_the_counter_the_key_count_and_the_directory()
    {
        var path = WriteStoreFile("""{"joins":{"Players":[]},"loads":7}""");
        var store = new ModStore("progenitor.sample", path);
        store.LoadFromDisk();

        string reply = Assert.Single(SampleStore.Run(store, SampleStore.Kind.Status, _root, out _));

        Assert.Contains("loads=7", reply, StringComparison.Ordinal);
        Assert.Contains("keys=2", reply, StringComparison.Ordinal);
        Assert.Contains(_root, reply, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_save_directory_is_reported_as_the_pending_state_and_not_as_blank()
    {
        Assert.Equal("<not known yet: the store is pending>", SampleStore.Directory(null));
        Assert.Equal("<not known yet: the store is pending>", SampleStore.Directory(""));
        Assert.Contains("<not known yet: the store is pending>", SampleStore.StatusReply(1, 1, null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_load_line_says_where_the_increment_ran_and_what_the_other_choice_would_cost()
    {
        // The point of the counter is the sentence it lets the log carry. A line that only
        // printed a number would teach nothing, and the line it used to print ("Core merges it
        // over the file's other keys") was a description of the merge working attached to the
        // evidence that it had eaten the value (R58 finding 2).
        // And which of WhenReady's two branches ran, because tutorial 3a sends the reader to this
        // line to tell them apart and one string emitted identically on both told them nothing
        // (R59 finding 9).
        string merged = SampleStore.LoadLine(18, inLine: false);
        string inLine = SampleStore.LoadLine(18, inLine: true);

        Assert.Equal(
            "loads=18 (WhenReady ran after the pending merge; a Set from OnLoad would have reset "
            + "it - see tutorial 3a)",
            merged);
        Assert.Equal(
            "loads=18 (WhenReady ran in line: the store was already real; a Set from OnLoad would "
            + "have reset it - see tutorial 3a)",
            inLine);
        Assert.NotEqual(merged, inLine);
        Assert.Equal("Sample store: " + merged, SampleLog.Format(SampleStore.Area, merged));
    }

    [Fact]
    public void The_join_book_key_is_one_string_shared_with_the_reader_in_Plugin_cs()
    {
        // SampleJoins.Key is `= SampleStore.JoinsKey`, so a rename cannot leave `.store reset`
        // deleting a key nothing reads. This pins the value the on-disk files already use.
        Assert.Equal("joins", SampleStore.JoinsKey);
    }
}

/// <summary>
/// Job J19 (RD6). The three <c>ModSave</c> statics the Sample now shows beside <c>ctx.Save</c> —
/// <c>Store(guid)</c>, <c>SaveAll()</c>, <c>CurrentSaveDirectory</c> — plus the
/// <c>RemoveAll(owner)</c> line <c>Detach</c> writes.
///
/// <para>All four are static Core calls that need the server process (<c>ModSave</c> short-circuits
/// on <c>Plugin.IsServerProcess</c> and patches the game's save trigger), so what is game-free is
/// the branch each takes, the path arithmetic, and the sentences. The live handler's calls are
/// pinned as source so a reply that <em>says</em> <c>SaveAll</c> ran cannot survive the call being
/// removed.</para>
/// </summary>
public class SampleStoreStaticsTests
{
    private const string Guid = "progenitor.sample";

    [Theory]
    [InlineData("dir", 3)]
    [InlineData("DIR", 3)]
    [InlineData("flush", 4)]
    [InlineData(" Flush ", 4)]
    [InlineData("static", 5)]
    [InlineData("server", 6)]
    [InlineData("SERVER", 6)]
    public void The_three_new_tokens_parse(string token, int expected)
    {
        Assert.True(SampleStore.TryParse(token, out var kind));
        Assert.Equal((SampleStore.Kind)expected, kind);
    }

    [Fact]
    public void The_usage_line_lists_every_branch_the_parser_accepts()
    {
        // `status` is the default and is written `[status]`; the rest are named literally.
        Assert.Contains(".store [status]", SampleStore.UsageReply, StringComparison.Ordinal);
        Assert.True(SampleStore.TryParse("status", out _));

        foreach (string token in new[] { "keys", "dir", "flush", "static", "server", "reset" })
        {
            Assert.True(SampleStore.TryParse(token, out _));
            Assert.Contains(".store " + token, SampleStore.UsageReply, StringComparison.Ordinal);
        }
    }

    [Fact]
    // The path comes from SavePaths.StoreFile, the same public helper ModSave uses, rather than a
    // second Path.Combine that would go stale the day the layout moves.
    public void The_store_file_is_derived_through_the_same_helper_core_uses()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-j19");

        Assert.Equal(SavePaths.StoreFile(dir, Guid), SampleStore.StoreFileOf(dir, Guid));
        Assert.Equal("", SampleStore.StoreFileOf(null, Guid));
        Assert.Equal("", SampleStore.StoreFileOf("", Guid));
        Assert.Equal("", SampleStore.StoreFileOf(dir, null));
        Assert.Equal("", SampleStore.StoreFileOf(dir, ""));
    }

    [Fact]
    // Empty is a state, not a failure. A reply that printed nothing would read as a broken command
    // during the exact window - a server start, before the world save folder resolves - the
    // pending-store lesson is about.
    public void An_unresolved_directory_is_explained_rather_than_printed_as_blank()
    {
        string reply = SampleStore.DirReply(null, null);

        Assert.Contains("is empty", reply, StringComparison.Ordinal);
        Assert.Contains("pending", reply, StringComparison.Ordinal);
        Assert.Contains("WhenReady", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolved_directory_is_printed_with_the_json_path_and_the_do_not_build_paths_warning()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-j19");
        string file = SavePaths.StoreFile(dir, Guid);
        string reply = SampleStore.DirReply(dir, file);

        Assert.Contains(dir, reply, StringComparison.Ordinal);
        Assert.Contains(file, reply, StringComparison.Ordinal);
        Assert.Contains("do not build paths from it", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_dir_answers_from_the_directory_and_the_guid_it_was_given()
    {
        var store = new ModStore(Guid, SavePaths.StoreFile(Path.GetTempPath(), Guid));
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-j19");

        string reply = Assert.Single(SampleStore.Run(store, SampleStore.Kind.Dir, dir, out bool removed, Guid));

        Assert.False(removed);
        Assert.Contains(SavePaths.StoreFile(dir, Guid), reply, StringComparison.Ordinal);
    }

    [Fact]
    // SaveAll is void, so the reply has to be built from a read of the file afterwards. These are
    // the three answers that read can give.
    public void The_flush_reply_reports_what_is_on_disk_and_not_that_the_call_returned()
    {
        string file = SavePaths.StoreFile(Path.GetTempPath(), Guid);

        string wrote = SampleStore.FlushReply(file, exists: true, bytes: 412, keyCount: 2);
        Assert.Contains("412 byte(s)", wrote, StringComparison.Ordinal);
        Assert.Contains("2 key(s)", wrote, StringComparison.Ordinal);
        Assert.Contains(file, wrote, StringComparison.Ordinal);

        string missing = SampleStore.FlushReply(file, exists: false, bytes: 0, keyCount: 0);
        Assert.Contains("does not exist", missing, StringComparison.Ordinal);
        // R65 finding 4: the reply used to say "an empty store writes no file" and send the
        // operator off to add a key. ModStore.SaveToDisk refuses only a pending store or one with
        // no path; an empty non-pending store writes "{}". The two real causes are named instead,
        // and both of them are in the server log.
        Assert.DoesNotContain("empty store writes no file", missing, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("put a key in it", missing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{}", missing, StringComparison.Ordinal);
        Assert.Contains("pending", missing, StringComparison.Ordinal);
        Assert.Contains("threw", missing, StringComparison.Ordinal);
        Assert.Contains("server log", missing, StringComparison.Ordinal);

        string pending = SampleStore.FlushReply(null, exists: false, bytes: 0, keyCount: 3);
        Assert.Contains("nothing to write", pending, StringComparison.Ordinal);
        Assert.Contains("pending", pending, StringComparison.Ordinal);
    }

    /// <summary>
    /// The measurement behind the reply above (R65 finding 4's probe C). An empty store that is
    /// not pending is written like any other: <c>SaveToDisk</c> returns true and the file on disk
    /// is <c>{}</c>. So "the file is missing" can never mean "the store was empty", and the reply
    /// must not say it does.
    /// </summary>
    [Fact]
    public void An_empty_store_that_is_not_pending_writes_a_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-r65-" + Guid.Replace('.', '-') + "-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = SavePaths.StoreFile(dir, Guid);
            var store = new ModStore(Guid, file);

            Assert.True(store.SaveToDisk());
            Assert.True(File.Exists(file));
            Assert.Equal("{}", File.ReadAllText(file).Trim());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void The_static_reply_turns_on_the_reference_compare_and_not_on_a_claim()
    {
        string same = SampleStore.StaticReply(Guid, sameInstance: true, keyCount: 2);
        Assert.Contains("the SAME instance ctx.Save gave this plugin", same, StringComparison.Ordinal);
        Assert.Contains(Guid, same, StringComparison.Ordinal);

        string different = SampleStore.StaticReply(Guid, sameInstance: false, keyCount: 2);
        Assert.Contains("a DIFFERENT instance", different, StringComparison.Ordinal);
        Assert.Contains("should not happen", different, StringComparison.Ordinal);
    }

    [Fact]
    // The reply is where the "which route when" answer lives, because a mod author reading the
    // Sample is far more likely to run the command than to read the file.
    public void The_static_reply_says_when_each_route_is_the_right_one()
    {
        string reply = SampleStore.StaticReply(Guid, sameInstance: true, keyCount: 0);

        Assert.Contains("Use ctx.Save inside a plugin", reply, StringComparison.Ordinal);
        Assert.Contains("code that has no context", reply, StringComparison.Ordinal);
        Assert.Contains("On the client both are a no-op store", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_RemoveAll_line_distinguishes_nothing_pending_from_something_dropped()
    {
        string none = SampleStore.RemoveAllLine(0);
        Assert.Contains("dropped 0 pending", none, StringComparison.Ordinal);
        Assert.Contains("the increment had run", none, StringComparison.Ordinal);

        string some = SampleStore.RemoveAllLine(2);
        Assert.Contains("dropped 2 pending", some, StringComparison.Ordinal);
        Assert.Contains("before the save folder was resolved", some, StringComparison.Ordinal);
    }

    [Fact]
    // The three statics have to actually be called. A reply naming SaveAll while nothing flushed,
    // or a Detach line reporting a RemoveAll that never ran, is exactly the failure a sample
    // cannot afford - it is the thing readers copy.
    public void The_live_handler_calls_all_three_statics_and_Detach_calls_RemoveAll()
    {
        string source = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleStore.cs");

        string flush = SourcePin.MethodBody(
            source, "private static void HandleFlush(ICommandContext ctx, IModStore store, string? dir, string guid)");
        SourcePin.AssertStatementExactly(flush, "ModSave.SaveAll();");
        SourcePin.AssertStatement(flush, "string? after = ModSave.CurrentSaveDirectory;");

        string handle = SourcePin.MethodBody(source, "internal static void HandleCommand(ICommandContext ctx, Kind kind)");
        SourcePin.AssertStatement(handle, "var viaStatic = ModSave.Store(guid);");
        SourcePin.AssertStatement(handle, "ctx.Reply(StaticReply(guid, ReferenceEquals(viaStatic, store), viaStatic.Keys.Count));");
        SourcePin.AssertStatement(handle, "string? dir = ModSave.CurrentSaveDirectory;");

        string detach = SourcePin.MethodBody(source, "internal static void Detach()");
        SourcePin.AssertStatement(detach, "SampleLog.Info(Area, RemoveAllLine(ModSave.RemoveAll(owner)));");
        SourcePin.AssertStatementExactly(detach, "_loadCounter?.Dispose();");
    }

    [Fact]
    // Detach must run while Plugin.Active is still set, or the owner is null and RemoveAll is
    // never reached. Plugin.cs nulls Active at the end of OnUnload; this pins the order.
    public void Plugin_cs_detaches_the_store_before_it_drops_the_context()
    {
        string source = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");

        SourcePin.AssertStatementExactly(source, "SampleStore.Detach();");
        int detach = source.IndexOf("SampleStore.Detach();", StringComparison.Ordinal);
        int clear = source.IndexOf("Active = null;", StringComparison.Ordinal);
        Assert.True(detach > 0 && clear > 0);
        Assert.True(detach < clear, "SampleStore.Detach() must run before Plugin.Active is cleared");
    }
}

/// <summary>
/// <c>.store server</c> and the all-time join book: the Sample's worked example of
/// <c>ctx.ServerSave</c>, the store that survives wipes and world restores.
/// </summary>
public class SampleServerStoreTests
{
    [Fact]
    public void The_server_reply_says_it_survives_wipes_and_names_the_folder()
    {
        string reply = SampleStore.ServerReply(2, 3, 11, @"D:\data\progenitor-server");
        Assert.Contains("survives wipes and world restores", reply, StringComparison.Ordinal);
        Assert.Contains("allTimePlayers=3", reply, StringComparison.Ordinal);
        Assert.Contains("allTimeJoins=11", reply, StringComparison.Ordinal);
        Assert.EndsWith(@"dir=D:\data\progenitor-server", reply, StringComparison.Ordinal);
        Assert.Contains("<not known yet", SampleStore.ServerReply(0, 0, 0, null), StringComparison.Ordinal);
    }

    [Fact]
    public void A_join_is_recorded_in_the_world_book_and_in_the_server_book()
    {
        string plugin = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");
        SourcePin.AssertStatementExactly(plugin, "int joins = SampleJoins.Record(ctx.Save, platformId, name);");
        SourcePin.AssertStatementExactly(plugin, "int allTime = SampleJoins.Record(ctx.ServerSave, platformId, name);");
        SourcePin.AssertStatementExactly(plugin, "ServerStore = ctx.ServerSave;");

        string store = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleStore.cs");
        SourcePin.AssertStatement(store, "string line = ServerReply(server.Keys.Count, book.Players.Count, joins, ServerSave.CurrentDirectory);");
    }
}
