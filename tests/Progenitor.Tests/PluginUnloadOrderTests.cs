using System;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

public class PluginUnloadOrderTests
{
    [Fact]
    public void Dependent_unloads_before_its_dependency()
    {
        var nodes = new[]
        {
            new PluginUnloadOrder.Node("b", Array.Empty<string>(), loadIndex: 0),
            new PluginUnloadOrder.Node("a", new[] { "b" }, loadIndex: 1),
        };
        int[] order = PluginUnloadOrder.ReverseTopological(nodes);
        Assert.Equal(new[] { 1, 0 }, order);
    }

    [Fact]
    public void Independent_plugins_are_last_loaded_first()
    {
        var nodes = new[]
        {
            new PluginUnloadOrder.Node("first", Array.Empty<string>(), loadIndex: 0),
            new PluginUnloadOrder.Node("second", Array.Empty<string>(), loadIndex: 1),
            new PluginUnloadOrder.Node("third", Array.Empty<string>(), loadIndex: 2),
        };
        int[] order = PluginUnloadOrder.ReverseTopological(nodes);
        Assert.Equal(new[] { 2, 1, 0 }, order);
    }

    [Fact]
    public void Missing_and_bootstrap_dependencies_are_ignored()
    {
        var nodes = new[]
        {
            new PluginUnloadOrder.Node("sample", new[] { "progenitor", "missing.mod" }, loadIndex: 0),
            new PluginUnloadOrder.Node("other", Array.Empty<string>(), loadIndex: 1),
        };
        int[] order = PluginUnloadOrder.ReverseTopological(nodes);
        Assert.Equal(new[] { 1, 0 }, order);
    }

    [Fact]
    public void Chain_unloads_leaf_first()
    {
        var nodes = new[]
        {
            new PluginUnloadOrder.Node("base", Array.Empty<string>(), loadIndex: 0),
            new PluginUnloadOrder.Node("mid", new[] { "base" }, loadIndex: 1),
            new PluginUnloadOrder.Node("leaf", new[] { "mid" }, loadIndex: 2),
        };
        int[] order = PluginUnloadOrder.ReverseTopological(nodes);
        Assert.Equal(new[] { 2, 1, 0 }, order);
    }

    [Fact]
    public void Empty_and_single()
    {
        Assert.Empty(PluginUnloadOrder.ReverseTopological(Array.Empty<PluginUnloadOrder.Node>()));
        var one = new[] { new PluginUnloadOrder.Node("only", Array.Empty<string>(), 0) };
        Assert.Equal(new[] { 0 }, PluginUnloadOrder.ReverseTopological(one));
    }
}
