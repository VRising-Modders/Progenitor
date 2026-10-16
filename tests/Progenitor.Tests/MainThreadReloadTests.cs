using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using Progenitor.Bootstrap;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Evidence 53: a Core-swap reload of Dev reuses the collectible ALC name
/// (<c>progenitor-reload:Dev:{ticks}</c>). DropOwnedBy of generation 1 must not refuse
/// posts from generation 2. Serialized with the MainThread queue tests.
/// </summary>
[Collection("MainThreadQueue")]
public class MainThreadReloadTests
{
    private static readonly object Gate = new();

    public sealed class ReloadOwner { }

    sealed class Payload { internal int Runs; internal void Run() => Runs++; }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static WeakReference PostOne()
    {
        var p = new Payload();
        MainThread.Post(p.Run);
        return new WeakReference(p);
    }

    /// <summary>PINS1: describing the queue for the pin dump must not keep work that already ran referenced.</summary>
    [Fact]
    public void Describing_the_queue_does_not_keep_drained_work_alive()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
            var weak = PostOne();
            Assert.Contains("queuedTotal=1", MainThread.DescribeOwned("progenitor-core", "x"));
            MainThread.Drain();
            for (int i = 0; i < 3 && weak.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(weak.IsAlive);
            MainThread.ResetForTests();
        }
    }

    [Fact]
    public void Reload_same_alc_name_new_owner_token_posts_run()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
            string name = "progenitor-reload:co:" + Guid.NewGuid().ToString("N");
            int ran = 0;
            try
            {
                using (var first = CollectibleWorker.Load(name))
                {
                    object ownerA = first.CreateOwner();
                    MainThread.Post(() => ran++, ownerA);
                    MainThread.DropOwnedBy(ownerA, first.Assembly, first.ContextName);
                    MainThread.Drain();
                    Assert.Equal(0, ran);

                    MainThread.Post(() => ran++, ownerA);
                    MainThread.Drain();
                    Assert.Equal(0, ran);
                }

                using (var second = CollectibleWorker.Load(name))
                {
                    object ownerB = second.CreateOwner();
                    MainThread.Post(() => ran++, ownerB);
                    MainThread.Drain();
                    Assert.Equal(1, ran);
                }
            }
            finally
            {
                MainThread.ResetForTests();
            }
        }
    }

    [Fact]
    public async Task Reload_same_alc_name_new_owner_token_run_completes()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
        }

        string name = "progenitor-reload:co:" + Guid.NewGuid().ToString("N");
        try
        {
            Task<int> dropped;
            using (var first = CollectibleWorker.Load(name))
            {
                object ownerA = first.CreateOwner();
                lock (Gate)
                {
                    dropped = MainThread.Run(() => 1, ownerA);
                    MainThread.DropOwnedBy(ownerA, first.Assembly, first.ContextName);
                }

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => dropped);
                Assert.Equal(MainThread.DroppedBecauseUnloading, ex.Message);
            }

            using (var second = CollectibleWorker.Load(name))
            {
                object ownerB = second.CreateOwner();
                Task<int> next;
                lock (Gate)
                {
                    next = MainThread.Run(() => 7, ownerB);
                    MainThread.Drain();
                }

                Assert.Equal(7, await next);
            }
        }
        finally
        {
            lock (Gate) MainThread.ResetForTests();
        }
    }

    /// <summary>
    /// Tiny collectible assembly so tests can reload the same ALC name without the game.
    /// </summary>
    private sealed class CollectibleWorker : IDisposable
    {
        public AssemblyLoadContext Context { get; }
        public Assembly Assembly { get; }
        public string ContextName { get; }

        private CollectibleWorker(AssemblyLoadContext context, Assembly assembly, string contextName)
        {
            Context = context;
            Assembly = assembly;
            ContextName = contextName;
        }

        public static CollectibleWorker Load(string contextName)
        {
            var alc = new AssemblyLoadContext(contextName, isCollectible: true);
            string path = typeof(MainThreadReloadTests).Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("Test assembly has no Location to load collectibly.");
            Assembly asm;
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                asm = alc.LoadFromStream(stream);
            return new CollectibleWorker(alc, asm, contextName);
        }

        public object CreateOwner()
        {
            Type type = Assembly.GetType(typeof(ReloadOwner).FullName!, throwOnError: true)!;
            return Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("ReloadOwner missing on collectible copy.");
        }

        public void Dispose()
        {
            try { Context.Unload(); }
            catch { }
        }
    }
}

/// <summary>
/// Production WorkQueue: same reload token rule, plus IMainThreadDropTarget composition
/// so a DeferredResult completes when dropped (R13 finding 6 / evidence 53).
/// </summary>
[Collection("MainThreadQueue")]
public class WorkQueueReloadTests
{
    private static readonly object Gate = new();

    public sealed class ReloadOwner { }

    [Fact]
    public void Reload_same_alc_name_new_owner_token_posts_run()
    {
        lock (Gate)
        {
            WorkQueue.ResetForTests();
            string name = "progenitor-reload:wq:" + Guid.NewGuid().ToString("N");
            int ran = 0;
            try
            {
                using (var first = CollectibleWorker.Load(name))
                {
                    object ownerA = first.CreateOwner();
                    WorkQueue.Post(() => ran++, ownerA);
                    WorkQueue.DropOwnedBy(ownerA, first.Assembly, first.ContextName);
                    WorkQueue.Drain();
                    Assert.Equal(0, ran);

                    WorkQueue.Post(() => ran++, ownerA);
                    WorkQueue.Drain();
                    Assert.Equal(0, ran);
                }

                using (var second = CollectibleWorker.Load(name))
                {
                    object ownerB = second.CreateOwner();
                    WorkQueue.Post(() => ran++, ownerB);
                    WorkQueue.Drain();
                    Assert.Equal(1, ran);
                }
            }
            finally
            {
                WorkQueue.ResetForTests();
            }
        }
    }

    [Fact]
    public void DropOwnedBy_drops_delayed_posts_whose_delegate_is_Core_even_when_owner_is_not()
    {
        lock (Gate)
        {
            WorkQueue.ResetForTests();
            MainThread.ResetForTests();
            int workQueueRan = 0;
            int leftoverRan = 0;
            try
            {
                var foreign = new object();
                WorkQueue.PostDelayed(0, () => workQueueRan++, foreign);
                MainThread.PostDelayed(0, () => leftoverRan++, foreign);
                WorkQueue.DropOwnedBy(null, typeof(MainThreadReloadTests).Assembly);
                MainThread.DropOwnedBy(null, typeof(MainThreadReloadTests).Assembly);
                WorkQueue.Drain();
                MainThread.Drain();
                Assert.Equal(0, workQueueRan);
                Assert.Equal(0, leftoverRan);
            }
            finally
            {
                WorkQueue.ResetForTests();
                MainThread.ResetForTests();
            }
        }
    }

    [Fact]
    public async Task Drop_composes_IMainThreadDropTarget_on_a_deferred_result()
    {
        lock (Gate)
        {
            WorkQueue.ResetForTests();
        }

        try
        {
            var deferred = new DeferredResult(30_000);
            lock (Gate)
            {
                WorkQueue.PostDelayed(30, () => deferred.SetResult("late"), deferred);
                Assert.False(deferred.Completion.Task.IsCompleted);
                WorkQueue.DropOwnedBy(deferred, deferred.GetType().Assembly);
            }

            var dropped = await Assert.ThrowsAsync<InvalidOperationException>(() => deferred.Completion.Task);
            Assert.Equal(MainThread.DroppedBecauseUnloading, dropped.Message);
        }
        finally
        {
            lock (Gate) WorkQueue.ResetForTests();
            DeferredResult.AbortAll(new InvalidOperationException("test teardown"));
        }
    }

    private sealed class CollectibleWorker : IDisposable
    {
        public AssemblyLoadContext Context { get; }
        public Assembly Assembly { get; }
        public string ContextName { get; }

        private CollectibleWorker(AssemblyLoadContext context, Assembly assembly, string contextName)
        {
            Context = context;
            Assembly = assembly;
            ContextName = contextName;
        }

        public static CollectibleWorker Load(string contextName)
        {
            var alc = new AssemblyLoadContext(contextName, isCollectible: true);
            string path = typeof(WorkQueueReloadTests).Assembly.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new InvalidOperationException("Test assembly has no Location to load collectibly.");
            Assembly asm;
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
                asm = alc.LoadFromStream(stream);
            return new CollectibleWorker(alc, asm, contextName);
        }

        public object CreateOwner()
        {
            Type type = Assembly.GetType(typeof(ReloadOwner).FullName!, throwOnError: true)!;
            return Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("ReloadOwner missing on collectible copy.");
        }

        public void Dispose()
        {
            try { Context.Unload(); }
            catch { }
        }
    }
}
