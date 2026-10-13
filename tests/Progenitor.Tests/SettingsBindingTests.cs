using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

[Collection("PluginLogger")]
public sealed class SettingsBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-settings-binding-" + Guid.NewGuid().ToString("N"));
    private readonly object _owner = new();
    private readonly ConfigFile _file;
    private byte[]? _sent;

    public SettingsBindingTests()
    {
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.cfg"), true);
        UiApi.ResetForTests();
        HarmonySlotHandoff.SetSkipModules(null, false);
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestSend = bytes => { _sent = bytes; return true; };
        SettingsCatalog.UseSyncedProbe(e => ConfigSync.IsRegistered("example.mod", e));
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        ServerSettings.Uninstall();
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Foreign_entry_write_is_refused_and_the_stored_value_does_not_change()
    {
        var entry = _file.Bind("Sample", "Greeting", "hello", "");
        SettingsCatalog.Register("other.mod", _file, _owner, foreign: true);

        var result = SettingsCatalog.Write("other.mod/Sample/Greeting", "bye");
        Assert.Equal(SettingWriteState.Refused, result.State);
        Assert.Contains("another mod", result.Error);
        Assert.Equal("hello", entry.Value);

        using var binding = SettingsCatalog.RegisterSetting(
            "other.mod", entry, owner: _owner, foreign: true);
        var bound = binding.Commit("bye");
        Assert.Equal(SettingWriteState.Refused, bound.State);
        Assert.Equal("hello", entry.Value);
        Assert.Equal("hello", binding.CommittedValue);
    }

    [Fact]
    public void Server_owned_commit_is_pending_then_accepted_without_writing_the_local_file()
    {
        var entry = _file.Bind("General", "Count", 3, "");
        ConfigSync.Register("example.mod", entry);
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord("example.mod", "General", "Count", "7") }, true);
        using var binding = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { AllowServerEdits = true },
            _owner);

        int saves = _file.SaveCount;
        var pending = binding.Commit("9");
        Assert.Equal(SettingWriteState.Pending, pending.State);
        Assert.Equal(7, entry.Value);
        Assert.Equal(saves, _file.SaveCount);
        Assert.Equal(1, ServerSettings.PendingCount);

        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        ServerSettings.AcceptResponse(request.WithResult(new ServerSettingResult(true, "9", "")));
        Assert.Equal(SettingWriteState.Accepted, binding.State);
        Assert.Equal(7, entry.Value);
        Assert.Equal(saves, _file.SaveCount);
        Assert.Equal(0, ServerSettings.PendingCount);
    }

    [Fact]
    public void Server_owned_commit_is_refused_or_failed_without_changing_the_stored_value()
    {
        var entry = _file.Bind("General", "Count", 3, "");
        ConfigSync.Register("example.mod", entry);
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord("example.mod", "General", "Count", "7") }, true);
        using var binding = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { AllowServerEdits = true },
            _owner);

        var pending = binding.Commit("9");
        Assert.Equal(SettingWriteState.Pending, pending.State);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        ServerSettings.AcceptResponse(request.WithResult(new ServerSettingResult(false, "7", "Only server administrators can change this setting.")));
        Assert.Equal(SettingWriteState.Refused, binding.State);
        Assert.Equal(7, entry.Value);

        ServerSettings.TestSend = _ => false;
        var failed = binding.Commit("11");
        Assert.Equal(SettingWriteState.Failed, failed.State);
        Assert.Equal(7, entry.Value);
    }

    [Fact]
    public void ValueChanged_fires_exactly_once_when_the_stored_value_moves()
    {
        var entry = _file.Bind("Sample", "Enabled", false, "");
        using var binding = SettingsCatalog.RegisterSetting("example.mod", entry, owner: _owner);
        int calls = 0;
        string last = "";
        binding.ValueChanged += d =>
        {
            calls++;
            last = d.CurrentValue;
        };

        Assert.Equal(SettingWriteState.Accepted, binding.Commit("true").State);
        Assert.True(entry.Value);
        Assert.Equal(SettingWriteState.Accepted, binding.Commit("true").State);
        Assert.True(entry.Value);
        Assert.Equal(1, calls);
        Assert.Equal("true", last);
    }

    [Fact]
    public void Escape_and_Cancel_discard_uncommitted_edits_and_leave_applied_writes_in_place()
    {
        var entry = _file.Bind("Sample", "Count", 3, "");
        using var binding = SettingsCatalog.RegisterSetting("example.mod", entry, owner: _owner);

        Assert.Equal(SettingWriteState.Preview, binding.Preview("9").State);
        Assert.Equal(3, entry.Value);
        Assert.True(binding.HasUncommittedEdit);
        Assert.Equal("9", binding.DisplayedValue);

        Assert.Equal(SettingWriteState.Idle, binding.Escape().State);
        Assert.Equal(3, entry.Value);
        Assert.False(binding.HasUncommittedEdit);

        binding.Preview("8");
        Assert.Equal(SettingWriteState.Idle, binding.Cancel().State);
        Assert.Equal(3, entry.Value);

        Assert.Equal(SettingWriteState.Accepted, binding.Commit("12").State);
        Assert.Equal(12, entry.Value);
        SettingsPanel.Close();
        Assert.Equal(12, entry.Value);
    }

    [Fact]
    public void Reset_goes_through_the_same_validation_and_permissions_as_any_other_write()
    {
        var entry = _file.Bind("Sample", "Title", "ok", "");
        using var binding = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { DefaultValue = "toolong", MaxLength = 3 },
            _owner);

        var refused = binding.Reset();
        Assert.Equal(SettingWriteState.Refused, refused.State);
        Assert.Contains("longer than 3", refused.Error);
        Assert.Equal("ok", entry.Value);

        var foreign = _file.Bind("Sample", "Greeting", "hello", "");
        using var foreignBinding = SettingsCatalog.RegisterSetting(
            "other.mod", foreign, owner: _owner, foreign: true);
        var blocked = foreignBinding.Reset();
        Assert.Equal(SettingWriteState.Refused, blocked.State);
        Assert.Equal("hello", foreign.Value);

        var count = _file.Bind("Sample", "Count", 3, "");
        using var local = SettingsCatalog.RegisterSetting("example.mod", count, owner: _owner);
        Assert.Equal(SettingWriteState.Accepted, local.Commit("9").State);
        Assert.Equal(9, count.Value);
        Assert.Equal(SettingWriteState.Accepted, local.Reset().State);
        Assert.Equal(3, count.Value);
    }

    [Fact]
    public void A_stale_completion_from_a_previous_generation_is_rejected_after_re_registration()
    {
        var entry = _file.Bind("General", "Count", 3, "");
        ConfigSync.Register("example.mod", entry);
        ConfigSync.ApplyRecords(new[] { new ConfigSyncRecord("example.mod", "General", "Count", "7") }, true);

        var first = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { AllowServerEdits = true },
            _owner);
        int firstCalls = 0;
        first.ValueChanged += _ => firstCalls++;
        Assert.Equal(SettingWriteState.Pending, first.Commit("9").State);
        Assert.True(ServerSettingsWire.TryDecode(_sent!, false, out var request));
        int firstGeneration = first.Generation;
        first.Dispose();
        Assert.True(first.IsDisposed);

        using var second = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { AllowServerEdits = true },
            _owner);
        int secondCalls = 0;
        second.ValueChanged += _ => secondCalls++;
        Assert.NotEqual(firstGeneration, second.Generation);
        Assert.Equal(SettingWriteState.Idle, second.State);

        ServerSettings.AcceptResponse(request.WithResult(new ServerSettingResult(true, "9", "")));
        Assert.Equal(0, firstCalls);
        Assert.Equal(0, secondCalls);
        Assert.Equal(SettingWriteState.Idle, second.State);
        Assert.Equal(7, entry.Value);
    }

    [Fact]
    public void Slider_preview_does_not_write_and_commit_on_release_writes_once()
    {
        var entry = _file.Bind("Sample", "Volume", 1.0f, "");
        using var binding = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { EditorHint = SettingsEditorHint.Slider },
            _owner);

        Assert.Equal(SettingCommitPolicy.OnRelease, binding.Describe().CommitPolicy);
        int saves = _file.SaveCount;
        Assert.Equal(SettingWriteState.Preview, binding.Preview("0.25").State);
        Assert.Equal(SettingWriteState.Preview, binding.Preview("0.5").State);
        Assert.Equal(1.0f, entry.Value);
        Assert.Equal(saves, _file.SaveCount);

        Assert.Equal(SettingWriteState.Accepted, binding.Commit().State);
        Assert.Equal(0.5f, entry.Value);
        Assert.Equal(saves + 1, _file.SaveCount);
    }

    [Fact]
    public void Editor_hints_cover_all_eight_controls_and_the_descriptor_carries_no_delegates()
    {
        Assert.Equal(SettingsEditorHint.Toggle, SettingsEditors.HintFor(typeof(bool)));
        Assert.Equal(SettingsEditorHint.Numeric, SettingsEditors.HintFor(typeof(int)));
        Assert.Equal(SettingsEditorHint.Numeric, SettingsEditors.HintFor(typeof(float)));
        Assert.Equal(SettingsEditorHint.Dropdown, SettingsEditors.HintFor(typeof(DayOfWeek)));
        Assert.Equal(SettingsEditorHint.Text, SettingsEditors.HintFor(typeof(string)));
        Assert.Equal(SettingsEditorHint.Slider, SettingsEditors.HintFor(typeof(float), SettingsEditorHint.Slider));
        Assert.Equal(SettingsEditorHint.Color, SettingsEditors.HintFor(typeof(string), SettingsEditorHint.Color));
        Assert.Equal(SettingsEditorHint.Keybind, SettingsEditors.HintFor(null, SettingsEditorHint.Keybind));
        Assert.Equal(SettingsEditorHint.Action, SettingsEditors.HintFor(null, SettingsEditorHint.Action));

        Assert.Equal(SettingCommitPolicy.Immediate, SettingsEditors.CommitPolicyFor(SettingsEditorHint.Toggle));
        Assert.Equal(SettingCommitPolicy.Immediate, SettingsEditors.CommitPolicyFor(SettingsEditorHint.Dropdown));
        Assert.Equal(SettingCommitPolicy.OnSubmit, SettingsEditors.CommitPolicyFor(SettingsEditorHint.Text));
        Assert.Equal(SettingCommitPolicy.OnSubmit, SettingsEditors.CommitPolicyFor(SettingsEditorHint.Numeric));
        Assert.Equal(SettingCommitPolicy.OnRelease, SettingsEditors.CommitPolicyFor(SettingsEditorHint.Slider));

        Assert.True(SettingsEditors.TryParseColor("#336699", false, out string hex, out _));
        Assert.Equal("#336699", hex);

        var enabled = _file.Bind("Sample", "Enabled", true, "");
        using var toggle = SettingsCatalog.RegisterSetting("example.mod", enabled, owner: _owner);
        var descriptor = toggle.Describe();
        Assert.Equal(SettingsEditorHint.Toggle, descriptor.EditorHint);
        Assert.Equal(SettingCommitPolicy.Immediate, descriptor.CommitPolicy);
        Assert.Contains("\"pluginId\":\"example.mod\"", descriptor.ToJson());
        foreach (var property in typeof(SettingDescriptor).GetProperties())
            Assert.False(typeof(Delegate).IsAssignableFrom(property.PropertyType), property.Name);
        foreach (var property in typeof(SettingActionDescriptor).GetProperties())
            Assert.False(typeof(Delegate).IsAssignableFrom(property.PropertyType), property.Name);
    }

    [Fact]
    public void Independently_registered_commands_are_not_actions_until_the_adapter_is_deliberate()
    {
        Assert.False(SettingsCatalog.TryGetAction("example.mod/Actions/ping", out _));
        int runs = 0;
        SettingActionResult Ping(IReadOnlyList<string> args)
        {
            runs++;
            return SettingActionResult.Ok("pong");
        }

        using var action = SettingsCatalog.RegisterAction("example.mod", "ping", Ping, new SettingActionSpec { Label = "Ping" }, _owner);
        Assert.True(SettingsCatalog.TryGetAction("example.mod/Actions/ping", out var described));
        Assert.Equal("Ping", described.Label);
        Assert.True(described.Available);

        var busy = default(SettingActionResult);
        using var held = SettingsCatalog.RegisterAction(
            "example.mod",
            "hold",
            _ =>
            {
                busy = SettingsCatalog.InvokeAction("example.mod/Actions/hold");
                return SettingActionResult.Ok("done");
            },
            owner: _owner);
        var first = SettingsCatalog.InvokeAction("example.mod/Actions/hold");
        Assert.True(first.Completed);
        Assert.Equal(SettingActionOutcome.Busy, busy.Outcome);

        Assert.True(SettingsCatalog.InvokeAction("example.mod/Actions/ping").Completed);
        Assert.Equal(1, runs);

        action.Dispose();
        Assert.Equal(SettingActionOutcome.Unavailable, SettingsCatalog.InvokeAction("example.mod/Actions/ping").Outcome);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Snapshot_and_row_descriptor_use_the_wrapped_entry_as_source_of_truth()
    {
        var entry = _file.Bind("Sample", "Mode", DayOfWeek.Monday, "");
        using var binding = SettingsCatalog.RegisterSetting(
            "example.mod",
            entry,
            new SettingPresentation { EditorHint = SettingsEditorHint.Dropdown, Group = "Display", Order = 4 },
            _owner);

        Assert.Equal(SettingWriteState.Accepted, binding.Commit("Friday").State);
        Assert.Equal(DayOfWeek.Friday, entry.Value);
        var snapshot = Assert.Single(SettingsCatalog.SnapshotDescriptors(), d => d.Key == "Mode");
        Assert.Equal("Friday", snapshot.CurrentValue);
        Assert.Equal("Monday", snapshot.DefaultValue);
        Assert.Equal(SettingsEditorHint.Dropdown, snapshot.EditorHint);
        Assert.Equal("Display", snapshot.Group);
        Assert.Equal(4, snapshot.Order);
        Assert.Contains("Monday", snapshot.AllowedValues);

        var row = Assert.Single(SettingsCatalog.Rows(), r => r.Key == "Mode");
        Assert.Equal(SettingsEditorHint.Dropdown, row.EditorHint);
        Assert.Equal("Friday", row.ToDescriptor().CurrentValue);
    }
}
