using System;
using System.IO;
using System.Reflection;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Step C layout and the plugin-ALC Resolving map. Paths are string
/// combiners; the resolver returns the assemblies it is given and ignores other simple names.
/// </summary>
public class CoreLayoutTests
{
    [Fact]
    public void DllPath_is_in_the_single_folder_home_when_nothing_is_installed()
    {
        var root = Path.Combine(Path.GetTempPath(), "progenitor-corelayout-" + Guid.NewGuid().ToString("N"), "BepInEx");
        var home = Path.Combine(root, "plugins", "Progenitor", "progenitor-core");
        Assert.Equal(home, CoreLayout.Folder(root));
        Assert.Equal(Path.Combine(home, "Progenitor.core"), CoreLayout.DllPath(root));
        Assert.Equal("Progenitor.dll", CoreLayout.FileName);
        Assert.Equal("Progenitor.core", CoreLayout.ImageFileName);
        Assert.Equal("Progenitor", CoreLayout.AssemblySimpleName);
        Assert.Equal("Progenitor.Runtime", CoreLayout.RuntimeTypeName);
        Assert.Equal("Install", CoreLayout.InstallMethod);
        Assert.Equal("Uninstall", CoreLayout.UninstallMethod);
        Assert.Equal("ApplyKeyPlaceholdersAfterPlugins", CoreLayout.ApplyPlaceholdersMethod);
        Assert.Equal("DiagnosePins", CoreLayout.DiagnosePinsMethod);
        // R31 finding 13 (job FRLD3) made it 3; job FHOOK3 made it 4 by widening the four
        // system-update slot delegates to carry Core's call token (see CoreLayout's remarks).
        Assert.Equal(5, CoreLayout.ProtocolVersion);
        Assert.Equal("Progenitor.BootstrapLink", CoreLayout.ProtocolTypeName);
        Assert.Equal("ProtocolVersion", CoreLayout.ProtocolFieldName);
    }

    [Fact]
    public void Folder_rejects_a_missing_root()
    {
        Assert.Throws<ArgumentException>(() => CoreLayout.Folder(""));
        Assert.Throws<ArgumentException>(() => CoreLayout.Folder(null!));
    }

    [Fact]
    public void MissingLog_keeps_the_grep_prefix_and_lists_every_path_tried()
    {
        var root = @"C:\Fake\BepInEx";
        var home = Progenitor.SelfUpdate.InstallLayout.Resolve(root, @"C:\Fake\BepInEx\plugins\Progenitor", false, _ => false);
        string line = Progenitor.SelfUpdate.InstallLayout.FormatMissing(home);
        Assert.StartsWith(CoreLayout.MissingLogPrefix, line);
        Assert.EndsWith("Bootstrap will not load plugins.", line);
        Assert.Contains(Path.Combine(root, "plugins", "Progenitor", "progenitor-core", "Progenitor.core"), line);
        Assert.Contains(Path.Combine(root, "progenitor-core", "Progenitor.core"), line);
        Assert.Contains(Path.Combine(root, "progenitor-core", "Progenitor.dll"), line);
    }

    [Theory]
    [InlineData(AllowDecision.Listed, AllowDecision.Unlisted, AllowDecision.Listed)]
    [InlineData(AllowDecision.Unlisted, AllowDecision.Listed, AllowDecision.Listed)]
    [InlineData(AllowDecision.Unlisted, AllowDecision.Unlisted, AllowDecision.Unlisted)]
    [InlineData(AllowDecision.NoList, AllowDecision.NoList, AllowDecision.NoList)]
    [InlineData(AllowDecision.Malformed, AllowDecision.Malformed, AllowDecision.Malformed)]
    public void A_Core_row_under_either_name_lists_Core(AllowDecision legacyRow, AllowDecision imageRow, AllowDecision expected)
    {
        AllowDecision Decide(string name, string _) =>
            name == CoreLayout.FileName ? legacyRow : name == CoreLayout.ImageFileName ? imageRow : AllowDecision.Unlisted;
        Assert.Equal(expected, CoreLayout.DecideCoreRow(Decide, CoreLayout.FileName, "00"));
    }

    [Fact]
    public void Resolver_returns_Core_and_Bootstrap_by_simple_name()
    {
        var core = typeof(CoreLayoutTests).Assembly;
        var bootstrap = typeof(CoreLayout).Assembly;
        Assert.Same(core, PluginAssemblyResolver.Resolve("Progenitor", core, bootstrap));
        Assert.Same(bootstrap, PluginAssemblyResolver.Resolve("Progenitor.Bootstrap", core, bootstrap));
        Assert.Null(PluginAssemblyResolver.Resolve("BepInEx.Core", core, bootstrap));
        Assert.Null(PluginAssemblyResolver.Resolve("Progenitor", core: null, bootstrap));
        Assert.Null(PluginAssemblyResolver.Resolve("", core, bootstrap));
        Assert.Null(PluginAssemblyResolver.Resolve(null, core, bootstrap));
    }

    [Fact]
    public void Resolver_does_not_bind_on_version_or_partial_name()
    {
        var core = typeof(CoreLayoutTests).Assembly;
        Assert.Null(PluginAssemblyResolver.Resolve("Progenitor.Core", core, bootstrap: null));
        Assert.Null(PluginAssemblyResolver.Resolve("progenitor", core, bootstrap: null));
        Assert.Equal("Progenitor", new AssemblyName("Progenitor, Version=0.1.0.0").Name);
        Assert.Same(core, PluginAssemblyResolver.Resolve(new AssemblyName("Progenitor, Version=9.9.9.9").Name, core, bootstrap: null));
    }
}
