using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// PAD1 (controller keybinds): the gamepad chord model and its cfg form, the controller entry next to the keyboard
    /// one (old cfgs load unchanged), the gamepad picture's winner rule against the game's controller bindings and
    /// other binds, the pump and the press device, the controller capture, the Keybinds tab's controller cap, the map
    /// share's controller default and its hint glyph choice. Game-free, on <see cref="KeybindTabTests"/>' fakes. Not run
    /// in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord PadY = KeyChord.Gamepad(UiKey.GamepadNorth);
        private static readonly KeyChord PadYDown = KeyChord.Gamepad(UiKey.GamepadNorth, UiKey.GamepadDpadDown);

        private IKeybindHandle RegisterMapPad(string owner, string id, KeyChord keyboard, KeyChord pad, System.Action? press = null,
            System.Action? release = null)
        {
            var h = Keybinds.RegisterScoped(_owner, owner, id, File(owner), keyboard, "", press ?? (() => { }), null, release,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);
            return Keybinds.SetGamepadDefault(h, pad);
        }

        /// <summary>A cfg as the next start reads it: a new <see cref="ConfigFile"/> over the path, loaded from disk.</summary>
        private static ConfigFile Loaded(string path)
        {
            var file = new ConfigFile(path, saveOnInit: false);
            file.Reload();
            return file;
        }

        private static void UseShippedPad() => KeybindArbiter.GameGamepadSource = GamepadKeybindCatalog.DefaultSnapshot;

        // ---- the chord ---------------------------------------------------------------------------

        [Theory]
        [InlineData("GamepadNorth + GamepadDpadDown", UiKey.GamepadNorth, UiKey.GamepadDpadDown)]
        [InlineData("Pad Y + D-Pad Down", UiKey.GamepadNorth, UiKey.GamepadDpadDown)]
        [InlineData("Triangle + dpad/down", UiKey.GamepadNorth, UiKey.GamepadDpadDown)]
        [InlineData("buttonSouth", UiKey.GamepadSouth, UiKey.None)]
        [InlineData("Pad LB", UiKey.GamepadLeftShoulder, UiKey.None)]
        [InlineData("GamepadRightTrigger + L1", UiKey.GamepadRightTrigger, UiKey.GamepadLeftShoulder)]
        [InlineData("Pad Menu", UiKey.GamepadStart, UiKey.None)]
        public void A_gamepad_chord_parses_from_its_cfg_shown_and_control_names(string text, UiKey key, UiKey held)
        {
            Assert.True(KeyChord.TryParse(text, out var chord));
            Assert.Equal(KeyChord.Gamepad(key, held), chord);
            Assert.True(chord.IsGamepad);
            Assert.Equal(KeyModifiers.None, chord.Modifiers);
            Assert.Equal(held, chord.Held);
            // Both forms parse back to the same chord.
            Assert.True(KeyChord.TryParse(chord.ToString(), out var fromCfg));
            Assert.Equal(chord, fromCfg);
            Assert.True(KeyChord.TryParse(chord.DisplayText, out var fromShown));
            Assert.Equal(chord, fromShown);
        }

        [Fact]
        public void The_gamepad_chord_text_and_the_refusals()
        {
            Assert.Equal("GamepadNorth + GamepadDpadDown", PadYDown.ToString());
            Assert.Equal("Pad Y + D-Pad Down", PadYDown.DisplayText);
            Assert.Equal("Pad Y", PadY.DisplayText);
            // The bare letters stay keyboard keys.
            Assert.True(KeyChord.TryParse("Y", out var y));
            Assert.Equal(new KeyChord(UiKey.Y), y);
            Assert.False(KeyChord.TryParse("Up", out _));
            // A controller button never takes Ctrl, Shift or Alt, nor a keyboard key, nor itself twice, nor a third button.
            Assert.False(KeyChord.TryParse("GamepadNorth + Ctrl", out _));
            Assert.False(KeyChord.TryParse("GamepadNorth + F8", out _));
            Assert.False(KeyChord.TryParse("F8 + GamepadNorth", out _));
            Assert.False(KeyChord.TryParse("GamepadNorth + GamepadNorth", out _));
            Assert.False(KeyChord.TryParse("GamepadNorth + GamepadDpadDown + GamepadSouth", out _));
            // The held button is part of the chord's identity; keyboard modifiers are dropped on a controller button.
            Assert.NotEqual(PadY, PadYDown);
            Assert.NotEqual(PadYDown, KeyChord.Gamepad(UiKey.GamepadDpadDown, UiKey.GamepadNorth));
            Assert.Equal(PadY, new KeyChord(UiKey.GamepadNorth, KeyModifiers.Ctrl));
            Assert.Equal(KeyChord.None, KeyChord.Gamepad(UiKey.F8));
            Assert.Equal(PadY, KeyChord.Gamepad(UiKey.GamepadNorth, UiKey.F8));
            // A controller button types nothing and is no keyboard modifier problem.
            Assert.True(PadY.IsSafeWhileTyping);
            Assert.True(Keybinds.TypingSafeChord(PadYDown));
            Assert.False(UiTypedKeys.FieldTakes(PadY));
        }

        [Fact]
        public void A_gamepad_chord_fires_on_exactly_its_held_button()
        {
            uint Bits(params UiKey[] keys) => keys.Aggregate(0u, (m, k) => m | KeyChord.GamepadBit(k));
            Assert.True(PadYDown.MatchesGamepadHeld(Bits(UiKey.GamepadNorth, UiKey.GamepadDpadDown)));
            Assert.False(PadYDown.MatchesGamepadHeld(Bits(UiKey.GamepadNorth)));
            Assert.False(PadYDown.MatchesGamepadHeld(Bits(UiKey.GamepadNorth, UiKey.GamepadDpadDown, UiKey.GamepadLeftShoulder)));
            Assert.True(PadY.MatchesGamepadHeld(Bits(UiKey.GamepadNorth)));
            Assert.False(PadY.MatchesGamepadHeld(Bits(UiKey.GamepadNorth, UiKey.GamepadDpadDown)));
            Assert.False(new KeyChord(UiKey.F8).MatchesGamepadHeld(0));
        }

        // ---- persistence -------------------------------------------------------------------------

        [Fact]
        public void An_old_cfg_loads_unchanged_and_gets_no_controller_line_for_a_bind_without_one()
        {
            string path = Path.Combine(_dir, "old.cfg");
            // The cfg an earlier Core wrote: the keyboard entry only.
            var before = new ConfigFile(path, saveOnInit: true);
            before.Bind(Keybinds.Section, "Ping", "F9", "Old bind.").Value = "F8 + Ctrl";
            before.Save();
            var file = Loaded(path);
            var h = Keybinds.Register(_owner, "old.mod", "Ping", file, F9, "Old bind.", () => { });
            Assert.Equal(new KeyChord(UiKey.F8, KeyModifiers.Ctrl), h.Chord);
            Assert.Equal(KeyChord.None, h.GamepadChord);
            file.Save();
            Assert.DoesNotContain("Ping.Gamepad", System.IO.File.ReadAllText(path));
        }

        [Fact]
        public void A_controller_default_binds_its_own_entry_and_a_saved_choice_wins_after_a_reload()
        {
            string path = Path.Combine(_dir, "pad.cfg");
            var file = new ConfigFile(path, saveOnInit: true);
            var h = Keybinds.Register(_owner, "pad.mod", "Share", file, F9, "Shares.", () => { });
            Keybinds.SetGamepadDefault(h, PadYDown);
            Assert.Equal(PadYDown, h.GamepadChord);
            Assert.Equal(F9, h.Chord);
            Assert.Equal("GamepadNorth + GamepadDpadDown", file[new ConfigDefinition(Keybinds.Section, "Share" + Keybinds.GamepadEntrySuffix)].GetSerializedValue());

            // The player picks Pad X; a new generation reads it back, over the default.
            Assert.True(Keybinds.TryRebindGamepad("pad.mod", "Share", KeyChord.Gamepad(UiKey.GamepadWest), false, out var why), why);
            file.Save();
            Keybinds.ResetForTests();
            Keybinds.SetSourceForTests(_keys);
            var reloaded = new ConfigFile(path, saveOnInit: false);
            reloaded.Reload();
            var again = Keybinds.SetGamepadDefault(Keybinds.Register(_owner, "pad.mod", "Share", reloaded, F9, "Shares.", () => { }), PadYDown);
            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadWest), again.GamepadChord);

            // A bind with no controller default keeps a saved choice too (the entry is read when the cfg has one).
            Keybinds.ResetForTests();
            Keybinds.SetSourceForTests(_keys);
            var third = Keybinds.Register(_owner, "pad.mod", "Share", Loaded(path), F9, "", () => { });
            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadWest), third.GamepadChord);
        }

        [Fact]
        public void A_controller_value_in_the_keyboard_entry_and_a_keyboard_value_in_the_controller_entry_fall_back()
        {
            var file = File("mixed");
            file.Bind(Keybinds.Section, "Act", "GamepadSouth", "");
            file.Bind(Keybinds.Section, "Act" + Keybinds.GamepadEntrySuffix, "F7", "");
            var h = Keybinds.Register(_owner, "mixed.mod", "Act", file, F9, "", () => { });
            Assert.Equal(F9, h.Chord);
            Assert.Contains(_log.Warnings, l => l.Contains(Keybinds.KeyNotGamepadReason));
            Keybinds.SetGamepadDefault(h, PadY);
            Assert.Equal(PadY, h.GamepadChord);
            Assert.Contains(_log.Warnings, l => l.Contains("unreadable controller value 'F7'"));
            Assert.Throws<System.ArgumentException>(() => Keybinds.SetGamepadDefault(h, F8));
        }

        [Fact]
        public void A_controller_chord_given_as_the_default_is_the_controller_default()
        {
            var h = Keybinds.Register(_owner, "only.mod", "Pad", File("only"), PadY, "", () => { });
            Assert.Equal(KeyChord.None, h.Chord);
            Assert.Equal(PadY, h.GamepadChord);
            // Keybinds.Rebind routes a controller chord to the controller key.
            Assert.True(Keybinds.Rebind("only.mod", "Pad", PadYDown));
            Assert.Equal(PadYDown, h.GamepadChord);
            Assert.Equal(KeyChord.None, h.Chord);
        }

        // ---- conflicts ---------------------------------------------------------------------------

        [Fact]
        public void The_shipped_controller_bindings_read_as_game_claims_with_their_screens()
        {
            var snap = GamepadKeybindCatalog.DefaultSnapshot();
            Assert.True(snap.Available);
            var marker = snap.Claims.Where(c => c.ActionId == "MapMarker").ToList();
            Assert.Equal(new[] { KeyChord.Gamepad(UiKey.GamepadSouth), KeyChord.Gamepad(UiKey.GamepadRightTrigger) }, marker.Select(c => c.Chord));
            Assert.All(marker, c => Assert.Equal(GameKeybindCatalog.InTheMap, c.Mode));
            var teleport = snap.Claims.Single(c => c.ActionId == "MapTeleport");
            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadRightStick, UiKey.GamepadLeftStick), teleport.Chord);
            Assert.Equal(GameKeybindCatalog.InMenus, snap.Claims.First(c => c.ActionId == "Menu_North").Mode);
            Assert.Equal("", snap.Claims.First(c => c.ActionId == "Primary").Mode);
            Assert.False(snap.Claims.Single(c => c.ActionId == "DebugModifier").Comparable);
            // A composite's modifier is the held button: AutoMove is LT held, D-Pad Left pressed.
            Assert.Contains(snap.Claims, c => c.ActionId == "AutoMove" && c.Chord == KeyChord.Gamepad(UiKey.GamepadDpadLeft, UiKey.GamepadLeftTrigger));
            Assert.True(GamepadKeybindCatalog.TryButton("<XInputController>/select", out var select));
            Assert.Equal(UiKey.GamepadSelect, select);
            Assert.False(GamepadKeybindCatalog.TryButton("<Keyboard>/a", out _));
            Assert.False(GamepadKeybindCatalog.TryButton("<DualShockGamepad>/touchpadButton", out _));
        }

        [Fact]
        public void A_map_controller_chord_loses_to_the_game_map_and_menu_buttons_and_the_share_default_is_free()
        {
            UseShippedPad();
            RegisterMapPad("a.mod", "OnA", F7, KeyChord.Gamepad(UiKey.GamepadSouth));
            RegisterMapPad("a.mod", "OnY", F8, PadY);
            RegisterMapPad("a.mod", "Share", F9, MapShareLogic.DefaultGamepadKey);
            var pad = KeybindArbiter.CurrentGamepad();
            var onA = pad.FindProgenitor("a.mod", "OnA")!;
            Assert.False(onA.Active);
            Assert.StartsWith("Not active: Pad A is used by the game", onA.Reason);
            Assert.False(pad.FindProgenitor("a.mod", "OnY")!.Active); // the menus' North action
            var share = pad.FindProgenitor("a.mod", "Share")!;
            Assert.True(share.Active);
            Assert.False(share.InConflict);
            // The keyboard picture never sees a controller chord, and the other way round.
            Assert.True(KeybindArbiter.Current().FindProgenitor("a.mod", "OnA")!.Active);
            Assert.NotNull(Keybinds.GamepadClashReason("a.mod", "Other", KeybindScope.Map, KeyChord.Gamepad(UiKey.GamepadSouth)));
            Assert.Null(Keybinds.GamepadClashReason("a.mod", "Other", KeybindScope.Map, KeyChord.Gamepad(UiKey.GamepadDpadUp, UiKey.GamepadNorth)));
        }

        [Fact]
        public void The_share_default_meets_no_shipped_binding_in_the_map()
        {
            // Every shipped binding a map key meets (the map's, the menus') is checked against the default chord.
            foreach (var c in GamepadKeybindCatalog.DefaultSnapshot().Claims)
            {
                if (!c.Comparable || !KeybindArbiter.ScopesMeet(c, KeybindScope.Map)) continue;
                Assert.NotEqual(MapShareLogic.DefaultGamepadKey, c.Chord);
            }

            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadNorth, UiKey.GamepadDpadDown), MapShareLogic.DefaultGamepadKey);
        }

        [Fact]
        public void Two_binds_on_one_controller_chord_follow_the_first_installed_rule_and_scopes()
        {
            UseShippedPad();
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            RegisterMapPad("second.mod", "Thing", F7, PadYDown);
            RegisterMapPad("first.mod", "Thing", F8, PadYDown);
            // A gameplay bind on the same controller chord is another scope and meets neither map key.
            var gameplay = Keybinds.SetGamepadDefault(Keybinds.Register(_owner, "third.mod", "Act", File("third"), F9, "", () => { }), PadYDown);
            var pad = KeybindArbiter.CurrentGamepad();
            Assert.True(pad.FindProgenitor("first.mod", "Thing")!.Active);
            var loser = pad.FindProgenitor("second.mod", "Thing")!;
            Assert.False(loser.Active);
            Assert.StartsWith("Not active: Pad Y + D-Pad Down is used by first.mod", loser.Reason);
            Assert.True(pad.FindProgenitor("third.mod", "Act")!.Active);
            Assert.Equal(loser.Reason, KeybindArbiter.GamepadBlockedReason("second.mod", "Thing"));

            // A remap never creates a clash: a third map key may not move onto it.
            RegisterMapPad("fourth.mod", "Other", new KeyChord(UiKey.F6), KeyChord.Gamepad(UiKey.GamepadDpadUp, UiKey.GamepadNorth));
            Assert.False(Keybinds.TryRebindGamepad("fourth.mod", "Other", PadYDown, false, out var why));
            Assert.Contains("is used by", why);
            Assert.False(Keybinds.TryRebindGamepad("fourth.mod", "Other", F8, false, out why));
            Assert.Equal(Keybinds.NotGamepadReason, why);
            Assert.True(Keybinds.TryRebindGamepad("fourth.mod", "Other", KeyChord.None, false, out _));
            Assert.Equal(PadYDown, gameplay.GamepadChord);
        }

        // ---- the pump ----------------------------------------------------------------------------

        [Fact]
        public void A_controller_press_runs_the_bind_with_the_device_known_and_holds_until_its_button_is_up()
        {
            bool? pressedPad = null, releasedPad = null;
            int presses = 0;
            Keybinds.MapScopeOpen = () => true;
            RegisterMapPad("m.mod", "Share", F9, PadYDown,
                press: () => { presses++; pressedPad = Keybinds.PressedWithGamepad; },
                release: () => releasedPad = Keybinds.PressedWithGamepad);

            _keys.Down(PadYDown);
            _keys.Frame();
            Assert.Equal(1, presses);
            Assert.True(pressedPad);
            Assert.False(Keybinds.PressedWithGamepad); // only inside the handler
            _keys.Frame(); // still held
            Assert.Null(releasedPad);
            _keys.Up(UiKey.GamepadNorth);
            _keys.Frame();
            Assert.True(releasedPad);

            // The keyboard chord still fires, as the keyboard.
            _keys.Down(F9);
            _keys.Frame();
            Assert.Equal(2, presses);
            Assert.False(pressedPad);
            _keys.Up(UiKey.F9);
            _keys.Frame();
            Assert.False(releasedPad);

            // With the map closed neither fires.
            Keybinds.MapScopeOpen = () => false;
            _keys.Down(PadYDown);
            _keys.Frame();
            Assert.Equal(2, presses);
        }

        [Fact]
        public void A_controller_chord_that_lost_does_not_fire_and_the_bridge_press_says_why()
        {
            UseShippedPad();
            int presses = 0;
            Keybinds.MapScopeOpen = () => true;
            RegisterMapPad("m.mod", "OnA", F9, KeyChord.Gamepad(UiKey.GamepadSouth), press: () => presses++);
            KeybindArbiter.RefreshIfStale();
            _keys.Down(KeyChord.Gamepad(UiKey.GamepadSouth));
            _keys.Frame();
            Assert.Equal(0, presses);
            Assert.Contains(_log.Infos, l => l.Contains("'m.mod/OnA (controller)' was pressed but not fired"));
            var d = Keybinds.SimulateGamepadPress("m.mod", "OnA").Single();
            Assert.False(d.Fired);
            Assert.StartsWith("not-active: Not active: Pad A", d.Reason);
            Assert.Equal("GamepadSouth", d.Chord);
            Assert.Equal("unknown bind", Keybinds.SimulateGamepadPress(null, "nothing").Single().Reason);
            Keybinds.Register(_owner, "k.mod", "KeysOnly", File("k"), F8, "", () => { });
            Assert.Equal("no controller button", Keybinds.SimulateGamepadPress("k.mod", "KeysOnly").Single().Reason);
        }

        [Fact]
        public void The_bridge_press_by_chord_reaches_the_controller_chord_and_parses_gamepad()
        {
            bool? pad = null;
            Keybinds.MapScopeOpen = () => true;
            RegisterMapPad("m.mod", "Share", F9, PadYDown, press: () => pad = Keybinds.PressedWithGamepad);
            var d = Keybinds.SimulatePress(PadYDown).Single();
            Assert.True(d.Fired, d.Reason);
            Assert.True(pad);
            Assert.True(Keybinds.SimulateGamepadPress("m.mod", "Share").Single().Fired);

            var request = KeybindPressOps.Parse(System.Text.Json.JsonDocument.Parse("{\"bind\":\"Share\",\"gamepad\":true}").RootElement);
            Assert.True(request.Gamepad);
            Assert.Throws<System.ArgumentException>(() => KeybindPressOps.Parse(
                System.Text.Json.JsonDocument.Parse("{\"bind\":\"Share\",\"gamepad\":true,\"hold\":3}").RootElement));
            var byChord = KeybindPressOps.Parse(System.Text.Json.JsonDocument.Parse("{\"chord\":\"GamepadNorth + GamepadDpadDown\"}").RootElement);
            Assert.Equal(PadYDown, byChord.Chord);
        }

        // ---- the capture -------------------------------------------------------------------------

        [Fact]
        public void The_controller_capture_decides_on_the_first_release()
        {
            uint Bits(params UiKey[] keys) => keys.Aggregate(0u, (m, k) => m | KeyChord.GamepadBit(k));
            var c = new GamepadCapture();
            // The Pad A that clicked the cap is down at the start: ignored until it comes up.
            Assert.False(c.Observe(Bits(UiKey.GamepadSouth), out _, out _));
            Assert.False(c.Observe(0, out _, out _));
            // D-Pad Down held, then Pad Y; let go of Pad Y first.
            Assert.False(c.Observe(Bits(UiKey.GamepadDpadDown), out _, out _));
            Assert.False(c.Observe(Bits(UiKey.GamepadDpadDown, UiKey.GamepadNorth), out _, out _));
            Assert.True(c.Observe(Bits(UiKey.GamepadDpadDown), out var chord, out var refusal));
            Assert.Null(refusal);
            Assert.Equal(PadYDown, chord);

            // One button alone is that button, whichever frame it comes up.
            var single = new GamepadCapture();
            single.Observe(0, out _, out _);
            Assert.False(single.Observe(Bits(UiKey.GamepadLeftShoulder), out _, out _));
            Assert.True(single.Observe(0, out chord, out _));
            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadLeftShoulder), chord);

            // Three are refused; the buttons still held must come up before the next gesture.
            var three = new GamepadCapture();
            three.Observe(0, out _, out _);
            three.Observe(Bits(UiKey.GamepadNorth), out _, out _);
            three.Observe(Bits(UiKey.GamepadNorth, UiKey.GamepadSouth), out _, out _);
            three.Observe(Bits(UiKey.GamepadNorth, UiKey.GamepadSouth, UiKey.GamepadWest), out _, out _);
            Assert.True(three.Observe(Bits(UiKey.GamepadNorth, UiKey.GamepadSouth), out chord, out refusal));
            Assert.Equal(GamepadCapture.TooManyReason, refusal);
            Assert.Equal(KeyChord.None, chord);
            Assert.False(three.Observe(Bits(UiKey.GamepadNorth), out _, out _));
            Assert.False(three.Observe(0, out _, out _));
            Assert.False(three.Observe(Bits(UiKey.GamepadEast), out _, out _));
            Assert.True(three.Observe(0, out chord, out _));
            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadEast), chord);
        }

        [Fact]
        public void Two_buttons_down_on_one_read_are_refused_with_the_hold_first_hint()
        {
            uint Bits(params UiKey[] keys) => keys.Aggregate(0u, (m, k) => m | KeyChord.GamepadBit(k));
            var c = new GamepadCapture();
            c.Observe(0, out _, out _);
            Assert.False(c.Observe(Bits(UiKey.GamepadNorth, UiKey.GamepadDpadDown), out _, out _));
            Assert.True(c.Active);
            Assert.True(c.Observe(Bits(UiKey.GamepadDpadDown), out var chord, out var refusal));
            Assert.Equal(GamepadCapture.SameTimeReason, refusal);
            Assert.Equal(KeyChord.None, chord);
            // Let go of everything, then the right order works.
            Assert.False(c.Observe(0, out _, out _));
            c.Observe(Bits(UiKey.GamepadDpadDown), out _, out _);
            c.Observe(Bits(UiKey.GamepadDpadDown, UiKey.GamepadNorth), out _, out _);
            Assert.True(c.Observe(Bits(UiKey.GamepadDpadDown), out chord, out refusal));
            Assert.Null(refusal);
            Assert.Equal(PadYDown, chord);
        }

        [Fact]
        public void A_controller_capture_ends_after_5_seconds_with_no_button_and_a_held_button_keeps_it()
        {
            long now = 1000;
            Keybinds.Clock = () => now;
            Keybinds.Register(_owner, "t.mod", "Any", File("t"), F9, "", () => { });
            KeyCaptureResult? got = null;
            _keys.PadHeld = 0;
            Keybinds.BeginCapture(r => got = r, gamepad: true);
            _keys.Frame();
            now += 4000;
            _keys.PadHeld = KeyChord.GamepadBit(UiKey.GamepadEast); // B is held: the wait restarts
            _keys.Frame();
            now += 4900;
            _keys.Frame();
            Assert.True(Keybinds.CapturingGamepad);
            Assert.Null(got);
            _keys.PadHeld = 0; // B released: B is the key
            _keys.Frame();
            Assert.Equal(RebindOutcome.Bound, got!.Value.Outcome);
            Assert.Equal(KeyChord.Gamepad(UiKey.GamepadEast), got.Value.Chord);

            // No button for 5 s: canceled with the reason.
            got = null;
            Keybinds.BeginCapture(r => got = r, gamepad: true);
            _keys.Frame();
            now += Keybinds.GamepadCaptureTimeoutMs + 1;
            _keys.Frame();
            Assert.False(Keybinds.Capturing);
            Assert.Equal(RebindOutcome.Cancelled, got!.Value.Outcome);
            Assert.Equal(Keybinds.GamepadCaptureTimedOutReason, got.Value.Reason);
        }

        [Fact]
        public void The_game_controller_bindings_are_read_again_when_a_gamepad_connects()
        {
            int reads = 0;
            KeybindArbiter.GameGamepadSource = () => { reads++; return GamepadKeybindCatalog.DefaultSnapshot(); };
            Keybinds.Register(_owner, "r.mod", "Any", File("r"), F9, "", () => { });
            KeybindArbiter.Current();
            int first = reads;
            KeybindArbiter.RefreshIfStale();
            Assert.Equal(first, reads); // nothing changed
            _keys.PadHeld = 0; // a pad connects
            KeybindArbiter.RefreshIfStale();
            Assert.Equal(first + 1, reads);
            _keys.PadHeld = null; // and goes away
            KeybindArbiter.RefreshIfStale();
            Assert.Equal(first + 2, reads);
        }

        [Fact]
        public void The_keybinds_tab_controller_cap_captures_previews_refuses_and_saves()
        {
            UseShippedPad();
            Keybinds.MapScopeOpen = () => false;
            var h = RegisterMapPad("a.mod", "Share", F9, KeyChord.None);
            _keys.PadHeld = 0;
            OpenKeybindsTab();
            string row = ModRowId("a.mod");
            Assert.True(Pane().Model!.FindRow(row)!.HasGamepadCap);

            // A click on the second cap waits for a controller button; the pad's gesture ends it.
            Pane().PressSecondary(row);
            Assert.True(Keybinds.CapturingGamepad);
            Assert.Equal(KeybindsPane.GamepadSlot, Pane().EditingSlot);
            uint down = KeyChord.GamepadBit(UiKey.GamepadDpadDown), y = KeyChord.GamepadBit(UiKey.GamepadNorth);
            _keys.Frame(); // the first read
            _keys.PadHeld = down;
            _keys.Frame();
            _keys.PadHeld = down | y;
            _keys.Frame();
            _keys.PadHeld = down;
            _keys.Frame();
            Assert.False(Keybinds.Capturing);
            Assert.Equal(PadYDown, Pane().Pending);

            // Taken buttons are refused and the capture keeps waiting (Pad A is the map's Add Marker).
            Assert.True(Pane().CaptureForBridge(row, KeyChord.Gamepad(UiKey.GamepadSouth)));
            Assert.Null(Pane().Pending);
            Assert.True(Keybinds.CapturingGamepad);
            Assert.True(Pane().CaptureForBridge(row, PadYDown));
            Pane().SaveEdit();
            Assert.Equal(PadYDown, h.GamepadChord);
            var tab = Tab().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("owner").GetString() == "a.mod");
            Assert.Equal("Pad Y + D-Pad Down", tab.GetProperty("gamepad").GetString());
            Assert.True(tab.GetProperty("gamepadActive").GetBoolean());
            Assert.True(tab.GetProperty("gamepadChanged").GetBoolean());

            // Reset puts the controller default (none) back.
            Pane().ResetRow(ModRowId("a.mod"));
            Assert.Equal(KeyChord.None, h.GamepadChord);
        }

        // ---- the map share -----------------------------------------------------------------------

        [Fact]
        public void The_map_share_tip_shows_the_controller_chord_while_a_controller_is_active()
        {
            var mouse = MapShareLogic.DefaultKey;
            var pad = MapShareLogic.DefaultGamepadKey;
            Assert.Equal(mouse, MapShareLogic.HintChord(mouse, pad, controllerActive: false));
            Assert.Equal(pad, MapShareLogic.HintChord(mouse, pad, controllerActive: true));
            Assert.Equal(mouse, MapShareLogic.HintChord(mouse, KeyChord.None, controllerActive: true));

            var hint = MapShareLogic.Hint(pad);
            Assert.Equal(MapShareGlyph.Gamepad, hint.Glyph);
            Assert.Equal(UiKey.GamepadNorth, hint.GamepadKey);
            Assert.Equal(UiKey.GamepadDpadDown, hint.GamepadHeld);
            Assert.Equal("[D-Pad Down] + [Pad Y] Share Location", hint.Text);
            Assert.Equal("[Pad A] Share Location", MapShareLogic.Hint(KeyChord.Gamepad(UiKey.GamepadSouth)).Text);
            Assert.Equal("<Gamepad>/buttonNorth", GamepadKeybindCatalog.PathOf(UiKey.GamepadNorth));
            Assert.Equal("<Gamepad>/dpad/down", GamepadKeybindCatalog.PathOf(UiKey.GamepadDpadDown));

            // A controller tip copies any entry drawing a glyph (the controller row's entries all do).
            var row = new[]
            {
                new MapHintEntry(true, "Pan Map", false, "", true),
                new MapHintEntry(false, "Hidden", true, "Icon_XboxA", false),
                new MapHintEntry(true, "Add/Remove Marker", true, "Icon_XboxA", false),
            };
            Assert.Equal(2, MapShareLogic.PickDonor(row, MapShareGlyph.Gamepad));
            Assert.Equal(-1, MapShareLogic.PickDonor(new[] { new MapHintEntry(true, "Pan", false, "", true) }, MapShareGlyph.Gamepad));
        }

        [Fact]
        public void A_controller_share_needs_the_cursor_on_the_map_with_world_coordinates()
        {
            Assert.True(MapShareLogic.GamepadShareable(true, true, 1200f, -340f));
            Assert.False(MapShareLogic.GamepadShareable(false, true, 1f, 1f));
            Assert.False(MapShareLogic.GamepadShareable(true, false, 1f, 1f));
            Assert.False(MapShareLogic.GamepadShareable(true, true, float.NaN, 1f));
        }
    }
}
