using System;
using System.IO;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// PluginContext is not compiled into this game-free test project. These tests lock
/// the facade contracts against the source: Events.On passes plugin id, Config.Bind
/// with synced:true throws rather than leaking a handle.
/// </summary>
public class PluginContextContractTests
{
    [Fact]
    public void Events_On_passes_ctx_Guid_as_pluginId()
    {
        var src = ReadPluginContext();
        SourcePin.AssertStatement(src, "pluginId: _ctx.Guid");
        SourcePin.AssertUncommented(src, "EventBus.On(");
        SourcePin.AssertStatement(src, "owner: _ctx.Owner");
        SourcePin.AssertStatement(src, "before: before");
        SourcePin.AssertStatement(src, "after: after");
    }

    [Fact]
    public void Config_Bind_synced_true_throws_untracked()
    {
        var src = ReadPluginContext();
        SourcePin.AssertStatement(src, "ConfigSync.ThrowIfUntrackedSyncedBind()");
        SourcePin.AssertStatement(src, "Track(ConfigSync.Register(Guid, Version, entry))");
        Assert.DoesNotContain("return ConfigSync.Bind(File, pluginId", src, StringComparison.Ordinal);
    }

    [Fact]
    public void Hooks_default_pluginId_to_ctx_Guid()
    {
        var src = ReadPluginContext();
        SourcePin.AssertUncommented(src, "WithPluginId(options)");
        SourcePin.AssertStatement(src, "PluginId = _ctx.Guid");
    }

    [Fact]
    public void Content_exposes_DefineBuff_builder_without_MaxAmount()
    {
        var src = ReadPluginContext();
        SourcePin.AssertStatement(src, "public BuffDefinitionBuilder DefineBuff(string id)");
        SourcePin.AssertUncommented(src, "PrefabRegistry.DefineBuff(id)");

        int start = src.IndexOf("public sealed class BuffDefinitionBuilder", StringComparison.Ordinal);
        Assert.True(start >= 0, "PluginContent.BuffDefinitionBuilder is missing");
        int end = src.IndexOf("partial class PluginCastles", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not bound BuffDefinitionBuilder");
        string builder = src.Substring(start, end - start);
        SourcePin.AssertUncommented(builder, "CloneOf(PrefabGUID source)");
        SourcePin.AssertUncommented(builder, "CloneOf(string spawnableName)");
        SourcePin.AssertUncommented(builder, "Named(string localizedName)");
        SourcePin.AssertUncommented(builder, "Described(string description)");
        SourcePin.AssertStatement(builder, "public BuffDefinitionBuilder RefreshLiveInstances()");
        SourcePin.AssertStatement(builder, "public IDisposable Register()");
        Assert.DoesNotContain("MaxAmount(int", builder, StringComparison.Ordinal);
    }

    [Fact]
    public void Handshake_OfferPackage_stays_as_an_obsolete_no_op_and_Track_helper_exists()
    {
        var src = ReadPluginContext();
        // 2026-10-05: kept so older plugins still bind; it offers nothing (NoServerDistributionTests pins the no-op).
        SourcePin.AssertStatement(src, "public IDisposable OfferPackage(string path)");
        SourcePin.AssertStatement(src, "_ctx.Track(handle);");
        SourcePin.AssertStatement(src, "public bool TryGetOfferedPackageState(out string sha256, out long size, out string reason)");
        SourcePin.AssertStatement(src, "public IDisposable Track(IDisposable disposable)");
    }

    [Fact]
    public void Effects_accessor_returns_PluginEffects()
    {
        var src = ReadPluginContext();
        SourcePin.AssertStatement(src, "using Progenitor.Effects;");
        SourcePin.AssertStatement(src, "public PluginEffects Effects");
        SourcePin.AssertStatement(src, "return _effects ??= new PluginEffects(this);");
    }

    /// <summary>
    /// Job FCFG13: the plugin's <c>ConfigFile</c> leaves ConfigHelper's reload registry when the
    /// context is torn down, without the plugin calling anything. The handle's own behavior is
    /// exercised in <c>ConfigHelperTests</c> (that file <em>is</em> compiled here); what this pins
    /// is that <c>Attach</c> tracks one, unconditionally, for the file it just handed the plugin.
    /// </summary>
    [Fact]
    public void Attach_tracks_a_drop_of_this_plugins_config_file()
    {
        var src = ReadPluginContext();
        var attach = SourcePin.MethodBody(src, "internal void Attach(BasePlugin plugin, Side side)");
        SourcePin.AssertStatementExactly(attach, "Track(ConfigHelper.DropWatchedFileOnUnload(_config!.File));");
    }

    /// <summary>
    /// The two properties that make the tracked drop run for <b>every</b> unload reason, including
    /// a plugin that threw in its own teardown: <c>ProgenitorPlugin.Unload</c> calls
    /// <c>OnUnload</c> inside a try and then <c>UndoAll</c> unconditionally, and the bag disposes
    /// each item in its own try, so one throwing registration cannot skip the ones after it.
    /// </summary>
    [Fact]
    public void A_plugin_that_throws_in_its_own_unload_still_reaches_the_tracked_drops()
    {
        var plugin = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs");
        var unload = SourcePin.MethodBody(plugin, "public sealed override bool Unload()");
        SourcePin.AssertStatement(unload, "try { OnUnload(ctx, reason); }");
        SourcePin.AssertStatementExactly(unload, "ctx.UndoAll();");

        var src = ReadPluginContext();
        var undoAll = SourcePin.MethodBody(src, "public void UndoAll(ManualLogSource? log)");
        SourcePin.AssertStatement(undoAll, "try { snapshot[i].Dispose(); }");
    }

    /// <summary>
    /// The backstop for the two unloads <c>UndoAll</c> never sees — a raw <c>BasePlugin</c>, and a
    /// <c>Unload</c> that threw before reaching it. Guarded like every other sweep step (the count
    /// is pinned separately by <c>ReloadHardeningTests.Every_unload_step_is_inside_a_try</c>).
    /// </summary>
    [Fact]
    public void The_reloader_drops_the_config_file_of_a_plugin_whose_UndoAll_never_ran()
    {
        var reloader = SourcePin.ReadFile("src", "Progenitor.Core", "Reload", "PluginReloader.cs");
        SourcePin.AssertStatementExactly(
            reloader,
            "try { Progenitor.Config.ConfigHelper.DropWatchedFile(plugin.Config); }");
    }

    private static string ReadPluginContext()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Plugins", "PluginContext.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("src/Progenitor.Core/Plugins/PluginContext.cs");
    }
}
