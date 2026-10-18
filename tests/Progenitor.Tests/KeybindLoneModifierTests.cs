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
/// HOLDMOD1: Ctrl, Shift or Alt on its own as a hold key (requested for "hold Alt for advanced tooltip numbers"). The
/// chord model, the press and hold state machine over a physical keyboard, a lone modifier beside chords on the same
/// modifier, the press-bind refusal and the key capture. Game-free: <see cref="PhysicalKeys"/> reads its keys the way
/// <c>UnityKeyInputSource</c> does (both sides of a lone modifier through one press edge per key, then
/// <see cref="KeyChord.MatchesHeld"/>). Nothing here has run in the game.
/// </summary>
[Collection("PluginLogger")]
public sealed class KeybindLoneModifierTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly ConfigFile _file;
    private readonly object _owner = new();
    private readonly PhysicalKeys _keys = new();

    public KeybindLoneModifierTests()
    {
        Plugin.Logger = _log;
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-holdmod-" + Guid.NewGuid().ToString("N"));
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

    // ---- the chord ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Alt", KeyModifiers.Alt)]
    [InlineData("alt", KeyModifiers.Alt)]
    [InlineData("LeftAlt", KeyModifiers.Alt)]
    [InlineData("RightAlt", KeyModifiers.Alt)]
    [InlineData(" Alt + Alt ", KeyModifiers.Alt)]
    [InlineData("Ctrl", KeyModifiers.Ctrl)]
    [InlineData("LeftControl", KeyModifiers.Ctrl)]
    [InlineData("RightCtrl", KeyModifiers.Ctrl)]
    [InlineData("Shift", KeyModifiers.Shift)]
    [InlineData("RightShift", KeyModifiers.Shift)]
    public void One_modifier_on_its_own_parses_as_that_lone_modifier(string text, KeyModifiers modifier)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.True(chord.IsLoneModifier);
        Assert.True(chord.IsBound);
        Assert.Equal(KeyChord.LoneModifier(modifier), chord);
        Assert.Equal(KeyModifiers.None, chord.Modifiers);
        string name = modifier == KeyModifiers.Ctrl ? "Ctrl" : modifier == KeyModifiers.Shift ? "Shift" : "Alt";
        Assert.Equal(name, chord.ToString());
        Assert.Equal(name, chord.DisplayText);
        Assert.True(KeyChord.TryParse(chord.ToString(), out var back));
        Assert.Equal(chord, back);
        Assert.True(KeyChord.TryParseTyped(name, out var typed, out _));
        Assert.Equal(chord, typed);
    }

    [Fact]
    public void Lone_modifiers_are_side_agnostic_and_never_mix_with_other_modifiers()
    {
        Assert.Equal(KeyChord.Alt, new KeyChord(UiKey.LeftAlt));
        Assert.Equal(KeyChord.Alt, new KeyChord(UiKey.RightAlt));
        Assert.Equal(KeyChord.Alt, new KeyChord(UiKey.RightAlt, KeyModifiers.Alt));
        Assert.Equal(KeyChord.Ctrl, new KeyChord(UiKey.RightCtrl));
        Assert.Equal(KeyChord.Shift, new KeyChord(UiKey.LeftShift));
        Assert.Equal(KeyChord.Alt.GetHashCode(), new KeyChord(UiKey.RightAlt).GetHashCode());
        Assert.Equal(KeyChord.None, KeyChord.LoneModifier(KeyModifiers.Ctrl | KeyModifiers.Alt));
        Assert.Equal(KeyChord.None, KeyChord.LoneModifier(KeyModifiers.None));

        // Two modifiers with no key still read as unbound; Alt with a key is an ordinary chord.
        Assert.True(KeyChord.TryParse("Ctrl + Alt", out var two));
        Assert.False(two.IsBound);
        Assert.False(new KeyChord(UiKey.X, KeyModifiers.Alt).IsLoneModifier);
        Assert.False(new KeyChord(UiKey.LeftMeta).IsLoneModifier);
        Assert.False(KeyChord.IsLoneModifierKey(UiKey.LeftMeta));
        Assert.NotEqual(KeyChord.Alt, new KeyChord(UiKey.X, KeyModifiers.Alt));

        // A lone modifier types nothing and is never a Windows- or Steam-reserved chord.
        Assert.True(KeyChord.Alt.IsSafeWhileTyping);
        Assert.False(ReservedChords.IsReserved(KeyChord.Alt));
        Assert.False(ReservedChords.IsReserved(KeyChord.Shift));
        Assert.False(ReservedChords.IsReserved(KeyChord.Ctrl));
    }

    [Fact]
    public void A_lone_modifier_matches_only_itself_held_on_either_side()
    {
        Assert.True(KeyChord.Alt.MatchesHeld(ModifierSides.LeftAlt));
        Assert.True(KeyChord.Alt.MatchesHeld(ModifierSides.RightAlt));
        Assert.True(KeyChord.Alt.MatchesHeld(ModifierSides.LeftAlt | ModifierSides.RightAlt));
        Assert.False(KeyChord.Alt.MatchesHeld(ModifierSides.LeftAlt | ModifierSides.LeftCtrl));
        // AltGr is LeftCtrl + RightAlt on Windows: neither lone Alt nor lone Ctrl.
        Assert.False(KeyChord.Alt.MatchesHeld(ModifierSides.LeftCtrl | ModifierSides.RightAlt));
        Assert.False(KeyChord.Ctrl.MatchesHeld(ModifierSides.LeftCtrl | ModifierSides.RightAlt));
        Assert.False(KeyChord.Alt.MatchesHeld(ModifierSides.LeftAlt | ModifierSides.LeftMeta));
        Assert.False(KeyChord.Alt.MatchesHeld(ModifierSides.None));
        Assert.True(KeyChord.Shift.MatchesHeld(ModifierSides.RightShift));
    }

    [Fact]
    public void An_ordinary_mods_KeyCode_name_for_a_modifier_reads_and_writes_as_the_lone_modifier()
    {
        Assert.True(LegacyKeyNames.TryParse("LeftAlt", out var alt));
        Assert.Equal(KeyChord.Alt, alt);
        Assert.True(LegacyKeyNames.TryParse("RightControl", out var ctrl));
        Assert.Equal(KeyChord.Ctrl, ctrl);
        Assert.True(LegacyKeyNames.TryFormat(KeyChord.Alt, keyCodeOnly: false, out string text, out _));
        Assert.Equal("LeftAlt", text);
        Assert.True(LegacyKeyNames.TryFormat(KeyChord.Alt, keyCodeOnly: true, out text, out _, previous: "RightAlt"));
        Assert.Equal("RightAlt", text);
        Assert.True(LegacyKeyNames.TryFormat(KeyChord.Shift, keyCodeOnly: false, out text, out _));
        Assert.Equal("LeftShift", text);
    }

    // ---- the hold -------------------------------------------------------------------------------

    private sealed class Counts
    {
        public int Pressed, Held, Released;
    }

    private IKeybindHandle HoldOn(KeyChord chord, Counts c, string id = "Advanced") =>
        Keybinds.Register(_owner, "example.mod", id, _file, chord, "Show advanced numbers while held.",
            () => c.Pressed++, () => c.Held++, () => c.Released++);

    [Fact]
    public void Holding_Alt_presses_once_runs_while_held_and_releases_when_Alt_comes_up()
    {
        var c = new Counts();
        var bind = HoldOn(KeyChord.Alt, c);
        Assert.Equal(KeyChord.Alt, bind.Chord);
        Assert.Equal("Alt", ((ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Advanced")]).Value);

        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        Assert.Equal((1, 0, 0), (c.Pressed, c.Held, c.Released));
        _keys.Frame();
        _keys.Frame();
        Assert.Equal((1, 2, 0), (c.Pressed, c.Held, c.Released));

        // Another key joining does not end the hold, and is not a second press.
        _keys.Down(UiKey.X);
        _keys.Frame();
        _keys.Down(UiKey.LeftCtrl);
        _keys.Frame();
        Assert.Equal((1, 4, 0), (c.Pressed, c.Held, c.Released));
        _keys.Up(UiKey.X);
        _keys.Up(UiKey.LeftCtrl);
        _keys.Up(UiKey.LeftAlt);
        _keys.Frame();
        Assert.Equal((1, 4, 1), (c.Pressed, c.Held, c.Released));
        _keys.Frame();
        Assert.Equal((1, 4, 1), (c.Pressed, c.Held, c.Released));

        // The right Alt key works the same, and so does switching sides mid-hold.
        _keys.Down(UiKey.RightAlt);
        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Up(UiKey.RightAlt);
        _keys.Frame();
        Assert.Equal((2, 5, 1), (c.Pressed, c.Held, c.Released));
        _keys.Up(UiKey.LeftAlt);
        _keys.Frame();
        Assert.Equal((2, 5, 2), (c.Pressed, c.Held, c.Released));
    }

    [Fact]
    public void Alt_plus_X_still_fires_and_does_not_press_the_lone_Alt_hold_again()
    {
        var c = new Counts();
        HoldOn(KeyChord.Alt, c);
        int altX = 0, x = 0;
        Keybinds.Register(_owner, "example.mod", "AltX", _file, new KeyChord(UiKey.X, KeyModifiers.Alt), "", () => altX++);
        Keybinds.Register(_owner, "example.mod", "X", _file, new KeyChord(UiKey.X), "", () => x++);

        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        _keys.Down(UiKey.X);
        _keys.Frame();
        Assert.Equal(1, altX);
        Assert.Equal(0, x);
        Assert.Equal(1, c.Pressed);

        // X again with Alt still held: the chord fires again, the hold is still the one hold.
        _keys.Up(UiKey.X);
        _keys.Frame();
        _keys.Down(UiKey.X);
        _keys.Frame();
        Assert.Equal(2, altX);
        Assert.Equal(1, c.Pressed);
        Assert.Equal(0, c.Released);

        // Both down on the same frame: the chord fires and the hold starts once.
        _keys.Up(UiKey.X);
        _keys.Up(UiKey.LeftAlt);
        _keys.Frame();
        Assert.Equal(1, c.Released);
        _keys.Down(UiKey.LeftAlt);
        _keys.Down(UiKey.X);
        _keys.Frame();
        Assert.Equal(3, altX);
        Assert.Equal(2, c.Pressed);
    }

    [Fact]
    public void Alt_does_not_start_with_another_modifier_held_but_does_with_an_ordinary_key_held()
    {
        var c = new Counts();
        HoldOn(KeyChord.Alt, c);
        int ctrlAltX = 0;
        Keybinds.Register(_owner, "example.mod", "CtrlAltX", _file, new KeyChord(UiKey.F5, KeyModifiers.Ctrl | KeyModifiers.Alt), "", () => ctrlAltX++);

        // Ctrl first, then Alt: Ctrl+Alt is not lone Alt.
        _keys.Frame();
        _keys.Down(UiKey.LeftCtrl);
        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        _keys.Down(UiKey.F5);
        _keys.Frame();
        Assert.Equal(1, ctrlAltX);
        Assert.Equal(0, c.Pressed);

        // AltGr (LeftCtrl + RightAlt on the same frame) is neither.
        _keys.ReleaseAll();
        _keys.Frame();
        _keys.Down(UiKey.LeftCtrl);
        _keys.Down(UiKey.RightAlt);
        _keys.Frame();
        Assert.Equal(0, c.Pressed);

        // Walking with W held: Alt still starts the hold.
        _keys.ReleaseAll();
        _keys.Frame();
        _keys.Down(UiKey.W);
        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        Assert.Equal(1, c.Pressed);
    }

    [Fact]
    public void A_text_field_taking_focus_releases_the_hold_and_holds_it_back()
    {
        var c = new Counts();
        HoldOn(KeyChord.Shift, c);
        _keys.Frame();
        _keys.Down(UiKey.LeftShift);
        _keys.Frame();
        Assert.Equal(1, c.Pressed);

        _keys.TextField = true;
        _keys.Frame();
        Assert.Equal(1, c.Released);

        // Shift for a capital letter in a field never starts the hold.
        _keys.Up(UiKey.LeftShift);
        _keys.Frame();
        _keys.Down(UiKey.LeftShift);
        _keys.Frame();
        Assert.Equal(1, c.Pressed);
    }

    // ---- press binds ----------------------------------------------------------------------------

    [Fact]
    public void A_press_bind_never_takes_a_lone_modifier()
    {
        // A default on one is registered unbound; a cfg value on one falls back to the default.
        var press = Keybinds.Register(_owner, "example.mod", "Toggle", _file, KeyChord.Alt, "", () => { });
        Assert.False(press.Chord.IsBound);
        Assert.Contains(_log.Warnings, l => l == Keybinds.LoneModifierRefusedLog("example.mod/Toggle", KeyChord.Alt, KeyChord.None));

        _file.Bind(Keybinds.Section, "Other", "Shift", "");
        var other = Keybinds.Register(_owner, "example.mod", "Other", _file, new KeyChord(UiKey.F6), "", () => { });
        Assert.Equal(new KeyChord(UiKey.F6), other.Chord);

        // A rebind is refused with the reason; a hold-only handler set is not "press".
        Assert.False(Keybinds.TryRebind("example.mod", "Other", KeyChord.Alt, Keybinds.RebindCheck.Full, out string? why));
        Assert.Equal(Keybinds.LoneModifierPressReason(KeyChord.Alt), why);
        Assert.Equal(new KeyChord(UiKey.F6), other.Chord);
        Assert.StartsWith("Alt on its own only works for a key you hold down", why);

        // A RegisterHold with only onPressed is a press bind too.
        var pressOnly = Keybinds.Register(_owner, "example.mod", "PressOnly", _file, KeyChord.Alt, "", () => { }, null, null);
        Assert.False(pressOnly.Chord.IsBound);

        var c = new Counts();
        HoldOn(new KeyChord(UiKey.F7), c);
        Assert.True(Keybinds.TryRebind("example.mod", "Advanced", KeyChord.Alt, Keybinds.RebindCheck.Full, out _));
        Assert.Equal("Alt", ((ConfigEntry<string>)_file[new ConfigDefinition(Keybinds.Section, "Advanced")]).Value);
    }

    [Fact]
    public void A_cfg_value_of_a_lone_modifier_survives_a_reload_of_the_hold()
    {
        _file.Bind(Keybinds.Section, "Advanced", "RightAlt", "");
        var c = new Counts();
        var bind = HoldOn(new KeyChord(UiKey.F7), c);
        Assert.Equal(KeyChord.Alt, bind.Chord);
    }

    // ---- the capture ----------------------------------------------------------------------------

    [Fact]
    public void Pressing_and_releasing_Alt_alone_in_a_rebind_records_Alt()
    {
        var c = new Counts();
        var bind = HoldOn(new KeyChord(UiKey.F7), c);
        var finished = new List<RebindResult>();
        Keybinds.RebindFinished += finished.Add;
        try
        {
            Assert.True(Keybinds.BeginRebind("example.mod", "Advanced"));
            _keys.Frame();
            _keys.Down(UiKey.LeftAlt);
            _keys.Frame();
            _keys.Frame();
            Assert.Empty(finished);
            _keys.Up(UiKey.LeftAlt);
            _keys.Frame();
            Assert.Equal(KeyChord.Alt, bind.Chord);
            Assert.Equal(RebindOutcome.Bound, Assert.Single(finished).Outcome);
            // The capture never fired the bind it was rebinding.
            Assert.Equal(0, c.Pressed);
        }
        finally { Keybinds.RebindFinished -= finished.Add; }
    }

    [Fact]
    public void A_key_pressed_while_Alt_is_held_records_the_chord_not_Alt()
    {
        var c = new Counts();
        var bind = HoldOn(new KeyChord(UiKey.F7), c);
        Assert.True(Keybinds.BeginRebind("example.mod", "Advanced"));
        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        _keys.Down(UiKey.Q);
        _keys.Frame();
        Assert.Equal(new KeyChord(UiKey.Q, KeyModifiers.Alt), bind.Chord);
        _keys.ReleaseAll();
        _keys.Frame();
        Assert.Equal(new KeyChord(UiKey.Q, KeyModifiers.Alt), bind.Chord);
    }

    [Fact]
    public void Two_modifiers_or_a_click_record_nothing_and_a_modifier_held_at_the_start_waits_for_a_fresh_press()
    {
        var c = new Counts();
        var bind = HoldOn(new KeyChord(UiKey.F7), c);

        // Shift held while the capture starts (the click that opened it): its release is not a key.
        _keys.Down(UiKey.LeftShift);
        _keys.Frame();
        Assert.True(Keybinds.BeginRebind("example.mod", "Advanced"));
        _keys.Frame();
        _keys.Up(UiKey.LeftShift);
        _keys.Frame();
        Assert.Equal(new KeyChord(UiKey.F7), bind.Chord);

        // Ctrl and Alt together, then released: nothing.
        _keys.Down(UiKey.LeftCtrl);
        _keys.Frame();
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        _keys.ReleaseAll();
        _keys.Frame();
        Assert.Equal(new KeyChord(UiKey.F7), bind.Chord);

        // The Windows key with Alt: nothing as a lone modifier (the Windows key is refused on its own).
        _keys.Down(UiKey.LeftAlt);
        _keys.Frame();
        _keys.Down(UiKey.LeftMeta);
        _keys.Frame();
        _keys.ReleaseAll();
        _keys.Frame();
        Assert.Equal(new KeyChord(UiKey.F7), bind.Chord);

        // Still waiting: a clean Ctrl press and release is taken.
        _keys.Down(UiKey.RightCtrl);
        _keys.Frame();
        _keys.Up(UiKey.RightCtrl);
        _keys.Frame();
        Assert.Equal(KeyChord.Ctrl, bind.Chord);
    }

    [Fact]
    public void A_press_binds_capture_refuses_a_lone_modifier_and_keeps_waiting()
    {
        var press = Keybinds.Register(_owner, "example.mod", "Toggle", _file, new KeyChord(UiKey.F6), "", () => { });
        var refused = new List<RebindResult>();
        Keybinds.RebindRefused += refused.Add;
        try
        {
            Assert.True(Keybinds.BeginRebind("example.mod", "Toggle"));
            _keys.Frame();
            _keys.Down(UiKey.LeftAlt);
            _keys.Frame();
            _keys.Up(UiKey.LeftAlt);
            _keys.Frame();
            var r = Assert.Single(refused);
            Assert.Equal(RebindOutcome.Refused, r.Outcome);
            Assert.True(r.StillCapturing);
            Assert.Equal(Keybinds.LoneModifierPressReason(KeyChord.Alt), r.Reason);
            Assert.Equal(new KeyChord(UiKey.F6), press.Chord);

            _keys.Down(UiKey.F8);
            _keys.Frame();
            Assert.Equal(new KeyChord(UiKey.F8), press.Chord);
        }
        finally { Keybinds.RebindRefused -= refused.Add; }
    }

    // ---- the fake ------------------------------------------------------------------------------

    /// <summary>
    /// A keyboard of held keys with one sample per frame. A key's press is "down now, up on the previous sample", the
    /// edge rule of <see cref="KeyEdgeTracker"/>. Lone modifiers and chords are read as <c>UnityKeyInputSource</c> reads
    /// them: either side's edge, then <see cref="KeyChord.MatchesHeld"/> on the six side-specific modifiers.
    /// </summary>
    internal sealed class PhysicalKeys : IKeyInputSource
    {
        private readonly HashSet<UiKey> _held = new();
        private HashSet<UiKey> _previous = new();

        public bool IsAvailable => true;

        public ChatFocus ChatFocus => Progenitor.Ui.ChatFocus.NotFocused;

        public bool TextField { get; set; }

        public bool TextFieldFocused => TextField;

        public void Down(UiKey key) => _held.Add(key);

        public void Up(UiKey key) => _held.Remove(key);

        public void ReleaseAll() => _held.Clear();

        /// <summary>One frame: the pump samples, then this frame's keys become the previous sample.</summary>
        public void Frame()
        {
            Keybinds.Pump();
            _previous = new HashSet<UiKey>(_held);
        }

        private bool WentDown(UiKey key) => _held.Contains(key) && !_previous.Contains(key);

        private ModifierSides Sides()
        {
            var s = ModifierSides.None;
            if (_held.Contains(UiKey.LeftCtrl)) s |= ModifierSides.LeftCtrl;
            if (_held.Contains(UiKey.RightCtrl)) s |= ModifierSides.RightCtrl;
            if (_held.Contains(UiKey.LeftShift)) s |= ModifierSides.LeftShift;
            if (_held.Contains(UiKey.RightShift)) s |= ModifierSides.RightShift;
            if (_held.Contains(UiKey.LeftAlt)) s |= ModifierSides.LeftAlt;
            if (_held.Contains(UiKey.RightAlt)) s |= ModifierSides.RightAlt;
            if (_held.Contains(UiKey.LeftMeta)) s |= ModifierSides.LeftMeta;
            if (_held.Contains(UiKey.RightMeta)) s |= ModifierSides.RightMeta;
            return s;
        }

        public bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr)
        {
            refusedAsAltGr = false;
            if (!chord.IsBound) return false;
            if (chord.IsLoneModifier)
            {
                bool left = WentDown(KeyChord.LeftKeyOf(chord.LoneFlag));
                bool right = WentDown(KeyChord.RightKeyOf(chord.LoneFlag));
                return (left || right) && chord.MatchesHeld(Sides());
            }

            return WentDown(chord.Key) && chord.MatchesHeld(Sides());
        }

        public bool IsHeld(KeyChord chord)
        {
            if (!chord.IsBound) return false;
            if (chord.IsLoneModifier)
                return _held.Contains(KeyChord.LeftKeyOf(chord.LoneFlag)) || _held.Contains(KeyChord.RightKeyOf(chord.LoneFlag));
            return _held.Contains(chord.Key);
        }

        public bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides)
        {
            chord = KeyChord.None;
            sides = Sides();
            foreach (var key in _held.OrderBy(k => k))
            {
                // As the real capture: Ctrl, Shift and Alt are never a candidate; the Windows key is reported.
                if (KeyChord.IsLoneModifierKey(key) || !WentDown(key)) continue;
                chord = new KeyChord(key, KeyChord.ModifiersOf(sides));
                return true;
            }

            return false;
        }

        public bool TryReadHeldModifiers(out ModifierSides sides)
        {
            sides = Sides();
            return true;
        }
    }
}
