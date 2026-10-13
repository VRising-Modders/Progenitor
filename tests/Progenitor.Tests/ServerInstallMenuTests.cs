using System;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Dev;
using Progenitor.Install;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SERVERINST2: the server-install toggle driven through the workspace the way the Dev
/// bridge drives it (<c>mods_ui widget_click</c> → <see cref="ModsMenu.ClickVisibleWidget"/>),
/// with the recompose the pane asks for drained between clicks. Temp folders only; no
/// process is started or stopped. Shares the PluginLogger collection with ModsMenuTests
/// because both drive ModsMenu's static state.
/// </summary>
[Collection("PluginLogger")]
public sealed class ServerInstallMenuTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new() { ToggleSetterDispatches = true };
    private readonly TestLogSource _log = new();
    private readonly string _root;
    private readonly string _server;
    private readonly string _client;
    private readonly ServerInstallSource _source;

    public ServerInstallMenuTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-serverinst2-" + Guid.NewGuid().ToString("N"));
        _server = Path.Combine(_root, "server");
        _client = Path.Combine(_root, "client", "BepInEx");
        Directory.CreateDirectory(_server);
        File.WriteAllText(Path.Combine(_server, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(_server, "BepInEx", "core", "BepInEx.Core.dll"), "bepinex");
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(_client, "plugins"));
        Directory.CreateDirectory(Path.Combine(_client, "progenitor-core"));
        File.WriteAllBytes(Path.Combine(_client, "plugins", "Progenitor.Bootstrap.dll"), Encoding.UTF8.GetBytes("bootstrap-v1"));
        File.WriteAllBytes(Path.Combine(_client, "progenitor-core", "Progenitor.dll"), Encoding.UTF8.GetBytes("core-v1"));
        _source = ServerInstallSource.FromClientRoot(_client, "0.1.0");

        ServerInstall.RunningServerProbe = _ => null;
        ServerInstall.FreeSpaceOverride = null;
        ServerInstall.WriteAccessOverride = null;
        ServerInstall.ClockUtc = () => new DateTime(2026, 9, 12, 12, 30, 0, DateTimeKind.Utc);
        LocalServerFolder.WriteAccessOverride = _ => true;
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ReparseLookup = null;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigurationPane.ResetServerInstallStateForTests();
        ConfigurationPane.DefaultSource = _source;
        MainThread.ResetForTests();
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        MainThread.ResetForTests();
        ConfigurationPane.ResetServerInstallStateForTests();
        ServerInstall.RunningServerProbe = null;
        ServerInstall.FreeSpaceOverride = null;
        ServerInstall.WriteAccessOverride = null;
        ServerInstall.ClockUtc = null;
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ReparseLookup = null;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        Plugin.Logger = null;
        UiApi.ResetForTests();
        ConfigHelper.UninstallAll();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    [Fact]
    public void Widget_click_toggle_off_shows_the_prompt_rows_after_the_recompose_and_Remove_uninstalls()
    {
        OpenConfiguration();

        var toggle = Row(ConfigurationPane.ServerInstallToggle);
        Assert.Equal("false", toggle.GetProperty("value").GetString());
        Assert.Equal("not-installed", toggle.GetProperty("serverInstall").GetProperty("state").GetString());

        // Toggle on through the bridge: the recompose lands on the next drain.
        var on = Click(ConfigurationPane.ServerInstallToggle);
        Assert.Equal(1, on.GetProperty("invoked").GetInt32());
        Assert.Equal("true", on.GetProperty("value").GetString());
        MainThread.Drain();
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.True(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor.Bootstrap.dll")));
        toggle = Row(ConfigurationPane.ServerInstallToggle);
        Assert.Equal("true", toggle.GetProperty("value").GetString());
        Assert.Equal("installed", toggle.GetProperty("serverInstall").GetProperty("state").GetString());
        Assert.False(toggle.GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        string reloadFile = Path.Combine(_server, "BepInEx", "progenitor-reload", "OtherTool.dll");
        File.WriteAllText(reloadFile, "not Progenitor's bytes");

        // Toggle off: the live defect. The recreated pane must build the prompt rows.
        var off = Click(ConfigurationPane.ServerInstallToggle);
        Assert.Equal(1, off.GetProperty("invoked").GetInt32());
        Assert.Equal("false", off.GetProperty("value").GetString());
        MainThread.Drain();
        Assert.False(TryRow(ConfigurationPane.ServerInstallToggle, out _));
        var heading = Row(ConfigurationPane.ServerRemoveHeading);
        Assert.True(heading.GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        Assert.Equal("installed", heading.GetProperty("serverInstall").GetProperty("state").GetString());
        var body = RowStartingWith(ServerInstall.PromptPrefix);
        Assert.Contains("(2 files: " + ServerInstall.BootstrapRelative + ", " + ServerInstall.CoreRelative + ")", body.GetProperty("label").GetString());
        Assert.True(body.GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        Assert.True(Row(ConfigurationPane.ServerRemoveButton).GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        Assert.True(Row(ConfigurationPane.ServerCancelButton).GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));

        // Cancel: toggle back, checked, nothing removed.
        Click(ConfigurationPane.ServerCancelButton);
        MainThread.Drain();
        toggle = Row(ConfigurationPane.ServerInstallToggle);
        Assert.Equal("true", toggle.GetProperty("value").GetString());
        Assert.False(toggle.GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        Assert.False(TryRow(ConfigurationPane.ServerRemoveHeading, out _));
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));

        // Toggle off again, then Remove.
        Click(ConfigurationPane.ServerInstallToggle);
        MainThread.Drain();
        Assert.True(TryRow(ConfigurationPane.ServerRemoveButton, out _));
        Click(ConfigurationPane.ServerRemoveButton);
        MainThread.Drain();
        toggle = Row(ConfigurationPane.ServerInstallToggle);
        Assert.Equal("false", toggle.GetProperty("value").GetString());
        Assert.Equal("not-installed", toggle.GetProperty("serverInstall").GetProperty("state").GetString());
        Assert.False(toggle.GetProperty("serverInstall").GetProperty("promptOpen").GetBoolean());
        Assert.False(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor.Bootstrap.dll")));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-core")));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-pending")));
        Assert.Equal("not Progenitor's bytes", File.ReadAllText(reloadFile));
        // Revert: an instance-held prompt flag fails the heading Row after the first off click
        // (the drain rebuilds a fresh pane whose flag is false and re-checks the toggle).
    }

    [Fact]
    public void Show_on_other_servers_survives_the_recompose_that_builds_a_new_pane()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(Path.Combine(other, "BepInEx"));
        File.WriteAllText(Path.Combine(other, LocalServerFolder.ServerExecutableName), "exe");
        var sources = new FakeDiscoverySources();
        sources.Running.Add(_server);
        sources.Running.Add(other);
        ConfigurationPane.DefaultScanFactory = () => new SteamLibraryScan(new MapSteamLibraryFileSystem(), sources);
        OpenConfiguration();

        // The widget read lists the header row by its key-cap caption (Show / Hide).
        AssertCollapsed();
        Assert.Equal(0, CountRows(ConfigurationPane.UseThisFolder));
        // Order: the path field and its Validated line come before the other-servers row.
        int validated = IndexOfRowStartingWith(ConfigurationPane.ValidatedPrefix);
        // INSTALLSIDES1: "Show mods with NSFW content" also starts with "Show"; the key cap is the exact caption.
        Assert.True(validated >= 0 && validated < IndexOfRow(ConfigurationPane.ShowCaption), "Other servers row must follow the Validated line");

        // The live defect: the click recomposes into a new pane, which must still be expanded.
        var shown = Click(ConfigurationPane.ShowCaption);
        Assert.Equal(1, shown.GetProperty("invoked").GetInt32());
        MainThread.Drain();
        AssertExpanded();

        // Hide folds it again through the same path.
        Click(ConfigurationPane.HideCaption);
        MainThread.Drain();
        AssertCollapsed();

        // Open, close the workspace, reopen: collapsed again.
        Click(ConfigurationPane.ShowCaption);
        MainThread.Drain();
        Assert.Equal(1, CountRows(ConfigurationPane.UseThisFolder));
        ModsMenu.Close();
        ModsMenu.Open();
        ModsMenu.SelectTab("configuration");
        MainThread.Drain();
        AssertCollapsed();
        // Revert: an instance-held OtherServersOpen fails the first HideCaption check (the drain
        // builds a fresh pane that reads as collapsed).
    }

    private static System.Collections.Generic.List<JsonElement> Rows()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        var rows = new System.Collections.Generic.List<JsonElement>();
        foreach (var element in doc.RootElement.EnumerateArray()) rows.Add(element.Clone());
        return rows;
    }

    private static string Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int CountRows(string label)
    {
        int n = 0;
        foreach (var row in Rows())
            if (Text(row, "label") == label) n++;
        return n;
    }

    private static int IndexOfRow(string label)
    {
        var rows = Rows();
        for (int i = 0; i < rows.Count; i++)
            if (string.Equals(Text(rows[i], "label"), label, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static int IndexOfRowStartingWith(string prefix)
    {
        var rows = Rows();
        for (int i = 0; i < rows.Count; i++)
            if (Text(rows[i], "label").StartsWith(prefix, StringComparison.Ordinal)
                || (rows[i].TryGetProperty(ConfigurationPane.RowLocalStatus, out var status)
                    && Text(status, "text").StartsWith(prefix, StringComparison.Ordinal))) return i;
        return -1;
    }

    private static void AssertCollapsed()
    {
        Assert.Equal(1, CountRows(ConfigurationPane.ShowCaption));
        Assert.Equal(0, CountRows(ConfigurationPane.HideCaption));
        Assert.Equal(0, CountRows(ConfigurationPane.UseThisFolder));
    }

    private static void AssertExpanded()
    {
        Assert.Equal(0, CountRows(ConfigurationPane.ShowCaption));
        Assert.Equal(1, CountRows(ConfigurationPane.HideCaption));
        Assert.Equal(1, CountRows(ConfigurationPane.UseThisFolder));
    }

    private void OpenConfiguration()
    {
        string cfgDir = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(cfgDir);
        var file = new ConfigFile(Path.Combine(cfgDir, ConfigHelper.SharedFileName), saveOnInit: true);
        file.Bind(ModsConfigKeys.Section, ModsConfigKeys.DedicatedServerPath, "").Value = _server;
        ModsMenu.SetConfigFileForTests(file);
        ModsMenu.BindModsConfig(file);
        ModsMenu.Open();
        ModsMenu.SelectTab("configuration");
        MainThread.Drain();
        Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
    }

    private static JsonElement Click(string label)
    {
        var result = ModsMenu.ClickVisibleWidget("content", label, null, null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return doc.RootElement.Clone();
    }

    private static JsonElement Row(string label)
    {
        Assert.True(TryRow(label, out var row), "no content row labeled '" + label + "'");
        return row;
    }

    private static bool TryRow(string label, out JsonElement row)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("label", out var l) && string.Equals(l.GetString(), label, StringComparison.Ordinal))
            {
                row = element.Clone();
                return true;
            }
        }

        row = default;
        return false;
    }

    private static JsonElement RowStartingWith(string prefix)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("label", out var l) && (l.GetString() ?? "").StartsWith(prefix, StringComparison.Ordinal))
                return element.Clone();
        }

        throw new Xunit.Sdk.XunitException("no content row starting with '" + prefix + "'");
    }
}
