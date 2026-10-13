using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Dev;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX7 (R58 finding 2). <see cref="IModStore.WhenReady"/> against the <b>real</b>
/// <see cref="ModStore"/> over a temp folder.
///
/// The gap this closes is not a Sample bug. A mod that needs a read-modify-write on its store had
/// no way to know when the store was real: on a server start <c>OnLoad</c> runs before the world
/// save folder is known, <c>Get</c> reads an empty document, and the dirty overlay then lays that
/// answer over the file. Every counter written that way is stuck at 1 forever, silently, and the
/// merge that does it is the same merge that makes a blind <c>Set</c> safe. <c>WhenReady</c> is
/// the seam where the disk value is visible.
///
/// Dispatch after a merge goes through Core's main-thread queue (so the callback does not run
/// inside the load's call stack, and so an owner the reloader retired is dropped rather than
/// invoked), which is why every merge here is followed by <c>MainThread.Drain()</c> — the test
/// standing in for the frame tick.
///
/// Shares the <c>PluginLogger</c> collection with the other suites that assign the process-wide
/// <see cref="Plugin.Logger"/> shim.
/// </summary>
[Collection("PluginLogger")]
public class ModStoreReadyTests : IDisposable
{
    private const string Guid1 = "progenitor.readytests";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-storeready", Guid.NewGuid().ToString("N"));
    private readonly TestLogSource _log = new();

    public ModStoreReadyTests()
    {
        Directory.CreateDirectory(_root);
        Plugin.Logger = _log;
        ModStore.ResetSaveSkippedWarning();
        // Job FEX8 (R59 finding 9). The main-thread id is a process-wide static that the first
        // Drain claims, so which branch of WhenReady a test took used to depend on whether some
        // earlier test in this collection had drained, and on which pool thread it ran. Clearing
        // it here makes the default deterministic - not the main thread, so every callback is
        // posted - and the two tests that are about the in-line branch claim the thread on
        // purpose, with a Drain of their own.
        MainThread.ResetForTests();
    }

    public void Dispose()
    {
        MainThread.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteStoreFile(string json)
    {
        var path = SavePaths.StoreFile(_root, Guid1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private static string OwnerName => typeof(ModStoreReadyTests).Assembly.GetName().Name!;

    /// <summary>
    /// The identity <c>ModSave.RemoveAll</c> resolves for this suite's own assembly. Job FEX9
    /// (R60 finding 3): the drop matches on load context + MVID + simple name, not on the name
    /// alone, so a test that means "this assembly" has to say it the same way production does.
    /// </summary>
    private static string OwnerId => ModStore.OwnerIdOf(typeof(ModStoreReadyTests).Assembly);

    /// <summary>An identity of the production shape for an assembly this process does not have.</summary>
    private static string FakeId(string context, string name) =>
        context + "|" + Guid.NewGuid().ToString("N") + "|" + name;

    [Fact]
    public void A_pending_store_runs_the_callback_after_the_merge_with_the_disk_value_visible()
    {
        var path = WriteStoreFile("""{"loads":17,"other":"kept"}""");
        var store = new ModStore(Guid1, filePath: "");
        Assert.True(store.IsPending);
        Assert.False(store.IsReady);

        var seen = new List<int>();
        store.WhenReady(s => seen.Add(s.Get("loads", 0)));

        // Nothing yet: the store cannot answer a read from disk, which is the whole point.
        MainThread.Drain();
        Assert.Empty(seen);
        Assert.Equal(1, store.PendingReadyCallbacks);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.True(store.IsReady);
        Assert.Equal(17, Assert.Single(seen));
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    [Fact]
    public void A_read_modify_write_in_the_callback_survives_the_merge_and_reaches_the_file()
    {
        var path = WriteStoreFile("""{"loads":17,"other":"kept"}""");
        var store = new ModStore(Guid1, filePath: "");

        store.WhenReady(s => s.Set("loads", s.Get("loads", 0) + 1));

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Equal(18, store.Get("loads", 0));
        Assert.True(store.SaveToDisk());

        string json = File.ReadAllText(path);
        Assert.Contains("\"loads\": 18", json, StringComparison.Ordinal);
        Assert.Contains("\"other\": \"kept\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_callback_runs_once_and_a_later_load_does_not_repeat_it()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();
        Assert.Equal(1, calls);

        // A relocate, an autosave-driven reload, a second WorldReady: none of them is a second
        // "the store just became real".
        store.LoadFromDisk();
        MainThread.Drain();
        Assert.Equal(1, calls);
    }

    [Fact]
    // R59 finding 9, job FEX8. The contract's first branch: already real, caller on the main
    // thread - which is every OnLoad in the game once a world is up - runs the callback *on this
    // line*, before WhenReady returns. It was stated in four places and driven by nothing: the
    // main-thread id is -1 until the first Drain, so the whole suite took the posted branch and
    // deleting the in-line one would have failed no test. The Drain below is what claims this
    // thread as the main one; the assertion is that no second Drain is needed.
    public void A_store_that_is_already_real_runs_the_callback_in_line_on_the_main_thread()
    {
        var path = WriteStoreFile("""{"loads":9}""");
        var store = new ModStore(Guid1, path);
        store.LoadFromDisk();
        Assert.True(store.IsReady);

        MainThread.Drain();                     // captures this thread as the main thread
        Assert.True(MainThread.IsMainThread);

        var seen = new List<int>();
        store.WhenReady(s => seen.Add(s.Get("loads", 0)));

        // No merge, no queue, no frame: the value was available and the caller was not made to
        // wait for it.
        Assert.Equal(9, Assert.Single(seen));
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    [Fact]
    // The second branch, which the old "otherwise ... after the pending merge" sentence described
    // wrongly: the store is already real, so there is no merge to be after, and the callback is
    // posted because the caller is not on the main thread. A background task must not have its
    // callback run on its own thread.
    public void A_store_that_is_already_real_posts_when_the_caller_is_off_the_main_thread()
    {
        var path = WriteStoreFile("""{"loads":9}""");
        var store = new ModStore(Guid1, path);
        store.LoadFromDisk();

        MainThread.Drain();                     // this thread is the main thread
        int mainThread = Environment.CurrentManagedThreadId;

        var seen = new List<int>();
        int ranOn = -1;
        System.Threading.Tasks.Task.Run(() =>
        {
            Assert.False(MainThread.IsMainThread);
            store.WhenReady(s =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                seen.Add(s.Get("loads", 0));
            });
        }).GetAwaiter().GetResult();

        // Not run in line on the worker, and not run yet at all.
        Assert.Empty(seen);

        MainThread.Drain();
        Assert.Equal(9, Assert.Single(seen));
        Assert.Equal(mainThread, ranOn);
    }

    [Fact]
    public void An_unload_before_the_merge_drops_the_callback()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);
        Assert.Equal(1, store.PendingReadyCallbacks);

        // ModSave.RemoveAll(plugin) resolves the owner's assembly and calls exactly this.
        Assert.Equal(1, store.DropReadyCallbacks(OwnerId));
        Assert.Equal(0, store.PendingReadyCallbacks);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void A_drop_naming_another_assembly_leaves_the_callback_alone()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        Assert.Equal(0, store.DropReadyCallbacks(FakeId("someothercontext", "SomeOtherMod")));

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void A_null_assembly_name_drops_every_callback_which_is_what_Core_uninstall_does()
    {
        var store = new ModStore(Guid1, filePath: "");
        store.WhenReady(_ => { });
        store.WhenReady(_ => { });

        Assert.Equal(2, store.DropReadyCallbacks((string?)null));
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    [Fact]
    public void Disposing_the_handle_cancels_a_pending_callback_and_is_idempotent()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        var handle = store.WhenReady(_ => calls++);
        handle.Dispose();
        handle.Dispose();
        Assert.Equal(0, store.PendingReadyCallbacks);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void Disposing_after_the_callback_ran_is_harmless()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        var handle = store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();
        Assert.Equal(1, calls);

        handle.Dispose();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void One_callback_that_throws_does_not_stop_the_others_or_the_load()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int ran = 0;
        store.WhenReady(_ => throw new InvalidOperationException("mod bug"));
        store.WhenReady(_ => ran++);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Equal(1, ran);
        Assert.Equal(17, store.Get("loads", 0));
        Assert.Contains(_log.Errors, l => l.Contains("WhenReady callback", StringComparison.Ordinal)
            && l.Contains("mod bug", StringComparison.Ordinal));
    }

    [Fact]
    public void A_callback_registered_from_inside_a_callback_runs_at_once_because_the_store_is_real_by_then()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int inner = 0;
        store.WhenReady(s => s.WhenReady(_ => inner++));

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();
        MainThread.Drain();

        Assert.Equal(1, inner);
    }

    [Fact]
    public void A_null_callback_is_refused()
    {
        var store = new ModStore(Guid1, filePath: "");
        Assert.Throws<ArgumentNullException>(() => store.WhenReady(null!));
    }

    [Fact]
    public void The_client_no_op_store_never_runs_the_callback()
    {
        IModStore store = NoOpStore.Instance;

        int calls = 0;
        using var handle = store.WhenReady(_ => calls++);
        MainThread.Drain();

        // There is no save folder on the client, so the store never becomes real; a callback that
        // ran here would read defaults and write them nowhere.
        Assert.Equal(0, calls);
        Assert.Throws<ArgumentNullException>(() => store.WhenReady(null!));
    }

    // ---- R59 finding 3: the window between the post and the drain ------------------------------

    [Fact]
    // The registration is still a registration until it runs. FireReady used to empty the list and
    // null the callback at *post* time, so for the frame between the merge and the drain all three
    // named droppers were no-ops: Dispose and the sweeps walked a list the row had already left,
    // and Cancel nulled a field the queued closure had already copied into a local.
    public void A_posted_callback_is_still_pending_until_it_runs()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();

        // Posted, not run.
        Assert.Equal(0, calls);
        Assert.Equal(1, store.PendingReadyCallbacks);

        MainThread.Drain();
        Assert.Equal(1, calls);
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    [Fact]
    // Cancel after post: disposing the handle inside that frame skips the run. Before FEX8 the
    // callback ran anyway, because the queued closure was holding it.
    public void Disposing_the_handle_after_the_merge_but_before_the_drain_skips_the_callback()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        var handle = store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();
        handle.Dispose();

        MainThread.Drain();
        Assert.Equal(0, calls);
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    [Fact]
    // Unload after post: the plugin sweep lands in the same frame and the callback does not run.
    // This is the case the Lifetime paragraph promised and named the wrong mechanism for - the
    // main-thread queue's retired-context check reads the *posting closure's* context, which is
    // Core's, so it never covered a plugin unload at all.
    public void An_unload_after_the_merge_but_before_the_drain_drops_the_callback()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();

        // ModSave.RemoveAll(plugin, owned) resolves the names and calls exactly this. That it
        // still finds a row to drop is the fix: the post did not retire the registration.
        Assert.Equal(1, store.DropReadyCallbacks(OwnerId));

        MainThread.Drain();
        Assert.Equal(0, calls);
    }

    [Fact]
    // And Core's uninstall, in the same window.
    public void A_core_uninstall_after_the_merge_but_before_the_drain_drops_the_callback()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();
        Assert.Equal(1, store.DropReadyCallbacks((string?)null));

        MainThread.Drain();
        Assert.Equal(0, calls);
    }

    [Fact]
    // Two merges before one drain post the same registration twice; the exchange inside the invoke
    // is the once-guard, so it runs once. (The old shape got this from emptying the list, which is
    // what cost the cancel window.)
    public void Two_posts_before_a_drain_still_run_the_callback_once()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        store.Relocate(path);
        store.LoadFromDisk();
        store.LoadFromDisk();

        MainThread.Drain();
        Assert.Equal(1, calls);
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    // ---- R59 finding 10: the plugin's whole set of owned assemblies ----------------------------

    [Fact]
    // The drop matches against every name in the set, not the first. A plugin's collectible
    // context holds the plugin assembly plus the rewritten dependencies loaded into it, so a
    // callback declared in a helper library the plugin ships has that library's name and must be
    // dropped by the same sweep.
    public void A_drop_matches_any_of_the_owned_assembly_names()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        int calls = 0;
        store.WhenReady(_ => calls++);

        // The plugin's own name alone would not reach a callback written in the library...
        Assert.Equal(0, store.DropReadyCallbacks(new[] { FakeId("progenitor-reload:A:1", "Shared") }));
        Assert.Equal(1, store.PendingReadyCallbacks);

        // ...and the set is walked, so the second name matches.
        Assert.Equal(1, store.DropReadyCallbacks(new[] { FakeId("progenitor-reload:A:1", "Shared"), OwnerId }));
        Assert.Equal(0, store.PendingReadyCallbacks);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();
        Assert.Equal(0, calls);
    }

    [Fact]
    // An empty set is "this owner owns nothing nameable", not "drop everything". Null still means
    // all of them, which is what Core's uninstall passes.
    public void An_empty_owned_set_drops_nothing_and_null_still_drops_everything()
    {
        var store = new ModStore(Guid1, filePath: "");
        store.WhenReady(_ => { });
        store.WhenReady(_ => { });

        Assert.Equal(0, store.DropReadyCallbacks(Array.Empty<string>()));
        Assert.Equal(2, store.PendingReadyCallbacks);

        Assert.Equal(2, store.DropReadyCallbacks((System.Collections.Generic.IReadOnlyCollection<string>?)null));
        Assert.Equal(0, store.PendingReadyCallbacks);
    }

    // ---- R60 finding 3: the same simple name in two plugins ------------------------------------

    [Fact]
    // Two plugins that each vendor a helper called Shared.dll. FEX8 widened the matched set to
    // the rewritten dependencies, and with a simple-name match that made unloading plugin A cancel
    // plugin B's pending callback - B's read-modify-write against its own save file then never ran,
    // silently, for the session. The identity carries the load context and the MVID as well, so the
    // shared name is not enough to match. (One test project cannot build two assemblies of the same
    // name, so both identities are synthetic - the same three-part shape OwnerIdOf mints.)
    public void A_second_plugins_helper_with_the_same_simple_name_is_not_dropped()
    {
        var path = WriteStoreFile("""{"loads":17}""");
        var store = new ModStore(Guid1, filePath: "");

        string sharedA = FakeId("progenitor-reload:PluginA.dll:1", "Shared");
        string sharedB = FakeId("progenitor-reload:PluginB.dll:2", "Shared");
        string pluginA = FakeId("progenitor-reload:PluginA.dll:1", "PluginA");

        int a = 0, b = 0;
        store.WhenReadyAs(_ => a++, sharedA);
        store.WhenReadyAs(_ => b++, sharedB);

        // Plugin A unloads: the reloader hands over A's own assembly and the helper inside A's ALC.
        Assert.Equal(1, store.DropReadyCallbacks(new[] { pluginA, sharedA }));
        Assert.Equal(1, store.PendingReadyCallbacks);

        store.Relocate(path);
        store.LoadFromDisk();
        MainThread.Drain();

        Assert.Equal(0, a);
        Assert.Equal(1, b);
    }

    [Fact]
    // The same file loaded into two collectible contexts has one MVID, so the context name is the
    // part that separates those - and a real registration's identity is stable and matches itself.
    public void An_identity_is_the_context_the_mvid_and_the_name()
    {
        var mine = ModStore.OwnerIdOf(typeof(ModStoreReadyTests).Assembly);
        Assert.Equal(mine, ModStore.OwnerIdOf(typeof(ModStoreReadyTests).Assembly));
        Assert.Equal(2, mine.Split('|').Length - 1);
        Assert.EndsWith("|" + OwnerName, mine, StringComparison.Ordinal);
        Assert.Equal("", ModStore.OwnerIdOf(null));

        // Same MVID and name, different context: not the same owner.
        string mvid = mine.Split('|')[1];
        Assert.NotEqual("progenitor-reload:x:1|" + mvid + "|" + OwnerName, mine);

        // And a callback registered the ordinary way carries exactly this identity.
        var store = new ModStore(Guid1, filePath: "");
        store.WhenReady(_ => { });
        Assert.Equal(1, store.DropReadyCallbacks(mine));
    }

    [Fact]
    // ModSave and the reloader are not compiled into this suite (ModSave binds ProjectM), so the
    // two ends of the set are pinned in source: the sweep hands over the list it already computed
    // for PluginOwnedAssemblies, and RemoveAll takes it.
    public void The_reloader_hands_the_sweep_the_plugins_owned_assemblies()
    {
        string reloader = SourcePin.ReadFile("src", "Progenitor.Core", "Reload", "PluginReloader.cs");
        SourcePin.AssertStatementExactly(
            reloader, "try { Progenitor.Persistence.ModSave.RemoveAll(plugin, owned); }");
        SourcePin.AssertNoStatementContaining(reloader, "ModSave.RemoveAll(plugin);");

        string modsave = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "ModSave.cs");
        SourcePin.AssertStatement(
            modsave, "public static int RemoveAll(object owner, IReadOnlyList<Assembly>? owned = null)");
        SourcePin.AssertStatement(modsave, "int dropped = DropReadyCallbacks(ids);");
        // Job FEX9 (R60 finding 3): identities, not simple names. The identity set is built in
        // ModStore.OwnerIdsFor, shared with ServerSave.RemoveAll, and pinned there.
        SourcePin.AssertStatement(modsave, "var ids = ModStore.OwnerIdsFor(owner, owned, names);");
        string modstore = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "ModStore.cs");
        SourcePin.AssertStatement(modstore, "string id = OwnerIdOf(own);");

        string serversave = SourcePin.ReadFile("src", "Progenitor.Core", "Persistence", "ServerSave.cs");
        SourcePin.AssertStatement(
            serversave, "public static int RemoveAll(object owner, IReadOnlyList<Assembly>? owned = null)");
        SourcePin.AssertStatement(serversave, "var ids = ModStore.OwnerIdsFor(owner, owned, names);");
        SourcePin.AssertStatementExactly(
            reloader, "try { Progenitor.Persistence.ServerSave.RemoveAll(plugin, owned); }");
    }

    [Fact]
    public void A_store_whose_pending_flag_cleared_with_no_path_is_not_treated_as_ready()
    {
        // LoadFromDisk on a pending store with no file path clears the flag but cannot answer a
        // read from disk. WhenReady waits for the path as well, so a callback is not handed a
        // store that only looks real.
        var store = new ModStore(Guid1, filePath: "");
        int calls = 0;
        store.WhenReady(_ => calls++);

        store.LoadFromDisk();
        MainThread.Drain();

        Assert.False(store.IsPending);
        Assert.False(store.IsReady);
        Assert.Equal(0, calls);
        Assert.Equal(1, store.PendingReadyCallbacks);
    }
}
