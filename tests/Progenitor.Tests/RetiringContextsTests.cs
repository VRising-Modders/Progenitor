using System;
using System.Linq;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

public class RetiringContextsTests
{
    public sealed class RetiredOwner { }

    [Fact]
    public void Retired_assembly_stays_tracked_until_its_last_instance_is_released()
    {
        var tracker = new RetiringContexts();
        HoldRetiredInstance(tracker);
        for (int i = 0; i < 10 && tracker.Alive().Count > 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.Empty(tracker.Alive());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HoldRetiredInstance(RetiringContexts tracker)
    {
        var context = new AssemblyLoadContext("retiring-assembly-test", isCollectible: true);
        using var stream = File.OpenRead(typeof(RetiringContextsTests).Assembly.Location);
        var assembly = context.LoadFromStream(stream);
        var instance = Activator.CreateInstance(assembly.GetType(typeof(RetiredOwner).FullName!)!);
        tracker.Track(context);
        context.Unload();
        Assert.DoesNotContain(context, AssemblyLoadContext.All);
        context = null;
        assembly = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Single(tracker.Alive());
        GC.KeepAlive(instance);
    }

    [Fact]
    public void Unloading_context_remains_tracked_after_leaving_runtime_enumeration()
    {
        var tracker = new RetiringContexts();
        var context = new AssemblyLoadContext("retiring-test", isCollectible: true);
        tracker.Track(context);
        context.Unload();
        Assert.DoesNotContain(context, AssemblyLoadContext.All);
        var entry = Assert.Single(tracker.Alive());
        Assert.Equal("retiring-test", entry.Name);
        Assert.True(entry.Reference.TrackResurrection);
        GC.KeepAlive(context);
    }

    [Fact]
    public void Tracking_does_not_keep_unloaded_context_alive()
    {
        var tracker = new RetiringContexts();
        Retire(tracker);
        for (int i = 0; i < 10 && tracker.Alive().Count > 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.Empty(tracker.Alive());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Retire(RetiringContexts tracker)
    {
        var context = new AssemblyLoadContext("collect-test", isCollectible: true);
        tracker.Track(context);
        context.Unload();
    }
}
