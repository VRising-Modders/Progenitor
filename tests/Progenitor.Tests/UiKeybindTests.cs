using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI5. Game-free coverage of the keybind registry: the cfg round trip, the two suppression
/// rules (a panel capturing input, chat focused), the conflict line, the rebind flow, and the
/// unregister-on-unload contract. The keyboard stays behind <see cref="IKeyInputSource"/>; this
/// file supplies a fake, so nothing here needs a client.
/// </summary>
[Collection("PluginLogger")]
public class UiKeybindTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly ConfigFile _file;
    private readonly object _owner = new();
    private readonly FakeKeyInput _keys = new();

    public UiKeybindTests()
    {
        Plugin.Logger = _log;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-keybinds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = new ConfigFile(Path.Combine(_dir, "example.mod.cfg"), saveOnInit: true);
        UiApi.ResetForTests();
        UiApi.SetKeyInputSourceForTests(_keys);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    // ---- KeyChord format ----

    [Theory]
    [InlineData("F10", UiKey.F10, KeyModifiers.None)]
    [InlineData(" f10 ", UiKey.F10, KeyModifiers.None)]
    [InlineData("F10 + Ctrl", UiKey.F10, KeyModifiers.Ctrl)]
    [InlineData("Ctrl+F10", UiKey.F10, KeyModifiers.Ctrl)]
    [InlineData("LeftControl + Shift + K", UiKey.K, KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("Alt + 3", UiKey.Digit3, KeyModifiers.Alt)]
    [InlineData("Return", UiKey.Enter, KeyModifiers.None)]
    [InlineData("esc", UiKey.Escape, KeyModifiers.None)]
    public void KeyChord_parses_the_BepInEx_KeyboardShortcut_shape(string text, UiKey key, KeyModifiers mods)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(key, mods), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("None")]
    [InlineData("Ctrl + Shift")]
    public void KeyChord_unbound_values_parse_to_None_and_are_not_errors(string text)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.False(chord.IsBound);
        Assert.Equal("None", chord.ToString());
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("F10 + K")]
    [InlineData("103")]
    public void KeyChord_rejects_what_it_cannot_mean(string text) =>
        Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void KeyChord_round_trips_through_its_own_string_form()
    {
        var chord = new KeyChord(UiKey.K, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt);
        Assert.Equal("K + Ctrl + Shift + Alt", chord.ToString());
        Assert.True(KeyChord.TryParse(chord.ToString(), out var back));
        Assert.Equal(chord, back);
    }

    // ---- registration / config round trip ----

    [Fact]
    public void Register_binds_a_cfg_entry_logs_one_line_and_fires_on_the_chord()
    {
        int fired = 0;
        var handle = Keybinds.Register(
            _owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => fired++);

        var entry = (ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Demo")];
        Assert.Equal("F9", entry.Value);
        Assert.Contains(_log.Infos, l => l == Keybinds.RegisteredLog("example.mod", "Demo", new KeyChord(UiKey.F9), "Demo bind."));

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, fired);

        // No press, no fire.
        _keys.Clear();
        Keybinds.Pump();
        Assert.Equal(1, fired);
        Assert.Equal(new KeyChord(UiKey.F9), handle.Chord);
    }

    [Fact]
    public void A_value_already_in_the_cfg_wins_over_the_default()
    {
        _file.Bind(Keybinds.Section, "Demo", "K + Ctrl", "");
        var handle = Keybinds.Register(
            _owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });

        Assert.Equal(new KeyChord(UiKey.K, KeyModifiers.Ctrl), handle.Chord);

        int fired = 0;
        handle.Dispose();
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);
        _keys.Press(new KeyChord(UiKey.K, KeyModifiers.Ctrl));
        Keybinds.Pump();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void An_unreadable_cfg_value_keeps_the_default_and_says_so()
    {
        _file.Bind(Keybinds.Section, "Demo", "Banana", "");
        var handle = Keybinds.Register(
            _owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "Demo bind.", () => { });

        Assert.Equal(new KeyChord(UiKey.F9), handle.Chord);
        Assert.Contains(_log.Warnings, l => l.Contains("unreadable value 'Banana'"));
    }

    [Fact]
    public void Rebind_writes_through_the_config_entry_so_the_cfg_file_updates()
    {
        Keybinds.Register(_owner, "example.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        Assert.True(Keybinds.Rebind("example.mod", "Demo", new KeyChord(UiKey.F10, KeyModifiers.Ctrl)));

        var entry = (ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Demo")];
        Assert.Equal("F10 + Ctrl", entry.Value);
        _file.Save();
        Assert.Contains("F10 + Ctrl", File.ReadAllText(_file.ConfigFilePath));
    }

    // ---- conflicts ----

    [Fact]
    public void Two_plugins_on_one_key_log_a_conflict_and_only_the_winner_fires()
    {
        // KEYS1 (decided 2026-09-29): "first installed" wins and the loser's handler does not run.
        // Neither has an install time here, so the first by plugin id (a.mod) keeps the key.
        int a = 0, b = 0;
        var other = new ConfigFile(Path.Combine(_dir, "other.mod.cfg"), saveOnInit: true);
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => a++);
        Keybinds.Register(_owner, "b.mod", "Demo", other, new KeyChord(UiKey.F9), "", () => b++);

        Assert.Contains(
            _log.Warnings,
            l => l == Keybinds.ConflictLog(new KeyChord(UiKey.F9), "a.mod/Demo", "b.mod/Demo"));
        Assert.Contains(_log.Warnings, l => l.StartsWith(
            "Ui: keybind 'b.mod/Demo' is not active: Not active: F9 is used by a.mod", StringComparison.Ordinal));

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, a);
        Assert.Equal(0, b);
        Assert.Contains(_log.Infos, l => l.StartsWith("Ui: keybind 'b.mod/Demo' was pressed but not fired: Not active: F9", StringComparison.Ordinal));

        // A conflict clears the moment either side is remapped: b fires on its own key.
        Assert.True(Keybinds.Rebind("b.mod", "Demo", new KeyChord(UiKey.F8)));
        _keys.Press(new KeyChord(UiKey.F8));
        Keybinds.Pump();
        Assert.Equal(1, b);
    }

    [Fact]
    public void A_different_modifier_is_not_a_conflict()
    {
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        Keybinds.Register(_owner, "b.mod", "Demo2", _file, new KeyChord(UiKey.F9, KeyModifiers.Ctrl), "", () => { });
        Assert.DoesNotContain(_log.Warnings, l => l.Contains("keybind conflict"));
    }

    [Fact]
    public void A_rebind_onto_an_occupied_key_is_refused_and_only_the_unchecked_path_logs_the_conflict()
    {
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        Keybinds.Register(_owner, "b.mod", "Other", _file, new KeyChord(UiKey.F8), "", () => { });

        // KEYS2: a remap never creates a conflict.
        Assert.False(Keybinds.Rebind("b.mod", "Other", new KeyChord(UiKey.F9)));
        Assert.Equal(new KeyChord(UiKey.F8), Keybinds.Snapshot().Single(b => b.PluginId == "b.mod").Chord);
        Assert.Contains(_log.Warnings, l => l.Contains("'b.mod/Other' was not rebound to F9: F9 is used by a.mod (Demo). Pick another key."));
        Assert.DoesNotContain(_log.Warnings, l => l.Contains("keybind conflict on F9"));

        // The unchecked path (a grayed row's undo, test setup) still stores it and logs the conflict.
        Assert.True(Keybinds.Rebind("b.mod", "Other", new KeyChord(UiKey.F9), checkConflicts: false));
        Assert.Contains(_log.Warnings, l => l.Contains("keybind conflict on F9"));
    }

    // ---- suppression ----

    [Fact]
    public void A_capturing_panel_suppresses_binds_unless_they_opted_out()
    {
        int normal = 0, exempt = 0;
        Keybinds.Register(_owner, "a.mod", "Normal", _file, new KeyChord(UiKey.F9), "", () => normal++);
        Keybinds.Register(
            _owner, "a.mod", "Exempt", _file, new KeyChord(UiKey.F8), "", () => exempt++, allowWhileCaptured: true);

        UiInputCapture.Acquire();
        Assert.Equal(1, UiInputCapture.Count);

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        _keys.Press(new KeyChord(UiKey.F8));
        Keybinds.Pump();
        Assert.Equal(0, normal);
        Assert.Equal(1, exempt);

        UiInputCapture.Release();
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, normal);
    }

    [Fact]
    public void CaptureInput_false_means_an_open_panel_no_longer_suppresses()
    {
        int fired = 0;
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);
        UiInputCapture.Acquire();
        UiInputCapture.SetEnabled(false);

        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Chat_focus_suppresses_every_bind_including_the_exempt_ones()
    {
        int normal = 0, exempt = 0;
        Keybinds.Register(_owner, "a.mod", "Normal", _file, new KeyChord(UiKey.F9), "", () => normal++);
        Keybinds.Register(
            _owner, "a.mod", "Exempt", _file, new KeyChord(UiKey.F8), "", () => exempt++, allowWhileCaptured: true);

        _keys.ChatFocused = true;
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        _keys.Press(new KeyChord(UiKey.F8));
        Keybinds.Pump();
        Assert.Equal(0, normal);
        Assert.Equal(0, exempt);

        _keys.ChatFocused = false;
        _keys.Press(new KeyChord(UiKey.F8));
        Keybinds.Pump();
        Assert.Equal(1, exempt);
    }

    [Fact]
    public void An_unavailable_keyboard_and_a_throwing_chat_read_both_fire_nothing()
    {
        int fired = 0;
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);

        _keys.Available = false;
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);

        _keys.Available = true;
        _keys.ChatThrows = true;
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);
        Assert.Contains(_log.Warnings, l => l.Contains("chat-focus read threw"));
    }

    [Fact]
    public void A_throwing_handler_is_logged_and_does_not_stop_the_next_bind()
    {
        int later = 0;
        Keybinds.Register(_owner, "a.mod", "Bad", _file, new KeyChord(UiKey.F9), "", () => throw new InvalidOperationException("boom"));
        Keybinds.Register(_owner, "a.mod", "Good", _file, new KeyChord(UiKey.F9, KeyModifiers.Ctrl), "", () => later++);

        _keys.Press(new KeyChord(UiKey.F9));
        _keys.Press(new KeyChord(UiKey.F9, KeyModifiers.Ctrl));
        Keybinds.Pump();
        Assert.Equal(1, later);
        Assert.Contains(_log.Errors, l => l.Contains("a.mod/Bad") && l.Contains("boom"));
    }

    // ---- rebind flow ----

    [Fact]
    public void BeginRebind_swallows_the_next_press_and_writes_it_to_the_cfg()
    {
        int fired = 0;
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);
        Assert.True(Keybinds.BeginRebind("a.mod", "Demo"));
        Assert.Equal("Demo", Keybinds.RebindingId);

        // The press that lands the new bind must not also run the old one.
        _keys.Press(new KeyChord(UiKey.F9));
        _keys.NextChord = new KeyChord(UiKey.K, KeyModifiers.Alt);
        Keybinds.Pump();

        Assert.Equal(0, fired);
        Assert.Null(Keybinds.RebindingId);
        var entry = (ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Demo")];
        Assert.Equal("K + Alt", entry.Value);

        _keys.Clear();
        _keys.Press(new KeyChord(UiKey.K, KeyModifiers.Alt));
        Keybinds.Pump();
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Escape_cancels_a_rebind_and_leaves_the_key_alone()
    {
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        Keybinds.BeginRebind("a.mod", "Demo");
        _keys.NextChord = new KeyChord(UiKey.Escape);
        Keybinds.Pump();

        Assert.Null(Keybinds.RebindingId);
        var entry = (ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Demo")];
        Assert.Equal("F9", entry.Value);
    }

    // ---- lifetime ----

    [Fact]
    public void Dispose_and_RemoveAll_unregister_and_the_cfg_value_survives()
    {
        int a = 0, b = 0;
        var other = new object();
        var handle = Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => a++);
        Keybinds.Register(other, "b.mod", "Demo", _file, new KeyChord(UiKey.F8), "", () => b++);
        Assert.Equal(2, Keybinds.Count);

        handle.Dispose();
        handle.Dispose();
        Assert.True(handle.IsDisposed);
        Assert.Equal(1, Keybinds.Count);

        _keys.Press(new KeyChord(UiKey.F9));
        _keys.Press(new KeyChord(UiKey.F8));
        Keybinds.Pump();
        Assert.Equal(0, a);
        Assert.Equal(1, b);

        Assert.Equal(1, Keybinds.RemoveAll(other));
        Assert.Equal(0, Keybinds.Count);

        // The player's value is still in the cfg for the next generation.
        var entry = (ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Demo")];
        Assert.Equal("F9", entry.Value);
    }

    [Fact]
    public void Ui_RemoveAll_drops_that_owners_keybinds_on_plugin_unload()
    {
        int fired = 0;
        var runtime = new UiTests.FakeUiRuntime { IsClient = true };
        UiApi.SetRuntimeForTests(runtime);
        UiApi.SetKeyInputSourceForTests(_keys);

        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);
        UiApi.RemoveAll(_owner);

        Assert.Equal(0, Keybinds.Count);
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);
        Assert.Contains(_log.Infos, l => l.StartsWith("Ui: keybinds removed=1", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_drops_every_bind_so_a_Core_swap_leaves_no_handler()
    {
        int fired = 0;
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);
        Keybinds.Uninstall();

        Assert.Equal(0, Keybinds.Count);
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, fired);
    }

    // ---- R34 finding 7: the chat gate fails closed ----

    [Fact]
    public void An_absent_or_unreadable_chat_window_suppresses_every_bind_and_says_so_once()
    {
        // The rule three documents state and the shipped source could not reach: an unreadable
        // chat window is not "the player is not typing". Unknown is the answer the real source
        // gives on the main menu, on a loading screen, and when the read throws; every one of
        // them has to suppress binds, because firing a letter bind into a chat line is worse
        // than missing a press.
        int fired = 0;
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => fired++);

        _keys.ChatUnknown = true;
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Keybinds.Pump();
        Assert.Equal(0, fired);

        // One line, on the transition - not one per frame: the window is legitimately absent for
        // minutes at a time on the main menu.
        Assert.Single(_log.Infos, l => l == Keybinds.ChatUnknownLog);

        // And it is not a latch: a readable window brings the binds straight back.
        _keys.ChatUnknown = false;
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(1, fired);
        Assert.Contains(_log.Infos, l => l.Contains("readable again", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rebind_capture_is_suppressed_by_an_unreadable_chat_window_too()
    {
        // Chat wins over the rebind flow as well: the keys the player is typing into chat must
        // not become the new binding.
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        Assert.True(Keybinds.BeginRebind("a.mod", "Demo"));

        _keys.ChatUnknown = true;
        _keys.NextChord = new KeyChord(UiKey.K);
        Keybinds.Pump();

        Assert.Equal("Demo", Keybinds.RebindingId);
        Assert.Equal(new KeyChord(UiKey.F9), Keybinds.Snapshot()[0].Chord);
    }

    // ---- R35 finding 5: the settings opener is reachable at the main menu ----

    [Fact]
    public void The_opener_fires_while_no_chat_window_can_be_read_and_an_ordinary_bind_does_not()
    {
        // Unknown means "no chat window exists in this scene" - the main menu, or the loading
        // screen after a server kicked the player for declining its mods. That is exactly where
        // the settings panel has to open, so the opener opts in; everything else still fails
        // closed, because Unknown cannot see a chat box.
        int opener = 0, normal = 0, captureExempt = 0;
        Keybinds.Register(
            _owner, "a.mod", "Opener", _file, new KeyChord(UiKey.F10), "", () => opener++,
            allowWhileCaptured: true, allowWhenChatUnknown: true);
        Keybinds.Register(_owner, "a.mod", "Normal", _file, new KeyChord(UiKey.F9), "", () => normal++);
        Keybinds.Register(
            _owner, "a.mod", "CaptureExempt", _file, new KeyChord(UiKey.F8), "", () => captureExempt++,
            allowWhileCaptured: true);

        _keys.ChatUnknown = true;
        _keys.Press(new KeyChord(UiKey.F10));
        _keys.Press(new KeyChord(UiKey.F9));
        _keys.Press(new KeyChord(UiKey.F8));
        Keybinds.Pump();

        Assert.Equal(1, opener);
        Assert.Equal(0, normal);
        // The capture exemption is not the chat exemption: opting out of one does not opt out of
        // the other.
        Assert.Equal(0, captureExempt);
    }

    [Fact]
    public void A_focused_chat_window_still_suppresses_the_opener()
    {
        // The opt-in is for "no chat window exists", never for "the player is typing in one".
        int opener = 0, normal = 0;
        Keybinds.Register(
            _owner, "a.mod", "Opener", _file, new KeyChord(UiKey.F10), "", () => opener++,
            allowWhileCaptured: true, allowWhenChatUnknown: true);
        Keybinds.Register(_owner, "a.mod", "Normal", _file, new KeyChord(UiKey.F9), "", () => normal++);

        _keys.ChatFocused = true;
        _keys.Press(new KeyChord(UiKey.F10));
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, opener);
        Assert.Equal(0, normal);

        _keys.ChatFocused = false;
        Keybinds.Pump();
        Assert.Equal(1, opener);
        Assert.Equal(1, normal);
    }

    [Fact]
    public void Unknown_that_persists_while_a_client_world_exists_warns_once()
    {
        // The real failure. At the menu there is no client world and Unknown is expected forever;
        // in a world it means the HUDChatWindow lookup has broken and every non-exempt bind is
        // dead, which has to be visible in the log - once, not per frame.
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        _keys.ChatUnknown = true;

        // No client world: this is the main menu, and it stays quiet however long it lasts.
        Keybinds.SetClientWorldProbeForTests(() => false);
        for (int i = 0; i < Keybinds.ChatUnknownWorldPumps + 5; i++) Keybinds.Pump();
        Assert.DoesNotContain(_log.Warnings, l => l == Keybinds.ChatUnknownWithClientWorldLog);

        // A client world with no readable chat window: one warning, and only one.
        Keybinds.SetClientWorldProbeForTests(() => true);
        for (int i = 0; i < 10; i++) Keybinds.Pump();
        Assert.Single(_log.Warnings, l => l == Keybinds.ChatUnknownWithClientWorldLog);

        // A readable window ends the episode, so a later break warns again rather than staying
        // silent for the session.
        _keys.ChatUnknown = false;
        Keybinds.Pump();
        _keys.ChatUnknown = true;
        for (int i = 0; i < Keybinds.ChatUnknownWorldPumps + 1; i++) Keybinds.Pump();
        int warned = 0;
        foreach (var line in _log.Warnings)
            if (line == Keybinds.ChatUnknownWithClientWorldLog) warned++;
        Assert.Equal(2, warned);
    }

    // ---- R36 finding 5: the gate is about what the player types, not what the handler types ----

    [Fact]
    public void A_focused_text_field_suppresses_the_opener_that_the_chat_gate_lets_through()
    {
        // The menu is Unknown to the chat probe *and* full of TMP fields it cannot see: the
        // character-name box, the direct-connect address, a private server's password. The
        // exemption is from the chat probe and from nothing else, so the opener - the one bind
        // that fires under Unknown - is suppressed while any field has focus, as is the rebind
        // capture, whose whole job is to swallow the next key the player presses.
        int opener = 0, normal = 0;
        Keybinds.Register(
            _owner, "progenitor", "OpenSettingsPanel", _file, new KeyChord(UiKey.F10), "",
            () => opener++, allowWhileCaptured: true, allowWhenChatUnknown: true);
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => normal++);

        _keys.ChatUnknown = true;
        _keys.TextFieldFocusedValue = true;
        _keys.Press(new KeyChord(UiKey.F10));
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(0, opener);
        Assert.Equal(0, normal);
        Assert.Contains(_log.Infos, l => l == Keybinds.TextFieldFocusedLog);

        // The rebind capture does not read a key either.
        Assert.True(Keybinds.BeginRebind("a.mod", "Demo"));
        _keys.NextChord = new KeyChord(UiKey.K);
        Keybinds.Pump();
        Assert.Equal("Demo", Keybinds.RebindingId);
        Keybinds.CancelRebind();

        // Focus leaves the field: still Unknown, and now the opener fires and the ordinary bind
        // does not - which is what the chat exemption is for.
        _keys.TextFieldFocusedValue = false;
        Keybinds.Pump();
        Assert.Equal(1, opener);
        Assert.Equal(0, normal);
    }

    [Fact]
    public void A_focused_text_field_holds_key_binds_back_but_not_mouse_button_binds_and_releases_them_on_blur()
    {
        // User report 2026-10-10: with Search settings focused, Mouse Back (Navigate back) did nothing. A mouse button
        // types nothing, so its binds still run; keys wait for the field to lose focus.
        int back = 0, key = 0, ctrlBack = 0;
        Keybinds.Register(_owner, "progenitor", "NavigateBack", _file, new KeyChord(UiKey.MouseBack), "", () => back++);
        Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => key++);
        Keybinds.Register(_owner, "b.mod", "CtrlBack", _file, new KeyChord(UiKey.MouseForward, KeyModifiers.Ctrl), "", () => ctrlBack++);

        _keys.TextFieldFocusedValue = true;
        _keys.Press(new KeyChord(UiKey.MouseBack));
        _keys.Press(new KeyChord(UiKey.F9));
        _keys.Press(new KeyChord(UiKey.MouseForward, KeyModifiers.Ctrl));
        Keybinds.Pump();
        Assert.Equal(1, back);
        Assert.Equal(0, key);
        Assert.Equal(1, ctrlBack);
        Assert.Equal(true, Keybinds.TextFocusReadback()["textFieldFocused"]);
        Assert.Equal(true, Keybinds.TextFocusReadback()["pumpSawTextField"]);

        // The simulated press (the bridge's keybind_press) follows the same rule.
        Assert.True(Keybinds.SimulatePress("progenitor", "NavigateBack").Single().Fired);
        var refused = Keybinds.SimulatePress("a.mod", "Demo").Single();
        Assert.False(refused.Fired);
        Assert.Equal("suppressed: a text field has focus", refused.Reason);

        // The field loses focus: keys are live again on the next pump.
        _keys.TextFieldFocusedValue = false;
        Keybinds.Pump();
        Assert.Equal(false, Keybinds.TextFocusReadback()["pumpSawTextField"]);
        int before = key;
        _keys.Press(new KeyChord(UiKey.F9));
        Keybinds.Pump();
        Assert.Equal(before + 1, key);
        Assert.True(Keybinds.TypingSafeChord(new KeyChord(UiKey.MouseBack)));
        Assert.False(Keybinds.TypingSafeChord(new KeyChord(UiKey.K)));
        Assert.False(Keybinds.TypingSafeChord(new KeyChord(UiKey.None)));
    }

    [Fact]
    public void A_bare_letter_is_refused_for_a_bind_that_fires_while_chat_is_unknown()
    {
        // The failure R36 finding 5 names: the opener is rebindable, F10 is only the default, and
        // a player who puts it on P types a character name at the menu and opens the panel over
        // the creation screen. Both halves refuse it - the cfg the player edited, read at
        // registration, and the press-a-key flow.
        _file.Bind(Keybinds.Section, "OpenSettingsPanel", "P", "");
        var opener = Keybinds.Register(
            _owner, "progenitor", "OpenSettingsPanel", _file, new KeyChord(UiKey.F10), "",
            () => { }, allowWhileCaptured: true, allowWhenChatUnknown: true);

        Assert.Equal(new KeyChord(UiKey.F10), opener.Chord);
        Assert.Contains(_log.Warnings, l => l.Contains("cannot be bound to P") && l.Contains("Ctrl"));

        // The rebind flow refuses it too, and leaves the bind exactly where it was.
        Assert.False(Keybinds.Rebind("progenitor", "OpenSettingsPanel", new KeyChord(UiKey.K)));
        Assert.Equal(new KeyChord(UiKey.F10), opener.Chord);

        // Every printable class, not just letters.
        foreach (var key in new[] { UiKey.K, UiKey.Digit4, UiKey.Space, UiKey.Comma, UiKey.Numpad7 })
            Assert.False(Keybinds.Rebind("progenitor", "OpenSettingsPanel", new KeyChord(key)));

        // An ordinary bind is not exempt from the chat gate, so it is not refused either: this
        // rule is the price of the exemption, not a rule about keybinds in general.
        var ordinary = Keybinds.Register(_owner, "a.mod", "Demo", _file, new KeyChord(UiKey.F9), "", () => { });
        Assert.True(Keybinds.Rebind("a.mod", "Demo", new KeyChord(UiKey.P)));
        Assert.Equal(new KeyChord(UiKey.P), ordinary.Chord);
    }

    [Fact]
    public void A_letter_with_a_modifier_is_allowed_for_a_bind_that_fires_while_chat_is_unknown()
    {
        // Ctrl or Alt held is a shortcut, not a character a text field would take, so the
        // chord is accepted, written to the cfg, and fires under Unknown like the default did.
        // Shift alone is not, and Ctrl+Alt is not either: see the R37 finding 1 test below.
        int opener = 0;
        var handle = Keybinds.Register(
            _owner, "progenitor", "OpenSettingsPanel", _file, new KeyChord(UiKey.F10), "",
            () => opener++, allowWhileCaptured: true, allowWhenChatUnknown: true);

        var chord = new KeyChord(UiKey.P, KeyModifiers.Ctrl);
        Assert.True(Keybinds.Rebind("progenitor", "OpenSettingsPanel", chord));
        Assert.Equal(chord, handle.Chord);

        _keys.ChatUnknown = true;
        _keys.Press(chord);
        Keybinds.Pump();
        Assert.Equal(1, opener);

        // And a function key with no modifier stays legal, which is what the default is.
        Assert.True(Keybinds.Rebind("progenitor", "OpenSettingsPanel", new KeyChord(UiKey.F8)));
        Assert.Equal(new KeyChord(UiKey.F8), handle.Chord);
    }

    // ---- R37 finding 1: Shift types capitals, and Ctrl+Alt is AltGr ----

    [Fact]
    public void Shift_alone_does_not_make_a_printable_chord_safe_and_neither_does_Ctrl_plus_Alt()
    {
        // The old rule was "any modifier". Shift+N is how a capital N is typed, so a player who
        // followed the old refusal message onto Shift+N opened the panel over the character
        // creation screen on the first keystroke of "Nosferatu" - R36 finding 5 reached through
        // the advice its own fix printed. Ctrl+Alt+Q is the same failure one layout away: Windows
        // delivers AltGr as LeftCtrl+RightAlt, which is how a German layout types '@'.
        var handle = Keybinds.Register(
            _owner, "progenitor", "OpenSettingsPanel", _file, new KeyChord(UiKey.F10), "",
            () => { }, allowWhileCaptured: true, allowWhenChatUnknown: true);

        Assert.False(Keybinds.Rebind("progenitor", "OpenSettingsPanel", new KeyChord(UiKey.N, KeyModifiers.Shift)));
        Assert.False(Keybinds.Rebind(
            "progenitor", "OpenSettingsPanel",
            new KeyChord(UiKey.Q, KeyModifiers.Ctrl | KeyModifiers.Alt)));
        Assert.Equal(new KeyChord(UiKey.F10), handle.Chord);

        // Ctrl or Alt, with or without Shift, is accepted; so is a function key with anything.
        foreach (var chord in new[]
                 {
                     new KeyChord(UiKey.N, KeyModifiers.Ctrl),
                     new KeyChord(UiKey.N, KeyModifiers.Alt),
                     new KeyChord(UiKey.N, KeyModifiers.Ctrl | KeyModifiers.Shift),
                     new KeyChord(UiKey.N, KeyModifiers.Alt | KeyModifiers.Shift),
                     new KeyChord(UiKey.F8, KeyModifiers.Shift),
                     new KeyChord(UiKey.F8, KeyModifiers.Ctrl | KeyModifiers.Alt),
                 })
        {
            Assert.True(Keybinds.Rebind("progenitor", "OpenSettingsPanel", chord), chord.ToString());
            Assert.Equal(chord, handle.Chord);
        }

        // The refusal message tells the truth about all three rules, and still names Ctrl.
        Assert.Contains(_log.Warnings, l =>
            l.Contains("cannot be bound to N + Shift")
            && l.Contains("Add Ctrl or Alt")
            && l.Contains("Shift alone is not enough")
            && l.Contains("AltGr"));

        // The default the shipped opener actually uses is a function key, so none of this fires
        // on a player who never rebinds.
        Assert.False(KeyChord.IsPrintableKey(SettingsPanel.DefaultOpenKey.Key));
    }

    [Fact]
    public void AltGr_does_not_fire_a_Ctrl_plus_Alt_bind_but_LeftCtrl_plus_LeftAlt_does()
    {
        // The chord is stored side-agnostically and an ordinary bind may legitimately be on
        // Ctrl+Alt+Q, so the second half of the fix is at the read: LeftCtrl+RightAlt is AltGr and
        // cannot be told apart from a player typing '@', so it does not fire. Any other pairing of
        // the two sides is a real chord and does.
        int fired = 0;
        var chord = new KeyChord(UiKey.Q, KeyModifiers.Ctrl | KeyModifiers.Alt);
        Keybinds.Register(_owner, "a.mod", "Demo", _file, chord, "", () => fired++);

        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftCtrl | ModifierSides.RightAlt);
        Keybinds.Pump();
        Assert.Equal(0, fired);

        _keys.Clear();
        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftCtrl | ModifierSides.LeftAlt);
        Keybinds.Pump();
        Assert.Equal(1, fired);

        // RightCtrl+RightAlt is not AltGr either - AltGr is the left Ctrl.
        _keys.Clear();
        _keys.PressPhysical(UiKey.Q, ModifierSides.RightCtrl | ModifierSides.RightAlt);
        Keybinds.Pump();
        Assert.Equal(2, fired);

        // And a function key is never refused: no layout types F5.
        int fkey = 0;
        Keybinds.Register(
            _owner, "a.mod", "Fkey", _file, new KeyChord(UiKey.F5, KeyModifiers.Ctrl | KeyModifiers.Alt), "",
            () => fkey++);
        _keys.Clear();
        _keys.PressPhysical(UiKey.F5, ModifierSides.LeftCtrl | ModifierSides.RightAlt);
        Keybinds.Pump();
        Assert.Equal(1, fkey);
    }

    // ---- R38 finding 4: the AltGr refusal is not silent ----

    [Fact]
    public void A_press_refused_as_AltGr_says_why_and_names_the_pairing_that_works()
    {
        // The refusal itself is right and stays (there is no layout to read, so LeftCtrl+RightAlt
        // has to be treated as AltGr for everyone). What was wrong is that it said nothing: on a
        // US or UK layout right Alt is a plain Alt, so LeftCtrl+RightAlt is a natural way to hold
        // Ctrl+Alt, and the bind looked broken for exactly one hand position. R38 finding 4.
        int fired = 0;
        var chord = new KeyChord(UiKey.Q, KeyModifiers.Ctrl | KeyModifiers.Alt);
        Keybinds.Register(_owner, "a.mod", "Demo", _file, chord, "", () => fired++);

        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftCtrl | ModifierSides.RightAlt);
        Keybinds.Pump();
        Assert.Equal(0, fired);
        Assert.Contains(_log.Warnings, l =>
            l.Contains("keybind 'a.mod/Demo' was not fired")
            && l.Contains("Q + Ctrl + Alt")
            && l.Contains("LeftCtrl+RightAlt")
            && l.Contains("LeftCtrl+LeftAlt"));

        // One line per attempt, and nothing at all on the pairing that fires.
        int before = _log.Warnings.Count;
        _keys.Clear();
        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftCtrl | ModifierSides.LeftAlt);
        Keybinds.Pump();
        Assert.Equal(1, fired);
        Assert.Equal(before, _log.Warnings.Count);

        // A chord that is not AltGr-shaped never reaches the branch: a bare miss stays quiet.
        _keys.Clear();
        _keys.PressPhysical(UiKey.Q, ModifierSides.LeftShift);
        Keybinds.Pump();
        Assert.Equal(1, fired);
        Assert.Equal(before, _log.Warnings.Count);
    }

    // ---- R34 finding 16 (Ui half): what one frame of the poll costs ----

    [Fact]
    public void The_poll_reads_nothing_when_no_keybind_is_registered()
    {
        _keys.ResetCounters();
        Keybinds.Pump();
        Keybinds.Pump();

        Assert.Equal(0, _keys.TotalReads);
        Assert.False(Keybinds.PumpArmedForTests);
    }

    [Fact]
    public void The_poll_reads_only_the_keys_that_are_bound_and_stops_before_the_source_when_none_can_fire()
    {
        Keybinds.Register(_owner, "a.mod", "Bound", _file, new KeyChord(UiKey.F9, KeyModifiers.Ctrl), "", () => { });
        Keybinds.Register(_owner, "a.mod", "Unbound", _file, KeyChord.None, "", () => { });

        _keys.ResetCounters();
        _keys.ReadChords.Clear();
        Keybinds.Pump();

        // One availability read, one chat read, and exactly one key read: the chord that is
        // bound. No scan of the keyboard, and nothing asked about the unbound registration.
        Assert.Equal(1, _keys.AvailableReads);
        Assert.Equal(1, _keys.ChatReads);
        Assert.Equal(0, _keys.ChordScans);
        Assert.Equal(new[] { new KeyChord(UiKey.F9, KeyModifiers.Ctrl) }, _keys.ReadChords);

        // With the panel capturing input, the one bind left is not exempt, so nothing on this
        // frame could fire: the pump returns before it touches the source at all.
        UiInputCapture.Acquire();
        _keys.ResetCounters();
        Keybinds.Pump();
        Assert.Equal(0, _keys.TotalReads);
        UiInputCapture.Release();
    }

    [Fact]
    public void The_poll_allocates_no_snapshot_per_frame_and_rebuilds_it_when_the_registry_changes()
    {
        var first = Keybinds.Register(_owner, "a.mod", "One", _file, new KeyChord(UiKey.F9), "", () => { });

        Keybinds.Pump();
        var snapshot = Keybinds.PumpSnapshotForTests();
        Keybinds.Pump();
        Assert.Same(snapshot, Keybinds.PumpSnapshotForTests());

        // A registration change is the only thing that rebuilds it.
        Keybinds.Register(_owner, "a.mod", "Two", _file, new KeyChord(UiKey.F8), "", () => { });
        Keybinds.Pump();
        var second = Keybinds.PumpSnapshotForTests();
        Assert.NotSame(snapshot, second);
        Assert.Equal(2, second.Length);

        first.Dispose();
        Keybinds.Pump();
        Assert.NotSame(second, Keybinds.PumpSnapshotForTests());
        Assert.Single(Keybinds.PumpSnapshotForTests());
    }

    /// <summary>Stands in for <c>UnityKeyInputSource</c>: a set of chords "pressed this frame".</summary>
    internal sealed class FakeKeyInput : IKeyInputSource
    {
        private readonly HashSet<KeyChord> _pressed = new();

        public bool Available { get; set; } = true;

        public bool ChatFocused { get; set; }

        public bool ChatThrows { get; set; }

        /// <summary>
        /// What the real source answers when no HUDChatWindow can be found or read: not
        /// "the player is not typing" but "I do not know" (R34 finding 7).
        /// </summary>
        public bool ChatUnknown { get; set; }

        /// <summary>
        /// What the real source answers when the UI event system's current selection is a focused
        /// <c>TMP_InputField</c> — the menu's name, address or password box, which the chat probe
        /// cannot see by construction (R36 finding 5).
        /// </summary>
        public bool TextFieldFocusedValue { get; set; }

        /// <summary>What <see cref="TryReadPressedChord"/> answers, for the rebind flow.</summary>
        public KeyChord? NextChord { get; set; }

        /// <summary>Per-frame read counters, so a test can measure what one Pump costs.</summary>
        public int AvailableReads { get; private set; }

        public int ChatReads { get; private set; }

        public int KeyReads { get; private set; }

        public int ChordScans { get; private set; }

        public int TextFieldReads { get; private set; }

        public int TotalReads => AvailableReads + ChatReads + KeyReads + ChordScans + TextFieldReads;

        public void ResetCounters()
        {
            AvailableReads = 0;
            ChatReads = 0;
            KeyReads = 0;
            ChordScans = 0;
            TextFieldReads = 0;
        }

        public bool TextFieldFocused
        {
            get
            {
                TextFieldReads++;
                return TextFieldFocusedValue;
            }
        }

        public bool IsAvailable
        {
            get
            {
                AvailableReads++;
                return Available;
            }
        }

        public ChatFocus ChatFocus
        {
            get
            {
                ChatReads++;
                if (ChatThrows) throw new InvalidOperationException("test: chat read throws");
                if (ChatUnknown) return Progenitor.Ui.ChatFocus.Unknown;
                return ChatFocused ? Progenitor.Ui.ChatFocus.Focused : Progenitor.Ui.ChatFocus.NotFocused;
            }
        }

        public void Press(KeyChord chord) => _pressed.Add(chord);

        /// <summary>
        /// A key down with a set of <b>physical</b> modifier keys held, as the real source reads
        /// them off <c>Keyboard.current</c>'s six side-specific controls. The firing decision is
        /// <see cref="KeyChord.MatchesHeld"/>, the same predicate <c>UnityKeyInputSource</c> calls,
        /// so an AltGr keystroke can be constructed here (R37 finding 1).
        /// </summary>
        public void PressPhysical(UiKey key, ModifierSides sides)
        {
            _physicalKey = key;
            _physicalSides = sides;
        }

        private UiKey _physicalKey = UiKey.None;
        private ModifierSides _physicalSides = ModifierSides.None;

        public void Clear()
        {
            _pressed.Clear();
            _physicalKey = UiKey.None;
            _physicalSides = ModifierSides.None;
            NextChord = null;
        }

        public bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr)
        {
            KeyReads++;
            ReadChords.Add(chord);
            refusedAsAltGr = false;
            if (_pressed.Contains(chord)) return true;
            if (_physicalKey == UiKey.None || !chord.IsBound || chord.Key != _physicalKey)
                return false;
            if (chord.MatchesHeld(_physicalSides)) return true;

            // The real source reports the one refusal it can explain, so the pump can log it
            // instead of the bind silently doing nothing (R38 finding 4).
            refusedAsAltGr = chord.IsAltGrShaped && KeyChord.IsAltGrPair(_physicalSides)
                && KeyChord.ModifiersOf(_physicalSides) == chord.Modifiers;
            return false;
        }

        /// <summary>Every chord the pump asked about, so a test can assert it read only what is bound.</summary>
        public List<KeyChord> ReadChords { get; } = new();

        /// <summary>
        /// The capture half of the physical model. A <see cref="PressPhysical"/> keystroke is
        /// reported with the sides it was held with, exactly as <c>UnityKeyInputSource</c> reads
        /// them off the six side-specific controls — so the rebind AltGr refusal is reachable from
        /// a test, which it was not while this returned <see cref="NextChord"/> verbatim (R38
        /// finding 4, Tests). <see cref="NextChord"/> still works and is read as the left-hand
        /// sides, the pairing that fires.
        /// </summary>
        public bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides)
        {
            ChordScans++;
            if (_physicalKey != UiKey.None)
            {
                sides = _physicalSides;
                chord = new KeyChord(_physicalKey, KeyChord.ModifiersOf(_physicalSides));
                _physicalKey = UiKey.None;
                _physicalSides = ModifierSides.None;
                return true;
            }

            if (NextChord.HasValue)
            {
                chord = NextChord.Value;
                sides = LeftSidesOf(chord.Modifiers);
                NextChord = null;
                return true;
            }

            chord = KeyChord.None;
            sides = ModifierSides.None;
            return false;
        }

        /// <summary>The left-hand pairing of <paramref name="modifiers"/>: never the AltGr pair.</summary>
        private static ModifierSides LeftSidesOf(KeyModifiers modifiers)
        {
            var sides = ModifierSides.None;
            if ((modifiers & KeyModifiers.Ctrl) != 0) sides |= ModifierSides.LeftCtrl;
            if ((modifiers & KeyModifiers.Shift) != 0) sides |= ModifierSides.LeftShift;
            if ((modifiers & KeyModifiers.Alt) != 0) sides |= ModifierSides.LeftAlt;
            return sides;
        }
    }
}
