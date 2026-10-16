using System;
using System.Collections.Generic;
using Progenitor.Reload;
using Xunit;

namespace Progenitor.Tests;

public sealed class PluginPinHoldersTests
{
    // PluginThing stands in for every type of an unloaded plugin; the holder statics stand in for Core.
    public sealed class PluginThing { public void Handle() { } }

    sealed class Wrapper { public object? Inner; }

    static class Holder
    {
        internal static readonly List<object> Items = new();
        internal static Action? Callback;
        internal static readonly Dictionary<string, object> ByKey = new();
        internal static Type? RememberedType;
        internal static List<PluginThing>? Typed;
    }

    static class CleanHolder
    {
        internal static readonly List<object> Values = new() { "text", 3, new object(), new Wrapper() };
        internal static Action? Core = () => { };
    }

    static bool IsPlugin(Type t) => t == typeof(PluginThing);

    [Fact]
    public void Walk_names_each_path_into_the_plugin()
    {
        var lines = new List<string>();
        var plugin = new PluginThing();
        Holder.Items.Add(plugin);
        Holder.Callback = plugin.Handle;
        Holder.ByKey["x"] = new Wrapper { Inner = plugin };
        Holder.RememberedType = typeof(PluginThing);
        Holder.Typed = new List<PluginThing>();
        try
        {
            int hits = PluginPinHolders.Walk(new[] { typeof(Holder) }, IsPlugin, null, lines.Add, 5000, mayRunStaticInit: _ => true);
            Assert.Equal(5, hits);
            Assert.Contains(lines, l => l.Contains("Holder.Items._items[0] -> ") && l.EndsWith("PluginThing"));
            Assert.Contains(lines, l => l.Contains("Holder.Callback -> delegate ") && l.EndsWith("PluginThing.Handle"));
            Assert.Contains(lines, l => l.Contains("Holder.ByKey.") && l.Contains(".Inner -> "));
            Assert.Contains(lines, l => l.Contains("Holder.RememberedType -> type "));
            Assert.Contains(lines, l => l.Contains("Holder.Typed -> ")); // a generic over a plugin type pins it too
            Assert.StartsWith("pin-diag plugin-holders found=5", lines[^1]);
        }
        finally
        {
            Holder.Items.Clear();
            Holder.ByKey.Clear();
            Holder.Callback = null;
            Holder.RememberedType = null;
            Holder.Typed = null;
        }
    }

    [Fact]
    public void Walk_reports_nothing_when_no_plugin_value_is_reachable()
    {
        var lines = new List<string>();
        int hits = PluginPinHolders.Walk(new[] { typeof(CleanHolder) }, IsPlugin, null, lines.Add, 5000, mayRunStaticInit: _ => true);
        Assert.Equal(0, hits);
        Assert.Single(lines);
        Assert.StartsWith("pin-diag plugin-holders found=0", lines[0]);
    }

    static class InitProbe { internal static bool Ran; }

    static class NotYetInitialized
    {
        internal static readonly object Value = Touch();
        static object Touch() { InitProbe.Ran = true; return new object(); }
    }

    [Fact]
    public void Walk_never_runs_a_static_initializer_outside_the_registry_namespaces()
    {
        var lines = new List<string>();
        PluginPinHolders.Walk(new[] { typeof(NotYetInitialized) }, IsPlugin, null, lines.Add, 5000);
        Assert.False(InitProbe.Ran);
        Assert.False(PluginPinHolders.ShouldReadStatics(typeof(NotYetInitialized)));
        Assert.True(PluginPinHolders.ShouldReadStatics(typeof(PluginPinHolders))); // Progenitor.Reload: a registry namespace
        Assert.True(PluginPinHolders.IsRegistryNamespace(typeof(Progenitor.Ui.VanillaPanelTabs)));
        Assert.False(PluginPinHolders.IsRegistryNamespace(typeof(PluginPinHoldersTests)));
    }

    [Fact]
    public void Diagnose_is_silent_when_no_unloaded_plugin_context_is_alive()
    {
        var lines = new List<string>();
        PluginPinHolders.Clear();
        // A type from a non-collectible context (a chainloader plugin) is never recorded.
        PluginPinHolders.NoteUnloaded(typeof(PluginThing), "test");
        PluginPinHolders.NoteUnloaded(null, null);
        Assert.Equal(0, PluginPinHolders.AliveCount);
        PluginPinHolders.Diagnose(lines.Add, 0);
        Assert.Empty(lines);
    }
}
