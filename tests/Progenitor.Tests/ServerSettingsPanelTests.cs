using System;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

[Collection("PluginLogger")]
public sealed class ServerSettingsPanelTests : IDisposable
{
    private const string Id = "example.remote";
    private const string RowId = Id + "/General/Count";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-server-panel-" + Guid.NewGuid().ToString("N"));
    private readonly object _owner = new();
    private readonly ConfigFile _file;
    private readonly ConfigEntry<int> _entry;
    private readonly IDisposable _sync;
    private readonly UiTests.FakeUiRuntime _runtime = new();

    public ServerSettingsPanelTests()
    {
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.cfg"), true);
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        HarmonySlotHandoff.SetSkipModules(null, false);
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestSend = _ => true;
        _entry = _file.Bind("General", "Count", 3, "");
        _sync = ConfigSync.Register(Id, _entry);
        SettingsCatalog.Register(Id, _file, _owner);
        SettingsCatalog.UseSyncedProbe(e => ConfigSync.IsRegistered(Id, e));
    }

    private void ReceiveServerValue() => ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord(Id, "General", "Count", "7") }, true);

    public void Dispose()
    {
        UiApi.ResetForTests();
        ServerSettings.Uninstall();
        _sync.Dispose();
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Global_panel_owner_unload_closes_remote_only_rows_and_pending_callbacks()
    {
        using var editable = ServerSettings.Register(Id, _entry);
        ReceiveServerValue();
        var panel = SettingsPanel.Open();
        Assert.Contains(RowId, SettingsPanel.BoundRowIds());
        Assert.True(_runtime.FireText("Count", "9"));
        Assert.True(_runtime.FireClick("Apply Count"));
        Assert.Equal(1, ServerSettings.PendingCount);
        UiApi.RemoveAll(_owner);
        Assert.True(panel.IsDisposed);
        Assert.False(SettingsPanel.IsOpen);
        Assert.Empty(SettingsPanel.BoundRowIds());
        Assert.Equal(0, ServerSettings.PendingCount);
        Assert.Equal(0, _runtime.Capture);
        Assert.False(_runtime.FireClick("Apply Count"));
    }

    [Fact]
    public void Synced_declaration_is_not_labelled_as_server_value_before_snapshot()
    {
        SettingsPanel.Open();
        Assert.Contains(_runtime.WidgetTexts("label"), text => text.Contains("awaiting server synchronization"));
        Assert.DoesNotContain(_runtime.WidgetTexts("label"), text => text.Contains("set by the server"));
        ReceiveServerValue();
        SettingsPanel.Open();
        Assert.Contains(_runtime.WidgetTexts("label"), text => text.Contains("= 7") && text.Contains("set by the server"));
        Assert.DoesNotContain(_runtime.WidgetTexts("label"), text => text.Contains("awaiting server synchronization"));
    }

    [Fact]
    public void Catalog_merges_registered_synced_entries_without_duplicates_before_and_after_parking()
    {
        Assert.Single(SettingsCatalog.Rows(), row => row.RowId == RowId);
        ReceiveServerValue();
        var row = Assert.Single(SettingsCatalog.Rows(), row => row.RowId == RowId);
        Assert.Equal("7", row.Read());
        Assert.True(row.IsSynced);
        var entries = ConfigHelper.GetEntries(_file);
        Assert.NotNull(entries);
        Assert.Empty(entries); // The save-protection parking remains intact.
    }

    [Fact]
    public void Source_owner_removal_hides_its_registered_settings_even_if_registration_still_exists()
    {
        ReceiveServerValue();
        Assert.Single(SettingsCatalog.Rows(), row => row.RowId == RowId);
        SettingsCatalog.RemoveAll(_owner);
        Assert.Empty(SettingsCatalog.Rows());
        Assert.True(ConfigSync.IsRegistered(Id, _entry));
    }

    [Fact]
    public void Detached_session_lock_is_not_resurrected_as_a_registered_settings_row()
    {
        ReceiveServerValue();
        _sync.Dispose();
        Assert.True(ConfigSync.IsLocked(Id, "General", "Count"));
        Assert.Empty(SettingsCatalog.Rows());
    }
}
