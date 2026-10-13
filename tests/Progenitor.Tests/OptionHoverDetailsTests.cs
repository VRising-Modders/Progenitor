using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// OPTIONHOVER1 (requested 2026-10-06: "When hovering over the different settings / difficulties the tool tip should
/// update"): an option of a dropdown's open list or of a radio list, under the pointer, is described in the settings
/// page's details pane — the setting's name, the option and its explanation, then the setting's timing note — and the pane
/// goes back to the setting when the pointer leaves the options or the list closes. Options carry an optional explanation
/// in the model (<see cref="SettingPresentation.ChoiceDescriptions"/>, <see cref="SettingsPageBuilder.OptionDescriptions"/>,
/// <see cref="AdminSettingSpec.ChoiceDescriptions"/>), and the bridge can hover an option and read the pane.
/// </summary>
[Collection("PluginLogger")]
public sealed class OptionHoverDetailsTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly List<(ModSettingsView View, IUiPanel Panel)> _built = new();

    public OptionHoverDetailsTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        SettingsCatalog.UseSyncedProbe(_ => false);
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        SettingsArtLadder.PrepareHook = null;
        SettingsArtLadder.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-optionhover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        foreach (var (view, panel) in _built)
        {
            try { view.Release(); } catch { }
            try { panel.Dispose(); } catch { }
        }

        UiLayoutBatch.ResetForTests();
        ModSettingsPages.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ConfigSync.ResetForTests();
        SettingsArtLadder.PrepareHook = null;
        SettingsArtLadder.Reset();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private ConfigFile File(string name) => new(Path.Combine(_dir, name + ".cfg"), saveOnInit: true);

    private ModSettingsView Build(string id, List<IUiHandle> handles)
    {
        Assert.True(ModSettingsPages.TryGet(id, out var registration));
        var view = new ModSettingsView(id, ModSettingsLayout.Build(registration!.Sections(), null, null), "", () => ServerPartState.Running);
        var panel = UiApi.Panel("OptionHover", _owner, "example.ui.test");
        view.Build(panel, (h, _, _) => { if (h != null && !handles.Contains(h)) handles.Add(h); });
        _built.Add((view, panel));
        return view;
    }

    // ---- the model --------------------------------------------------------------------------------------------------

    [Fact]
    public void Option_descriptions_reach_the_row_aligned_with_its_choices()
    {
        var file = File("model");
        const string id = "example.optionhover.model";
        var mode = file.Bind("M", "Mode", Speed3.Fast, "");
        var repeat = file.Bind("M", "Repeat", "None", "");
        ModSettingsPages.Register(_owner, id, page => page
            .Dropdown(mode).OptionDescriptions("Takes its time.", "", "Never runs")
            .Radio(repeat, choices: new[] { "None", "Days" }, labels: new[] { "None", "Every few days" })
                .OptionDescriptions("One time.", "Rolls forward."));
        Assert.True(ModSettingsPages.TryGet(id, out var registration));
        var sections = ModSettingsLayout.Build(registration!.Sections(), null, null);
        var rows = sections.SelectMany(s => s.Items).Where(i => i.Row != null).Select(i => i.Row!).ToList();

        var modeRow = rows.Single(r => r.Key == "Mode");
        Assert.Equal(new[] { "Slow", "Fast", "Off" }, ModSettingsLayout.ChoicesFor(modeRow).Select(c => c.Value).ToArray());
        Assert.Equal(new[] { "Takes its time.", "", "Never runs" }, ModSettingsLayout.ChoiceDescriptionsFor(modeRow).ToArray());
        var repeatRow = rows.Single(r => r.Key == "Repeat");
        Assert.Equal(new[] { "One time.", "Rolls forward." }, ModSettingsLayout.ChoiceDescriptionsFor(repeatRow).ToArray());

        // A row without descriptions reads empty strings, one per choice.
        var bare = SettingsRow.ForEntry(id, mode, synced: false);
        Assert.Equal(new[] { "", "", "" }, ModSettingsLayout.ChoiceDescriptionsFor(bare).ToArray());

        // Merge keeps the builder's descriptions over a registered presentation's.
        var merged = ModSettingsLayout.Merge(
            new SettingPresentation { ChoiceDescriptions = new[] { "registered" } },
            new SettingPresentation { ChoiceDescriptions = new[] { "builder" } });
        Assert.Equal(new[] { "builder" }, merged!.ChoiceDescriptions);
        Assert.Equal(new[] { "registered" }, ModSettingsLayout.Merge(new SettingPresentation { ChoiceDescriptions = new[] { "registered" } },
            new SettingPresentation { Label = "x" })!.ChoiceDescriptions);
    }

    [Fact]
    public void Option_descriptions_refuse_a_wrong_count_and_a_row_that_is_not_a_choice()
    {
        var file = File("refuse");
        var repeat = file.Bind("M", "Repeat", "None", "");
        var on = file.Bind("M", "On", true, "");
        var builder = new SettingsPageBuilder("example.optionhover.refuse");
        Assert.Throws<InvalidOperationException>(() => builder.OptionDescriptions("a"));
        builder.Radio(repeat, choices: new[] { "None", "Days" });
        Assert.Throws<ArgumentException>(() => builder.OptionDescriptions("only one"));
        builder.Toggle(on);
        Assert.Throws<InvalidOperationException>(() => builder.OptionDescriptions("a", "b"));
        builder.Button("Go", () => { });
        Assert.Throws<InvalidOperationException>(() => builder.OptionDescriptions("a"));
    }

    // ---- what the pane says -----------------------------------------------------------------------------------------

    [Fact]
    public void An_option_reads_as_the_setting_name_the_option_and_its_explanation_then_the_timing_note()
    {
        var setting = new ModSettingsDescription("row", "Difficulty",
            "Listed as Easy, Normal or Hard. Applies at the next restart.", new[] { "Saved by the server." }, ModSettingsDescription.ToneMuted,
            ModSettingsDescription.SourceOption);
        var d = ModSettingsDescription.ForOption(setting, new UiChoiceOption("Hard", "Hard", "The game's Brutal preset"));
        Assert.Equal("row", d.RowId);
        Assert.Equal("Difficulty", d.Heading);
        Assert.Equal("Hard: The game's Brutal preset.", d.Body);
        Assert.Equal(new[] { "Applies at the next restart.", "Saved by the server." }, d.Notes.ToArray());
        Assert.Equal(ModSettingsDescription.ToneMuted, d.Tone);
        Assert.Equal(ModSettingsDescription.SourceOption, d.Source);

        var readback = d.Readback();
        Assert.Equal("option", readback["source"]);
        Assert.Equal("Hard", readback["option"]);
        Assert.Equal("Hard", readback["optionLabel"]);
        Assert.Equal("The game's Brutal preset.", readback["optionDescription"]);
        Assert.Equal("Difficulty\nHard: The game's Brutal preset.\nApplies at the next restart. Saved by the server.", readback["text"]);

        // No explanation: the label alone; no notes: no tone. A warning stays a warning.
        var plain = ModSettingsDescription.ForOption(
            new ModSettingsDescription("r", "Repeat", "Days or weeks.", null, "", ModSettingsDescription.SourceOption),
            new UiChoiceOption("None"));
        Assert.Equal("None", plain.Body);
        Assert.Empty(plain.Notes);
        Assert.Equal("", plain.Tone);
        var warn = ModSettingsDescription.ForOption(
            new ModSettingsDescription("r", "Mode", "x", new[] { "Locked." }, ModSettingsDescription.ToneWarn, "hover"),
            new UiChoiceOption("a", "A", "b"));
        Assert.Equal(ModSettingsDescription.ToneWarn, warn.Tone);

        // The row's own pane carries no option fields.
        Assert.Equal("", setting.Readback()["option"]);
    }

    [Fact]
    public void The_timing_note_is_the_helps_sentences_that_start_with_applies()
    {
        Assert.Equal(new[] { "Applies at the next restart; the world is kept." },
            ModSettingsDescription.TimingNotes("PvP or PvE. Applies at the next restart; the world is kept.").ToArray());
        Assert.Equal(new[] { "Applies at the next restart." },
            ModSettingsDescription.TimingNotes("The weekday a scheduled wipe moves to (Any: no move). Applies at the next restart.").ToArray());
        Assert.Equal(new[] { "Applies at once, to the next player who joins." },
            ModSettingsDescription.TimingNotes("At most 32 characters. Applies at once, to the next player who joins. Never shown.").ToArray());
        Assert.Empty(ModSettingsDescription.TimingNotes("Days or weeks per repeat."));
        Assert.Empty(ModSettingsDescription.TimingNotes(null));
        Assert.Equal(new[] { "Applies later." }, ModSettingsDescription.TimingNotes("Applies later").ToArray());
    }

    // ---- the page: radio lines and a dropdown's open list -------------------------------------------------------------

    [Fact]
    public void Hovering_a_radio_line_describes_it_and_leaving_goes_back_to_the_setting()
    {
        var file = File("radio");
        const string id = "example.optionhover.radio";
        var repeat = file.Bind("R", "Repeat", "None", "");
        ModSettingsPages.Register(_owner, id, page => page
            .Radio(repeat, choices: new[] { "None", "Days" }, label: "Repeat", help: "Rolls the countdown forward. Applies at once.")
                .OptionDescriptions("One time.", "By days."));
        var handles = new List<IUiHandle>();
        var view = Build(id, handles);
        var seen = new List<ModSettingsDescription>();
        view.DescriptionChanged = d => seen.Add(d);
        var radio = Assert.Single(handles.OfType<UiOptionsHandle>());
        Assert.Equal("By days.", radio.Options[1].Description);

        Assert.True(_runtime.FireOptionHover("radio", "Repeat", 1));
        var d = view.Description();
        Assert.Equal(ModSettingsDescription.SourceOption, d.Source);
        Assert.Equal(id + "/R/Repeat", d.RowId);
        Assert.Equal("Repeat", d.Heading);
        Assert.Equal("Days: By days.", d.Body);
        Assert.Equal(new[] { "Applies at once." }, d.Notes.ToArray());
        Assert.Equal(1, radio.HoveredOption);
        Assert.Equal(1, radio.Describe()["hovered"]);
        Assert.Equal("Days", seen[seen.Count - 1].OptionValue);

        // Line to line: the leave of the old line does not undo the new one.
        Assert.True(_runtime.FireOptionHover("radio", "Repeat", 0));
        Assert.True(_runtime.FireOptionHover("radio", "Repeat", 1, enter: false));
        Assert.Equal("None: One time.", view.Description().Body);

        Assert.True(_runtime.FireOptionHover("radio", "Repeat", 0, enter: false));
        var back = view.Description();
        Assert.NotEqual(ModSettingsDescription.SourceOption, back.Source);
        Assert.Equal("Rolls the countdown forward. Applies at once.", back.Body);
        Assert.Null(view.HoveredOptionItemId);
    }

    [Fact]
    public void A_dropdown_describes_the_hovered_option_only_while_its_list_is_open()
    {
        var file = File("dropdown");
        const string id = "example.optionhover.dropdown";
        var mode = file.Bind("D", "Mode", Speed2.Fast, "");
        ModSettingsPages.Register(_owner, id, page => page
            .Dropdown(mode, help: "How fast it runs. Applies at the next restart.")
                .OptionDescriptions("Takes its time", "Hurries"));
        var handles = new List<IUiHandle>();
        var view = Build(id, handles);
        var dropdown = Assert.Single(handles.OfType<UiChoiceHandle>());
        Assert.Equal("Hurries", dropdown.Options[1].Description);

        // Closed: nothing to hover.
        Assert.False(dropdown.HoverOption(0));
        Assert.NotEqual(ModSettingsDescription.SourceOption, view.Description().Source);

        dropdown.OpenList();
        Assert.True(dropdown.HoverOption(0));
        var d = view.Description();
        Assert.Equal(("Mode", "Slow: Takes its time."), (d.Heading, d.Body));
        Assert.Equal(new[] { "Applies at the next restart." }, d.Notes.ToArray());
        var list = dropdown.DescribeList();
        Assert.Equal(0, list["hovered"]);
        var first = Assert.IsType<Dictionary<string, object?>>(((IList<object?>)list["options"]!)[0]);
        Assert.Equal("Takes its time", first["description"]);

        // The list closes (a pick, Escape, a click outside): the pane goes back to the setting.
        dropdown.Dismiss();
        Assert.Equal(-1, dropdown.HoveredOption);
        Assert.Equal("How fast it runs. Applies at the next restart.", view.Description().Body);

        // A pick closes it the same way, after describing the option.
        dropdown.OpenList();
        dropdown.HoverOption(0);
        Assert.Equal(ModSettingsDescription.SourceOption, view.Description().Source);
        Assert.True(dropdown.ChooseIndex(0));
        Assert.NotEqual(ModSettingsDescription.SourceOption, view.Description().Source);

        // Releasing the page forgets the option.
        dropdown.OpenList();
        dropdown.HoverOption(1);
        view.Release();
        Assert.Null(view.HoveredOptionItemId);
    }

    [Fact]
    public void An_admin_dropdown_carries_its_option_explanations_to_the_pane()
    {
        const string id = "example.optionhover.admin";
        var spec = new AdminSettingSpec
        {
            SettingId = "progenitor/ServerRules/GameDifficulty",
            Key = "GameDifficulty",
            Label = "Difficulty",
            Help = "Listed as Easy, Normal or Hard. Applies at the next restart.",
            Kind = AdminSettingKind.Choice,
            Choices = new[] { "Relaxed", "Normal", "Hard" },
            ChoiceLabels = new[] { "Easy (Relaxed)", "Normal", "Hard" },
            ChoiceDescriptions = ServerRules.DifficultyDescriptions,
            Read = () => "Normal",
        };
        ModSettingsPages.Register(_owner, id, page => page.Section("Server rules").AdminSetting(spec));
        var handles = new List<IUiHandle>();
        var view = Build(id, handles);
        var dropdown = Assert.Single(handles.OfType<UiChoiceHandle>());
        dropdown.OpenList();
        Assert.True(dropdown.HoverOption(2));
        var d = view.Description();
        Assert.Equal("Difficulty", d.Heading);
        Assert.Equal("Hard: " + ServerRules.DifficultyDescriptions[2], d.Body);
        Assert.Equal(new[] { "Applies at the next restart." }, d.Notes.ToArray());
        Assert.Equal("Hard", d.Readback()["option"]);
        dropdown.Dismiss();
        Assert.Equal("Difficulty", view.Description().Heading);
        Assert.Equal(ModSettingsLayout.Sentence(spec.Help), view.Description().Body);
    }

    // ---- the Server tab's data --------------------------------------------------------------------------------------

    [Fact]
    public void Every_server_rule_choice_has_an_explanation_per_option()
    {
        foreach (var rule in ServerRules.Specs)
        {
            if (rule.Control != ServerRuleControl.Choice) continue;
            Assert.Equal(rule.Choices.Count, rule.ChoiceDescriptions.Count);
            foreach (string about in rule.ChoiceDescriptions)
            {
                Assert.False(string.IsNullOrWhiteSpace(about), rule.Key);
                Assert.True(about.Length <= 160, rule.Key + ": " + about);
            }

            Assert.Equal(rule.ChoiceDescriptions, ServerRulesRows.Spec(rule).ChoiceDescriptions);
            Assert.NotEmpty(ModSettingsDescription.TimingNotes(rule.Help));
        }

        Assert.Contains("Monday", ServerRules.DayDescriptions[1], StringComparison.Ordinal);
        Assert.Contains("Merciless", ServerRules.CastleHeartDescriptions[1], StringComparison.Ordinal);
    }

    /// <summary>The Difficulty explanations say the numbers the game's preset files hold.</summary>
    [Fact]
    public void The_difficulty_explanations_match_the_games_presets()
    {
        string[] words = { "Relaxed", "Normal", "Hard" };
        for (int i = 0; i < words.Length; i++)
        {
            var preset = ServerRules.DifficultyPresets[words[i]];
            string about = ServerRules.DifficultyDescriptions[i];
            if (words[i] == "Normal")
            {
                Assert.All(new[] { "SunDamageModifier", "UnitStatModifiers_Global.PowerModifier", "UnitStatModifiers_VBlood.PowerModifier" },
                    k => Assert.Equal("1", preset[k]));
                continue;
            }

            Assert.Contains(Percent(preset["UnitStatModifiers_Global.PowerModifier"]) + " power", about, StringComparison.Ordinal);
            Assert.Contains(Percent(preset["UnitStatModifiers_VBlood.MaxHealthModifier"]) + " health", about, StringComparison.Ordinal);
        }

        Assert.Contains("75%", ServerRules.DifficultyDescriptions[0], StringComparison.Ordinal);
        Assert.Equal("0.75", ServerRules.DifficultyPresets["Relaxed"]["SunDamageModifier"]);
        Assert.Contains("3 levels higher", ServerRules.DifficultyDescriptions[2], StringComparison.Ordinal);
        Assert.Equal("3", ServerRules.DifficultyPresets["Hard"]["UnitStatModifiers_VBlood.LevelIncrease"]);
    }

    /// <summary>
    /// On a machine with the game installed (<c>VRISING_GAME_DIR</c>, else the build's default Steam folder), the recorded
    /// preset numbers equal the bundled server's and the client's files (a game update that changes them fails here).
    /// Elsewhere the check has nothing to read and passes.
    /// </summary>
    [Fact]
    public void The_recorded_presets_equal_the_installed_games_files_when_present()
    {
        string game = Environment.GetEnvironmentVariable("VRISING_GAME_DIR") is { Length: > 0 } dir
            ? dir
            : @"C:\Program Files (x86)\Steam\steamapps\common\VRising";
        foreach (string data in new[] { Path.Combine("VRising_Server", "VRisingServer_Data"), "VRising_Data" })
            CheckPresets(Path.Combine(game, data, "StreamingAssets", "GameDifficultyPresets"));
    }

    private static void CheckPresets(string folder)
    {
        if (!Directory.Exists(folder)) return;
        string[] words = { "Relaxed", "Normal", "Hard" };
        foreach (string word in words)
        {
            var preset = ServerRules.DifficultyPresets[word];
            string path = Path.Combine(folder, preset["File"] + ".json");
            if (!System.IO.File.Exists(path)) continue;
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(path).TrimStart('\uFEFF'));
            foreach (var (key, expected) in preset)
            {
                if (key == "File") continue;
                var element = doc.RootElement;
                foreach (string part in key.Split('.'))
                    element = element.GetProperty(part);
                Assert.Equal(double.Parse(expected, CultureInfo.InvariantCulture), element.GetDouble(), 3);
            }
        }
    }

    [Fact]
    public void The_server_message_radios_explain_each_option()
    {
        // RESETWIPE1: Repeat and Countdown precision left the menus.
        Assert.Equal(JoinServerSettings.MessageFrequencyChoices.Count, JoinServerSettings.MessageFrequencyDescriptions.Length);
        Assert.Equal(JoinServerSettings.ModsFrequencyChoices.Count, JoinServerSettings.ModsFrequencyDescriptions.Length);
        foreach (string about in JoinServerSettings.MessageFrequencyDescriptions.Concat(JoinServerSettings.ModsFrequencyDescriptions))
            Assert.InRange(about.Length, 1, 120);
    }

    // ---- the bridge -------------------------------------------------------------------------------------------------

    [Fact]
    public void Hover_parses_needs_an_open_workspace_and_finds_options_by_value_or_label()
    {
        using var doc = JsonDocument.Parse("{\"action\":\"hover\",\"pane\":\"content\",\"label\":\"Difficulty\",\"option\":\"Hard\"}");
        var request = Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement);
        Assert.Equal("hover", request.Action);
        Assert.Equal("content", request.Pane);
        Assert.Equal("Difficulty", request.Label);
        Assert.Equal("Hard", request.Option);
        Assert.False(request.Leave);
        Assert.Contains("hover", Progenitor.Dev.Ops.ModsUiArgs.Actions);
        Assert.True(Progenitor.Dev.Ops.ModsUiArgs.RequiresOpenWorkspace("hover"));
        Assert.False(Progenitor.Dev.Ops.ModsUiArgs.IsLightRead("hover", null));

        using var leave = JsonDocument.Parse("{\"action\":\"hover\",\"label\":\"Difficulty\",\"leave\":true}");
        var off = Progenitor.Dev.Ops.ModsUiArgs.Parse(leave.RootElement);
        Assert.True(off.Leave);
        Assert.Null(off.Option);

        using var missing = JsonDocument.Parse("{\"action\":\"hover\",\"label\":\"Difficulty\"}");
        Assert.Throws<ArgumentException>(() => Progenitor.Dev.Ops.ModsUiArgs.Parse(missing.RootElement));
        using var noLabel = JsonDocument.Parse("{\"action\":\"hover\",\"option\":\"Hard\"}");
        Assert.Throws<ArgumentException>(() => Progenitor.Dev.Ops.ModsUiArgs.Parse(noLabel.RootElement));

        var options = new[] { new UiChoiceOption("Relaxed", "Easy (Relaxed)"), new UiChoiceOption("Normal"), new UiChoiceOption("Hard") };
        Assert.Equal(2, ModsMenu.OptionIndex(options, "Hard"));
        Assert.Equal(0, ModsMenu.OptionIndex(options, "easy (relaxed)"));
        Assert.Equal(0, ModsMenu.OptionIndex(options, "relaxed"));
        Assert.Equal(-1, ModsMenu.OptionIndex(options, "Brutal"));

        // The workspace closed: the op refuses.
        Assert.Throws<ArgumentException>(() => ModsMenu.HoverOption("content", "Difficulty", "Hard", leave: false));
    }

    public enum Speed3 { Slow, Fast, Off }

    public enum Speed2 { Slow, Fast }

    private static string Percent(string factor) =>
        Math.Round(double.Parse(factor, CultureInfo.InvariantCulture) * 100).ToString(CultureInfo.InvariantCulture) + "%";
}
