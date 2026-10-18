using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

[Collection("PluginLogger")]
public sealed class ScopedSettingsUiTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private ModSettingsView? _view;
    private readonly Dictionary<string, IUiInput> _inputs = new();
    private readonly Dictionary<string, Func<object?>> _readbacks = new();
    public ScopedSettingsUiTests()
    {
        UiApi.ResetForTests(); UiApi.SetRuntimeForTests(_runtime);
        UiInfoHandle.Scheduler = null;
        ScopedSettings.SetForUiTests(false, false);
    }
    public void Dispose()
    {
        _view?.Release(); UiApi.ResetForTests();
        ScopedSettings.SetForUiTests(false, false);
    }
    private static ScopedSettingRecord Record(bool personal, bool editable = true) => new()
    {
        Id = personal ? "remote/General/Greeting" : "remote/General/Speed", PluginId = "remote",
        ModName = "Remote only", Version = "1.0", Section = "General", Key = personal ? "Greeting" : "Speed",
        Personal = personal, Editable = editable, Type = "string", Value = "old", Default = "default", MaxLength = 100,
    };
    private ModSettingsView Draw(params ScopedSettingRecord[] records)
    {
        ScopedSettings.SetForUiTests(true, false, records);
        _view = new ModSettingsView("remote", ScopedSettingsRows.Merge("remote", Array.Empty<ModSettingsSection>()), "",
            () => ServerPartState.Running, () => new SettingsScopeContext
            {
                Connection = SettingConnection.Progenitor, ServerPart = ServerPartState.Running, CanEditServerWide = false,
            });
        _view.Build(UiApi.Panel("Scoped settings", this, "remote"), (handle, key, extra) =>
        {
            if (handle is IUiInput input) _inputs[handle.Text] = input;
            if (extra is Func<object?> read && key == ModSettingsView.RowSetting)
                _readbacks[handle.Text] = read;
        });
        return _view;
    }
    [Fact]
    public void Every_scope_draws_and_reads_back_its_full_explanation_with_wrapping()
    {
        string name = "Moo World Copy - the full community server name that exceeds thirty-two characters";
        var personal = Record(true); var server = Record(false);
        ScopedSettings.SetForUiTests(true, false, personal, server);
        var local = ModSettingsItem.ForInfo("local", "Hints", () => "on", 1, null);
        var sections = new[] { new ModSettingsSection("General", ModSettingsSectionKind.Group,
            new List<ModSettingsItem> { local, ScopedSettingsRows.Item(personal), ScopedSettingsRows.Item(server) }) };
        _view = new ModSettingsView("remote", sections, "", null, () => new SettingsScopeContext
        {
            Connection = SettingConnection.Progenitor, ServerPart = ServerPartState.Running, ServerName = name,
        });
        var readbacks = new List<Func<object?>>();
        _view.Build(UiApi.Panel("Scoped explanations", this, "remote"), (_, key, extra) =>
        {
            if (key == ModSettingsScopes.RowScopeSection && extra is Func<object?> read) readbacks.Add(read);
        });
        var expected = new[]
        {
            "Only affects you. Saved on this device.",
            "Only affects you on " + name + ". Saved by this server.",
            "Applies to everyone on " + name,
        };
        Assert.Equal(3, readbacks.Count);
        for (int n = 0; n < expected.Length; n++)
        {
            var data = Assert.IsType<Dictionary<string, object?>>(readbacks[n]());
            Assert.Equal(expected[n], data["line"]);
            var row = _runtime.CellRow(ModSettingsScopes.ScopeLineKeyPrefix + n)!;
            Assert.Equal(expected[n], row.Cells().Single(c => c.Key == ModSettingsScopes.CellScopeLine).Text);
            var spec = ModSettingsScopes.LineRow(n, expected[n]);
            Assert.True(spec.FitWrap);
            Assert.Equal(CellWrap.Wrap, spec.Cells.Single(c => c.Key == ModSettingsScopes.CellScopeLine).Wrap);
        }
    }

    [Fact]
    public void Manifest_refreshes_existing_server_metadata_but_never_local_metadata()
    {
        var record = Record(true);
        ScopedSettings.SetForUiTests(true, false, record);
        var old = new ModMenuInventoryRow
        {
            Id = "remote", DisplayName = "remote", Version = "old", Location = "Server list",
            Requirement = "Optional", Explanation = "No loaded client copy was observed.",
        };
        var updated = Assert.Single(ScopedSettingsRows.Inventory(new[] { old }));
        Assert.Equal(record.ModName, updated.DisplayName);
        Assert.Equal(record.Version, updated.Version);
        Assert.Equal("Optional", updated.Requirement);
        Assert.Equal(ScopedSettingsRows.ServerModExplanation, updated.Explanation);
        Assert.Equal("remote", old.DisplayName); // Inventory snapshots are never mutated.
        var local = new ModMenuInventoryRow
        {
            Id = "remote", DisplayName = "Local title", Version = "local", Location = "Local + server list",
        };
        Assert.Same(local, Assert.Single(ScopedSettingsRows.Inventory(new[] { local })));
        ScopedSettings.SetForUiTests(false, false, record);
        Assert.Same(old, Assert.Single(ScopedSettingsRows.Inventory(new[] { old })));
    }

    [Fact]
    public void Valid_server_settings_replace_the_missing_client_header_word_only_for_server_rows()
    {
        var facts = new ModFrameworkFacts { OnClient = false, SharesServerSettings = true };
        Assert.Equal("Runs on this server", ModFrameworkLine.WordOf(ModRunStatus.NotOnClient, facts));
        Assert.Equal(ModFrameworkLine.WordNotOnClient, ModFrameworkLine.WordOf(ModRunStatus.NotOnClient,
            new ModFrameworkFacts { OnClient = false }));
        Assert.Equal(ModFrameworkLine.WordOf(ModRunStatus.Enabled, new ModFrameworkFacts()),
            ModFrameworkLine.WordOf(ModRunStatus.Enabled, facts));
    }

    [Fact]
    public void Groups_distinguish_personal_owners_and_server_wide()
    {
        var local = ModSettingsItem.ForInfo("local", "Hints", () => "on", 1, null);
        var groups = ModSettingsScopes.Regroup(new[] { new ModSettingsSection("General", ModSettingsSectionKind.Group,
            new List<ModSettingsItem> { ScopedSettingsRows.Item(Record(false)), local, ScopedSettingsRows.Item(Record(true)) }) });
        Assert.Equal(new[] { "Your preferences", "Your settings on this server", "Server settings" }, groups.Select(g => g.Heading));
        Assert.Equal(new[] { SettingOwner.Client, SettingOwner.Server, SettingOwner.Server }, groups.Select(g => g.Owner));
    }
    [Fact]
    public void Nonadmin_can_edit_personal_but_cannot_apply_server_wide_or_unregistered_rows()
    {
        var personal = Record(true); var server = Record(false);
        var view = Draw(personal, server);
        view.ApplyForBridge("Greeting", "mine");
        Assert.Single(ScopedSettings.Requests);
        Assert.Equal("mine", personal.Value);
        Assert.Throws<ArgumentException>(() => view.ApplyForBridge("Speed", "for everyone"));
        Assert.Single(ScopedSettings.Requests);
        Assert.Equal("old", server.Value);
    }
    [Fact]
    public void Unregistered_personal_row_is_read_only_even_for_its_owner()
    {
        var view = Draw(Record(true, editable: false));
        Assert.Throws<ArgumentException>(() => view.ApplyForBridge("Greeting", "mine"));
        Assert.Empty(ScopedSettings.Requests);
        Assert.Equal(0, view.PersonalResetCount);
    }
    [Fact]
    public void Reset_personal_only_sends_personal_rows()
    {
        var view = Draw(Record(true), Record(false));
        Assert.Equal(1, view.PersonalResetCount);
        Assert.Equal(1, view.ResetRemotePersonal());
        var request = Assert.Single(ScopedSettings.Requests);
        Assert.Equal("remote/General/Greeting", request.Id);
        Assert.Equal("old", request.Expected);
        Assert.Equal("default", request.Value);
    }
    [Fact]
    public void Draft_keeps_displayed_expected_value_when_another_update_arrives()
    {
        var personal = Record(true);
        var view = Draw(personal);
        var input = Assert.Single(_inputs).Value;
        input.Value = "draft";
        personal.Value = "new server value";
        view.Reevaluate();
        Assert.Equal("draft", input.Value);
        view.ApplyForBridge("Greeting");
        Assert.Equal("old", Assert.Single(ScopedSettings.Requests).Expected);
        Assert.Equal("new server value", personal.Value);
        Assert.Equal("new server value", input.Value);
    }
    [Fact]
    public void Personal_readback_reports_server_ownership_without_admin_requirement()
    {
        Draw(Record(true));
        var d = Assert.IsType<Dictionary<string, object?>>(Assert.Single(_readbacks).Value());
        Assert.Equal("server", d["owner"]);
        Assert.Equal("personal", d["scopeSection"]);
        Assert.Equal(false, d["adminOnly"]);
        Assert.Equal("server-player", d["store"]);
        Assert.Equal(true, d["enabled"]);
    }
    [Fact]
    public void A_disconnect_refuses_stale_control_before_page_rebuild()
    {
        var view = Draw(Record(true));
        ScopedSettings.SetForUiTests(false, false);
        view.ApplyForBridge("Greeting", "stale");
        Assert.Empty(ScopedSettings.Requests);
        Assert.Equal(0, view.PersonalResetCount);
    }
    [Fact]
    public void Full_BCL_tokens_choose_controls_and_integer_fields_do_not_default_to_zero_bounds()
    {
        var record = Record(true);
        record.Type = "System.Boolean";
        Assert.Equal(AdminSettingKind.Toggle, ScopedSettingsRows.Spec(record).Kind);
        record.Type = "System.Int32";
        var spec = ScopedSettingsRows.Spec(record);
        Assert.Equal(AdminSettingKind.Number, spec.Kind);
        Assert.Equal(int.MinValue, spec.Min);
        Assert.Equal(int.MaxValue, spec.Max);
        record.Min = "1"; record.Max = "120";
        spec = ScopedSettingsRows.Spec(record);
        Assert.Equal(1, spec.Min); Assert.Equal(120, spec.Max);
        Assert.Equal("default", spec.DefaultValue);
    }

    [Fact]
    public void Matching_manifest_preserves_authored_conditions_and_restart_baseline()
    {
        var options = new ModSettingOptions
        {
            ShowIf = () => false, EnableIf = () => false, NeedsRestart = true, LoadValue = "old",
            NeedsServerPart = true, WhenAlone = AloneBehavior.Hide, Label = "Authored label", Help = "Authored help",
            Control = SettingControl.Radio,
        };
        var record = Record(true);
        var local = ModSettingsItem.ForInfo(record.Id, "local", () => "", 1, options);
        var merged = ScopedSettingsRows.MergeItem(local, record);
        Assert.Same(options.ShowIf, merged.Options.ShowIf);
        Assert.Same(options.EnableIf, merged.Options.EnableIf);
        Assert.True(merged.Options.NeedsRestart);
        Assert.True(merged.Options.NeedsServerPart);
        Assert.Equal(AloneBehavior.Hide, merged.Options.WhenAlone);
        Assert.Equal("old", merged.Options.LoadValue);
        Assert.Equal("Authored label", merged.Admin!.Label);
        Assert.Equal("Authored help", merged.Admin.Help);
        Assert.True(merged.Admin.ChoiceRadio);
        ScopedSettings.SetForUiTests(true, false, record);
        record.Value = "new";
        Assert.True(merged.Admin.PendingNow());
    }

    [Fact]
    public void Mismatched_type_keeps_local_row_read_only_without_mutating_authored_options()
    {
        string path = Path.Combine(Path.GetTempPath(), "scoped-ui-" + Guid.NewGuid().ToString("N") + ".cfg");
        try
        {
            var config = new ConfigFile(path, false);
            var entry = config.Bind("General", "Greeting", 7);
            var options = new ModSettingOptions { Help = "Local help" };
            var row = SettingsRow.ForEntry("remote", entry, true);
            var local = ModSettingsItem.ForRow(row, options);
            var remote = Record(true); remote.Type = "System.String";
            var merged = ScopedSettingsRows.MergeItem(local, remote);
            Assert.Same(row, merged.Row);
            Assert.Null(merged.Admin);
            Assert.Equal(ScopedSettingsRows.TypeMismatchReason, merged.Options.CompatibilityReason);
            Assert.Equal("", options.CompatibilityReason);
            remote.Type = "System.Int32";
            Assert.NotNull(ScopedSettingsRows.MergeItem(local, remote).Admin);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Manifest_rows_replace_matching_local_items_and_vanish_on_disconnect()
    {
        var row = Record(true);
        ScopedSettings.SetForUiTests(true, false, row);
        var local = new[] { new ModSettingsSection("General", ModSettingsSectionKind.Group,
            new List<ModSettingsItem> { ModSettingsItem.ForInfo(row.Id, "old", () => "", 1, null) }) };
        var merged = ScopedSettingsRows.Merge("remote", local);
        Assert.Single(merged.SelectMany(s => s.Items));
        Assert.True(merged[0].Items[0].Admin!.Manifest);
        Assert.Single(ScopedSettingsRows.Inventory(Array.Empty<ModMenuInventoryRow>()));
        ScopedSettings.SetForUiTests(false, false, row); // Stale cached rows must not leak after disconnect.
        Assert.Empty(ScopedSettingsRows.Merge("remote", Array.Empty<ModSettingsSection>()));
        Assert.Empty(ScopedSettingsRows.Inventory(Array.Empty<ModMenuInventoryRow>()));
    }
}
