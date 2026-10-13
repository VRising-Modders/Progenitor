using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI5. The settings panel's rules, without a client: which rows exist, what the editors
/// accept and reject, that an edit lands in the cfg file on disk, that a synced entry is shown but
/// not editable, and that a keybind row rebinds through the same path the button click takes.
/// </summary>
[Collection("PluginLogger")]
public class UiSettingsTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly ConfigFile _file;
    private readonly object _owner = new();
    private readonly UiKeybindTests.FakeKeyInput _keys = new();
    private readonly UiTests.FakeUiRuntime _runtime = new() { IsClient = true };

    public UiSettingsTests()
    {
        Plugin.Logger = _log;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-uisettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiApi.SetKeyInputSourceForTests(_keys);
        SettingsCatalog.UseSyncedProbe(_ => false);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- editor parse / format rules ----

    [Theory]
    [InlineData(typeof(bool), SettingsEditorKind.Bool)]
    [InlineData(typeof(int), SettingsEditorKind.Int)]
    [InlineData(typeof(long), SettingsEditorKind.Int)]
    [InlineData(typeof(float), SettingsEditorKind.Float)]
    [InlineData(typeof(double), SettingsEditorKind.Float)]
    [InlineData(typeof(string), SettingsEditorKind.String)]
    [InlineData(typeof(DayOfWeek), SettingsEditorKind.Enum)]
    [InlineData(typeof(Guid), SettingsEditorKind.ReadOnly)]
    public void KindFor_maps_the_types_the_panel_can_edit(Type type, SettingsEditorKind expected) =>
        Assert.Equal(expected, SettingsEditors.KindFor(type));

    [Fact]
    public void Format_is_invariant_and_round_trips_floats()
    {
        Assert.Equal("true", SettingsEditors.Format(true));
        Assert.Equal("false", SettingsEditors.Format(false));
        Assert.Equal("1.5", SettingsEditors.Format(1.5f));
        Assert.Equal("0.1", SettingsEditors.Format(0.1f));
        Assert.Equal("12", SettingsEditors.Format(12));
        Assert.Equal("Monday", SettingsEditors.Format(DayOfWeek.Monday));
        Assert.Equal("", SettingsEditors.Format(null));

        Assert.True(SettingsEditors.TryParse(SettingsEditors.Format(0.1f), typeof(float), out var back, out _));
        Assert.Equal(0.1f, (float)back!);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData(" on ", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void Bool_accepts_the_forms_a_player_types(string text, bool expected)
    {
        Assert.True(SettingsEditors.TryParse(text, typeof(bool), out var value, out _));
        Assert.Equal(expected, (bool)value!);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("")]
    public void Bool_refuses_everything_else(string text)
    {
        Assert.False(SettingsEditors.TryParse(text, typeof(bool), out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void Int_is_whole_numbers_only_and_range_checked()
    {
        Assert.True(SettingsEditors.TryParse(" 12 ", typeof(int), out var value, out _));
        Assert.Equal(12, (int)value!);
        Assert.True(SettingsEditors.TryParse("-3", typeof(int), out value, out _));
        Assert.Equal(-3, (int)value!);

        // Not silently truncated.
        Assert.False(SettingsEditors.TryParse("1.0", typeof(int), out _, out var error));
        Assert.Contains("whole number", error);

        Assert.False(SettingsEditors.TryParse("99999999999", typeof(int), out _, out error));
        Assert.Contains("out of range", error);
    }

    [Fact]
    public void Float_is_invariant_and_refuses_a_comma_or_a_non_finite_value()
    {
        Assert.True(SettingsEditors.TryParse("1.5", typeof(float), out var value, out _));
        Assert.Equal(1.5f, (float)value!);

        // A German keyboard's "1,5" must not read as 15.
        Assert.False(SettingsEditors.TryParse("1,5", typeof(float), out _, out var error));
        Assert.Contains("'.'", error);

        Assert.False(SettingsEditors.TryParse("NaN", typeof(float), out _, out error));
        Assert.Contains("finite", error);
    }

    [Fact]
    public void Enum_is_by_name_and_never_by_number()
    {
        Assert.True(SettingsEditors.TryParse("monday", typeof(DayOfWeek), out var value, out _));
        Assert.Equal(DayOfWeek.Monday, (DayOfWeek)value!);

        Assert.False(SettingsEditors.TryParse("1", typeof(DayOfWeek), out _, out var error));
        Assert.Contains("Monday", error);
        Assert.False(SettingsEditors.TryParse("Funday", typeof(DayOfWeek), out _, out _));
    }

    [Fact]
    public void String_is_verbatim_so_a_trailing_space_survives()
    {
        Assert.True(SettingsEditors.TryParse("[mod] ", typeof(string), out var value, out _));
        Assert.Equal("[mod] ", (string)value!);
        Assert.True(SettingsEditors.TryParse("", typeof(string), out value, out _));
        Assert.Equal("", (string)value!);
    }

    // ---- R34 finding 1: an edit commits once, at the end of the edit ----

    [Fact]
    public void A_float_is_typed_through_the_widget_one_character_at_a_time_and_only_the_commit_writes()
    {
        var entry = _file.Bind("Sample", "Speed", 2.0, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        var runtime = Runtime();

        SettingsPanel.Open();

        int savesBefore = _file.SaveCount;

        // What the player does: select all, type "1", ".", "5". TMP raises onValueChanged for
        // each of those and onEndEdit once, on Enter. The panel is wired to the second one, so
        // the intermediate strings never reach it - which is the whole fix. Firing the commit
        // with each intermediate string would be testing the old binding, so what is asserted
        // here is the commit itself plus the field's untouched content in between.
        Assert.True(runtime.FireText("Speed", "1.5"));

        Assert.Equal(1.5, entry.Value);
        Assert.Equal("1.5", runtime.WidgetValue("input", "Speed"));

        // Exactly one cfg write for the whole edit. Per keystroke this was three, each one a
        // full rewrite of the file, and "1." would have stored 1 and erased the dot.
        Assert.Equal(savesBefore + 1, _file.SaveCount);
        Assert.Contains(_log.Infos, l => l.Contains("Speed = 1.5", StringComparison.Ordinal));

        SettingsPanel.Close();
    }

    [Fact]
    public void A_negative_number_is_typeable_and_an_unchanged_commit_writes_nothing()
    {
        var entry = _file.Bind("Sample", "Offset", -1, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        var runtime = Runtime();
        SettingsPanel.Open();

        Assert.True(runtime.FireText("Offset", "-3"));
        Assert.Equal(-3, entry.Value);

        // Tabbing out of a field nobody changed raises onEndEdit again. That must not rewrite
        // the cfg: BepInEx saves the whole file on every set.
        int saves = _file.SaveCount;
        Assert.True(runtime.FireText("Offset", "-3"));
        Assert.Equal(saves, _file.SaveCount);
        Assert.Equal(-3, entry.Value);

        SettingsPanel.Close();
    }

    [Fact]
    public void A_rejected_commit_writes_nothing_and_snaps_the_field_back_to_what_is_stored()
    {
        var entry = _file.Bind("Sample", "Count", 7, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        var runtime = Runtime();
        SettingsPanel.Open();

        int saves = _file.SaveCount;
        Assert.True(runtime.FireText("Count", "not a number"));

        Assert.Equal(7, entry.Value);
        Assert.Equal(saves, _file.SaveCount);
        Assert.Equal("7", runtime.WidgetValue("input", "Count"));
        Assert.Contains(_log.Warnings, l => l.Contains("rejected 'not a number'", StringComparison.Ordinal));

        SettingsPanel.Close();
    }

    [Fact]
    public void A_toggle_and_a_close_button_go_through_the_same_widget_callbacks()
    {
        var flag = _file.Bind("Sample", "Enabled", false, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        var runtime = Runtime();
        SettingsPanel.Open();

        Assert.True(runtime.FireToggle("[Sample] Enabled", true));
        Assert.True(flag.Value);

        Assert.True(runtime.FireClick("Close"));
        Assert.False(SettingsPanel.IsOpen);
    }

    [Fact]
    public void Settings_panel_stays_centre_so_overview_consent_and_deny_do_not_move()
    {
        SettingsCatalog.Register("example.mod", _file, _owner);
        SettingsPanel.Open();
        Assert.Equal(SettingsAlign.Centre, SettingsPanel.LayoutAlignment);
        Assert.NotEqual(SettingsAlign.Left, SettingsPanel.LayoutAlignment);
        SettingsPanel.Close();
        // Revert: OpenRows opting the settings dump into left moves every existing page.
    }

    // ---- R34 finding 8: AcceptableValueRange / AcceptableValueList ----

    [Fact]
    public void A_value_outside_an_acceptable_range_is_refused_with_the_range_in_the_message()
    {
        // BepInEx does not throw on an out-of-range set: it clamps. The fake entry clamps exactly
        // as BepInEx does, so if the panel wrote, the value would silently become 10 and the log
        // would call it a success.
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        var row = SettingsRow.ForEntry("example.mod", entry, synced: false);

        Assert.Equal("1 to 10", row.Bounds);
        Assert.False(row.TryWrite("50", out var error));
        Assert.Contains("1 to 10", error);
        Assert.Equal(3, entry.BoxedValue);
        Assert.Equal(0, entry.Clamps);

        // In range still works, and lands as typed.
        Assert.True(row.TryWrite("9", out _));
        Assert.Equal(9, entry.BoxedValue);
    }

    [Fact]
    public void A_value_outside_an_acceptable_list_is_refused_rather_than_clamped_to_the_first_element()
    {
        // AcceptableValueList.Clamp returns AcceptableValues[0] for anything invalid - so the
        // failure this prevents is "the player typed High and the setting became Low".
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "Difficulty"), typeof(string), "High",
            new FakeList("Low", "Med", "High"));
        var row = SettingsRow.ForEntry("example.mod", entry, synced: false);

        Assert.Equal("one of: Low, Med, High", row.Bounds);
        Assert.False(row.TryWrite("extreme", out var error));
        Assert.Contains("Low, Med, High", error);
        Assert.Equal("High", entry.BoxedValue);
        Assert.Equal(0, entry.Clamps);

        Assert.True(row.TryWrite("Med", out _));
        Assert.Equal("Med", entry.BoxedValue);
    }

    [Fact]
    public void An_acceptable_list_option_containing_a_comma_is_one_option_and_saves_and_reads_back()
    {
        // BepInEx describes an AcceptableValueList as its values joined with ", ", unescaped. The
        // options must come from the list itself, or "Always, even in combat" is drawn as "Always"
        // and "even in combat" - two options the list refuses, so picking either saves nothing.
        const string comma = "Always, even in combat";
        var expected = new[] { "Never", "Out of combat", comma };
        var entry = new BoundedEntry(_file, new ConfigDefinition("Display", "ShowHud"), typeof(string), "Never",
            new FakeList(expected));
        var row = SettingsRow.ForEntry("example.mod", entry, synced: false);

        // The row, the dropdown/radio choices drawn from it, and a registered binding: three options.
        Assert.Equal(expected, row.ToDescriptor().AllowedValues);
        Assert.Equal(expected, ModSettingsLayout.ChoicesFor(row).Select(c => c.Value).ToArray());
        using var binding = SettingsCatalog.RegisterSetting("example.mod", entry, owner: _owner);
        Assert.Equal(expected, binding.Describe().AllowedValues);

        // Choosing it saves, unclamped, through both write paths and reads back as written.
        Assert.True(row.TryWrite(comma, out var error), error);
        Assert.Equal(comma, entry.BoxedValue);
        Assert.Equal(comma, row.Read());
        Assert.True(row.TryWrite("Never", out error), error);
        Assert.Equal(SettingWriteState.Accepted, binding.Commit(comma).State);
        Assert.Equal(comma, entry.BoxedValue);
        Assert.Equal(comma, binding.CommittedValue);
        Assert.Equal(comma, binding.Describe().CurrentValue);
        Assert.Equal(0, entry.Clamps);

        // Half of the option is still not a value the list accepts.
        Assert.False(row.TryWrite("Always", out _));
        Assert.Equal(comma, entry.BoxedValue);
    }

    [Fact]
    public void The_commit_log_reports_what_was_stored_not_what_was_typed()
    {
        // A clamp this module did not anticipate must not read as an accepted edit. The fake
        // entry rounds on write, so stored and typed differ and the line has to show both.
        var entry = new RoundingEntry(_file, new ConfigDefinition("Sample", "Rounded"), typeof(int), 0);
        var row = SettingsRow.ForEntry("example.mod", entry, synced: false);

        Assert.True(SettingsPanel.Commit(row, "7"));
        Assert.Contains(_log.Infos, l => l.Contains("= 10", StringComparison.Ordinal)
                                         && l.Contains("typed 7", StringComparison.Ordinal));
    }

    [Fact]
    public void The_row_label_carries_the_allowed_set_so_the_player_sees_it_before_typing()
    {
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        SettingsCatalog.Register("example.mod", _file, _owner);
        var runtime = Runtime();

        // Drive the panel's own row builder with the bounded row.
        var panel = UiApi.Panel("bounds", _owner, "example.mod");
        AddRowForTests(panel, SettingsRow.ForEntry("example.mod", entry, synced: false));
        Assert.Contains(runtime.WidgetTexts("label"), t => t.Contains("[1 to 10]", StringComparison.Ordinal));
        panel.Dispose();
    }

    // ---- R34 panel notes ----

    [Fact]
    public void Escape_hiding_the_panel_leaves_it_closed_so_the_opener_does_not_need_two_presses()
    {
        SettingsCatalog.Register("example.mod", _file, _owner);
        var panel = SettingsPanel.Open();
        Assert.True(SettingsPanel.IsOpen);

        // What Ui.OnBootstrapKey does on Escape: it hides every shown panel and tells nobody.
        panel.Hide();
        Assert.False(SettingsPanel.IsOpen);

        // So the next press of the opener shows a panel instead of logging "closed" at nothing.
        SettingsPanel.Toggle();
        Assert.True(SettingsPanel.IsOpen);
        SettingsPanel.Close();
    }

    /// <summary>
    /// Job FUI29, the other half of the same lie. The bindings belong to the panel, so a row is
    /// rewritten while the panel is on screen and not after Escape took it off — the stored value
    /// still changes, because that is the cfg file's business and not the panel's.
    /// </summary>
    [Fact]
    public void A_row_is_rewritten_while_the_panel_is_open_and_not_after_Escape_hid_it()
    {
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });
        SettingsPanel.Open();
        var row = SettingsCatalog.Rows().Single(r => r.Kind == SettingsEditorKind.Keybind);

        Assert.True(SettingsPanel.Commit(row, "F10 + Ctrl"));
        Assert.True(SettingsPanel.TryRowTextForTests(row.RowId, out _, out string shown));
        Assert.Equal("F10 + Ctrl", shown);

        UiApi.PressKeyForTests(27, down: true);
        Assert.True(SettingsPanel.Commit(row, "F7"));

        Assert.Equal("F7", row.Read());
        Assert.True(SettingsPanel.TryRowTextForTests(row.RowId, out _, out string stale));
        Assert.Equal("F10 + Ctrl", stale);

        SettingsPanel.Close();
    }

    [Fact]
    public void Another_mods_settings_are_shown_but_not_written_unless_the_operator_opts_in()
    {
        _file.Bind("Sample", "Greeting", "hello", "");
        SettingsCatalog.Register("other.mod", _file, _owner, foreign: true);

        var row = SettingsCatalog.Rows().Single(r => r.Key == "Greeting");
        Assert.True(row.IsForeign);
        Assert.Equal(SettingsEditorKind.ReadOnly, row.Kind);
        Assert.False(row.TryWrite("bye", out var error));
        Assert.Contains("another mod", error);
        Assert.Equal("hello", row.Read());

        SettingsCatalog.AllowForeignEdits = true;
        var editable = SettingsCatalog.Rows().Single(r => r.Key == "Greeting");
        Assert.False(editable.IsForeign);
        Assert.True(editable.TryWrite("bye", out _));
        Assert.Equal("bye", editable.Read());
    }

    [Fact]
    public void An_entry_the_server_takes_over_while_the_panel_is_open_refuses_the_write()
    {
        var entry = _file.Bind("Sample", "EmberStack", 20, "");
        SettingsCatalog.Register("example.mod", _file, _owner);

        // Built at the main menu, where nothing is synced yet.
        var row = SettingsCatalog.Rows().Single(r => r.Key == "EmberStack");
        Assert.False(row.IsSynced);
        Assert.Equal(SettingsEditorKind.Int, row.Kind);

        // The player joins a server with the panel still open; the entry becomes the server's.
        SettingsCatalog.UseSyncedProbe(e => ReferenceEquals(e, entry));

        Assert.False(row.TryWrite("50", out var error));
        Assert.Contains("server owns", error);
        Assert.Equal(20, entry.Value);
    }

    [Fact]
    public void A_flags_enum_accepts_the_combined_string_its_own_format_prints()
    {
        Assert.Equal("Combat, Loot", SettingsEditors.Format(Channels.Combat | Channels.Loot));

        Assert.True(SettingsEditors.TryParse("Combat, Loot", typeof(Channels), out var value, out _));
        Assert.Equal(Channels.Combat | Channels.Loot, (Channels)value!);

        Assert.True(SettingsEditors.TryParse("combat,loot", typeof(Channels), out value, out _));
        Assert.Equal(Channels.Combat | Channels.Loot, (Channels)value!);

        // A name this enum does not declare is still refused, and so is a trailing comma.
        Assert.False(SettingsEditors.TryParse("Combat, Fishing", typeof(Channels), out _, out var error));
        Assert.Contains("Combat", error);
        Assert.False(SettingsEditors.TryParse("Combat,", typeof(Channels), out _, out _));
        // SETTINGS1 stage 2 (review of 28d23886): a [Flags] value with bits no name covers is stored
        // as its number, so a flags enum reads a bare number as raw bits; a plain enum still refuses one.
        Assert.True(SettingsEditors.TryParse("3", typeof(Channels), out value, out _));
        Assert.Equal((Channels)3, (Channels)value!);
        Assert.False(SettingsEditors.TryParse("1", typeof(DayOfWeek), out _, out _));
    }

    [Flags]
    private enum Channels
    {
        None = 0,
        Combat = 1,
        Loot = 2,
    }

    [Fact]
    public void A_presentation_label_is_the_row_caption_and_help_is_the_secondary_text()
    {
        var entry = _file.Bind("Mods", "UseThunderstore", true, "Browse Thunderstore listings. Personal to this BepInEx profile.");
        var presentation = new SettingPresentation
        {
            Label = "Use Thunderstore",
            Help = "Browse Thunderstore listings. Personal to this BepInEx profile.",
        };
        var row = SettingsRow.ForEntry("progenitor", entry, synced: false, presentation: presentation);
        Assert.Equal("Use Thunderstore", row.Label);
        Assert.DoesNotContain("[Mods]", row.Label, StringComparison.Ordinal);
        Assert.DoesNotContain("UseThunderstore", row.Label, StringComparison.Ordinal);

        var panel = UiApi.Panel("configuration", _owner, "progenitor");
        AddRowForTests(panel, row);
        var labels = Runtime().WidgetTexts("label");
        var toggles = Runtime().WidgetTexts("toggle");
        Assert.Contains("Use Thunderstore", toggles);
        Assert.DoesNotContain(toggles, t => t.Contains("[Mods] UseThunderstore", StringComparison.Ordinal));
        Assert.DoesNotContain(toggles, t => t.Contains("Browse Thunderstore listings", StringComparison.Ordinal));
        Assert.Contains(labels, t => t.Contains("Browse Thunderstore listings", StringComparison.Ordinal));
        panel.Dispose();
        // Revert: concatenating [Section] Key — Description into the toggle caption fails this.
        // Revert: Label = "Thunderstore" (the Configuration caption before "Use Thunderstore") fails this.
    }

    private UiTests.FakeUiRuntime Runtime() => _runtime;

    /// <summary>Build one row into a panel through the panel's own builder.</summary>
    private static void AddRowForTests(IUiPanel panel, SettingsRow row) => SettingsPanel.AddRowForTests(panel, row);

    /// <summary>Duck-typed stand-in for <c>AcceptableValueRange&lt;int&gt;</c>.</summary>
    private sealed class FakeRange
    {
        private readonly int _min;
        private readonly int _max;

        public FakeRange(int min, int max)
        {
            _min = min;
            _max = max;
        }

        public object MinValue => _min;

        public object MaxValue => _max;

        public bool IsValid(object? value) => value is int i && i >= _min && i <= _max;

        public object Clamp(object? value) => value is int i ? Math.Min(_max, Math.Max(_min, i)) : _min;

        public string ToDescriptionString() => "# Acceptable value range: From " + _min + " to " + _max;
    }

    /// <summary>Duck-typed stand-in for <c>AcceptableValueList&lt;string&gt;</c>, clamp included.</summary>
    private sealed class FakeList
    {
        private readonly string[] _values;

        public FakeList(params string[] values) => _values = values;

        public object AcceptableValues => _values;

        public bool IsValid(object? value) =>
            value is string s && Array.IndexOf(_values, s) >= 0;

        /// <summary>BepInEx returns the FIRST element for anything invalid. Not the nearest one.</summary>
        public object Clamp(object? value) => IsValid(value) ? value! : _values[0];

        public string ToDescriptionString() => "# Acceptable values: " + string.Join(", ", _values);
    }

    /// <summary>
    /// A config entry with a <c>Description.AcceptableValues</c>, whose setter clamps the way
    /// BepInEx's does instead of throwing. <see cref="Clamps"/> counts the silent corrections the
    /// panel must never cause.
    /// </summary>
    private sealed class BoundedEntry : ConfigEntryBase
    {
        private readonly object _acceptable;
        private object? _value;

        public BoundedEntry(ConfigFile file, ConfigDefinition definition, Type type, object value, object acceptable)
            : base(file, definition, type)
        {
            _value = value;
            _acceptable = acceptable;
        }

        public int Clamps { get; private set; }

        public object Description => new FakeDescription(_acceptable);

        public override object? BoxedValue
        {
            get => _value;
            set
            {
                var clamp = _acceptable.GetType().GetMethod("Clamp");
                var clamped = clamp?.Invoke(_acceptable, new[] { value });
                if (!Equals(clamped, value)) Clamps++;
                _value = clamped;
            }
        }
    }

    /// <summary>An entry that stores something other than what it was given, with no bounds at all.</summary>
    private sealed class RoundingEntry : ConfigEntryBase
    {
        private object? _value;

        public RoundingEntry(ConfigFile file, ConfigDefinition definition, Type type, object value)
            : base(file, definition, type)
        {
            _value = value;
        }

        public override object? BoxedValue
        {
            get => _value;
            set => _value = value is int i ? (i + 9) / 10 * 10 : value;
        }
    }

    private sealed class FakeDescription
    {
        public FakeDescription(object acceptableValues) => AcceptableValues = acceptableValues;

        public object AcceptableValues { get; }
    }

    // ---- the catalog ----

    [Fact]
    public void Rows_list_every_keybind_and_every_non_synced_entry()
    {
        _file.Bind("Sample", "Greeting", "hello", "");
        _file.Bind("Sample", "Count", 3, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });

        var rows = SettingsCatalog.Rows();
        Assert.Contains(rows, r => r.Kind == SettingsEditorKind.Keybind && r.Key == "Demo");
        Assert.Contains(rows, r => r.Kind == SettingsEditorKind.String && r.Key == "Greeting");
        Assert.Contains(rows, r => r.Kind == SettingsEditorKind.Int && r.Key == "Count");

        // The keybind's raw [Keybinds] string is not listed a second time.
        Assert.Single(rows.Where(r => r.Key == "Demo"));
    }

    [Fact]
    public void A_synced_entry_is_shown_read_only_and_refuses_a_write()
    {
        var synced = _file.Bind("Sample", "EmberStack", 20, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        SettingsCatalog.UseSyncedProbe(e => ReferenceEquals(e, synced));

        var row = SettingsCatalog.Rows().Single(r => r.Key == "EmberStack");
        Assert.True(row.IsSynced);
        Assert.Equal(SettingsEditorKind.ReadOnly, row.Kind);
        Assert.Equal("20", row.Read());

        Assert.False(row.TryWrite("50", out var error));
        Assert.Contains("server", error);
        Assert.Equal(20, synced.Value);
    }

    [Fact]
    public void A_synced_bounded_row_still_shows_its_range_while_read_only()
    {
        // FCFG: ctx.Bind can now put an AcceptableValue* on a synced entry. The server owns the
        // value for the session, so the row stays read-only - but the player should still see
        // what this client would accept, which is the same set the clamp enforces.
        var entry = new BoundedEntry(_file, new ConfigDefinition("Sample", "SpawnMultiplier"), typeof(int), 3,
            new FakeRange(1, 10));
        var row = SettingsRow.ForEntry("example.mod", entry, synced: true);

        Assert.True(row.IsSynced);
        Assert.Equal(SettingsEditorKind.ReadOnly, row.Kind);
        Assert.Equal("1 to 10", row.Bounds);

        Assert.False(row.TryWrite("9", out var error));
        Assert.Contains("server", error);
        Assert.Equal(3, entry.BoxedValue);
        Assert.Equal(0, entry.Clamps);
    }

    [Fact]
    public void A_probe_that_throws_is_treated_as_synced_rather_than_written_over()
    {
        _file.Bind("Sample", "Count", 3, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        SettingsCatalog.UseSyncedProbe(_ => throw new InvalidOperationException("boom"));

        var row = SettingsCatalog.Rows().Single(r => r.Key == "Count");
        Assert.True(row.IsSynced);
        Assert.Contains(_log.Warnings, l => l.Contains("synced probe threw"));
    }

    [Fact]
    public void RemoveAll_drops_that_owners_config_source()
    {
        _file.Bind("Sample", "Count", 3, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        Assert.Equal(1, SettingsCatalog.SourceCount);
        Assert.Equal(1, SettingsCatalog.RemoveAll(_owner));
        Assert.Equal(0, SettingsCatalog.SourceCount);
        Assert.Empty(SettingsCatalog.Rows());
    }

    // ---- writing through Config ----

    [Fact]
    public void Editing_a_row_writes_through_the_config_entry_and_reaches_the_cfg_file()
    {
        var entry = _file.Bind("Sample", "Count", 3, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        var row = SettingsCatalog.Rows().Single(r => r.Key == "Count");

        Assert.True(SettingsPanel.Commit(row, "7"));
        Assert.Equal(7, entry.Value);
        _file.Save();
        Assert.Contains("Count = 7", File.ReadAllText(_file.ConfigFilePath));
        Assert.Contains(_log.Infos, l => l.Contains("[Sample] Count = 7"));
    }

    [Fact]
    public void A_rejected_edit_logs_the_reason_and_leaves_the_value_alone()
    {
        var entry = _file.Bind("Sample", "Count", 3, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        var row = SettingsCatalog.Rows().Single(r => r.Key == "Count");

        Assert.False(SettingsPanel.Commit(row, "banana"));
        Assert.Equal(3, entry.Value);
        Assert.Contains(_log.Warnings, l => l.Contains("rejected 'banana'"));
    }

    [Fact]
    public void A_keybind_row_reads_and_rebinds_through_the_same_path_the_button_uses()
    {
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });
        var row = SettingsCatalog.Rows().Single(r => r.Kind == SettingsEditorKind.Keybind);
        Assert.Equal("F9", row.Read());

        Assert.True(SettingsPanel.Commit(row, "F10 + Ctrl"));
        Assert.Equal("F10 + Ctrl", row.Read());
        var entry = (ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Demo")];
        Assert.Equal("F10 + Ctrl", entry.Value);

        Assert.False(SettingsPanel.Commit(row, "Banana"));
        Assert.Equal("F10 + Ctrl", row.Read());
    }

    // ---- the panel itself ----

    [Fact]
    public void Open_builds_a_row_per_setting_and_Close_disposes_the_panel()
    {
        _file.Bind("Sample", "Greeting", "hello", "");
        _file.Bind("Sample", "Loud", true, "");
        SettingsCatalog.Register("example.mod", _file, _owner);
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });

        var panel = SettingsPanel.Open();
        Assert.True(SettingsPanel.IsOpen);
        Assert.Equal(UiHandleState.Shown, panel.State);

        var ids = SettingsPanel.BoundRowIds();
        Assert.Contains("example.mod/Keybinds/Demo", ids);
        Assert.Contains("example.mod/Sample/Greeting", ids);
        Assert.Contains("example.mod/Sample/Loud", ids);
        Assert.Contains(_log.Infos, l => l.StartsWith("Ui: settings panel opened rows=", StringComparison.Ordinal));

        SettingsPanel.Close();
        Assert.False(SettingsPanel.IsOpen);
        Assert.Equal(UiHandleState.Disposed, panel.State);
        Assert.Empty(SettingsPanel.BoundRowIds());
    }

    [Fact]
    public void The_panel_rebind_flow_suppresses_every_bind_until_a_key_lands()
    {
        int fired = 0;
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => fired++);
        SettingsPanel.Open();

        Assert.True(Keybinds.BeginRebind("example.mod", "Demo"));
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);

        _keys.NextChord = new KeyChord(UiKey.F7);
        Keybinds.Pump();
        var row = SettingsCatalog.Rows().Single(r => r.Kind == SettingsEditorKind.Keybind);
        Assert.Equal("F7", row.Read());

        SettingsPanel.Close();
    }

    [Fact]
    public void A_rebind_refused_as_AltGr_shows_the_reason_and_leaves_press_a_key()
    {
        // R38 finding 4. The capture used to skip an AltGr-shaped keystroke and answer "nothing
        // was pressed", so the panel sat in "press a key" with no log and no reason - which reads
        // as a broken panel to the player who pressed exactly the three keys they wanted. The
        // refusal stands; the silence does not.
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });
        SettingsPanel.Open();
        var row = SettingsCatalog.Rows().Single(r => r.Kind == SettingsEditorKind.Keybind);

        SettingsPanel.BeginRebindForTests(row);
        Assert.Equal("Demo", Keybinds.RebindingId);
        Assert.True(SettingsPanel.TryRowTextForTests(row.RowId, out _, out string waiting));
        Assert.Equal(SettingsPanel.PressAKeyText, waiting);

        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftCtrl | ModifierSides.RightAlt);
        Keybinds.Pump();

        // The flow is over, the bind is untouched, and the row says why and what to press.
        Assert.Null(Keybinds.RebindingId);
        Assert.Equal("F9", row.Read());
        Assert.True(SettingsPanel.TryRowTextForTests(row.RowId, out string label, out string display));
        Assert.Equal("F9", display);
        Assert.NotEqual(SettingsPanel.PressAKeyText, display);
        Assert.Contains("Not bound", label);
        Assert.Contains("LeftCtrl+RightAlt", label);
        Assert.Contains("LeftCtrl+LeftAlt", label);
        Assert.Contains(_log.Warnings, l =>
            l.Contains("was not rebound to Q + Ctrl + Alt") && l.Contains("AltGr"));

        // The same three keys held as LeftCtrl+LeftAlt are an ordinary chord and land - and the
        // row then carries the limitation and the suggested alternative for the stored chord,
        // because it still will not fire on the AltGr pairing.
        SettingsPanel.BeginRebindForTests(row);
        _keys.Clear();
        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftCtrl | ModifierSides.LeftAlt);
        Keybinds.Pump();

        Assert.Equal("Q + Ctrl + Alt", row.Read());
        Assert.True(SettingsPanel.TryRowTextForTests(row.RowId, out string bound, out string boundDisplay));
        Assert.Equal("Q + Ctrl + Alt", boundDisplay);
        Assert.DoesNotContain("Not bound", bound);
        Assert.Contains("LeftCtrl+LeftAlt", bound);
        Assert.Contains("Ctrl + Shift", bound);

        SettingsPanel.Close();
    }

    [Fact]
    public void Ui_Uninstall_closes_the_panel_and_leaves_no_keybind_behind()
    {
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        SettingsCatalog.Register("example.mod", _file, _owner);
        SettingsPanel.Open();

        UiApi.Uninstall();

        Assert.False(SettingsPanel.IsOpen);
        Assert.Equal(0, Keybinds.Count);
        Assert.Equal(0, SettingsCatalog.SourceCount);
    }
}
