using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Localization;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Probe order for file-based plugin localization, expressed as a game-free helper so this
/// project can compile it without interop. Production <c>LoadForPlugin</c> calls the same method
/// with <see cref="Directory.Exists"/>.
/// </summary>
public class LocalizationProbeTests
{
    [Fact]
    public void Prefers_assembly_named_folder_over_sibling()
    {
        var nested = Path.Combine(@"C:\mods", "Progenitor.Sample", "localization");
        var sibling = Path.Combine(@"C:\mods", "localization");
        bool Exists(string path) =>
            string.Equals(path, nested, StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, sibling, StringComparison.OrdinalIgnoreCase);

        var chosen = LocalizationParser.ResolvePluginFolder(@"C:\mods", "Progenitor.Sample", Exists);
        Assert.Equal(nested, chosen);
    }

    [Fact]
    public void Falls_back_to_sibling_when_named_folder_is_missing()
    {
        var sibling = Path.Combine(@"C:\mods", "localization");
        var chosen = LocalizationParser.ResolvePluginFolder(
            @"C:\mods",
            "Progenitor.Sample",
            path => string.Equals(path, sibling, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(sibling, chosen);
    }

    [Fact]
    public void Returns_null_when_neither_folder_exists()
    {
        Assert.Null(LocalizationParser.ResolvePluginFolder(@"C:\mods", "Progenitor.Sample", _ => false));
    }

    [Fact]
    public void Returns_null_when_dll_directory_is_empty()
    {
        Assert.Null(LocalizationParser.ResolvePluginFolder("", "Progenitor.Sample", _ => true));
        Assert.Null(LocalizationParser.ResolvePluginFolder(null, "Progenitor.Sample", _ => true));
    }

    [Fact]
    public void Throws_when_exists_predicate_is_null()
    {
        Assert.Throws<ArgumentNullException>(
            () => LocalizationParser.ResolvePluginFolder(@"C:\mods", "X", null!));
    }

    [Fact]
    public void Empty_assembly_name_skips_the_named_folder()
    {
        var sibling = Path.Combine(@"C:\mods", "localization");
        var order = new List<string>();
        var chosen = LocalizationParser.ResolvePluginFolder(@"C:\mods", "", path =>
        {
            order.Add(path);
            return string.Equals(path, sibling, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(new[] { sibling }, order);
        Assert.Equal(sibling, chosen);
    }

    [Fact]
    public void Probes_named_folder_before_sibling()
    {
        var order = new List<string>();
        LocalizationParser.ResolvePluginFolder(@"C:\mods", "Foo", path =>
        {
            order.Add(path);
            return false;
        });
        Assert.Equal(new[]
        {
            Path.Combine(@"C:\mods", "Foo", "localization"),
            Path.Combine(@"C:\mods", "localization"),
        }, order);
    }
}
