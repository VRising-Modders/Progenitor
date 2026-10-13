using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Progenitor.Bootstrap;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of how <see cref="MainThread"/> tags queued work and matches it on
/// unload. The queue itself is not exercised: AssemblyTag / AssemblyNameTag /
/// MatchesOwnerOrAssembly / DelegateContextName are the pure rules the reloader relies on.
/// WorkItem stores assembly full names (not live Assembly refs); the delegate's collectible
/// context is recorded even when the owner object lives in another assembly.
/// </summary>
public class MainThreadOwnershipTests
{
    private sealed class PluginOwner { }

    private sealed class OtherOwner { }

    private static void LocalAction() { }

    private static int LocalFunc() => 7;

    [Fact]
    public void AssemblyTag_null_owner_uses_action_declaring_assembly()
    {
        Action work = LocalAction;
        Assert.Same(typeof(MainThreadOwnershipTests).Assembly, MainThread.AssemblyTag(null, work));
    }

    [Fact]
    public void AssemblyTag_null_owner_uses_func_declaring_assembly()
    {
        Func<int> work = LocalFunc;
        Assert.Same(typeof(MainThreadOwnershipTests).Assembly, MainThread.AssemblyTag(null, work));
    }

    [Fact]
    public void AssemblyTag_null_owner_follows_a_delegate_defined_in_another_assembly()
    {
        Action work = GC.Collect;
        Assert.Same(typeof(GC).Assembly, MainThread.AssemblyTag(null, work));
        Assert.NotSame(typeof(MainThreadOwnershipTests).Assembly, MainThread.AssemblyTag(null, work));
    }

    [Fact]
    public void AssemblyTag_explicit_owner_wins_over_delegate_assembly()
    {
        var owner = new PluginOwner();
        Action work = GC.Collect;
        Assert.Same(typeof(PluginOwner).Assembly, MainThread.AssemblyTag(owner, work));
        Assert.NotSame(typeof(GC).Assembly, MainThread.AssemblyTag(owner, work));
    }

    [Fact]
    public void AssemblyTag_null_owner_and_null_work_is_null()
    {
        Assert.Null(MainThread.AssemblyTag(null, null));
        Assert.Null(MainThread.AssemblyNameTag(null, null));
    }

    [Fact]
    public void AssemblyNameTag_is_the_assembly_full_name()
    {
        Action work = LocalAction;
        Assert.Equal(
            typeof(MainThreadOwnershipTests).Assembly.FullName,
            MainThread.AssemblyNameTag(null, work));
        var owner = new PluginOwner();
        Assert.Equal(
            typeof(PluginOwner).Assembly.FullName,
            MainThread.AssemblyNameTag(owner, (Action)GC.Collect));
    }

    [Fact]
    public void MatchesDelegateAssembly_drops_core_methods_even_when_owner_is_a_plugin()
    {
        string core = typeof(MainThreadOwnershipTests).Assembly.FullName!;
        string plugin = typeof(object).Assembly.FullName!;
        Assert.True(MainThread.MatchesDelegateAssembly(core, core));
        Assert.False(MainThread.MatchesDelegateAssembly(core, plugin));
        Assert.False(MainThread.MatchesDelegateAssembly(null, core));
        Assert.False(MainThread.MatchesDelegateAssembly(core, null));
        Assert.True(WorkQueue.MatchesDelegateAssembly(core, core));
        Assert.False(WorkQueue.MatchesDelegateAssembly(plugin, core));
    }

    [Fact]
    public void Matches_explicit_owner_instance()
    {
        var owner = new PluginOwner();
        Assert.True(MainThread.MatchesOwnerOrAssembly(owner, itemSourceName: null, plugin: owner, unloadingName: null));
    }

    [Fact]
    public void Matches_explicit_owner_by_assembly_when_plugin_is_a_different_instance()
    {
        var owner = new PluginOwner();
        var other = new OtherOwner();
        Assert.True(MainThread.MatchesOwnerOrAssembly(
            owner, itemSourceName: null, plugin: other, unloadingName: typeof(PluginOwner).Assembly.FullName));
    }

    [Fact]
    public void Explicit_owner_is_not_dropped_for_an_unrelated_assembly()
    {
        var owner = new PluginOwner();
        Assert.False(MainThread.MatchesOwnerOrAssembly(
            owner, itemSourceName: typeof(GC).Assembly.FullName, plugin: null, unloadingName: typeof(GC).Assembly.FullName));
        Assert.True(MainThread.MatchesOwnerOrAssembly(
            owner, itemSourceName: typeof(GC).Assembly.FullName, plugin: null, unloadingName: typeof(PluginOwner).Assembly.FullName));
    }

    [Fact]
    public void Null_owner_matches_the_tagged_source_assembly_full_name()
    {
        string source = typeof(MainThreadOwnershipTests).Assembly.FullName!;
        Assert.True(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null, itemSourceName: source, plugin: null, unloadingName: source));
        Assert.False(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null, itemSourceName: source, plugin: null, unloadingName: typeof(GC).Assembly.FullName));
    }

    [Fact]
    public void Null_owner_is_not_dropped_when_unloading_assembly_is_null()
    {
        Assert.False(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null,
            itemSourceName: typeof(MainThreadOwnershipTests).Assembly.FullName,
            plugin: new PluginOwner(),
            unloadingName: null));
    }

    [Fact]
    public void Null_owner_with_no_source_tag_never_matches_an_assembly()
    {
        Assert.False(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null, itemSourceName: null, plugin: null, unloadingName: typeof(MainThreadOwnershipTests).Assembly.FullName));
    }

    [Fact]
    public void Matches_compares_assembly_full_names_not_instance_identity()
    {
        string name = typeof(MainThreadOwnershipTests).Assembly.FullName!;
        string copy = new string(name.ToCharArray());
        Assert.False(ReferenceEquals(name, copy));
        Assert.True(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null, itemSourceName: copy, plugin: null, unloadingName: name));
    }

    [Fact]
    public void Core_owned_null_owner_work_does_not_match_a_plugin_assembly()
    {
        string? coreName = MainThread.AssemblyNameTag(null, (Action)LocalAction);
        string? pluginName = typeof(GC).Assembly.FullName;
        Assert.False(string.Equals(coreName, pluginName, StringComparison.Ordinal));
        Assert.False(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null, itemSourceName: coreName, plugin: null, unloadingName: pluginName));
        Assert.True(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null, itemSourceName: coreName, plugin: null, unloadingName: coreName));
    }

    [Fact]
    public void AssemblyTag_null_owner_uses_the_supplied_delegate_not_an_unrelated_wrapper()
    {
        // MainThread.Run queues a lambda declared in MainThread, then tags from Func<T>
        // (sourceWork: func) instead of that wrapper. The two delegates can live in
        // different assemblies; the tag must follow the caller's func.
        Func<int> func = LocalFunc;
        Action wrapper = GC.Collect;
        Assert.Same(typeof(GC).Assembly, MainThread.AssemblyTag(null, wrapper));
        Assert.Same(typeof(MainThreadOwnershipTests).Assembly, MainThread.AssemblyTag(null, func));
        Assert.NotSame(MainThread.AssemblyTag(null, wrapper), MainThread.AssemblyTag(null, func));
    }

    [Fact]
    public void Delegate_context_is_null_in_the_default_load_context()
    {
        Assert.Null(MainThread.DelegateContextName((Action)LocalAction));
        Assert.Null(MainThread.DelegateContextName((Action)GC.Collect));
        Assert.Null(MainThread.DelegateContextName(null));
    }

    [Fact]
    public void Delegate_context_follows_the_delegate_even_when_owner_lives_elsewhere()
    {
        using var worker = CollectibleWorker.Load("progenitor-reload:f14:" + Guid.NewGuid().ToString("N"));
        var owner = new object();

        Assert.Same(typeof(object).Assembly, MainThread.AssemblyTag(owner, worker.Action));
        Assert.Equal(typeof(object).Assembly.FullName, MainThread.AssemblyNameTag(owner, worker.Action));
        Assert.NotEqual(typeof(object).Assembly.FullName, worker.Assembly.FullName);

        Assert.Equal(worker.ContextName, MainThread.DelegateContextName(worker.Action));
        Assert.Null(MainThread.DelegateContextName((Action)GC.Collect));
    }

    [Fact]
    public void Collectible_delegate_with_null_owner_is_tagged_by_that_assembly_full_name()
    {
        using var worker = CollectibleWorker.Load("progenitor-reload:f14:" + Guid.NewGuid().ToString("N"));
        Assert.Equal(worker.Assembly.FullName, MainThread.AssemblyNameTag(null, worker.Action));
        Assert.Equal(worker.ContextName, MainThread.DelegateContextName(worker.Action));
        Assert.True(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null,
            itemSourceName: MainThread.AssemblyNameTag(null, worker.Action),
            plugin: null,
            unloadingName: worker.Assembly.FullName));
        // Name matching is by FullName, so a second load of the same identity would also
        // match. A Core-owned (or any other-assembly) tag does not.
        Assert.False(MainThread.MatchesOwnerOrAssembly(
            itemOwner: null,
            itemSourceName: MainThread.AssemblyNameTag(null, (Action)GC.Collect),
            plugin: null,
            unloadingName: worker.Assembly.FullName));
    }

    /// <summary>
    /// Tiny collectible assembly so tests can assert ALC-name tagging without the game.
    /// </summary>
    private sealed class CollectibleWorker : IDisposable
    {
        public AssemblyLoadContext Context { get; }
        public Assembly Assembly { get; }
        public Action Action { get; }
        public string ContextName { get; }

        private CollectibleWorker(AssemblyLoadContext context, Assembly assembly, Action action, string contextName)
        {
            Context = context;
            Assembly = assembly;
            Action = action;
            ContextName = contextName;
        }

        public static CollectibleWorker Load(string contextName)
        {
            var alc = new AssemblyLoadContext(contextName, isCollectible: true);
            string path = typeof(MainThreadOwnershipTests).Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("Test assembly has no Location to load collectibly.");
            Assembly asm;
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                asm = alc.LoadFromStream(stream);
            Type type = asm.GetType(typeof(MainThreadOwnershipTests).FullName!, throwOnError: true)!;
            MethodInfo go = type.GetMethod(nameof(LocalAction), BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("LocalAction missing on collectible copy.");
            return new CollectibleWorker(
                alc,
                asm,
                (Action)go.CreateDelegate(typeof(Action)),
                contextName);
        }

        public void Dispose()
        {
            try { Context.Unload(); }
            catch { }
        }
    }
}
