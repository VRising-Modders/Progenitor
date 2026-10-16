using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Progenitor.Tests;

/// <summary>
/// A reloadable plugin for the owner-sweep tests: this test assembly loaded again into a collectible load context, so
/// its owner object and its callback come from a collectible assembly that is not the (test-compiled) Core.
/// </summary>
public sealed class UnloadingPluginStandIn : IDisposable
{
    /// <summary>The stand-in plugin type inside the collectible copy.</summary>
    public sealed class Plugin
    {
        public static void Callback() { }
    }

    private readonly AssemblyLoadContext _context;

    private UnloadingPluginStandIn(AssemblyLoadContext context, object owner, Action callback)
    {
        _context = context;
        Owner = owner;
        Callback = callback;
    }

    /// <summary>The plugin instance, from the collectible copy.</summary>
    public object Owner { get; }

    /// <summary>A callback declared in the collectible copy.</summary>
    public Action Callback { get; }

    public static UnloadingPluginStandIn Load()
    {
        var alc = new AssemblyLoadContext("progenitor-reload:standin:" + Guid.NewGuid().ToString("N"), isCollectible: true);
        string path = typeof(UnloadingPluginStandIn).Assembly.Location;
        Assembly asm;
        using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            asm = alc.LoadFromStream(stream);
        var type = asm.GetType(typeof(Plugin).FullName!, throwOnError: true)!;
        var callback = (Action)type.GetMethod(nameof(Plugin.Callback))!.CreateDelegate(typeof(Action));
        return new UnloadingPluginStandIn(alc, Activator.CreateInstance(type)!, callback);
    }

    public void Dispose()
    {
        try { _context.Unload(); }
        catch { /* */ }
    }
}
