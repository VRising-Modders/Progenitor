using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SETTINGS1 (docs/mod-settings.md), stage 1 of the mod settings redesign: the framework-owned status
/// line, the mod's own header area, settings drawn inline in sections with the control each type and
/// bound asks for, the modified marker and reset, the search threshold, and the readback a live test
/// reads instead of a screenshot.
///
/// <para>Game-free: the fake UI runtime and the <c>ConfigFile</c> shim. Nothing here touches the
/// game; the live controls are covered by the acceptance procedure in the doc.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class ModSettingsPageTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private JsonDocument? _rows;

    public ModSettingsPageTests()
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
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-settings1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _rows?.Dispose();
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
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
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ---- the framework line ------------------------------------------------------------------

    /// <summary>
    /// Every status the framework line reports, from its evidence, in the order the rule decides it.
    /// What this would catch: a pause read as enabled, a pending restart hidden behind the switch, a
    /// server-listed row offering a switch for a mod this client does not have, or a stage-1 switch
    /// that claims to be interactive.
    /// </summary>
    [Fact]
    public void The_framework_line_reports_each_status_with_the_real_switch_position()
    {
        var enabled = ModFrameworkLine.Compose(new ModFrameworkFacts { Version = "1.2.0" });
        Assert.Equal(ModRunStatus.Enabled, enabled.Status);
        Assert.Equal("Enabled", enabled.StatusWord);
        Assert.Equal(LabelTone.Ok, enabled.StatusTone);
        Assert.True(enabled.SwitchVisible);
        Assert.True(enabled.SwitchOn);
        Assert.False(enabled.SwitchInteractive);

        var disabled = ModFrameworkLine.Compose(new ModFrameworkFacts { EnabledIntent = false });
        Assert.Equal(ModRunStatus.Disabled, disabled.Status);
        Assert.Equal("Disabled", disabled.StatusWord);
        Assert.False(disabled.SwitchOn);

        var paused = ModFrameworkLine.Compose(new ModFrameworkFacts { Paused = true, PageState = ModPageState.PendingLoad });
        Assert.Equal(ModRunStatus.Paused, paused.Status);
        Assert.Equal(ModMenuInventory.StatusPaused, paused.StatusWord);
        Assert.True(paused.SwitchOn, "a pause is the server's doing, not the player's switch");

        var resuming = ModFrameworkLine.Compose(new ModFrameworkFacts { Resuming = true });
        Assert.Equal(ModRunStatus.Paused, resuming.Status);
        Assert.Equal(ModMenuInventory.StatusResuming, resuming.StatusWord);

        var load = ModFrameworkLine.Compose(new ModFrameworkFacts { PageState = ModPageState.PendingLoad });
        Assert.Equal(ModRunStatus.PendingRestart, load.Status);
        Assert.Equal(ModPageCopy.StatusPendingLoad, load.StatusWord);
        var uninstall = ModFrameworkLine.Compose(new ModFrameworkFacts { PageState = ModPageState.PendingUninstall });
        Assert.Equal(ModPageCopy.StatusPendingUninstall, uninstall.StatusWord);
        Assert.Equal(LabelTone.Orange, uninstall.StatusTone);

        var remote = ModFrameworkLine.Compose(new ModFrameworkFacts { OnClient = false });
        Assert.Equal(ModRunStatus.NotOnClient, remote.Status);
        Assert.False(remote.SwitchVisible);

        // The one reading a log or a test sees. Stage 2: the version is `v1.2.0`, plain text.
        Assert.Equal("On · Enabled · v1.2.0", enabled.Text);
    }

    /// <summary>
    /// Sides from the strongest evidence first, and the server part only for a two-sided mod and
    /// only when something says so.
    /// What this would catch: a server-part sentence on a client-only mod, a guess drawn when
    /// nothing is known, or a pause not read as "the server does not run it".
    /// </summary>
    [Fact]
    public void Sides_and_the_server_part_are_reported_only_from_evidence()
    {
        Assert.Equal(ModSides.Both, ModFrameworkLine.DecideSides(PackageSide.Client, requiresBoth: true));
        Assert.Equal(ModSides.Server, ModFrameworkLine.DecideSides(PackageSide.Server, false));
        Assert.Equal(ModSides.Client, ModFrameworkLine.DecideSides(PackageSide.Unknown, false, NetworkCompatibilityLevel.ClientOnly));
        Assert.Equal(ModSides.Both, ModFrameworkLine.DecideSides(PackageSide.Unknown, false, NetworkCompatibilityLevel.ServerMustHave));
        Assert.Equal(ModSides.Unknown, ModFrameworkLine.DecideSides(PackageSide.Unknown, false, NetworkCompatibilityLevel.NoNeedForSync));

        Assert.Equal(ServerPartState.Unknown, ModFrameworkLine.DecideServerPart(ModSides.Client, paused: true, serverAskedAboutIt: true));
        Assert.Equal(ServerPartState.NotOnServer, ModFrameworkLine.DecideServerPart(ModSides.Both, true, false));
        Assert.Equal(ServerPartState.Running, ModFrameworkLine.DecideServerPart(ModSides.Both, false, true));
        Assert.Equal(ServerPartState.Unknown, ModFrameworkLine.DecideServerPart(ModSides.Both, false, false));

        var both = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            Version = "2.0.0", Sides = ModSides.Both, ServerPart = ServerPartState.Running,
        });
        Assert.Equal("On · Enabled · v2.0.0 · Client and server · " + ModFrameworkLine.ServerPartRunning, both.Text);
        var client = ModFrameworkLine.Compose(new ModFrameworkFacts
        {
            Sides = ModSides.Client, ServerPart = ServerPartState.Running,
        });
        Assert.Equal("", client.ServerPartText);

        // The drawn row: the switch is a track-and-knob switch cell (never a button that does
        // nothing), the sides a chip in plain words, no server-part cell for a one-sided mod.
        var row = client.BuildRow("page-framework", 1200f);
        Assert.Equal(CellKind.Switch, row.Find(ModFrameworkLine.CellSwitch)!.Kind);
        Assert.Equal("Client only", row.Find(ModFrameworkLine.CellSides)!.Text);
        Assert.Null(row.Find(ModFrameworkLine.CellServerPart));
        var bothRow = both.BuildRow("page-framework", 1200f);
        Assert.Equal(ModFrameworkLine.ServerPartRunning, bothRow.Find(ModFrameworkLine.CellServerPart)!.Text);

        var readback = both.Readback();
        var sw = (Dictionary<string, object?>)readback["switch"]!;
        Assert.Equal(false, sw["interactive"]);
        Assert.Equal(ModFrameworkLine.SwitchUnavailableReason, sw["reason"]);
        Assert.Equal("both", readback["sides"]);
        Assert.Equal("running", readback["serverPart"]);
        Assert.Equal("framework", readback["owner"]);
    }

    // ---- the mod's header area ---------------------------------------------------------------

    [Fact]
    public void The_default_by_line_drops_what_is_unknown()
    {
        Assert.Equal("by Ana · Makes nights longer.", ModHeaderDefault.ByLine("Ana", "Makes nights longer."));
        Assert.Equal("by Ana", ModHeaderDefault.ByLine("Ana", ""));
        Assert.Equal("Makes nights longer.", ModHeaderDefault.ByLine(null, "Makes nights longer."));
        Assert.Equal("", ModHeaderDefault.ByLine(" ", null));
    }

    /// <summary>
    /// A mod's header content is drawn under the framework line, which stays the page's first row
    /// with the same facts; a builder that throws falls back to the default and says so.
    /// What this would catch: a custom header drawn above (or instead of) the framework line, the
    /// default title drawn as well as the custom content, or a throwing builder leaving a page with
    /// no name.
    /// </summary>
    [Fact]
    public void A_custom_header_draws_under_the_framework_line_and_a_throwing_one_falls_back()
    {
        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, "example.headerdemo", "HeaderDemo", "A header demo.");
        mod.Header(panel => panel.Label("Custom banner for HeaderDemo"));
        Assert.Throws<ArgumentException>(() => mod.Header(_ => { }));
        ModsMenu.OpenMod("example.headerdemo");

        var rows = PageRows();
        var labels = RowLabels(rows);
        int framework = Array.IndexOf(labels, ModPagePane.FrameworkKey);
        int banner = Array.IndexOf(labels, "Custom banner for HeaderDemo");
        Assert.True(framework >= 0, "the framework line is drawn");
        // MODPAGE2: the mod's own header takes the title's place, above the actions bar.
        Assert.True(banner >= 0 && banner < framework, "the mod's content is where the title would be, above the actions bar");
        Assert.DoesNotContain(ModPagePane.HeadKey + "-title", labels);
        var header = Row(rows, ModPagePane.RowHeader).GetProperty(ModPagePane.RowHeader);
        Assert.Equal("custom", header.GetProperty("kind").GetString());
        Assert.Equal("enabled", Row(rows, ModPagePane.RowFramework).GetProperty(ModPagePane.RowFramework).GetProperty("status").GetString());
        Assert.Equal("HeaderDemo", Row(rows, ModPagePane.RowTitle).GetProperty(ModPagePane.RowTitle).GetString());

        using var broken = ModsMenu.Register(new object(), "example.brokenheader", "BrokenHeader", "Throws.");
        broken.Header(_ => throw new InvalidOperationException("boom"));
        ModsMenu.OpenMod("example.brokenheader");
        var fallback = PageRows();
        var fallbackHeader = Row(fallback, ModPagePane.RowHeader).GetProperty(ModPagePane.RowHeader);
        Assert.Equal("default", fallbackHeader.GetProperty("kind").GetString());
        Assert.Equal("boom", fallbackHeader.GetProperty("error").GetString());
        Assert.Contains(ModPagePane.HeadKey + "-title", RowLabels(fallback));
        Assert.Equal("Throws.", Row(fallback, ModPagePane.RowOneLiner).GetProperty(ModPagePane.RowOneLiner).GetString());
    }

    // ---- sections ------------------------------------------------------------------------------

    /// <summary>
    /// Groups first in registration order, then the mod's other entries, keybinds and actions; a
    /// row once; empty sections dropped.
    /// What this would catch: a grouped entry repeated under General, keybinds mixed into General,
    /// or an empty heading.
    /// </summary>
    [Fact]
    public void Sections_are_the_groups_then_general_then_keys_then_actions()
    {
        var file = new ConfigFile(Path.Combine(_dir, "sections.cfg"), saveOnInit: true);
        var speed = file.Bind("Gameplay", "Speed", 6, "How fast.");
        var name = file.Bind("Gameplay", "Name", "x", "");
        var other = file.Bind("Misc", "Other", true, "");
        const string id = "example.sections";
        var group = ModSettingsLayout.RowsOf(id, new ConfigEntryBase[] { speed });
        var catalog = new List<SettingsRow>
        {
            SettingsRow.ForEntry(id, speed, false),
            SettingsRow.ForEntry(id, name, false),
            SettingsRow.ForEntry(id, other, false),
        };

        var sections = ModSettingsLayout.Build(
            new List<(string, IReadOnlyList<SettingsRow>)>
            {
                ("Personal", group),
                ("Empty", new List<SettingsRow>()),
            },
            catalog);

        Assert.Equal(new[] { "Personal", ModSettingsLayout.GeneralTitle }, sections.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { id + "/Gameplay/Speed" }, sections[0].Rows.Select(r => r.RowId).ToArray());
        Assert.Equal(
            new[] { id + "/Gameplay/Name", id + "/Misc/Other" },
            sections[1].Rows.Select(r => r.RowId).ToArray());
        Assert.Equal(ModSettingsSectionKind.General, sections[1].Kind);
        Assert.Equal(3, ModSettingsLayout.RowCount(sections));
    }

    // ---- controls ------------------------------------------------------------------------------

    /// <summary>
    /// The control each row gets, by edit path, hint, type and bounds.
    /// What this would catch: an enum or an AcceptableValueList drawn as free text, a bounded number
    /// without a slider, an unbounded one with a slider it cannot scale, a [Flags] enum forced into
    /// a one-of selector, or a server-owned value given an editor.
    /// </summary>
    [Fact]
    public void Each_type_and_bound_gets_its_control()
    {
        var file = new ConfigFile(Path.Combine(_dir, "controls.cfg"), saveOnInit: true);
        const string id = "example.controls";
        SettingsRow Row(ConfigEntryBase e, SettingPresentation? p = null, bool synced = false) =>
            SettingsRow.ForEntry(id, e, synced, presentation: p);

        Assert.Equal(SettingControl.Toggle, ModSettingsLayout.ControlFor(Row(file.Bind("A", "Flag", true, ""))));
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.ControlFor(Row(file.Bind("A", "Mode", Mode.Slow, ""))));
        // Stage 2: a [Flags] enum is a multi-select of its single-bit values.
        Assert.Equal(SettingControl.MultiSelect, ModSettingsLayout.ControlFor(Row(file.Bind("A", "Tags", Tags.Combat, ""))));
        Assert.Equal(SettingControl.Number, ModSettingsLayout.ControlFor(Row(file.Bind("A", "Count", 3, ""))));
        Assert.Equal(SettingControl.Number, ModSettingsLayout.ControlFor(Row(file.Bind("A", "Scale", 1.5f, ""))));
        Assert.Equal(SettingControl.Text, ModSettingsLayout.ControlFor(Row(file.Bind("A", "Motd", "hi", ""))));

        // An AcceptableValueRange is a slider by default; an explicit Numeric hint keeps the stepper.
        var range = new BoundedEntry<int>(file, "B", "Limit", 10, new FakeRange(1, 20));
        Assert.Equal("1 to 20", Row(range).Bounds);
        Assert.Equal(SettingControl.Slider, ModSettingsLayout.ControlFor(Row(range)));
        Assert.Equal(
            SettingControl.Number,
            ModSettingsLayout.ControlFor(Row(range, new SettingPresentation { EditorHint = SettingsEditorHint.Numeric })));

        // An AcceptableValueList is a dropdown of its values.
        var list = new BoundedEntry<string>(file, "B", "Difficulty", "Med", new FakeList("Low", "Med", "High"));
        Assert.Equal(SettingControl.Dropdown, ModSettingsLayout.ControlFor(Row(list)));
        Assert.Equal(new[] { "Low", "Med", "High" }, ModSettingsLayout.ChoicesFor(Row(list)).Select(c => c.Value).ToArray());

        // Presentation bounds make a slider too; a color hint makes a color field.
        Assert.Equal(
            SettingControl.Slider,
            ModSettingsLayout.ControlFor(Row(file.Bind("C", "Volume", 0.5f, ""), new SettingPresentation { Min = "0", Max = "1" })));
        Assert.Equal(
            SettingControl.Color,
            ModSettingsLayout.ControlFor(Row(file.Bind("C", "Tint", "#FF0000", ""), new SettingPresentation { EditorHint = SettingsEditorHint.Color })));

        // Nothing may write a synced value here, whatever its type.
        Assert.Equal(SettingControl.ReadOnly, ModSettingsLayout.ControlFor(Row(file.Bind("D", "Synced", 5, ""), synced: true)));
    }

    /// <summary>
    /// A number's step and decimals keep the stored value on the grid: the value the player has is
    /// never shown rounded, an integer steps by one, and a declared range gets a readable step.
    /// </summary>
    [Fact]
    public void Number_bounds_step_and_decimals_keep_the_stored_value_exact()
    {
        var file = new ConfigFile(Path.Combine(_dir, "numbers.cfg"), saveOnInit: true);
        const string id = "example.numbers";
        var integer = ModSettingsLayout.NumberFor(SettingsRow.ForEntry(id, new BoundedEntry<int>(file, "N", "I", 10, new FakeRange(1, 20)), false));
        Assert.True(integer.Bounded);
        Assert.True(integer.Integral);
        Assert.Equal(1, integer.Min);
        Assert.Equal(20, integer.Max);
        Assert.Equal(1, integer.Step);
        Assert.Equal("12", integer.Format(12.4));

        var fine = ModSettingsLayout.NumberFor(SettingsRow.ForEntry(id, file.Bind("N", "F", 1.25f, ""), false));
        Assert.False(fine.Bounded);
        Assert.Equal(0.01, fine.Step, 6);
        Assert.Equal(2, fine.Decimals);
        var state = new UiNumericState(1.25, fine.Min, fine.Max, fine.Step, fine.Decimals, "");
        Assert.Equal("1.25", state.Draft);

        var ranged = ModSettingsLayout.NumberFor(SettingsRow.ForEntry(
            id, file.Bind("N", "R", 0.5f, ""), false, presentation: new SettingPresentation { Min = "0", Max = "1", Units = "x" }));
        Assert.True(ranged.Bounded);
        Assert.Equal(0.01, ranged.Step, 6);
        Assert.Equal("x", ranged.Units);
    }

    // ---- modified marker, reset, search ------------------------------------------------------

    /// <summary>
    /// A row is modified when its stored value differs from its default; reset writes the default
    /// through the ordinary path; a server-owned row is never offered a reset.
    /// </summary>
    [Fact]
    public void Reset_puts_the_default_back_and_the_marker_follows_the_value()
    {
        var file = new ConfigFile(Path.Combine(_dir, "reset.cfg"), saveOnInit: true);
        const string id = "example.reset";
        var entry = new BoundedEntry<int>(file, "R", "Count", 5, new FakeRange(1, 10));
        var row = SettingsRow.ForEntry(id, entry, false);
        Assert.False(ModSettingsLayout.IsModified(row));
        Assert.False(ModSettingsLayout.CanReset(row));
        Assert.Equal("", ModSettingsView.NoteText(row, ""));

        Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.Submit(row, "8").State);
        Assert.True(ModSettingsLayout.IsModified(row));
        Assert.True(ModSettingsLayout.CanReset(row));
        Assert.Equal(ModSettingsLayout.ChangedFromPrefix + "5.", ModSettingsView.NoteText(row, ""));

        Assert.Equal(SettingWriteState.Accepted, ModSettingsLayout.Reset(row).State);
        Assert.Equal("5", row.Read());
        Assert.False(ModSettingsLayout.IsModified(row));

        var synced = SettingsRow.ForEntry(id, new BoundedEntry<int>(file, "R", "Owned", 1, null) { Stored = 3 }, synced: true);
        Assert.True(ModSettingsLayout.IsModified(synced));
        Assert.False(ModSettingsLayout.CanReset(synced), "the server owns it");
    }

    [Fact]
    public void Search_shows_above_ten_rows_and_filters_by_caption_key_or_section()
    {
        Assert.False(ModSettingsLayout.ShowSearch(10));
        Assert.True(ModSettingsLayout.ShowSearch(11));

        var file = new ConfigFile(Path.Combine(_dir, "search.cfg"), saveOnInit: true);
        const string id = "example.search";
        var rows = new List<SettingsRow>
        {
            SettingsRow.ForEntry(id, file.Bind("Combat", "Damage", 1, "Hit harder."), false),
            SettingsRow.ForEntry(id, file.Bind("Travel", "Speed", 1, ""), false),
        };
        var sections = new List<ModSettingsSection>
        {
            new("One", ModSettingsSectionKind.Group, new List<SettingsRow> { rows[0] }),
            new("Movement", ModSettingsSectionKind.Group, new List<SettingsRow> { rows[1] }),
        };

        Assert.Equal(new[] { "One" }, ModSettingsLayout.Filter(sections, "damage").Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "Movement" }, ModSettingsLayout.Filter(sections, "movement").Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "Movement" }, ModSettingsLayout.Filter(sections, "TRAVEL").Select(s => s.Title).ToArray());
        Assert.Equal(2, ModSettingsLayout.Filter(sections, "").Count);
        Assert.Empty(ModSettingsLayout.Filter(sections, "nothing"));
    }

    // ---- the page and its readback -----------------------------------------------------------

    /// <summary>
    /// The whole page through the workspace: sections as headings, a search field above ten rows,
    /// Reset all, and each setting's readback — which follows a commit and a reset without a rebuild.
    /// What this would catch: settings still behind buttons to another pane, a readback frozen at
    /// build time, a reset link that never appears or never goes, or a search field on a short page.
    /// </summary>
    [Fact]
    public void Settings_are_inline_sections_whose_readback_follows_commits_and_resets()
    {
        var file = new ConfigFile(Path.Combine(_dir, "page.cfg"), saveOnInit: true);
        const string id = "example.settingspage";
        // The live entry's DefaultValue, which the shim does not carry, is what reset writes back.
        var flag = new BoundedEntry<bool>(file, "Personal", "ShowHints", true, null);
        var extra = new List<ConfigEntryBase>();
        for (int i = 0; i < 10; i++) extra.Add(file.Bind("More", "Item" + i, i, ""));
        SettingsCatalog.Register(id, file, _owner);

        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, id, "SettingsPage", "Inline settings.");
        mod.Settings("Personal", flag);
        ModsMenu.OpenMod(id);

        var rows = PageRows();
        var sections = new List<string>();
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(ModSettingsView.RowSection, out var s))
                sections.Add(s.GetProperty("title").GetString() ?? "");
        // Settings scopes (step 4): both groups are Your settings; the "Personal" subheading restates the
        // scope section, so it is not drawn (its rows keep their order and their readback section).
        Assert.Equal(new[] { ModSettingsLayout.GeneralTitle }, sections.ToArray());
        Assert.Equal("Personal", SettingRow(rows, id + "/Personal/ShowHints").GetProperty("section").GetString());
        Assert.Equal(SettingScopeSections.YourHeading,
            Row(rows, ModSettingsScopes.RowScopeSection).GetProperty(ModSettingsScopes.RowScopeSection).GetProperty("heading").GetString());

        var search = Row(rows, ModSettingsView.RowSearch).GetProperty(ModSettingsView.RowSearch);
        Assert.Equal(11, search.GetProperty("total").GetInt32());
        Assert.Equal(11, search.GetProperty("shown").GetInt32());
        Assert.Equal(0, Row(rows, ModSettingsView.RowResetAll).GetProperty(ModSettingsView.RowResetAll).GetProperty("resettable").GetInt32());

        var hints = SettingRow(rows, id + "/Personal/ShowHints");
        Assert.Equal("toggle", hints.GetProperty("control").GetString());
        Assert.Equal("true", hints.GetProperty("value").GetString());
        Assert.False(hints.GetProperty("modified").GetBoolean());
        Assert.Equal("number", SettingRow(rows, id + "/More/Item3").GetProperty("control").GetString());

        // A commit through the control the player uses: the readback follows it in place. Stage 2:
        // the row is drawn under its display name, derived from the cfg key.
        Assert.Equal("Show hints", hints.GetProperty("displayName").GetString());
        Assert.Equal("[Personal] ShowHints", hints.GetProperty("label").GetString());
        Assert.True(_runtime.FireToggle("Show hints", false));
        Assert.Equal(false, flag.Stored);
        rows = PageRows();
        hints = SettingRow(rows, id + "/Personal/ShowHints");
        Assert.Equal("false", hints.GetProperty("value").GetString());
        Assert.True(hints.GetProperty("modified").GetBoolean());
        Assert.True(ResetRow(rows, id + "/Personal/ShowHints").GetProperty("visible").GetBoolean());
        Assert.Equal(1, Row(rows, ModSettingsView.RowResetAll).GetProperty(ModSettingsView.RowResetAll).GetProperty("resettable").GetInt32());

        // The reset link puts the default back and hides itself.
        var view = ModsMenu.ModPage!.SettingsView!;
        Assert.Equal(SettingWriteState.Accepted, ModSettingsLayout.Reset(SettingsRow.ForEntry(id, flag, false)).State);
        view.Refresh(SettingsRow.ForEntry(id, flag, false));
        Assert.Equal(true, flag.Stored);
        rows = PageRows();
        Assert.False(SettingRow(rows, id + "/Personal/ShowHints").GetProperty("modified").GetBoolean());

        // A filter hides the other rows in place: the page is not rebuilt (review: a focus-loss
        // commit that rebuilt the page destroyed the control the player had just clicked).
        var page = ModsMenu.ModPage;
        var head = (UiCellRowHandle)ModsMenu.ModPage!.SettingsView!.HeaderRow!;
        UiSlotTable.BootstrapDispatch(head.CellSlotId(ModSettingsView.CellSearch), (byte)UiSlotKind.Text, 0f, false, "Item7");
        Assert.Same(page, ModsMenu.ModPage);
        rows = PageRows();
        Assert.Throws<Xunit.Sdk.XunitException>(() => SettingRow(rows, id + "/More/Item3"));
        Assert.Equal("number", SettingRow(rows, id + "/More/Item7").GetProperty("control").GetString());
        Assert.Equal(1, Row(rows, ModSettingsView.RowSearch).GetProperty(ModSettingsView.RowSearch).GetProperty("shown").GetInt32());
        Assert.Equal("Item7", Row(rows, ModSettingsView.RowSearch).GetProperty(ModSettingsView.RowSearch).GetProperty("query").GetString());
    }

    /// <summary>A page with ten settings or fewer has no search field.</summary>
    [Fact]
    public void A_short_page_has_no_search_field()
    {
        var file = new ConfigFile(Path.Combine(_dir, "short.cfg"), saveOnInit: true);
        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, "example.short", "Short", "Few settings.");
        mod.Settings("Personal", file.Bind("Personal", "A", 1, ""), file.Bind("Personal", "B", true, ""));
        ModsMenu.OpenMod("example.short");
        var rows = PageRows();
        Assert.False(TryRow(rows, ModSettingsView.RowSearch, out _));
        Assert.True(TryRow(rows, ModSettingsView.RowResetAll, out _));
    }

    // ---- the controls' raw events ------------------------------------------------------------

    /// <summary>
    /// The published controls commit from the raw events the runtime wires: the number field's
    /// end-of-edit and its steppers, the selector's arrows (wrapping), a slider sample.
    /// What this would catch: a control that draws and never writes — the shells these were before
    /// SETTINGS1 — or a raw event reaching the plugin's handler before the state machine snapped it.
    /// </summary>
    [Fact]
    public void A_dropdown_opens_without_a_write_and_only_one_list_is_open()
    {
        // DROPDOWN1: a click opens, a pick writes once, a dismiss never writes, and opening one list closes another.
        var panel = UiApi.Panel("Dropdowns", _owner, "example.ui.test");
        var a = (UiChoiceHandle)panel.Dropdown("A", new[] { "x", "y", "z" }, "x");
        var b = (UiChoiceHandle)panel.Dropdown("B", new[] { "p", "q" }, "p");
        var writes = new List<string>();
        a.OnChanged(v => writes.Add("a:" + v));
        b.OnChanged(v => writes.Add("b:" + v));

        UiSlotTable.BootstrapDispatch(a.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.True(a.IsOpen);
        Assert.Same(a, UiChoiceHandle.OpenNow);
        UiSlotTable.BootstrapDispatch(b.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.False(a.IsOpen);
        Assert.True(b.IsOpen);
        Assert.True(UiApi.TryEscapeHandlers());
        Assert.False(b.IsOpen);
        Assert.False(UiApi.TryEscapeHandlers());
        Assert.Empty(writes);

        UiSlotTable.BootstrapDispatch(a.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.True(a.Choose("Z"));
        Assert.Equal(new[] { "a:z" }, writes.ToArray());
        Assert.Equal("z", a.Value);
        var list = a.DescribeList();
        Assert.Equal(false, list["open"]);
        Assert.Null(UiChoiceHandle.OpenNow);

        // Disposed while open: the list and its Escape go with it.
        UiSlotTable.BootstrapDispatch(b.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        b.Dispose();
        Assert.Null(UiChoiceHandle.OpenNow);
        Assert.False(UiApi.TryEscapeHandlers());
        Assert.Single(writes);
    }

    [Fact]
    public void The_published_controls_commit_from_their_raw_events()
    {
        var panel = UiApi.Panel("Controls", _owner, "example.ui.test");
        var number = panel.Number("Count", 2, 0, 10, 1);
        var seen = new List<double>();
        number.OnChanged(v => seen.Add(v));
        var numberHandle = (UiNumericHandle)number;
        UiSlotTable.BootstrapDispatch(numberHandle.WidgetId, (byte)UiSlotKind.Text, 0f, false, "7.6");
        Assert.Equal(8, number.Value);
        Assert.True(numberHandle.StepBy(1));
        Assert.Equal(new double[] { 8, 9 }, seen.ToArray());

        var dropdown = panel.Dropdown("Mode", new[] { "slow", "fast" }, "fast", new[] { "Slow", "Fast" });
        var picks = new List<string>();
        dropdown.OnChanged(v => picks.Add(v));
        var choice = (UiChoiceHandle)dropdown;
        // DROPDOWN1: a click opens the list and writes nothing; a pick from the list writes once.
        UiSlotTable.BootstrapDispatch(choice.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.True(choice.IsOpen);
        Assert.Equal("fast", dropdown.Value);
        Assert.Empty(picks);
        Assert.True(choice.ChooseIndex(0));
        Assert.False(choice.IsOpen);
        Assert.Equal("slow", dropdown.Value);
        // The off-grid selector's arrows still step and write.
        Assert.True(choice.Cycle(-1));
        Assert.Equal(new[] { "slow", "fast" }, picks.ToArray());
        dropdown.Value = "slow";
        Assert.Equal(2, picks.Count);
        Assert.Equal(new[] { "Slow", "Fast" }, dropdown.Labels.ToArray());

        var slider = panel.SliderField("Volume", 0f, 1f, 0.1f, 0.5f);
        var committed = new List<float>();
        slider.OnChanged(v => committed.Add(v));
        var sliderHandle = (UiSliderEntryHandle)slider;
        UiSliderEntryHandle.SettleScheduler = null;
        UiSlotTable.BootstrapDispatch(sliderHandle.WidgetId, (byte)UiSlotKind.Float, 0.72f, false, null);
        Assert.Single(committed);
        Assert.Equal(0.7f, slider.Value, 3);
        UiSlotTable.BootstrapDispatch(sliderHandle.WidgetId, (byte)UiSlotKind.Text, 0f, false, "0.2");
        Assert.Equal(0.2f, slider.Value, 3);
        Assert.Equal(2, committed.Count);

        var color = panel.ColorField("Tint", "#FF0000");
        string? hex = null;
        color.OnChanged(v => hex = v);
        UiSlotTable.BootstrapDispatch(((UiColorPickerHandle)color).WidgetId, (byte)UiSlotKind.Text, 0f, false, "#00FF00");
        Assert.Equal("#00FF00", color.Color);
        Assert.Equal("#00FF00", hex);
        panel.Dispose();
    }

    /// <summary>A settling slider commits once, after the last sample, not once per sample.</summary>
    [Fact]
    public void A_dragged_slider_commits_once_when_it_settles()
    {
        var scheduled = new List<Action>();
        UiSliderEntryHandle.SettleScheduler = (_, action) => scheduled.Add(action);
        try
        {
            var panel = UiApi.Panel("Slider", _owner, "example.ui.test");
            var slider = panel.SliderField("Volume", 0f, 1f, 0.1f, 0.5f);
            int commits = 0;
            slider.OnChanged(_ => commits++);
            var handle = (UiSliderEntryHandle)slider;
            handle.Sample(0.6f);
            handle.Sample(0.7f);
            handle.Sample(0.8f);
            Assert.Equal(0, commits);
            Assert.Equal(0.8f, handle.Preview, 3);
            foreach (var action in scheduled) action();
            Assert.Equal(1, commits);
            Assert.Equal(0.8f, slider.Value, 3);
            panel.Dispose();
        }
        finally
        {
            UiSliderEntryHandle.SettleScheduler = null;
        }
    }

    // ---- review fixes ------------------------------------------------------------------------

    /// <summary>
    /// Review 1: a slider on a double setting writes the value, not a widened float's noise, and
    /// the noise never counts as decimals.
    /// What this would catch: 0.3 stored as 0.30000001192092896, the row reading modified, and the
    /// next build's step collapsing to 1e-6.
    /// </summary>
    [Fact]
    public void A_slider_on_a_double_setting_writes_no_float_noise()
    {
        Assert.Equal(1, ModSettingsLayout.DecimalsOf(((double)0.3f).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(2, ModSettingsLayout.DecimalsOf("1.25"));
        Assert.Equal(5, ModSettingsLayout.DecimalsOf("1E-05"));

        var file = new ConfigFile(Path.Combine(_dir, "noise.cfg"), saveOnInit: true);
        const string id = "example.noise";
        var entry = new BoundedEntry<double>(file, "N", "Level", 0.3, new FakeRangeD(0, 1));
        var spec = ModSettingsLayout.NumberFor(SettingsRow.ForEntry(id, entry, false));
        Assert.Equal("0.3", spec.Format((double)0.3f));
        Assert.Equal("0.7", spec.Format((double)0.70000005f));

        UiSliderEntryHandle.SettleScheduler = null;
        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, id, "Noise", "Double slider.");
        mod.Settings("Personal", entry);
        ModsMenu.OpenMod(id);
        ushort slot = _runtime.WiredIdOf("slider-entry", "Level");
        Assert.NotEqual(0, slot);
        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Float, 0.7f, false, null);
        Assert.Equal(0.7, (double)entry.Stored!);
        UiSlotTable.BootstrapDispatch(slot, (byte)UiSlotKind.Float, 0.3f, false, null);
        Assert.Equal(0.3, (double)entry.Stored!);
        var setting = SettingRow(PageRows(), id + "/N/Level");
        Assert.False(setting.GetProperty("modified").GetBoolean());
        Assert.Equal(0.01, setting.GetProperty("step").GetDouble(), 6);
    }

    /// <summary>
    /// Review 2: the Reset-all dialog belongs to the page. A mod switch, a workspace close and the
    /// mod's unload close it, and a Yes from a view that is no longer on show resets nothing.
    /// </summary>
    [Fact]
    public void The_reset_all_dialog_closes_with_its_page_and_a_stale_yes_resets_nothing()
    {
        var file = new ConfigFile(Path.Combine(_dir, "resetall.cfg"), saveOnInit: true);
        const string id = "example.resetall";
        var entry = new BoundedEntry<int>(file, "R", "Count", 5, new FakeRange(1, 10)) { Stored = 8 };
        ModsMenu.Open();
        var mod = ModsMenu.Register(_owner, id, "ResetAll", "Reset demo.");
        mod.Settings("Personal", entry);
        using var other = ModsMenu.Register(new object(), "example.other", "Other", "Other mod.");

        bool OpenDialog()
        {
            ModsMenu.OpenMod(id);
            Assert.True(UiCellRowHandle.TryInvoke(ModSettingsView.HeaderKey + "/" + ModSettingsView.CellResetAll, out _));
            return UiDialog.Current is { IsOpen: true } d && d.Tag == ModsMenu.ResetAllDialogTag;
        }

        Assert.True(OpenDialog());
        ModsMenu.OpenMod("example.other");
        Assert.False(UiDialog.Current is { IsOpen: true }, "a mod switch closes it");

        Assert.True(OpenDialog());
        ModsMenu.Close();
        Assert.False(UiDialog.Current is { IsOpen: true }, "closing the workspace closes it");

        ModsMenu.Open();
        Assert.True(OpenDialog());
        var stale = ModsMenu.ModPage!.SettingsView!;
        ModsMenu.OpenMod(id);
        Assert.False(ModsMenu.ConfirmResetAll(stale), "a rebuilt page's old view is refused");
        Assert.Equal(8, entry.Stored);

        Assert.True(OpenDialog());
        mod.Dispose();
        Assert.False(UiDialog.Current is { IsOpen: true }, "the mod's unload closes it");

        using var again = ModsMenu.Register(_owner, id, "ResetAll", "Reset demo.");
        again.Settings("Personal", entry);
        ModsMenu.Open();
        ModsMenu.OpenMod(id);
        Assert.True(ModsMenu.ConfirmResetAll(ModsMenu.ModPage!.SettingsView!));
        Assert.Equal(5, entry.Stored);
    }

    /// <summary>
    /// Review 3: a header builder can only add rows. It cannot hide, retitle or dispose the page or
    /// set its footer; an inline cluster it leaves open is closed; a throw removes its rows before
    /// the default header is drawn.
    /// </summary>
    [Fact]
    public void A_header_builder_gets_a_row_only_panel_and_a_throw_is_rolled_back()
    {
        var counting = new CountingPanel();
        var restricted = new ModHeaderPanel(counting);
        restricted.Label("one");
        restricted.BeginInline();
        restricted.Text = "hijacked";
        restricted.Footer = "hijacked";
        restricted.Visible = false;
        restricted.Hide();
        restricted.Dispose();
        restricted.ContentAlignment = SettingsAlign.Centre;
        Assert.Equal(1, restricted.Finish());
        Assert.Throws<InvalidOperationException>(() => restricted.Label("from a timer, after the build"));
        Assert.Throws<InvalidOperationException>(() => restricted.BeginInline());
        Assert.Equal(1, counting.Begins);
        Assert.Equal(1, counting.Ends);
        Assert.Equal(SettingsAlign.Left, counting.ContentAlignment);
        Assert.Equal("page", counting.Text);
        Assert.Equal("", counting.Footer);
        Assert.Equal(0, counting.Hides);
        Assert.False(counting.IsDisposed);

        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, "example.partialheader", "PartialHeader", "Partial.");
        mod.Header(panel =>
        {
            panel.Label("Partial row that must not survive");
            panel.BeginInline();
            throw new InvalidOperationException("half way");
        });
        var shown = ModsMenu.OpenMod("example.partialheader");
        var rows = PageRows();
        Assert.DoesNotContain("Partial row that must not survive", RowLabels(rows));
        Assert.Equal("default", Row(rows, ModPagePane.RowHeader).GetProperty(ModPagePane.RowHeader).GetProperty("kind").GetString());
        Assert.Contains(_log.Warnings, l => l.Contains("removed 1 row(s)"));
        Assert.Equal(UiHandleState.Shown, shown.State);
    }

    /// <summary>Review 4: a drag the page closes on before it settles is committed, not lost.</summary>
    [Fact]
    public void A_pending_slider_commit_is_flushed_when_the_control_is_disposed()
    {
        var scheduled = new List<Action>();
        UiSliderEntryHandle.SettleScheduler = (_, action) => scheduled.Add(action);
        try
        {
            var panel = UiApi.Panel("Flush", _owner, "example.ui.test");
            var slider = panel.SliderField("Volume", 0f, 1f, 0.1f, 0.5f);
            var committed = new List<float>();
            slider.OnChanged(v => committed.Add(v));
            var handle = (UiSliderEntryHandle)slider;
            handle.Sample(0.8f);
            Assert.True(handle.CommitPending);
            panel.Dispose();
            Assert.Single(committed);
            Assert.Equal(0.8f, committed[0], 3);
            foreach (var action in scheduled) action();
            Assert.Single(committed);
        }
        finally
        {
            UiSliderEntryHandle.SettleScheduler = null;
        }
    }

    /// <summary>
    /// Review 5: a group entry from another plugin's config file is that plugin's: read-only unless
    /// EditOtherModsSettings, and never part of this page's Reset all.
    /// </summary>
    [Fact]
    public void A_group_entry_from_another_plugins_config_is_not_this_mods()
    {
        var mine = new ConfigFile(Path.Combine(_dir, "mine.cfg"), saveOnInit: true);
        var theirs = new ConfigFile(Path.Combine(_dir, "theirs.cfg"), saveOnInit: true);
        SettingsCatalog.Register("example.mine", mine, _owner);
        SettingsCatalog.Register("other.theirs", theirs, new object(), foreign: true);
        var own = new BoundedEntry<int>(mine, "A", "Own", 1, null) { Stored = 2 };
        var foreignEntry = new BoundedEntry<int>(theirs, "B", "Theirs", 1, null) { Stored = 2 };
        try
        {
            var rows = ModSettingsLayout.RowsOf("example.mine", new ConfigEntryBase[] { own, foreignEntry });
            Assert.False(rows[0].OwnedElsewhere);
            Assert.True(rows[1].OwnedElsewhere);
            Assert.True(rows[1].IsForeign);
            Assert.Equal(SettingControl.ReadOnly, ModSettingsLayout.ControlFor(rows[1]));

            SettingsCatalog.AllowForeignEdits = true;
            rows = ModSettingsLayout.RowsOf("example.mine", new ConfigEntryBase[] { own, foreignEntry });
            Assert.Equal(SettingControl.Number, ModSettingsLayout.ControlFor(rows[1]));
            Assert.True(ModSettingsLayout.CanReset(rows[1]), "editable when allowed, so its own reset link works");
            var sections = new List<ModSettingsSection> { new("Mixed", ModSettingsSectionKind.Group, rows) };
            Assert.Equal(new[] { "example.mine/A/Own" }, ModSettingsLayout.Resettable(sections).Select(r => r.RowId).ToArray());
        }
        finally
        {
            SettingsCatalog.AllowForeignEdits = false;
        }
    }

    /// <summary>Review 6: an unbounded long keeps its type's range and a big value is not clamped.</summary>
    [Fact]
    public void Unbounded_numbers_use_their_types_range()
    {
        var file = new ConfigFile(Path.Combine(_dir, "wide.cfg"), saveOnInit: true);
        var spec = ModSettingsLayout.NumberFor(SettingsRow.ForEntry("example.wide", file.Bind("W", "Big", 5_000_000_000_000L, ""), false));
        Assert.Equal(long.MinValue, spec.Min);
        // The top is the largest double below 2^63, so a stepper at the maximum writes a value
        // that still parses as a long.
        Assert.True(spec.Max < long.MaxValue);
        Assert.True(long.TryParse(spec.Format(spec.Max), out _));
        var top = new UiNumericState(spec.Max, spec.Min, spec.Max, spec.Step, spec.Decimals, "");
        top.StepBy(1);
        Assert.True(long.TryParse(spec.Format(top.Committed), out _));
        var u = ModSettingsLayout.NumberFor(SettingsRow.ForEntry("example.wide", file.Bind("W", "Huge", 5UL, ""), false));
        Assert.True(ulong.TryParse(u.Format(u.Max), out _));
        var state = new UiNumericState(5e12, spec.Min, spec.Max, spec.Step, spec.Decimals, "");
        Assert.Equal(5e12, state.Committed);
        Assert.True(state.StepBy(1));
        Assert.Equal(5e12 + 1, state.Committed);

        var d = ModSettingsLayout.NumberFor(SettingsRow.ForEntry("example.wide", file.Bind("W", "Scale", 1.25, ""), false));
        Assert.Equal(double.MaxValue, d.Max);
        var dstate = new UiNumericState(1.25, d.Min, d.Max, d.Step, d.Decimals, "");
        Assert.Equal(1.25, dstate.Committed, 9);
        Assert.Equal("1.25", dstate.Draft);
    }

    /// <summary>
    /// Re-review: the written value and the stepper share one grid. With only a minimum of 0.05 and
    /// a step of 0.1, both snap from 0.05 (0.15, 0.25, ...), so the field never shows one number and
    /// writes another.
    /// </summary>
    [Fact]
    public void The_written_value_snaps_on_the_steppers_grid()
    {
        var spec = new SettingNumberSpec(0.05, double.MaxValue, 0.1, 2, "", bounded: false, integral: false);
        foreach (double v in new[] { 0.12, 0.17, 0.26, 3.33 })
        {
            double shown = UiNumericParse.Snap(v, spec.Min, spec.Max, spec.Step);
            Assert.Equal(shown, double.Parse(spec.Format(v), System.Globalization.CultureInfo.InvariantCulture), 9);
        }
    }

    /// <summary>Review 7 (source pin): a part widget is built over the part's own object, not the row's.</summary>
    [Fact]
    public void Control_parts_wrap_their_own_objects()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        Assert.Contains("_parts[key] = new UnityWidget(null, part, click", src, StringComparison.Ordinal);
        Assert.DoesNotContain("new UnityWidget(null, GameObject, click", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review 8: the Configuration tab's Default install target selector is now clickable, and a
    /// click writes the next target to <c>[Install] DefaultTarget</c> through the pane's setter.
    /// </summary>
    [Fact]
    public void The_default_install_target_selector_is_no_longer_drawn_and_the_key_stays_bound()
    {
        var file = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        try
        {
            var entry = ConfigHelper.BindDefaultInstallTarget(file);
            var pane = new ConfigurationPane(ConfigurationPane.Bind(file), new LocalServerFolder(), _owner);
            var panel = UiApi.Panel("Configuration", _owner, "example.ui.test");
            pane.Build(panel);
            // INSTALLSIDES1: the Install tab's rows act per side on the picked server, so the
            // Configuration tab no longer draws the selector; the key stays bound so old files load.
            Assert.Equal(DefaultInstallTarget.FollowSideValue, entry.Value);
            Assert.False(_runtime.FireControlClick("choice", ConfigurationPane.DefaultInstallTargetLabel));
            Assert.Equal(DefaultInstallTarget.FollowSideValue, entry.Value);
            pane.Dispose();
            panel.Dispose();
        }
        finally
        {
            ConfigHelper.UninstallAll();
        }
    }

    /// <summary>
    /// A key capture starts on a mouse click. KEYS4 made the middle, back and forward buttons keys; KEYS8 adds
    /// the left and right buttons, which only ever bind with a modifier (a bare click stays the game's and the
    /// panel's, and the click that started a capture is never read as its key: KeybindTabTests); and TMP's
    /// second end-of-edit for one edit (Enter, then the focus loss) writes nothing.
    /// </summary>
    [Fact]
    public void Key_chords_hold_mouse_buttons_only_as_keys_and_a_repeated_end_of_edit_writes_once()
    {
        var mouse = Enum.GetNames(typeof(UiKey)).Where(n => n.IndexOf("Mouse", StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
        Assert.Equal(new[] { "MouseMiddle", "MouseBack", "MouseForward", "MouseLeft", "MouseRight" }, mouse);
        Assert.All(mouse, n => Assert.True(KeyChord.IsMouseKey((UiKey)Enum.Parse(typeof(UiKey), n))));
        Assert.Equal(MouseChords.BareClickReason, MouseChords.Refusal(new KeyChord(UiKey.MouseLeft), KeybindScope.Gameplay));

        var file = new ConfigFile(Path.Combine(_dir, "twice.cfg"), saveOnInit: true);
        const string id = "example.twice";
        var motd = file.Bind("T", "Motd", "hi", "");
        ModsMenu.Open();
        using var mod = ModsMenu.Register(_owner, id, "Twice", "Twice.");
        mod.Settings("Personal", motd);
        ModsMenu.OpenMod(id);
        int before = _log.Infos.Count(l => l.Contains("Ui: settings " + id));
        Assert.True(_runtime.FireText("Motd", "hello"));
        Assert.True(_runtime.FireText("Motd", "hello"));
        Assert.Equal("hello", motd.Value);
        Assert.Equal(before + 1, _log.Infos.Count(l => l.Contains("Ui: settings " + id)));
        Assert.True(ModSettingsView.SameAsStored(SettingsRow.ForEntry(id, motd, false), "hello"));
    }

    private sealed class CountingPanel : IUiPanel
    {
        public int Begins, Ends, Hides;
        public string Text { get; set; } = "page";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Shown;
        public string PluginId => "test";
        public string RootName => "test";
        public string ContentAlignment { get; set; } = SettingsAlign.Left;
        public string Footer { get; set; } = "";
        public UiCaptureMode CaptureMode { get; set; }
        public IUiButton Button(string text) => throw new NotSupportedException();
        public IUiToggle Toggle(string text, bool initial = false) => throw new NotSupportedException();
        public IUiLabel Label(string text) => new CountingLabel(text);
        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();
        public IUiInput Input(string text, string initial = "") => throw new NotSupportedException();
        public void Show() { }
        public void Hide() => Hides++;
        public void BeginInline() => Begins++;
        public void EndInline() => Ends++;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class CountingLabel : IUiLabel
    {
        public CountingLabel(string text) => Text = text;
        public string Text { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Shown;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }

    public sealed class FakeRangeD
    {
        public FakeRangeD(double min, double max)
        {
            MinValue = min;
            MaxValue = max;
        }

        public double MinValue { get; }

        public double MaxValue { get; }

        public bool IsValid(object value) => value is double d && d >= MinValue && d <= MaxValue;

        public string ToDescriptionString() => "# Acceptable value range: From " + MinValue + " to " + MaxValue;
    }

    // ---- helpers ------------------------------------------------------------------------------

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

    private static string[] RowLabels(JsonElement rows)
    {
        var labels = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (row.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                labels.Add(label.GetString() ?? "");
        }

        return labels.ToArray();
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
    }

    /// <summary>
    /// A config entry with the live BepInEx members the settings rows read reflectively —
    /// <c>DefaultValue</c> and <c>Description.AcceptableValues</c> — which the shim does not carry.
    /// </summary>
    private sealed class BoundedEntry<T> : ConfigEntryBase
    {
        public BoundedEntry(ConfigFile file, string section, string key, T value, object? acceptable)
            : base(file, new ConfigDefinition(section, key), typeof(T))
        {
            Stored = value;
            DefaultValue = value;
            Description = new FakeDescription(acceptable);
        }

        public object? Stored { get; set; }

        public object? DefaultValue { get; }

        public FakeDescription Description { get; }

        public override object? BoxedValue
        {
            get => Stored;
            set => Stored = value;
        }
    }

    public sealed class FakeDescription
    {
        public FakeDescription(object? acceptable) => AcceptableValues = acceptable;

        public string Description => "";

        public object? AcceptableValues { get; }
    }

    public sealed class FakeRange
    {
        public FakeRange(int min, int max)
        {
            MinValue = min;
            MaxValue = max;
        }

        public int MinValue { get; }

        public int MaxValue { get; }

        public bool IsValid(object value) => value is int i && i >= MinValue && i <= MaxValue;

        public string ToDescriptionString() => "# Acceptable value range: From " + MinValue + " to " + MaxValue;
    }

    public sealed class FakeList
    {
        public FakeList(params string[] values) => AcceptableValues = values;

        public string[] AcceptableValues { get; }

        public bool IsValid(object value) => value is string s && Array.IndexOf(AcceptableValues, s) >= 0;

        public string ToDescriptionString() => "# Acceptable values: " + string.Join(", ", AcceptableValues);
    }
}
