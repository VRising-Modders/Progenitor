using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SETTINGS1 stage 2 (docs/mod-settings.md): the settings page builder (<c>ctx.Settings.Page</c>),
/// the stage 2 controls (multi-line text, radio, multi-select, info line, divider, confirmed button),
/// the common options (help, show-if / enable-if, needs-restart, needs-server-part, scope), the page
/// redesign (status bar, settings grid, display names, segmented pickers) and the readback a live test
/// reads.
///
/// <para>Game-free: the fake UI runtime and the <c>ConfigFile</c> shim. Nothing here touches the game;
/// the live look is covered by the stage 2 acceptance procedure in the doc.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class ModSettingsStage2Tests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private JsonDocument? _rows;

    public ModSettingsStage2Tests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(_ => false);
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        UiInfoHandle.Scheduler = null;
        UiSliderEntryHandle.SettleScheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-settings2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        UiInfoHandle.Scheduler = null;
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ServerSettings.Uninstall();
        ConfigSync.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- the builder's controls commit and read back ---------------------------------------------

    /// <summary>
    /// Every stage 2 control, laid out by the builder, commits through the control the player uses
    /// and reads back the stored value. What this would catch: a builder control that draws and never
    /// writes, a [Flags] selection written as a number, a list stored in another order, a multi-line
    /// text cut at the first line, or an info line whose value is never read.
    /// </summary>
    [Fact]
    public void The_builder_controls_commit_and_read_back()
    {
        var file = File("controls");
        const string id = "example.stage2.controls";
        var flag = file.Bind("Combat", "ShowHints", true, "Show the hints.");
        var mode = file.Bind("Combat", "Mode", Mode.Slow, "");
        var difficulty = file.Bind("Combat", "Difficulty", "Med", "");
        var tags = file.Bind("Combat", "Tags", Tags.Combat, "");
        var topics = file.Bind("Combat", "Topics", "a", "");
        var motd = file.Bind("Text", "Motd", "hello", "");
        int pings = 0, clock = 0;
        ModSettingsPages.Register(_owner, id, page => page
            .Section("Combat")
                .Toggle(flag, help: "Show the combat hints.")
                .Setting(mode)
                .Radio(difficulty, choices: new[] { "Low", "Med", "High" })
                .MultiSelect(tags)
                .MultiSelect(topics, choices: new[] { "a", "b", "c" }, labels: new[] { "Alpha", "Beta", "Gamma" })
            .Section("Text")
                .Text(motd, multiline: true, lines: 4)
                .Info("Clock", () => "t" + clock)
                .Divider()
                .Button("Ping", () => pings++));
        Open(id);

        var rows = PageRows();
        Assert.Equal("toggle", SettingRow(rows, id + "/Combat/ShowHints").GetProperty("control").GetString());
        Assert.Equal("Show the combat hints.", SettingRow(rows, id + "/Combat/ShowHints").GetProperty("help").GetString());

        // An automatic two-option enum is a segmented choice, drawn (MENUKIT2) as the game draws every
        // one-of choice: the dropdown bar. DROPDOWN1: a click opens its list and writes nothing; a pick writes once.
        Assert.Equal("segmented", SettingRow(rows, id + "/Combat/Mode").GetProperty("control").GetString());
        var before = mode.Value;
        Assert.True(_runtime.FireControlClick("choice", "Mode"));
        Assert.Equal(before, mode.Value);
        var open = UiChoiceHandle.OpenNow;
        Assert.NotNull(open);
        Assert.True(open!.Choose(nameof(Mode.Fast)));
        Assert.Equal(Mode.Fast, mode.Value);
        Assert.Null(UiChoiceHandle.OpenNow);

        // A radio over presentation choices.
        var radio = SettingRow(rows, id + "/Combat/Difficulty");
        Assert.Equal("radio", radio.GetProperty("control").GetString());
        Assert.Equal(new[] { "Low", "Med", "High" }, Strings(radio.GetProperty("choices")));
        Assert.True(_runtime.FireOption("radio", "Difficulty", 2));
        Assert.Equal("High", difficulty.Value);
        Assert.Equal(new[] { false, false, true }, _runtime.OptionStatesOf("radio", "Difficulty")!.ToArray());

        // A [Flags] enum: each click flips one bit; an empty set is the zero-valued name.
        Assert.True(_runtime.FireOption("multiselect", "Tags", 1));
        Assert.Equal(Tags.Combat | Tags.Loot, tags.Value);
        var tagRow = SettingRow(PageRows(), id + "/Combat/Tags");
        Assert.Equal("multi-select", tagRow.GetProperty("control").GetString());
        Assert.Equal(new[] { "Combat", "Loot" }, Strings(tagRow.GetProperty("selected")));
        Assert.True(_runtime.FireOption("multiselect", "Tags", 0));
        Assert.True(_runtime.FireOption("multiselect", "Tags", 1));
        Assert.Equal(Tags.None, tags.Value);

        // A string list: stored comma-separated in option order, whatever order it was clicked in.
        Assert.True(_runtime.FireOption("multiselect", "Topics", 2));
        Assert.Equal("a, c", topics.Value);
        Assert.True(_runtime.FireOption("multiselect", "Topics", 1));
        Assert.Equal("a, b, c", topics.Value);

        // Multi-line text keeps its lines.
        Assert.True(_runtime.FireTextOn("textarea", "Motd", "line one\nline two"));
        Assert.Equal("line one\nline two", motd.Value);
        var motdRow = SettingRow(PageRows(), id + "/Text/Motd");
        Assert.Equal("textarea", motdRow.GetProperty("control").GetString());
        Assert.True(motdRow.GetProperty("multiline").GetBoolean());
        Assert.Equal(4, motdRow.GetProperty("lines").GetInt32());

        // The info line and the divider.
        Assert.Equal("t0", _runtime.ValueOf("info", "Clock"));
        Assert.True(TryRow(PageRows(), ModSettingsView.RowDivider, out _));

        // A plain button runs at once and says so on its help line.
        Assert.True(_runtime.FireClick("Ping"));
        Assert.Equal(1, pings);
        var ping = SettingRow(PageRows(), id + "/button/Ping");
        Assert.Equal("button", ping.GetProperty("control").GetString());
        Assert.Contains("Done.", ping.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// TEXTAREA1: <c>.MaxLength(n)</c> caps a text row. What this would catch: a cap that only limits
    /// the field while a longer commit (the bridge, or a field without TMP's limit) is still stored, or
    /// a modifier that silently applies to a non-text row.
    /// </summary>
    [Fact]
    public void MaxLength_refuses_a_longer_text_and_applies_only_to_text_rows()
    {
        var file = File("maxlength");
        const string id = "example.stage2.maxlength";
        var notes = file.Bind("Text", "Notes", "hi", "");
        var flag = file.Bind("Text", "Flag", true, "");
        ModSettingsPages.Register(_owner, id, page => page
            .Section("Text")
                .Text(notes, multiline: true).MaxLength(5));
        Open(id);

        Assert.True(_runtime.FireTextOn("textarea", "Notes", "abcde"));
        Assert.Equal("abcde", notes.Value);
        _runtime.FireTextOn("textarea", "Notes", "abcdef");
        Assert.Equal("abcde", notes.Value);

        var builder = new SettingsPageBuilder("x");
        builder.Toggle(flag);
        Assert.Throws<InvalidOperationException>(() => builder.MaxLength(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SettingsPageBuilder("y").MaxLength(0));
    }

    [Theory]
    [InlineData(null, false, 0, 256)]
    [InlineData(null, true, 0, 4096)]
    [InlineData(40, true, 0, 40)]
    [InlineData(40, false, 100, 100)]
    [InlineData(null, false, 300, 300)]
    public void Text_field_limit_is_the_cap_or_the_stored_length(int? author, bool multiline, int current, int expected)
    {
        Assert.Equal(expected, ModSettingsLayout.TextFieldLimit(author, multiline, current));
    }

    /// <summary>
    /// The published controls on a plain panel: a radio, a multi-select, a text area, an info line and
    /// a divider, each through its raw events. What this would catch: a control that exists only on
    /// the mod page, or a handler raised for a programmatic set.
    /// </summary>
    [Fact]
    public void The_published_stage2_controls_work_on_any_panel()
    {
        var panel = UiApi.Panel("Controls", _owner, "example.ui.test");
        var radio = panel.Radio("Speed", new[] { "slow", "fast" }, "slow", new[] { "Slow", "Fast" });
        var picks = new List<string>();
        radio.OnChanged(v => picks.Add(v));
        Assert.True(_runtime.FireOption("radio", "Speed", 1));
        Assert.Equal("fast", radio.Value);
        radio.Value = "slow";
        Assert.Equal(new[] { "fast" }, picks.ToArray());

        var multi = panel.MultiSelect("Topics", new[] { "a", "b" }, new[] { "b" });
        IReadOnlyList<string>? last = null;
        multi.OnChanged((IReadOnlyList<string> set) => last = set);
        Assert.True(multi.IsSelected("b"));
        Assert.True(_runtime.FireOption("multiselect", "Topics", 0));
        Assert.Equal(new[] { "a", "b" }, last!.ToArray());

        var area = panel.TextArea("Notes", "x", 5);
        string? committed = null;
        area.OnChanged(v => committed = v);
        Assert.True(_runtime.FireTextOn("textarea", "Notes", "a\nb"));
        Assert.Equal("a\nb", committed);
        Assert.Equal(5, _runtime.WidgetTexts("textarea").Count == 1 ? ((UiInputHandle)area).Lines : 0);

        int reads = 0;
        var info = panel.Info("Clock", () => (++reads).ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
        Assert.Equal(0, info.RefreshSeconds);
        string before = info.Value;
        info.Refresh();
        Assert.NotEqual(before, info.Value);
        Assert.Equal(info.Value, _runtime.ValueOf("info", "Clock"));

        var divider = panel.Divider();
        Assert.True(divider.Enabled);
        panel.Dispose();
    }

    // ---- show-if / enable-if ----------------------------------------------------------------------

    /// <summary>
    /// show-if hides and enable-if grays in place, re-evaluated after a commit, without a rebuild; a
    /// grayed row refuses a commit that still reaches it. What this would catch: a page rebuilt for a
    /// predicate (destroying the control the player clicked), a hidden row still drawn, or a grayed row
    /// that writes anyway.
    /// </summary>
    [Fact]
    public void Show_if_and_enable_if_follow_a_commit_in_place()
    {
        var file = File("predicates");
        const string id = "example.stage2.predicates";
        var advanced = file.Bind("A", "Advanced", false, "");
        var extra = file.Bind("A", "Extra", 1, "");
        var locked = file.Bind("A", "Locked", true, "");
        ModSettingsPages.Register(_owner, id, page => page
            .Section("Advanced")
                .Toggle(advanced)
                .Number(extra, showIf: () => advanced.Value)
                .Toggle(locked, enableIf: () => advanced.Value));
        Open(id);
        var page = ModsMenu.ModPage;

        Assert.False(_runtime.VisibleOf("numeric", "Extra"));
        Assert.False(_runtime.InteractableOf("toggle", "Locked"));
        var resetAll = Row(PageRows(), ModSettingsView.RowResetAll).GetProperty(ModSettingsView.RowResetAll);
        Assert.Contains(id + "/A/Extra", Strings(resetAll.GetProperty("hidden")));
        var lockedRow = SettingRow(PageRows(), id + "/A/Locked");
        Assert.False(lockedRow.GetProperty("enabled").GetBoolean());
        Assert.Equal(ModSettingsView.DisabledReason, lockedRow.GetProperty("why").GetString());

        // A commit that reaches a grayed row is refused and the control re-read.
        Assert.True(_runtime.FireToggle("Locked", false));
        Assert.True(locked.Value);

        // The commit that satisfies both predicates shows and enables in place.
        Assert.True(_runtime.FireToggle("Advanced", true));
        Assert.Same(page, ModsMenu.ModPage);
        Assert.True(_runtime.VisibleOf("numeric", "Extra"));
        Assert.True(_runtime.InteractableOf("toggle", "Locked"));
        Assert.True(SettingRow(PageRows(), id + "/A/Locked").GetProperty("enabled").GetBoolean());
        Assert.True(_runtime.FireToggle("Locked", false));
        Assert.False(locked.Value);

        // And back.
        Assert.True(_runtime.FireToggle("Advanced", false));
        Assert.False(_runtime.VisibleOf("numeric", "Extra"));
        Assert.False(_runtime.InteractableOf("toggle", "Locked"));
    }

    /// <summary>
    /// A predicate that throws is logged once and the row is treated as shown and enabled. What this
    /// would catch: a throw escaping into the frame loop, a warning per evaluation, or a row hidden
    /// because its predicate broke.
    /// </summary>
    [Fact]
    public void A_throwing_predicate_logs_once_and_leaves_the_row_shown_and_enabled()
    {
        var file = File("throwing");
        const string id = "example.stage2.throwing";
        var trigger = file.Bind("T", "Trigger", false, "");
        var shown = file.Bind("T", "Shown", 1, "");
        var enabled = file.Bind("T", "Enabled", true, "");
        ModSettingsPages.Register(_owner, id, page => page
            .Toggle(trigger)
            .Number(shown, showIf: () => throw new InvalidOperationException("show boom"))
            .Toggle(enabled, enableIf: () => throw new InvalidOperationException("enable boom")));
        Open(id);
        Assert.True(_runtime.FireToggle("Trigger", true));
        Assert.True(_runtime.FireToggle("Trigger", false));

        Assert.True(_runtime.VisibleOf("numeric", "Shown"));
        Assert.True(_runtime.InteractableOf("toggle", "Enabled"));
        Assert.Single(_log.Warnings, l => l.Contains("show boom", StringComparison.Ordinal));
        Assert.Single(_log.Warnings, l => l.Contains("enable boom", StringComparison.Ordinal));
        Assert.True(_runtime.FireToggle("Enabled", false));
        Assert.False(enabled.Value);
    }

    // ---- needs-restart ------------------------------------------------------------------------------

    /// <summary>
    /// A needs-restart row whose value differs from its value at load is marked on its help line and
    /// puts the framework status line on Pending restart; putting it back clears both, in place. What
    /// this would catch: a marker compared against the default instead of the load value, or a status
    /// line that needs a rebuild to follow.
    /// </summary>
    [Fact]
    public void Needs_restart_marks_the_row_and_the_status_line()
    {
        var file = File("restart");
        const string id = "example.stage2.restart";
        var experimental = file.Bind("R", "Experimental", false, "");
        experimental.Value = true; // the value at load differs from the default: not pending
        ModSettingsPages.Register(_owner, id, page => page.Toggle(experimental, needsRestart: true));
        Open(id);

        var framework = Row(PageRows(), ModPagePane.RowFramework).GetProperty(ModPagePane.RowFramework);
        Assert.Equal("enabled", framework.GetProperty("status").GetString());
        var row = SettingRow(PageRows(), id + "/R/Experimental");
        Assert.True(row.GetProperty("needsRestart").GetBoolean());
        Assert.False(row.GetProperty("pending").GetBoolean());

        Assert.True(_runtime.FireToggle("Experimental", false));
        framework = Row(PageRows(), ModPagePane.RowFramework).GetProperty(ModPagePane.RowFramework);
        Assert.Equal("pending-restart", framework.GetProperty("status").GetString());
        Assert.Equal("settings", framework.GetProperty("restart").GetString());
        Assert.Equal(ModFrameworkLine.WordSettingsRestart, framework.GetProperty("word").GetString());
        Assert.Equal("pending", framework.GetProperty("tone").GetString());
        row = SettingRow(PageRows(), id + "/R/Experimental");
        Assert.True(row.GetProperty("pending").GetBoolean());
        Assert.Contains(ModSettingsLayout.PendingRestartMarker + ".", row.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            ModFrameworkLine.WordSettingsRestart,
            _runtime.CellRow(ModPagePane.FrameworkKey)!.Cells().Single(c => c.Key == ModFrameworkLine.CellStatusWord).Text);

        // A page opened while a change is pending starts on Pending restart.
        Open(id);
        Assert.Equal(
            "pending-restart",
            Row(PageRows(), ModPagePane.RowFramework).GetProperty(ModPagePane.RowFramework).GetProperty("status").GetString());

        Assert.True(_runtime.FireToggle("Experimental", true));
        Assert.Equal(
            "enabled",
            Row(PageRows(), ModPagePane.RowFramework).GetProperty(ModPagePane.RowFramework).GetProperty("status").GetString());

        // The status order: a pending install still wins over a settings restart, a pause over both.
        var facts = new ModFrameworkFacts { SettingsPendingRestart = true };
        Assert.Equal(ModRunStatus.PendingRestart, ModFrameworkLine.DecideStatus(facts));
        Assert.Equal("settings", ModFrameworkLine.Compose(facts).RestartReason);
        Assert.Equal("load", ModFrameworkLine.Compose(new ModFrameworkFacts { SettingsPendingRestart = true, PageState = ModPageState.PendingLoad }).RestartReason);
        Assert.Equal(ModRunStatus.Paused, ModFrameworkLine.DecideStatus(new ModFrameworkFacts { SettingsPendingRestart = true, Paused = true }));
    }

    // ---- needs-server-part ------------------------------------------------------------------------

    /// <summary>
    /// A needs-server-part row is grayed with its reason and fallback until the server part is known
    /// to run; unknown counts as not running; the row enables in place when the evidence arrives.
    /// What this would catch: a row enabled offline, a grayed row with no explanation, or a fallback
    /// that is never shown.
    /// </summary>
    [Fact]
    public void Needs_server_part_greys_the_row_with_its_reason_and_fallback()
    {
        Assert.Equal(ServerPartState.NotOnServer, ModSettingsLayout.ServerPartForSettings(paused: true, serverAskedAboutIt: true));
        Assert.Equal(ServerPartState.Running, ModSettingsLayout.ServerPartForSettings(false, true));
        Assert.Equal(ServerPartState.Unknown, ModSettingsLayout.ServerPartForSettings(false, false));

        var file = File("serverpart");
        const string id = "example.stage2.serverpart";
        var pvp = file.Bind("S", "PvpTweak", false, "");
        const string fallback = "Uses the client-only behavior.";
        ModSettingsPages.Register(_owner, id, page => page.Toggle(pvp, needsServerPart: true, fallback: fallback));

        // Through the workspace: nothing says the part runs, so the row is grayed.
        Open(id);
        var row = SettingRow(PageRows(), id + "/S/PvpTweak");
        Assert.True(row.GetProperty("needsServerPart").GetBoolean());
        Assert.True(row.GetProperty("greyed").GetBoolean());
        Assert.False(row.GetProperty("enabled").GetBoolean());
        // The reason and the fallback are read back separately (the help line joins them).
        Assert.Equal(SettingAvailability.NotConnectedReason, row.GetProperty("reason").GetString());
        Assert.DoesNotContain(fallback, row.GetProperty("reason").GetString()!, StringComparison.Ordinal);
        Assert.Equal(fallback, row.GetProperty("fallback").GetString());
        Assert.Contains(fallback, row.GetProperty("why").GetString()!, StringComparison.Ordinal);
        Assert.False(_runtime.InteractableOf("toggle", "PvP tweak"));
        Assert.Contains(fallback, _runtime.HelpOf("toggle", "PvP tweak"), StringComparison.Ordinal);
        Assert.True(_runtime.FireToggle("PvP tweak", true));
        Assert.False(pvp.Value);
        ModsMenu.Close();

        // Directly: the evidence changes and the next evaluation enables the row in place.
        var state = ServerPartState.NotOnServer;
        Assert.True(ModSettingsPages.TryGet(id, out var registration));
        var view = new ModSettingsView(id, ModSettingsLayout.Build(registration!.Sections(), null, null), "", () => state);
        var panel = UiApi.Panel("ServerPart", _owner, "example.ui.test");
        view.Build(panel, (_, _, _) => { });
        Assert.False(_runtime.InteractableOf("toggle", "PvP tweak"));
        Assert.Contains(ModSettingsLayout.ServerPartMissingReason, _runtime.HelpOf("toggle", "PvP tweak"), StringComparison.Ordinal);
        state = ServerPartState.Running;
        view.Reevaluate();
        Assert.True(_runtime.InteractableOf("toggle", "PvP tweak"));
        Assert.DoesNotContain(fallback, _runtime.HelpOf("toggle", "PvP tweak"), StringComparison.Ordinal);
        view.Release();
        panel.Dispose();
    }

    /// <summary>
    /// Live 2026-10-14: <c>mods_ui widget_click</c> reported <c>invoked:1</c> on a needs-server-part button its page had
    /// grayed (<c>enabled:false</c>). The bridge's click (<see cref="ModsMenu.ClickVisibleWidget"/>) dispatches the slot
    /// itself, past Unity's interactable check, so it must refuse a grayed control: ok:false with the reason, tagged so the
    /// dev bridge does not retry the caption on a cell row, and nothing runs. The same for a grayed toggle and radio. What
    /// this would catch: a bridge check passing where a player's click does nothing.
    /// </summary>
    [Fact]
    public void A_bridge_click_on_a_grayed_control_is_refused_with_the_reason()
    {
        var file = File("grayclick");
        const string id = "example.stage2.grayclick";
        var flag = file.Bind("S", "Flag", false, "");
        var level = file.Bind("S", "Level", "Low", "");
        int ran = 0;
        bool on = false;
        ModSettingsPages.Register(_owner, id, page => page
            .Button("Open", () => ran++, needsServerPart: true, fallback: "Opens nothing here.")
            .Toggle(flag, enableIf: () => on)
            .Radio(level, choices: new[] { "Low", "High" }, enableIf: () => on));
        Open(id);

        var button = SettingRow(PageRows(), id + "/button/Open");
        Assert.False(button.GetProperty("enabled").GetBoolean());
        var refused = Assert.Throws<ArgumentException>(() => ModsMenu.ClickVisibleWidget("content", "Open", null, null));
        Assert.Contains("'Open' is disabled (not interactable); not clicked", refused.Message, StringComparison.Ordinal);
        // The reason is the row's own (offline here: not connected), as its help line shows it.
        string reason = button.GetProperty("reason").GetString()!;
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
        Assert.True(refused.Data.Contains(UiClickGate.RefusedKey));
        Assert.Equal(0, ran);

        var toggle = Assert.Throws<ArgumentException>(() => ModsMenu.ClickVisibleWidget("content", "Flag", null, "true"));
        Assert.Contains("disabled (not interactable)", toggle.Message, StringComparison.Ordinal);
        Assert.False(flag.Value);
        var radio = Assert.Throws<ArgumentException>(() => ModsMenu.ChooseRadio("content", "Level", "High"));
        Assert.Contains("disabled (not interactable)", radio.Message, StringComparison.Ordinal);
        Assert.Equal("Low", level.Value);

        // Enabled: the same click and pick are not refused.
        on = true;
        Open(id);
        Assert.NotNull(ModsMenu.ClickVisibleWidget("content", "Flag", null, "true"));
        ModsMenu.ChooseRadio("content", "Level", "High");
        Assert.Equal("High", level.Value);
    }

    // ---- scope ------------------------------------------------------------------------------------

    /// <summary>
    /// scope: Server opts a synced entry into the administrator request path and marks the row; the
    /// page's own registration is released with the page. Declared on an entry that is not synced it
    /// is logged and the row stays local. What this would catch: a server-set row that writes the
    /// local file, a marker with no path behind it, or an edit registration left after unload.
    /// </summary>
    [Fact]
    public void Server_scope_goes_through_the_admin_path_and_is_released_with_the_page()
    {
        ServerSettings.Install();
        var file = File("scope");
        const string id = "example.stage2.scope";
        var limit = file.Bind("Server", "Limit", 10, "");
        var local = file.Bind("Server", "Local", 1, "");
        var notSynced = file.Bind("Server", "NotSynced", 2, "");
        using var sync = ConfigSync.Register(id, limit);
        var registration = ModSettingsPages.Register(_owner, id, page => page
            .Number(limit, scope: SettingScope.Server)
            .Number(local, scope: SettingScope.Local)
            .Number(notSynced, scope: SettingScope.Server));

        Assert.True(ServerSettings.IsEditable(id + "/Server/Limit"));
        Assert.False(ServerSettings.IsEditable(id + "/Server/NotSynced"));
        Assert.Equal(1, registration.ServerEditCount);
        Assert.Contains(_log.Warnings, l => l.Contains(id + "/Server/NotSynced", StringComparison.Ordinal) && l.Contains("not bound synced", StringComparison.Ordinal));

        Open(id);
        var limitRow = SettingRow(PageRows(), id + "/Server/Limit");
        Assert.Equal("server", limitRow.GetProperty("scopeDeclared").GetString());
        Assert.Contains(ModSettingsLayout.ScopeServerMarker + ".", limitRow.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.Equal("local", SettingRow(PageRows(), id + "/Server/Local").GetProperty("scopeDeclared").GetString());

        registration.Dispose();
        Assert.False(ServerSettings.IsEditable(id + "/Server/Limit"));
        Assert.False(ModSettingsPages.Has(id));
    }

    // ---- builder and legacy groups ------------------------------------------------------------------

    /// <summary>
    /// An entry the page places appears once, in the page's section: a <c>.Settings</c> group and the
    /// automatic General section skip it; the group keeps its other entries after the page sections.
    /// What this would catch: a setting drawn twice, or the builder losing to a group.
    /// </summary>
    [Fact]
    public void The_builder_wins_over_legacy_groups_and_the_catalog()
    {
        var file = File("dedupe");
        const string id = "example.stage2.dedupe";
        var shared = file.Bind("G", "Shared", 1, "");
        var groupOnly = file.Bind("G", "GroupOnly", true, "");
        var loose = file.Bind("G", "Loose", "x", "");
        SettingsCatalog.Register(id, file, _owner);
        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, id, "Dedupe", "Dedupe.");
        mod.Settings("Legacy", shared, groupOnly);
        ModSettingsPages.Register(_owner, id, page => page.Section("Page").Number(shared).Number(shared));
        ModsMenu.OpenMod(id);

        var sections = ModsMenu.ModPage!.SettingsView!.Sections;
        Assert.Equal(new[] { "Page", "Legacy", ModSettingsLayout.GeneralTitle }, sections.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { id + "/G/Shared" }, sections[0].Rows.Select(r => r.RowId).ToArray());
        Assert.Equal(new[] { id + "/G/GroupOnly" }, sections[1].Rows.Select(r => r.RowId).ToArray());
        Assert.Equal(new[] { id + "/G/Loose" }, sections[2].Rows.Select(r => r.RowId).ToArray());
        Assert.Equal(ModSettingsSectionKind.Page, sections[0].Kind);
        _ = loose;
    }

    // ---- lifetime -----------------------------------------------------------------------------------

    /// <summary>
    /// Unload releases everything on every <see cref="PluginUnloadReason"/>: the page, the open mod
    /// page, its dialogs, its info refresh, its administrator-edit registration and the plugin's
    /// delegates. None of this branches on the reason (a Core swap drops the page like a removal; the
    /// next generation registers it again), so each reason runs the same path. What this would catch:
    /// a page or a delegate of an unloaded generation still reachable.
    /// </summary>
    [Fact]
    public void Unload_releases_the_page_on_every_reason()
    {
        ServerSettings.Install();
        var scheduled = new List<Action>();
        UiInfoHandle.Scheduler = (_, action, _) => scheduled.Add(action);
        foreach (PluginUnloadReason reason in Enum.GetValues(typeof(PluginUnloadReason)))
        {
            var owner = new object();
            var file = File("unload-" + reason);
            string id = "example.stage2.unload." + reason.ToString().ToLowerInvariant();
            var limit = file.Bind("U", "Limit", 3, "");
            using var sync = ConfigSync.Register(id, limit);
            int reads = 0, runs = 0;
            var registration = ModSettingsPages.Register(owner, id, page => page
                .Number(limit, scope: SettingScope.Server)
                .Info("Reads", () => (++reads).ToString(System.Globalization.CultureInfo.InvariantCulture), 1)
                .Button("Run", () => runs++, confirm: "Run it?"));
            Open(id);
            Assert.True(_runtime.FireClick("Run"));
            Assert.True(UiDialog.Current is { IsOpen: true } d && d.Tag == UiSettingsControls.ConfirmDialogTag, reason + ": dialog");
            Assert.True(ServerSettings.IsEditable(id + "/U/Limit"));

            // What unload does: the tracked registration is disposed, then the owner sweep runs.
            registration.Dispose();
            ModsMenu.RemoveAll(owner);

            Assert.False(ModSettingsPages.Has(id), reason + ": page");
            Assert.False(ModsMenu.IsOpen, reason + ": the open page of the unloaded mod closes");
            Assert.False(UiDialog.Current is { IsOpen: true }, reason + ": dialog closes");
            Assert.False(ServerSettings.IsEditable(id + "/U/Limit"), reason + ": server edits");
            Assert.All(registration.SectionSpecs.SelectMany(s => s.Items),
                item => Assert.True(item.Action == null && item.Value == null && item.Options.ShowIf == null, reason + ": delegates"));
            int before = reads;
            foreach (var action in scheduled.ToArray()) action();
            scheduled.Clear();
            Assert.Equal(before, reads);
            Assert.Equal(0, runs);
        }
    }

    /// <summary>
    /// The confirm dialog belongs to its button: No runs nothing, Yes runs once, and a page rebuild,
    /// the workspace closing or the panel's disposal close it; a Yes after the button is gone runs
    /// nothing. What this would catch: a dialog outliving its page, or a stale Yes reaching a plugin.
    /// </summary>
    [Fact]
    public void The_confirm_dialog_lives_and_dies_with_its_button()
    {
        var file = File("confirm");
        const string id = "example.stage2.confirm";
        _ = file;
        int wiped = 0;
        ModSettingsPages.Register(_owner, id, page => page.Button("Wipe", () => wiped++, confirm: "Wipe everything?"));
        Open(id);
        string yes = UiSettingsControls.ConfirmDialogTag + "-buttons/" + UiPromptBlock.CellYes;
        string no = UiSettingsControls.ConfirmDialogTag + "-buttons/" + UiPromptBlock.CellNo;

        Assert.True(_runtime.FireClick("Wipe"));
        Assert.True(UiDialog.Current is { IsOpen: true, Tag: UiSettingsControls.ConfirmDialogTag });
        Assert.Equal(0, wiped);
        Assert.True(UiCellRowHandle.TryInvoke(no, out _));
        Assert.False(UiDialog.Current is { IsOpen: true });
        Assert.Equal(0, wiped);

        Assert.True(_runtime.FireClick("Wipe"));
        Assert.True(UiCellRowHandle.TryInvoke(yes, out _));
        Assert.Equal(1, wiped);

        Assert.True(_runtime.FireClick("Wipe"));
        Open(id);
        Assert.False(UiDialog.Current is { IsOpen: true }, "a rebuild closes it");

        Assert.True(_runtime.FireClick("Wipe"));
        ModsMenu.Close();
        Assert.False(UiDialog.Current is { IsOpen: true }, "closing the workspace closes it");
        Assert.Equal(1, wiped);

        // On a plain panel: disposing the panel closes it, and a stale press runs nothing.
        var panel = UiApi.Panel("Confirm", _owner, "example.ui.test");
        var button = panel.ConfirmButton("Reset", "Reset now?");
        int resets = 0;
        button.OnClick(() => resets++);
        var handle = (UiButtonHandle)button;
        handle.Pressed();
        Assert.NotNull(handle.ConfirmDialog);
        panel.Dispose();
        Assert.False(UiDialog.Current is { IsOpen: true });
        handle.Pressed();
        Assert.Equal(0, resets);
        Assert.Throws<NotSupportedException>(() => ((IUiPanel)new BareHeaderPanel()).ConfirmButton("x", "y?"));
    }

    /// <summary>
    /// An info line refreshes on its own clock while its page is shown and stops for good once the
    /// page closes; a throwing function is logged once. What this would catch: a refresh that keeps
    /// calling into an unloaded plugin, or an exception thrown into the frame loop.
    /// </summary>
    [Fact]
    public void An_info_line_refreshes_while_shown_and_stops_after_the_page_closes()
    {
        var scheduled = new List<Action>();
        UiInfoHandle.Scheduler = (_, action, _) => scheduled.Add(action);
        const string id = "example.stage2.info";
        int reads = 0;
        bool boom = false;
        ModSettingsPages.Register(_owner, id, page => page
            .Info("Reads", () => boom ? throw new InvalidOperationException("info boom") : (++reads).ToString(System.Globalization.CultureInfo.InvariantCulture), 2)
            .Info("Once", () => "fixed", 0));
        // The Mods tab's landing page has its own refreshing Last bundle line: open it first and leave its read out.
        ModsMenu.Open();
        scheduled.Clear();
        ModsMenu.OpenMod(id);
        int afterBuild = reads;
        Assert.Single(scheduled);

        Run(scheduled);
        Assert.Equal(afterBuild + 1, reads);
        Assert.Equal(reads.ToString(System.Globalization.CultureInfo.InvariantCulture), _runtime.ValueOf("info", "Reads"));
        Assert.Single(scheduled);

        boom = true;
        Run(scheduled);
        Run(scheduled);
        Assert.Single(_log.Warnings, l => l.Contains("info boom", StringComparison.Ordinal));
        boom = false;

        ModsMenu.Close();
        int atClose = reads;
        Run(scheduled);
        Assert.Equal(atClose, reads);
        Assert.Empty(scheduled);
    }

    // ---- the redesigned page ------------------------------------------------------------------------

    /// <summary>
    /// The settings grid: the control column starts at the same x on every row of a pane, has a fixed
    /// width on a wide pane, and the caption column (where the help wraps), the control column and the
    /// reset gutter never overlap, at any pane width. What this would catch: a help line running under
    /// the reset link (live 2026-09-29: "changed · def« 1"), or controls at ragged x.
    /// </summary>
    [Theory]
    [InlineData(1664f)]
    [InlineData(1200f)]
    [InlineData(820f)]
    [InlineData(560f)]
    [InlineData(300f)]
    public void The_settings_grid_keeps_a_fixed_control_column_and_a_clear_gutter(float rowWidth)
    {
        var grid = ModSettingsGrid.For(rowWidth);
        var again = ModSettingsGrid.For(rowWidth);
        Assert.Equal(grid.ControlLeft, again.ControlLeft);
        Assert.True(grid.LabelLeft >= -rowWidth / 2f - 0.01f);
        Assert.True(grid.LabelRight <= grid.ControlLeft, "help wraps inside the caption column, before the control");
        Assert.True(grid.ControlRight <= grid.GutterLeft, "the control ends before the gutter");
        Assert.True(grid.GutterRight <= rowWidth / 2f - HeadBlockLayout.RightColumnInset + 0.01f, "the gutter ends before the scrollbar band");
        Assert.True(grid.GutterRight - grid.GutterLeft >= MenuPanelLayout.IconLinkSize, "the reset link fits its gutter");
        var columns = grid.Columns;
        Assert.Equal(grid.ControlLeft, columns.ControlLeft, 3);
        Assert.Equal(grid.LabelRight, columns.LabelRight, 3);
        if (rowWidth >= 1000f) Assert.Equal(ModSettingsGrid.ControlWidth, grid.ControlColumnWidth);
    }

    /// <summary>
    /// Every setting is one grid row: the caption is the display name, the help line is drawn under it
    /// in the same row (the fake records it on the row), the reset link sits in that row's gutter, and
    /// the modified marker is in the help line. What this would catch: a separate note row again, or a
    /// reset link placed on another row.
    /// </summary>
    [Fact]
    public void Each_setting_is_one_grid_row_with_its_help_and_reset_link()
    {
        var file = File("grid");
        const string id = "example.stage2.grid";
        var entry = new BoundedEntry<int>(file, "Sample", "SpawnMultiplier", 1, new ModSettingsPageTests.FakeRange(1, 10)) { Stored = 4 };
        ModSettingsPages.Register(_owner, id, page => page.Setting(entry, help: "Demo range setting."));
        Open(id);

        Assert.True(_runtime.GridOf("slider-entry", "Spawn multiplier"));
        string help = _runtime.HelpOf("slider-entry", "Spawn multiplier") ?? "";
        Assert.Equal("Demo range setting. " + ModSettingsLayout.ChangedFromPrefix + "1.", help);
        Assert.Equal(new[] { "Spawn multiplier" }, _runtime.GutterRows().ToArray());
        var reset = ResetRow(PageRows(), id + "/Sample/SpawnMultiplier");
        Assert.True(reset.GetProperty("visible").GetBoolean());
        Assert.Equal(ModSettingsView.ResetTooltipPrefix + "1", reset.GetProperty("tooltip").GetString());
        Assert.False(_runtime.WidgetTexts("label").Any(t => t.Contains("changed", StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("ShowHints", "Show hints")]
    [InlineData("UiDemoGreeting", "UI demo greeting")]
    [InlineData("PvpTweak", "PvP tweak")]
    [InlineData("AllowPvE", "Allow PvE")]
    [InlineData("ShowHudID", "Show HUD ID")]
    [InlineData("MaxXp", "Max XP")]
    [InlineData("NpcVfxScale", "NPC VFX scale")]
    [InlineData("IPv6Address", "IPv6 address")]
    [InlineData("Enable3DAudio", "Enable 3D audio")]
    [InlineData("Use4KTextures", "Use 4K textures")]
    [InlineData("spawn_multiplier", "Spawn multiplier")]
    [InlineData("MaxFPS", "Max FPS")]
    [InlineData("HTTPPort", "HTTP port")]
    [InlineData("Level2Cap", "Level 2 cap")]
    [InlineData("ExampleLimit", "Example limit")]
    [InlineData("a", "A")]
    [InlineData("", "")]
    public void Display_names_are_derived_from_the_cfg_key(string key, string expected) =>
        Assert.Equal(expected, ModSettingsLayout.DisplayName(key));

    /// <summary>
    /// A presentation or builder label wins over the derived name; the readback keeps the cfg caption
    /// as <c>label</c> and search still finds the cfg key.
    /// </summary>
    [Fact]
    public void A_given_label_wins_and_the_cfg_key_stays_searchable()
    {
        var file = File("labels");
        var entry = file.Bind("Sample", "UiDemoGreeting", "hi", "");
        var derived = SettingsRow.ForEntry("example.labels", entry, false);
        Assert.Equal("UI demo greeting", ModSettingsLayout.DisplayNameOf(derived));
        var labelled = SettingsRow.ForEntry("example.labels", entry, false, presentation: new SettingPresentation { Label = "Greeting" });
        Assert.Equal("Greeting", ModSettingsLayout.DisplayNameOf(labelled));
        Assert.Equal("Hello", ModSettingsLayout.DisplayNameOf(derived, new ModSettingOptions { Label = "Hello" }));
        Assert.True(ModSettingsLayout.Matches(derived, "UiDemo"));
        Assert.True(ModSettingsLayout.Matches(derived, "demo greeting"));
    }

    /// <summary>
    /// Two to four short options draw as a segmented picker; more, or longer, stay a dropdown; a
    /// builder dropdown stays a dropdown. What this would catch: a twelve-option list squeezed into a
    /// segmented row, or a builder's explicit choice overridden.
    /// </summary>
    [Fact]
    public void Short_choice_lists_are_segmented_and_long_ones_stay_dropdowns()
    {
        Assert.True(UiOptionsRules.FitsSegmented(new[] { "Low", "Med", "High" }));
        Assert.True(UiOptionsRules.FitsSegmented(new[] { "A", "B", "C", "D" }));
        Assert.False(UiOptionsRules.FitsSegmented(new[] { "A", "B", "C", "D", "E" }));
        Assert.False(UiOptionsRules.FitsSegmented(new[] { "Short", "A much longer option" }));
        Assert.False(UiOptionsRules.FitsSegmented(new[] { "Only" }));

        var file = File("segmented");
        const string id = "example.segmented";
        var two = SettingsRow.ForEntry(id, file.Bind("S", "Mode", Mode.Slow, ""), false);
        Assert.Equal(SettingControl.Segmented, ModSettingsLayout.DrawnControl(two, null));
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.DrawnControl(two, new ModSettingOptions { Control = SettingControl.Dropdown }));
        var many = SettingsRow.ForEntry(id, file.Bind("S", "Day", DayOfWeek.Monday, ""), false);
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.DrawnControl(many, null));
        var radioOnInt = SettingsRow.ForEntry(id, file.Bind("S", "Count", 3, ""), false);
        Assert.Equal(SettingControl.Number, ModSettingsLayout.ControlFor(radioOnInt, new ModSettingOptions { Control = SettingControl.Radio }));
        Assert.Equal(UiOptionsLayout.List, UiOptionsRules.LayoutFor(UiSettingsControls.Options(new[] { "a", "b" }, null), multi: true));
        // A list option draws a check box, so its caption is the label alone; the text marker is for readers.
        Assert.Equal("Slow", UiOptionsRules.Caption("Slow", true, false, UiOptionsLayout.List));
        Assert.Equal("Loot", UiOptionsRules.Caption("Loot", false, true, UiOptionsLayout.List));
        Assert.Equal("Slow", UiOptionsRules.Caption("Slow", true, false, UiOptionsLayout.Segmented));
        Assert.Equal("[x] ", UiOptionsRules.Marker(true, multi: true));
    }

    /// <summary>
    /// The status bar: one band row; switch, status word, plain version, sides chip, then the server
    /// part right-aligned and the action at the far right. What this would catch: the version drawn as
    /// a mono (letter-spaced) cell again, the switch drawn as a pill or a live button, or the server
    /// part shown when unknown.
    /// </summary>
    [Fact]
    public void The_status_bar_is_one_band_in_the_designed_order()
    {
        var line = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            Version = "0.1.0", Sides = ModSides.Both, ServerPart = ServerPartState.Running,
            ActionCaption = ModPageCopy.Uninstall, ActionTone = LabelTone.Orange,
        });
        var row = line.BuildRow(ModPagePane.FrameworkKey, 1600f);
        Assert.True(row.Band);
        Assert.Equal(
            new[]
            {
                ModFrameworkLine.CellSwitch, ModFrameworkLine.CellStatusWord, ModFrameworkLine.CellVersion,
                ModFrameworkLine.CellSides, ModFrameworkLine.CellGap, ModFrameworkLine.CellServerPart, ModFrameworkLine.CellAction,
            },
            row.Cells.Select(c => c.Key).ToArray());
        Assert.Equal(CellKind.Switch, row.Find(ModFrameworkLine.CellSwitch)!.Kind);
        var version = row.Find(ModFrameworkLine.CellVersion)!;
        Assert.NotEqual(CellKind.Mono, version.Kind);
        Assert.Equal("v0.1.0", version.Text);
        Assert.Equal("Client and server", row.Find(ModFrameworkLine.CellSides)!.Text);
        Assert.Equal(CellAlign.Right, row.Find(ModFrameworkLine.CellServerPart)!.Align);
        Assert.Equal("Server part running", row.Find(ModFrameworkLine.CellServerPart)!.Text);
        Assert.Equal(CellLayout.SwitchWidth, CellLayout.AutoWidth(row.Find(ModFrameworkLine.CellSwitch)!));

        var unknown = ModFrameworkLine.Compose(new ModFrameworkFacts { Sides = ModSides.Both }).BuildRow("k", 1600f);
        // COUNTERPART-SAMPLES: a two-sided mod keeps the cell, hidden while unknown, so the page can
        // rewrite it in place when the handshake evidence arrives (ModPagePane.SetServerPart).
        var hidden = unknown.Find(ModFrameworkLine.CellServerPart)!;
        Assert.False(hidden.Visible);
        Assert.Equal("", hidden.Text);
        Assert.Null(ModFrameworkLine.Compose(new ModFrameworkFacts { Sides = ModSides.Client }).BuildRow("k", 1600f).Find(ModFrameworkLine.CellServerPart));
        Assert.Equal("v1.2", ModFrameworkLine.VersionTextOf("1.2"));
        Assert.Equal("v1.2", ModFrameworkLine.VersionTextOf("v1.2"));
        Assert.Equal("", ModFrameworkLine.VersionTextOf(" "));
    }

    /// <summary>
    /// The settings header is one row: the heading, the search field only above the threshold, and a
    /// small Reset all; section headings are small muted rows. What this would catch: a full-width
    /// Reset bar again, or the store link back on the settings line.
    /// </summary>
    [Fact]
    public void The_settings_header_is_one_row_and_section_headings_are_the_games()
    {
        var file = File("header");
        const string id = "example.stage2.header";
        var entries = new List<ConfigEntryBase>();
        for (int i = 0; i < 3; i++) entries.Add(file.Bind("H", "Item" + i, i, ""));
        ModSettingsPages.Register(_owner, id, page => page.Section("Basics").Setting(entries[0]).Setting(entries[1]).Setting(entries[2]));
        Open(id);

        var head = _runtime.CellRow(ModSettingsView.HeaderKey)!;
        var keys = head.Cells().Select(c => c.Key).ToArray();
        Assert.Contains(ModSettingsView.CellResetAll, keys);
        Assert.DoesNotContain(ModSettingsView.CellSearch, keys);
        Assert.Equal(ModSettingsView.ResetAllCaption, head.Cells().Single(c => c.Key == ModSettingsView.CellResetAll).Text);
        // Settings scopes (step 4): the scope section is the game's section header (CustomGameSettings_Label):
        // its title size, middle-aligned, not muted; the red rule is drawn under it by the runtime.
        var scope = _runtime.CellRow(ModSettingsScopes.ScopeKeyPrefix + "0")!;
        var title = scope.Cells().Single(c => c.Key == ModSettingsView.CellTitle);
        Assert.Equal(SettingScopeSections.YourHeading, title.Text);
        Assert.Equal(CellKind.Text, title.Kind);
        Assert.Equal(ModSettingsRowGrid.HeaderFontSize, title.FontSize);
        // Middle-aligned: a bottom-aligned cell is capped at 30 units and cut the title away (live round C).
        Assert.Equal(CellVAlign.Middle, title.VAlign);
        var scopeRect = scope.RowRect();
        Assert.Equal(ModSettingsRowGrid.HeaderHeight, scopeRect.MaxY - scopeRect.MinY, 2);
        // The author's section is a subheading in small caps inside it: gold and a step larger, the same in both scopes
        // (settings organization review, live 2026-10-05: the muted 15-point titles were too faint).
        var sub = _runtime.CellRow(ModSettingsView.SectionKeyPrefix + "0")!.Cells().Single();
        Assert.Equal("Basics", sub.Text);
        Assert.Equal(ModSettingsScopes.SubheadingFontSize, sub.FontSize);
        Assert.True(sub.FontSize > ModSettingsView.SectionFontSize);
        Assert.Equal(CellTone.Gold, sub.Tone);
        Assert.True(sub.SmallCaps);
        Assert.False(TryRow(PageRows(), ModSettingsView.RowSearch, out _));
        // The store link sits on the header's button row now, not beside Settings.
        Assert.True(TryRow(PageRows(), ModPagePane.RowStoreLink, out _));
    }

    /// <summary>
    /// A read-only server value shows the lock word and its value in the control column and says why
    /// in plain words on its help line.
    /// </summary>
    [Fact]
    public void A_read_only_server_value_says_why_in_plain_words()
    {
        var file = File("readonly");
        const string id = "example.stage2.readonly";
        var synced = new BoundedEntry<int>(file, "R", "ExampleLimit", 10, null);
        SettingsCatalog.UseSyncedProbe(e => ReferenceEquals(e, synced));
        ModSettingsPages.Register(_owner, id, page => page.Setting(synced));
        Open(id);
        var row = SettingRow(PageRows(), id + "/R/ExampleLimit");
        Assert.Equal("readonly", row.GetProperty("control").GetString());
        // Settings scopes (step 4): a synced value is a Server setting; offline it is grayed with the
        // framework's reason, which replaces "Set by the server. Join a server to see its value."
        Assert.Equal("server-wide", row.GetProperty("scopeSection").GetString());
        Assert.Contains(SettingAvailability.NotConnectedReason, row.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ModSettingsLayout.ReadOnlyAwaitingReason, row.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.Equal(ModSettingsView.LockedPrefix + "10", _runtime.ValueOf("info", "Example limit"));
    }

    // ---- the kit seams ------------------------------------------------------------------------------

    /// <summary>A grayed button or option ignores its click; restoring it restores the click.</summary>
    [Fact]
    public void A_greyed_control_ignores_input()
    {
        var panel = UiApi.Panel("Grey", _owner, "example.ui.test");
        int clicks = 0;
        var button = panel.Button("Go");
        button.OnClick(() => clicks++);
        button.Interactable = false;
        Assert.True(_runtime.FireClick("Go"));
        Assert.Equal(0, clicks);
        Assert.False(_runtime.InteractableOf("button", "Go"));
        button.Interactable = true;
        Assert.True(_runtime.FireClick("Go"));
        Assert.Equal(1, clicks);

        var radio = panel.Radio("Pick", new[] { "a", "b" }, "a");
        radio.Interactable = false;
        Assert.True(_runtime.FireOption("radio", "Pick", 1));
        Assert.Equal("a", radio.Value);
        panel.Dispose();
    }

    /// <summary>A mod's header builder can add the stage 2 controls, and they are rolled back like any row.</summary>
    [Fact]
    public void The_header_panel_adds_the_stage2_controls_as_rows()
    {
        var panel = UiApi.Panel("Header", _owner, "example.ui.test");
        var restricted = new ModHeaderPanel(panel);
        Assert.True(restricted.Radio("R", new[] { "a", "b" }, "a").Enabled);
        Assert.True(restricted.MultiSelect("M", new[] { "a" }, null).Enabled);
        Assert.True(restricted.TextArea("T").Enabled);
        Assert.True(restricted.Info("I", () => "x", 0).Enabled);
        Assert.True(restricted.Divider().Enabled);
        Assert.True(restricted.ConfirmButton("C", "Sure?").Enabled);
        Assert.Equal(6, restricted.Added.Count);
        Assert.Equal(6, restricted.RollBack());
        panel.Dispose();
    }

    /// <summary>One page per plugin; a builder that throws registers nothing; bad arguments throw at the call.</summary>
    [Fact]
    public void A_page_is_registered_once_and_a_throwing_builder_registers_nothing()
    {
        var file = File("once");
        var entry = file.Bind("O", "Value", 1, "");
        const string id = "example.stage2.once";
        Assert.Throws<InvalidOperationException>(() =>
            ModSettingsPages.Register(_owner, id, _ => throw new InvalidOperationException("builder boom")));
        Assert.False(ModSettingsPages.Has(id));
        Assert.Throws<ArgumentNullException>(() => ModSettingsPages.Register(_owner, id, page => page.Setting(null!)));
        Assert.Throws<ArgumentException>(() => ModSettingsPages.Register(_owner, id, page => page.Radio(entry, choices: new[] { "a" }, labels: new[] { "A", "B" })));
        var first = ModSettingsPages.Register(_owner, id, page => page.Setting(entry));
        Assert.Throws<ArgumentException>(() => ModSettingsPages.Register(_owner, id, page => page.Setting(entry)));
        first.Dispose();
        ModSettingsPages.Register(_owner, id, page => page.Setting(entry)).Dispose();
    }

    /// <summary>Enter starts a new line only in a multi-line field, and only without Ctrl or Alt.</summary>
    [Fact]
    public void Enter_starts_a_new_line_only_in_a_multi_line_field()
    {
        Assert.True(UiTextEdit.EnterStartsLine(UiKey.Enter, false, false, multiline: true));
        Assert.True(UiTextEdit.EnterStartsLine(UiKey.NumpadEnter, false, false, multiline: true));
        Assert.False(UiTextEdit.EnterStartsLine(UiKey.Enter, false, false, multiline: false));
        Assert.False(UiTextEdit.EnterStartsLine(UiKey.Enter, ctrl: true, false, multiline: true));
        var result = UiTextEdit.Newline(new UiTextEditState("ab", 1, 1));
        Assert.Equal("a\nb", result.State.Text);
        Assert.Equal(2, result.State.Caret);
        Assert.True(result.TextChanged);
    }

    /// <summary>A builder keybind row claims the bind from the automatic Keys section.</summary>
    [Fact]
    public void A_builder_keybind_names_a_registered_bind_and_skips_an_unknown_one()
    {
        const string id = "example.stage2.keys";
        var registration = ModSettingsPages.Register(_owner, id, page => page.Section("Keys").Keybind("Missing"));
        var sections = registration.Sections();
        Assert.Empty(sections[0].Items);
        Assert.Single(_log.Warnings, l => l.Contains("'Missing'", StringComparison.Ordinal));
        registration.Sections();
        Assert.Single(_log.Warnings, l => l.Contains("'Missing'", StringComparison.Ordinal));
    }

    // ---- live round 1 (2026-09-29, 3840x1080) -------------------------------------------------------

    /// <summary>
    /// Rhythm: the help sits tight under its name and clearly more space separates settings, and
    /// every row ends with the same pad. What this would catch: help read as the next row's (live:
    /// ~12 px under its name, ~8 px above the next).
    /// </summary>
    [Fact]
    public void Help_hugs_its_name_and_settings_are_spaced_further_apart()
    {
        float gapUnderName = ModSettingsGrid.HelpTop(ModSettingsGrid.NameBand) - ModSettingsGrid.NameBand;
        Assert.InRange(gapUnderName, 0f, 4f);
        Assert.InRange(ModSettingsGrid.InterRowGap, 14f, 18f);
        Assert.True(gapUnderName < ModSettingsGrid.InterRowGap);

        float plain = ModSettingsGrid.RowHeight(0f, 0f, ModSettingsGrid.ControlHeight);
        Assert.Equal(ModSettingsGrid.ControlHeight + ModSettingsGrid.RowBottomPad, plain);
        float withHelp = ModSettingsGrid.RowHeight(0f, 36f, ModSettingsGrid.ControlHeight);
        Assert.Equal(ModSettingsGrid.HelpTop(ModSettingsGrid.NameBand) + 36f + ModSettingsGrid.RowBottomPad, withHelp);
        // Help of any length never makes a row taller than the panel's row cap.
        float huge = ModSettingsGrid.RowHeight(0f, ModSettingsGrid.HelpHeightFor(100000f, 0f), ModSettingsGrid.ControlHeight);
        Assert.True(huge <= PanelLayout.MaxRowHeight);
        Assert.Equal(0f, ModSettingsGrid.HelpHeightFor(float.NaN, 0f));
    }

    /// <summary>
    /// The control column: full-width controls fill it; compact ones (switch, info or read-only value)
    /// end on its right edge; every single-line control shares one height. What this would catch: a
    /// switch or a value at the column's left edge (live), or a tall keybind box.
    /// </summary>
    [Fact]
    public void Compact_controls_are_right_aligned_and_single_line_controls_share_one_height()
    {
        var grid = ModSettingsGrid.For(1664f);
        var toggle = grid.ControlSlot("toggle", ModSettingsGrid.SwitchWidth);
        Assert.Equal(grid.ControlRight, toggle.Left + toggle.Width, 3);
        Assert.Equal(ModSettingsGrid.SwitchWidth, toggle.Width);
        var info = grid.ControlSlot("info", 5000f);
        Assert.Equal(grid.ControlRight, info.Left + info.Width, 3);
        foreach (var full in new[] { "input", "choice", "numeric", "slider-entry", "keybind", "button", "radio", "multiselect", "textarea", "color" })
        {
            var slot = grid.ControlSlot(full, 40f);
            Assert.Equal(grid.ControlLeft, slot.Left);
            Assert.Equal(grid.ControlColumnWidth, slot.Width);
        }

        foreach (var single in new[] { "input", "choice", "numeric", "slider-entry", "keybind", "button", "radio", "color", "info" })
            Assert.Equal(ModSettingsGrid.ControlHeight, ModSettingsGrid.ControlHeightFor(single, UnityUiRuntimeMetrics.RowHeight));
        Assert.Equal(ModSettingsGrid.SwitchHeight, ModSettingsGrid.ControlHeightFor("toggle", 40f));
        Assert.Equal(94f, ModSettingsGrid.ControlHeightFor("multiselect", 94f));
        Assert.Equal(124f, ModSettingsGrid.ControlHeightFor("radio", 124f));
        Assert.Equal((ModSettingsGrid.ControlHeight - ModSettingsGrid.SwitchHeight) / 2f, ModSettingsGrid.ControlTop("toggle", ModSettingsGrid.SwitchHeight));
        Assert.Equal(0f, ModSettingsGrid.ControlTop("input", ModSettingsGrid.ControlHeight));
    }

    /// <summary>
    /// Header buttons are sized to their caption (never the estimate that drew "Exam…" live), the
    /// store link is a captioned <b>Store page</b> button in the row only when a url is known, and no
    /// stray <c>»</c> icon is drawn.
    /// </summary>
    [Fact]
    public void Header_buttons_fit_their_caption_and_the_store_link_is_a_captioned_button()
    {
        foreach (var caption in new[] { "Example", "Store page", "A long page title", "" })
        {
            float estimate = CellLayout.EstimateTextWidth(caption, HeadBlockLayout.MetaFontSize);
            Assert.True(HeadBlockLayout.ButtonWidth(caption) >= estimate * HeadBlockLayout.ButtonCaptionSlack + CellLayout.ButtonPad);
            Assert.True(HeadBlockLayout.ButtonWidth(caption) >= HeadBlockLayout.MinToggleWidth);
        }

        var pages = new List<(string, Action)> { ("Example", () => { }) };
        // MODPAGE2: the Details grid's Pages and Store page rows.
        var page = ModPagePane.PageButtons(pages).Single(b => b.Key == ModPagePane.PageButtonKey(0));
        Assert.True(page.Width.Value >= HeadBlockLayout.ButtonWidth("Example") - 0.01f);
        Assert.Equal(ModPageCopy.StoreLinkCaption, ModPagePane.StoreButtons("https://example.test/p", false).Single(b => b.Key == ModPagePane.CellStore).Text);
        Assert.DoesNotContain(ModPagePane.StoreButtons("", false), b => b.Key == ModPagePane.CellStore);

        const string id = "example.stage2.store";
        ModSettingsPages.Register(_owner, id, p => p.Info("x", () => "y"));
        Open(id);
        Assert.DoesNotContain(MenuPanelLayout.IconLinkGlyph, _runtime.WidgetTexts("button"));
        var store = Row(PageRows(), ModPagePane.RowStoreLink).GetProperty(ModPagePane.RowStoreLink);
        Assert.Equal("", store.GetProperty("url").GetString());
        Assert.False(store.GetProperty("shown").GetBoolean());
    }

    /// <summary>
    /// A button row has a name on the left and a normal button in the control column: the builder's
    /// <c>label</c>, else the caption; a registered action's row is its name with a <b>Run</b> button.
    /// </summary>
    [Fact]
    public void Button_rows_have_a_name_and_a_control_column_button()
    {
        const string id = "example.stage2.named";
        int runs = 0;
        ModSettingsPages.Register(_owner, id, page => page
            .Button("Log a line", () => runs++, label: "Sample log")
            .Button("Ping", () => runs++));
        Open(id);
        Assert.Equal("Sample log", _runtime.NameOf("Log a line"));
        Assert.Equal("Ping", _runtime.NameOf("Ping"));
        Assert.True(_runtime.GridOf("button", "Log a line"));
        Assert.Equal("Sample log", SettingRow(PageRows(), id + "/button/Log a line").GetProperty("displayName").GetString());
        Assert.True(_runtime.FireClick("Log a line"));
        Assert.Equal(1, runs);
    }

    /// <summary>A read-only server value says it once: no scope marker after the server reason.</summary>
    [Fact]
    public void A_read_only_server_row_does_not_repeat_the_server_reason()
    {
        var file = File("readonce");
        var synced = new BoundedEntry<int>(file, "R", "Limit", 99, null);
        SettingsCatalog.UseSyncedProbe(e => ReferenceEquals(e, synced));
        var row = SettingsRow.ForEntry("example.readonce", synced, true);
        string help = ModSettingsLayout.HelpText(row, new ModSettingOptions { Scope = SettingScope.Server }, SettingControl.ReadOnly, null, null);
        Assert.Equal(ModSettingsLayout.ReadOnlyAwaitingReason, help);
        Assert.DoesNotContain(ModSettingsLayout.ScopeServerMarker, help, StringComparison.Ordinal);
        string local = ModSettingsLayout.HelpText(row, new ModSettingOptions { Scope = SettingScope.Local }, SettingControl.ReadOnly, null, null);
        Assert.Contains(ModSettingsLayout.ScopeLocalMarker, local, StringComparison.Ordinal);
    }

    // ---- review of 28d23886 -------------------------------------------------------------------------

    /// <summary>
    /// Reset all acts on (and counts) only rows whose own reset would work: a row show-if hides or
    /// enable-if grays keeps its value. What this would catch: Reset all rewriting a grayed row.
    /// </summary>
    [Fact]
    public void Reset_all_skips_hidden_and_greyed_rows()
    {
        var file = File("resetall2");
        const string id = "example.stage2.resetall";
        var gate = new BoundedEntry<bool>(file, "A", "Gate", true, null);
        var hidden = new BoundedEntry<int>(file, "A", "Hidden", 1, null) { Stored = 5 };
        var greyed = new BoundedEntry<int>(file, "A", "Greyed", 1, null) { Stored = 5 };
        var plain = new BoundedEntry<int>(file, "A", "Plain", 1, null) { Stored = 5 };
        ModSettingsPages.Register(_owner, id, page => page
            .Setting(gate)
            .Setting(hidden, showIf: () => false)
            .Setting(greyed, enableIf: () => false)
            .Setting(plain));
        Open(id);
        var view = ModsMenu.ModPage!.SettingsView!;
        Assert.Equal(new[] { id + "/A/Plain" }, view.ResettableRows().Select(r => r.RowId).ToArray());
        Assert.Equal(1, Row(PageRows(), ModSettingsView.RowResetAll).GetProperty(ModSettingsView.RowResetAll).GetProperty("resettable").GetInt32());
        Assert.True(ModsMenu.ConfirmResetAll(view));
        Assert.Equal(1, plain.Stored);
        Assert.Equal(5, hidden.Stored);
        Assert.Equal(5, greyed.Stored);
    }

    /// <summary>
    /// A text multi-select matches stored values case-insensitively (writing the canonical spelling)
    /// and keeps stored values it does not list; an entry with an AcceptableValueList is not offered a
    /// multi-select. What this would catch: a click silently dropping "Crafting" from the cfg.
    /// </summary>
    [Fact]
    public void A_text_multi_select_keeps_unknown_values_and_matches_case_insensitively()
    {
        var file = File("textmulti");
        const string id = "example.textmulti";
        var entry = file.Bind("T", "Topics", "combat, Crafting", "");
        var row = SettingsRow.ForEntry(id, entry, false, presentation: new SettingPresentation { Choices = new[] { "Combat", "Travel" } });
        Assert.Equal(new[] { "Combat", "Crafting" }, ModSettingsLayout.SelectedFor(row, entry.Value).ToArray());
        Assert.True(ModSettingsLayout.TryFormatSelection(row, new[] { "Combat", "Travel" }, out var text, out _, entry.Value));
        Assert.Equal("Combat, Travel, Crafting", text);
        Assert.True(ModSettingsLayout.TryFormatSelection(row, Array.Empty<string>(), out text, out _, entry.Value));
        Assert.Equal("Crafting", text);

        var listed = new BoundedEntry<string>(file, "T", "Mode", "A", new ModSettingsPageTests.FakeList("A", "B"));
        var listedRow = SettingsRow.ForEntry(id, listed, false);
        Assert.Empty(ModSettingsLayout.MultiChoicesFor(listedRow));
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.ControlFor(listedRow, new ModSettingOptions { Control = SettingControl.MultiSelect }));
        ModSettingsPages.Register(_owner, id, page => page.MultiSelect(listed));
        Assert.Contains(_log.Warnings, l => l.Contains("accepts one value", StringComparison.Ordinal));
    }

    /// <summary>
    /// [Flags] round trip: a stored value with bits no name covers (written as a number) is read, and
    /// a click keeps those bits; a flag at 1 &lt;&lt; 31 is an option, not a silent fallback to text.
    /// </summary>
    [Fact]
    public void Flags_keep_unnamed_bits_and_the_high_bit()
    {
        var file = File("flags");
        const string id = "example.flags";
        var entry = new BoundedEntry<Tags>(file, "F", "Tags", Tags.Combat, null) { Stored = (Tags)9 };
        var row = SettingsRow.ForEntry(id, entry, false);
        Assert.Equal("9", row.Read());
        Assert.True(SettingsEditors.TryParse("9", typeof(Tags), out var parsed, out _));
        Assert.Equal((Tags)9, parsed);
        Assert.False(SettingsEditors.TryParse("1", typeof(DayOfWeek), out _, out _));
        Assert.Equal(new[] { "Combat" }, ModSettingsLayout.SelectedFor(row, row.Read()).ToArray());
        Assert.True(ModSettingsLayout.TryFormatSelection(row, new[] { "Combat", "Loot" }, out var text, out _, row.Read()));
        Assert.Equal("11", text);
        Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.Submit(row, text).State);
        Assert.Equal((Tags)11, entry.Stored);
        Assert.True(ModSettingsLayout.TryFormatSelection(row, new[] { "Loot" }, out text, out _, "Combat"));
        Assert.Equal("Loot", text);

        Assert.Equal(new[] { "Low", "High" }, ModSettingsLayout.FlagOptions(typeof(HighBit)).ToArray());
        var high = SettingsRow.ForEntry(id, file.Bind("F", "High", HighBit.High, ""), false);
        Assert.Equal(SettingControl.MultiSelect, ModSettingsLayout.ControlFor(high));
        Assert.Equal(new[] { "High" }, ModSettingsLayout.SelectedFor(high, high.Read()).ToArray());
    }

    /// <summary>A list radio or multi-select longer than <see cref="ModSettingsLayout.MaxListOptions"/> falls back so no row passes the row cap.</summary>
    [Fact]
    public void Long_option_lists_fall_back_instead_of_growing_past_the_row_cap()
    {
        var file = File("longlists");
        var many = Enumerable.Range(0, ModSettingsLayout.MaxListOptions + 1).Select(i => "o" + i).ToArray();
        var entry = file.Bind("L", "Pick", "o0", "");
        var row = SettingsRow.ForEntry("example.long", entry, false, presentation: new SettingPresentation { Choices = many });
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.ControlFor(row, new ModSettingOptions { Control = SettingControl.Radio }));
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.DrawnControl(row, new ModSettingOptions { Control = SettingControl.Radio }));
        Assert.Equal(SettingControl.Text, ModSettingsLayout.ControlFor(row, new ModSettingOptions { Control = SettingControl.MultiSelect }));
        var few = SettingsRow.ForEntry("example.long", entry, false, presentation: new SettingPresentation { Choices = many.Take(ModSettingsLayout.MaxListOptions).ToArray() });
        Assert.Equal(SettingControl.MultiSelect, ModSettingsLayout.ControlFor(few, new ModSettingOptions { Control = SettingControl.MultiSelect }));
        Assert.True(ModSettingsLayout.MaxListOptions * 30f + 4f <= PanelLayout.MaxRowHeight);
    }

    /// <summary>Info refresh intervals are clamped; closing the page drops its copies of the plugin's delegates.</summary>
    [Fact]
    public void Info_refresh_is_clamped_and_release_drops_the_page_copies_of_delegates()
    {
        var panel = UiApi.Panel("Clamp", _owner, "example.ui.test");
        Assert.Equal(UiInfoHandle.MaxRefreshSeconds, panel.Info("slow", () => "x", 1000).RefreshSeconds);
        Assert.Equal(UiInfoHandle.MaxRefreshSeconds, panel.Info("inf", () => "x", double.PositiveInfinity).RefreshSeconds);
        Assert.Equal(UiInfoHandle.MinRefreshSeconds, panel.Info("fast", () => "x", 0.01).RefreshSeconds);
        panel.Dispose();

        const string id = "example.stage2.release";
        var registration = ModSettingsPages.Register(_owner, id, page => page.Button("Go", () => { }).Info("I", () => "v"));
        Open(id);
        var view = ModsMenu.ModPage!.SettingsView!;
        var items = view.Sections.SelectMany(s => s.Items).ToList();
        Assert.Contains(items, i => i.ButtonAction != null);
        ModsMenu.Close();
        Assert.All(items, i => Assert.True(i.ButtonAction == null && i.InfoValue == null));
        // The registration's own delegates stay for the next page build.
        Assert.Contains(registration.SectionSpecs.SelectMany(s => s.Items), i => i.Action != null);
    }

    /// <summary>A grayed keybind does not start a capture from its click.</summary>
    [Fact]
    public void A_greyed_keybind_does_not_start_a_capture()
    {
        var file = File("keys");
        Keybinds.Register(_owner, "example.keys", "Open", file, new KeyChord(UiKey.F7), "", () => { });
        var panel = UiApi.Panel("Keys", _owner, "example.ui.test");
        var key = (UiKeybindPickerHandle)panel.KeyField("Open", "example.keys", "Open");
        key.Interactable = false;
        UiSlotTable.BootstrapDispatch(key.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.False(key.Capturing);
        key.Interactable = true;
        UiSlotTable.BootstrapDispatch(key.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.True(key.Capturing);
        key.CancelCapture();
        panel.Dispose();
    }

    /// <summary>Two buttons or info lines with one caption get distinct ids.</summary>
    [Fact]
    public void Duplicate_button_and_info_captions_get_distinct_ids()
    {
        const string id = "example.stage2.dupes";
        var registration = ModSettingsPages.Register(_owner, id, page => page
            .Button("Run", () => { }).Button("Run", () => { }).Info("T", () => "a").Info("T", () => "b"));
        var ids = registration.Sections().SelectMany(s => s.Items).Select(i => i.Id).ToArray();
        Assert.Equal(new[] { id + "/button/Run", id + "/button/Run#2", id + "/info/T", id + "/info/T#2" }, ids);
    }

    /// <summary>An entry bound synced after Page() is opted into server edits when the page is drawn.</summary>
    [Fact]
    public void Server_scope_resolves_when_the_page_is_drawn()
    {
        ServerSettings.Install();
        var file = File("latescope");
        const string id = "example.stage2.latescope";
        var limit = file.Bind("S", "Limit", 5, "");
        var registration = ModSettingsPages.Register(_owner, id, page => page.Number(limit, scope: SettingScope.Server));
        Assert.False(ServerSettings.IsEditable(id + "/S/Limit"));
        Assert.Contains(_log.Warnings, l => l.Contains("not bound synced yet", StringComparison.Ordinal));
        using var sync = ConfigSync.Register(id, limit);
        registration.Sections();
        Assert.True(ServerSettings.IsEditable(id + "/S/Limit"));
        registration.Dispose();
        Assert.False(ServerSettings.IsEditable(id + "/S/Limit"));
    }

    [Flags]
    public enum HighBit
    {
        None = 0,
        Low = 1,
        High = 1 << 31,
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private ConfigFile File(string name) => new(Path.Combine(_dir, name + ".cfg"), saveOnInit: true);

    private static void Open(string id)
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(id);
    }

    private static void Run(List<Action> scheduled)
    {
        var copy = scheduled.ToArray();
        scheduled.Clear();
        foreach (var action in copy) action();
    }

    private JsonElement PageRows()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static bool TryRow(JsonElement rows, string key, out JsonElement row)
    {
        foreach (var candidate in rows.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object) continue;
            if (!candidate.TryGetProperty(key, out _)) continue;
            row = candidate;
            return true;
        }

        row = default;
        return false;
    }

    private static JsonElement Row(JsonElement rows, string key) =>
        TryRow(rows, key, out var row) ? row : throw new Xunit.Sdk.XunitException("no page row carries " + key);

    private static JsonElement SettingRow(JsonElement rows, string id)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty(ModSettingsView.RowSetting, out var setting)) continue;
            if (string.Equals(setting.GetProperty("id").GetString(), id, StringComparison.Ordinal)) return setting;
        }

        throw new Xunit.Sdk.XunitException("no setting row " + id);
    }

    private static JsonElement ResetRow(JsonElement rows, string id)
    {
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (!row.TryGetProperty(ModSettingsView.RowSettingReset, out var reset)) continue;
            if (string.Equals(reset.GetProperty("id").GetString(), id, StringComparison.Ordinal)) return reset;
        }

        throw new Xunit.Sdk.XunitException("no reset link for " + id);
    }

    private static string[] Strings(JsonElement array)
    {
        var list = new List<string>();
        foreach (var item in array.EnumerateArray()) list.Add(item.GetString() ?? "");
        return list.ToArray();
    }

    public enum Mode
    {
        Slow,
        Fast,
    }

    [Flags]
    public enum Tags
    {
        None = 0,
        Combat = 1,
        Loot = 2,
        All = Combat | Loot,
    }

    /// <summary>An <see cref="IUiPanel"/> that is not a Progenitor panel (a confirm button cannot open a dialog on it).</summary>
    private sealed class BareHeaderPanel : IUiPanel
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed => false;
        public UiHandleState State => UiHandleState.Shown;
        public string PluginId => "test";
        public string RootName => "test";
        public UiCaptureMode CaptureMode { get; set; }
        public IUiButton Button(string text) => new BareButton();
        public IUiToggle Toggle(string text, bool initial = false) => throw new NotSupportedException();
        public IUiLabel Label(string text) => throw new NotSupportedException();
        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();
        public IUiInput Input(string text, string initial = "") => throw new NotSupportedException();
        public void Show() { }
        public void Hide() { }
        public void Dispose() { }
    }

    private sealed class BareButton : IUiButton
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnClick(Action handler) => new ActionDisposable(() => { });
        public void Dispose() => IsDisposed = true;
    }

    /// <summary>A config entry with the live members the rows read reflectively (default, acceptable values).</summary>
    private sealed class BoundedEntry<T> : ConfigEntryBase
    {
        public BoundedEntry(ConfigFile file, string section, string key, T value, object? acceptable)
            : base(file, new ConfigDefinition(section, key), typeof(T))
        {
            Stored = value;
            DefaultValue = value;
            Description = new ModSettingsPageTests.FakeDescription(acceptable);
        }

        public object? Stored { get; set; }

        public object? DefaultValue { get; }

        public ModSettingsPageTests.FakeDescription Description { get; }

        public override object? BoxedValue
        {
            get => Stored;
            set => Stored = value;
        }
    }
}
