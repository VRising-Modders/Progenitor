using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS8 (user report 2026-10-08, "I couldn't set the keybind to a mouse click"): keybinds on the left and
    /// right mouse buttons. The chord text and its parse, the scope rules (<see cref="MouseChords"/>), the capture
    /// (the click that started it never binds, a bare click stays the panel's, Escape still cancels), the read
    /// gates (not over a Progenitor window, a gameplay click not in the game's menu), the click swallow on
    /// Progenitor's widgets and the first-installed conflict rule. Game-free, on <see cref="KeybindTabTests"/>'
    /// fakes. Not run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord LeftCtrlShift = new(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift);
        private static readonly KeyChord LeftCtrl = new(UiKey.MouseLeft, KeyModifiers.Ctrl);
        private static readonly KeyChord BareLeft = new(UiKey.MouseLeft);
        private static readonly KeyChord BareRight = new(UiKey.MouseRight);

        [Theory]
        [InlineData("Mouse0 + Ctrl + Shift", UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift)]
        [InlineData("MouseLeft + Shift + Ctrl", UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift)]
        [InlineData("Left Mouse + Alt", UiKey.MouseLeft, KeyModifiers.Alt)]
        [InlineData("Ctrl + Left Click", UiKey.MouseLeft, KeyModifiers.Ctrl)]
        [InlineData("Mouse1", UiKey.MouseRight, KeyModifiers.None)]
        [InlineData("Right Mouse + Ctrl", UiKey.MouseRight, KeyModifiers.Ctrl)]
        [InlineData("mouseright", UiKey.MouseRight, KeyModifiers.None)]
        [InlineData("Mouse2", UiKey.MouseMiddle, KeyModifiers.None)]
        [InlineData("Mouse3", UiKey.MouseBack, KeyModifiers.None)]
        [InlineData("Mouse4 + Ctrl", UiKey.MouseForward, KeyModifiers.Ctrl)]
        [InlineData("Mouse Button 4", UiKey.MouseForward, KeyModifiers.None)]
        [InlineData("Mouse Button 5", UiKey.MouseBack, KeyModifiers.None)]
        [InlineData("MouseBack", UiKey.MouseBack, KeyModifiers.None)]
        [InlineData("F10 + Ctrl", UiKey.F10, KeyModifiers.Ctrl)]
        public void Mouse_chords_parse_in_the_cfg_format_and_old_values_still_read(string text, UiKey key, KeyModifiers mods)
        {
            Assert.True(KeyChord.TryParse(text, out var chord));
            Assert.Equal(new KeyChord(key, mods), chord);
            // The cfg form and the shown form both read back to the same chord.
            Assert.True(KeyChord.TryParse(chord.ToString(), out var again) && again == chord);
            Assert.True(KeyChord.TryParse(chord.DisplayText, out var shown) && shown == chord);
        }

        [Fact]
        public void Mouse_chords_format_with_the_key_first_and_the_buttons_in_words_on_screen()
        {
            Assert.Equal("MouseLeft + Ctrl + Shift", LeftCtrlShift.ToString());
            Assert.Equal("Left Mouse + Ctrl + Shift", LeftCtrlShift.DisplayText);
            Assert.Equal("MouseRight", BareRight.ToString());
            Assert.Equal("Right Mouse", BareRight.DisplayText);
            Assert.True(KeyChord.IsMouseKey(UiKey.MouseLeft) && KeyChord.IsMouseKey(UiKey.MouseRight));
            Assert.True(KeyChord.IsClickButton(UiKey.MouseLeft) && KeyChord.IsClickButton(UiKey.MouseRight));
            Assert.False(KeyChord.IsClickButton(UiKey.MouseMiddle));
            // A click is no text field's key, and is never refused as AltGr.
            Assert.True(new KeyChord(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Alt).IsSafeWhileTyping);
            Assert.True(new KeyChord(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Alt).MatchesHeld(ModifierSides.LeftCtrl | ModifierSides.RightAlt));
            // There is no sixth button and no bare number.
            Assert.False(KeyChord.TryParse("Mouse5", out _));
            Assert.False(KeyChord.TryParse("Mouse0 + Mouse1", out _));
        }

        [Fact]
        public void Another_mods_KeyCode_left_and_right_clicks_are_compared_and_its_other_buttons_stay_apart()
        {
            Assert.True(LegacyKeyNames.TryParse("Mouse0 + LeftControl", out var chord));
            Assert.Equal(LeftCtrl, chord);
            Assert.True(LegacyKeyNames.TryParse("Mouse1", out var right) && right == BareRight);
            Assert.True(LegacyKeyNames.TryFormat(LeftCtrlShift, keyCodeOnly: false, out var text, out _));
            Assert.Equal("Mouse0 + LeftControl + LeftShift", text);
            // Review: the middle, back and forward buttons stay not comparable, as before KEYS8.
            foreach (var code in new[] { "Mouse2", "Mouse3", "Mouse4", "Mouse5" })
                Assert.False(LegacyKeyNames.TryParse(code, out _));
            Assert.False(LegacyKeyNames.TryFormat(new KeyChord(UiKey.MouseBack), keyCodeOnly: true, out _, out _));
        }

        [Fact]
        public void A_bind_on_Mouse_Back_keeps_its_winner_next_to_an_older_mods_KeyCode_Mouse3()
        {
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Back", UnityEngine.KeyCode.Mouse3, "A mouse button.");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            // The other mod was installed first: had its Mouse3 become comparable, it would win.
            KeybindArbiter.InstallTimeOf = id => id == "plain.mod" ? Ledger(30) : Ledger(2);
            int fired = 0;
            Keybinds.Register(_owner, "prog.mod", "Back", File("prog"), new KeyChord(UiKey.MouseBack), "", () => fired++);

            var picture = KeybindArbiter.Current();
            var bind = picture.FindProgenitor("prog.mod", "Back")!;
            Assert.True(bind.Active);
            Assert.False(bind.InConflict);
            Assert.False(SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/Back").Comparable);
            _keys.Down(new KeyChord(UiKey.MouseBack));
            _keys.Frame();
            Assert.Equal(1, fired);
        }

        [Fact]
        public void Reset_is_off_for_another_mods_default_on_a_bare_click_so_Reset_all_skips_it()
        {
            // Another mod's KeyCode setting whose default is a bare Mouse0, changed to F7: the remap guard always
            // refuses a bare click, so Reset could never work and Reset all (which takes CanReset rows) skips it.
            KeybindsRowModel Row(string action, KeyChord current, KeyChord def)
            {
                var claim = new KeybindClaim(KeybindSource.Cfg, "plain.mod", action, action, current,
                    defaultChord: def, editable: true, keyCodeOnly: true);
                return new KeybindsRowModel("plain.mod", new KeybindVerdict(claim, true, null, "", null!, ""));
            }

            var click = Row("Hotkeys/Click", F7, BareLeft);
            Assert.True(click.Editable);
            Assert.True(click.Changed);
            Assert.False(click.CanReset);
            Assert.NotNull(KeybindRemapGuard.Check(KeybindSource.Cfg, "plain.mod", "Hotkeys/Click", F7, BareLeft, reset: true));
            Assert.True(Row("Hotkeys/Other", F7, F8).CanReset);
            Assert.True(Row("Hotkeys/Ctrl", F7, LeftCtrl).CanReset);
        }

        [Fact]
        public void The_scope_rules_need_a_modifier_and_keep_clicks_off_the_menus_and_the_map_region_unlock()
        {
            foreach (var scope in new[] { KeybindScope.Gameplay, KeybindScope.Map, KeybindScope.Photo })
            {
                Assert.Equal(MouseChords.BareClickReason, MouseChords.Refusal(BareLeft, scope));
                Assert.Equal(MouseChords.BareClickReason, MouseChords.Refusal(BareRight, scope));
                Assert.Null(MouseChords.Refusal(LeftCtrl, scope));
                Assert.Null(MouseChords.Refusal(LeftCtrlShift, scope));
            }

            // The Mods menu and the chat box take every left and right click.
            Assert.Equal(MouseChords.MenuClickReason, MouseChords.Refusal(LeftCtrlShift, KeybindScope.Menu));
            Assert.Equal(MouseChords.ChatClickReason, MouseChords.Refusal(new KeyChord(UiKey.MouseRight, KeyModifiers.Alt), KeybindScope.Chat));

            // The map: Ctrl+Shift+left is the game's admin teleport (refused by a server for anyone else) and is
            // allowed; Ctrl+Shift+right is its admin region unlock and is not, with or without Alt.
            var unlock = new KeyChord(UiKey.MouseRight, KeyModifiers.Ctrl | KeyModifiers.Shift);
            Assert.Equal(MouseChords.MapUnlockReason, MouseChords.Refusal(unlock, KeybindScope.Map));
            Assert.Equal(MouseChords.MapUnlockReason,
                MouseChords.Refusal(new KeyChord(UiKey.MouseRight, KeyModifiers.Ctrl | KeyModifiers.Shift | KeyModifiers.Alt), KeybindScope.Map));
            Assert.Null(MouseChords.Refusal(unlock, KeybindScope.Gameplay));
            Assert.Null(MouseChords.Refusal(new KeyChord(UiKey.MouseRight, KeyModifiers.Ctrl), KeybindScope.Map));

            // The other buttons and the keyboard are untouched.
            Assert.Null(MouseChords.Refusal(new KeyChord(UiKey.MouseMiddle), KeybindScope.Menu));
            Assert.Null(MouseChords.Refusal(F8, KeybindScope.Gameplay));
        }

        [Fact]
        public void A_bare_click_default_registers_unbound_and_a_bare_click_cfg_value_falls_back_to_the_default()
        {
            var bad = Keybinds.Register(_owner, "a.mod", "Attackish", File("a"), BareLeft, "", () => { });
            Assert.Equal(KeyChord.None, Keybinds.Snapshot().Single(b => b.Id == "Attackish").Chord);
            Assert.Contains(_log.Warnings, l => l.Contains("cannot be bound to MouseLeft") && l.Contains("registered unbound"));

            var file = File("b");
            file.Bind(Keybinds.Section, "Point", "MouseRight", "");
            Keybinds.Register(_owner, "b.mod", "Point", file, LeftCtrl, "", () => { });
            Assert.Equal(LeftCtrl, Keybinds.Snapshot().Single(b => b.Id == "Point").Chord);

            // A map key may sit on the game's own admin teleport chord.
            RegisterMap("c.mod", "Teleport", LeftCtrlShift);
            Assert.Equal(LeftCtrlShift, Keybinds.Snapshot().Single(b => b.Id == "Teleport").Chord);
        }

        [Fact]
        public void A_rebind_refuses_a_bare_click_and_a_menu_click_and_takes_a_modified_click()
        {
            var fileA = File("a");
            Keybinds.Register(_owner, "a.mod", "Ping", fileA, F8, "", () => { });

            Assert.False(Keybinds.TryRebind("a.mod", "Ping", BareRight, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal(MouseChords.BareClickReason, why);
            Assert.Equal(F8, Keybinds.Snapshot().Single().Chord);
            Assert.True(Keybinds.Rebind("a.mod", "Ping", LeftCtrl));
            Assert.Equal(LeftCtrl, Keybinds.Snapshot().Single().Chord);
            Assert.Equal("MouseLeft + Ctrl", fileA.Bind(Keybinds.Section, "Ping", "", "").Value);

            Keybinds.RegisterScoped(_owner, "m.mod", "Back", File("m"), F7, "", () => { }, null, null,
                allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Menu);
            Assert.False(Keybinds.TryRebind("m.mod", "Back", LeftCtrlShift, Keybinds.RebindCheck.Full, out why));
            Assert.Equal(MouseChords.MenuClickReason, why);
        }

        [Fact]
        public void The_capture_ignores_the_click_that_started_it_and_takes_the_next_modified_click()
        {
            KeyCaptureResult? got = null;
            // The click that opened the capture is still down when it begins.
            _keys.Down(LeftCtrlShift);
            Keybinds.BeginCapture(r => got = r);
            _keys.Next = LeftCtrlShift;
            _keys.Frame();
            Assert.Null(got);
            Assert.True(Keybinds.Capturing);
            Assert.False(Keybinds.SwallowsUiClicks);

            // Released, then a real Ctrl+Shift+click: that is the new key.
            _keys.Up(UiKey.MouseLeft);
            _keys.Frame();
            _keys.Down(LeftCtrlShift);
            _keys.Next = LeftCtrlShift;
            _keys.Frame();
            Assert.Equal(RebindOutcome.Bound, got!.Value.Outcome);
            Assert.Equal(LeftCtrlShift, got.Value.Chord);
            Assert.False(Keybinds.Capturing);

            // Its release does not also press the Progenitor button under the pointer.
            Assert.True(Keybinds.SwallowsUiClicks);
            int clicks = 0;
            ushort widget = UiSlotTable.Alloc(_owner);
            UiSlotTable.BindClick(widget, () => clicks++);
            UiSlotTable.BootstrapDispatch(widget, (byte)UiSlotKind.Click, 0f, false, null);
            Assert.Equal(0, clicks);
            _keys.Up(UiKey.MouseLeft);
            _keys.Frame();
            Assert.True(Keybinds.SwallowsUiClicks); // the widget's click lands on the release frame
            _keys.Frame();
            _keys.Frame();
            Assert.False(Keybinds.SwallowsUiClicks);
            UiSlotTable.BootstrapDispatch(widget, (byte)UiSlotKind.Click, 0f, false, null);
            Assert.Equal(1, clicks);
            UiSlotTable.Release(widget);
        }

        [Fact]
        public void A_long_hold_of_the_chosen_click_keeps_the_swallow_and_only_an_unread_button_times_out()
        {
            long now = 1000;
            Keybinds.Clock = () => now;
            KeyCaptureResult? got = null;
            Keybinds.BeginCapture(r => got = r);
            _keys.Frame();
            _keys.Down(LeftCtrlShift);
            _keys.Next = LeftCtrlShift;
            _keys.Frame();
            Assert.Equal(RebindOutcome.Bound, got!.Value.Outcome);

            // Held for 10 s, read every frame: still swallowing.
            for (int i = 0; i < 10; i++)
            {
                now += 1000;
                _keys.Frame();
            }

            Assert.True(Keybinds.SwallowsUiClicks);

            // Nothing reads the button any more (no pump): the cap ends it after its last held read.
            now += Keybinds.SwallowMaxMs + 1;
            Assert.False(Keybinds.SwallowsUiClicks);
        }

        [Fact]
        public void A_bare_click_on_Progenitor_UI_during_a_capture_is_silent_navigation()
        {
            bool overUi = true;
            Keybinds.PointerOverModUi = () => overUi;
            var results = new List<KeyCaptureResult>();
            Keybinds.BeginCapture(r => results.Add(r));
            _keys.Frame();
            _keys.Down(BareLeft);
            _keys.Next = BareLeft;
            _keys.Frame();
            Assert.Empty(results);
            Assert.True(Keybinds.Capturing);
            Assert.False(Keybinds.SwallowsUiClicks);
            _keys.Up(UiKey.MouseLeft);
            _keys.Frame();

            // Off Progenitor's UI the same click is refused with its reason.
            overUi = false;
            _keys.Down(BareLeft);
            _keys.Next = BareLeft;
            _keys.Frame();
            Assert.Equal(MouseChords.BareClickReason, Assert.Single(results).Reason);
            Keybinds.CancelCapture();
        }

        [Fact]
        public void A_bare_click_during_a_capture_is_refused_and_stays_the_panels_and_Escape_still_cancels()
        {
            var results = new List<KeyCaptureResult>();
            Keybinds.BeginCapture(r => results.Add(r));
            _keys.Frame(); // the capture sees both buttons up

            _keys.Down(BareLeft);
            _keys.Next = BareLeft;
            _keys.Frame();
            var refusal = Assert.Single(results);
            Assert.Equal(RebindOutcome.Refused, refusal.Outcome);
            Assert.True(refusal.StillCapturing);
            Assert.Equal(MouseChords.BareClickReason, refusal.Reason);
            Assert.True(Keybinds.Capturing);
            // A bare click is not swallowed: Cancel and Unbind still work with the mouse.
            Assert.False(Keybinds.SwallowsUiClicks);
            _keys.Up(UiKey.MouseLeft);

            _keys.Next = new KeyChord(UiKey.Escape);
            _keys.Frame();
            Assert.Equal(RebindOutcome.Cancelled, results.Last().Outcome);
            Assert.False(Keybinds.Capturing);
        }

        [Fact]
        public void The_settings_rebind_flow_takes_a_click_chord_with_the_same_rules()
        {
            Keybinds.Register(_owner, "a.mod", "Ping", File("a"), F8, "", () => { });
            Assert.True(Keybinds.BeginRebind("a.mod", "Ping"));
            _keys.Frame();
            _keys.Down(BareRight);
            _keys.Next = BareRight;
            _keys.Frame();
            Assert.Equal("Ping", Keybinds.RebindingId); // refused, still waiting
            _keys.Up(UiKey.MouseRight);
            _keys.Frame();
            var right = new KeyChord(UiKey.MouseRight, KeyModifiers.Alt);
            _keys.Down(right);
            _keys.Next = right;
            _keys.Frame();
            Assert.Null(Keybinds.RebindingId);
            Assert.Equal(right, Keybinds.Snapshot().Single().Chord);
        }

        [Fact]
        public void A_click_chord_never_fires_over_a_Progenitor_window_and_a_gameplay_one_not_in_the_game_menu()
        {
            int gameplay = 0, exempt = 0, map = 0;
            bool overUi = false, menu = false;
            Keybinds.PointerOverModUi = () => overUi;
            Keybinds.GameMenuOpen = () => menu;
            Keybinds.MapScopeOpen = () => menu;
            Keybinds.Register(_owner, "a.mod", "Act", File("a"), LeftCtrl, "", () => gameplay++);
            Keybinds.Register(_owner, "b.mod", "Exempt", File("b"), new KeyChord(UiKey.MouseLeft, KeyModifiers.Alt), "",
                () => exempt++, allowWhileCaptured: true);
            RegisterMap("c.mod", "Teleport", LeftCtrlShift, () => map++);

            void Click(KeyChord c)
            {
                _keys.Down(c);
                _keys.Frame();
                _keys.Up(c.Key);
                _keys.Frame();
            }

            Click(LeftCtrl);
            Click(new KeyChord(UiKey.MouseLeft, KeyModifiers.Alt));
            Assert.Equal(1, gameplay);
            Assert.Equal(1, exempt);

            // Over a Progenitor window the click is the window's, whatever allowWhileCaptured says.
            overUi = true;
            Click(LeftCtrl);
            Click(new KeyChord(UiKey.MouseLeft, KeyModifiers.Alt));
            Assert.Equal(1, gameplay);
            Assert.Equal(1, exempt);
            Assert.Equal(Keybinds.ClickOverModUiReason, Keybinds.SimulatePress("b.mod", "Exempt").Single().Reason);
            // A keyboard bind is not affected by where the pointer is.
            Assert.Null(Keybinds.ClickChordSuppressedReason(KeybindScope.Gameplay, F8));
            overUi = false;

            // Review: a passive Progenitor overlay (no input capture) under the pointer is found by the raycast on
            // the press, and only on the press.
            int raycasts = 0;
            bool hit = true;
            Keybinds.PointerHitsModUi = () => { raycasts++; return hit; };
            _keys.Frame();
            _keys.Frame();
            Assert.Equal(0, raycasts);
            Click(LeftCtrl);
            Assert.Equal(1, gameplay);
            Assert.Equal(1, raycasts);
            Assert.Equal(Keybinds.ClickOverModUiReason, Keybinds.SimulatePress("a.mod", "Act").Single().Reason);
            hit = false;

            // With the game's full-screen menu (the map) open, a gameplay click is the menu's; a map key fires.
            menu = true;
            Click(LeftCtrl);
            Assert.Equal(1, gameplay);
            Assert.Equal(Keybinds.ClickInGameMenuReason, Keybinds.SimulatePress("a.mod", "Act").Single().Reason);
            Click(LeftCtrlShift);
            Assert.Equal(1, map);
            Assert.True(Keybinds.SimulatePress(LeftCtrlShift).Single().Fired);
            Assert.Equal(2, map);
        }

        [Fact]
        public void Two_mods_on_one_click_chord_follow_the_first_installed_rule_and_a_remap_onto_it_is_refused()
        {
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            RegisterMap("second.mod", "Teleport", LeftCtrlShift);
            RegisterMap("first.mod", "Teleport", LeftCtrlShift);
            var picture = KeybindArbiter.Current();
            Assert.True(picture.FindProgenitor("first.mod", "Teleport")!.Active);
            var loser = picture.FindProgenitor("second.mod", "Teleport")!;
            Assert.False(loser.Active);
            Assert.StartsWith("Not active: Left Mouse + Ctrl + Shift is used by first.mod", loser.Reason);

            // A remap never creates a new clash on a click chord either.
            RegisterMap("third.mod", "Mark", new KeyChord(UiKey.MouseLeft, KeyModifiers.Alt));
            Assert.False(Keybinds.TryRebind("third.mod", "Mark", LeftCtrlShift, Keybinds.RebindCheck.Full, out var why));
            Assert.Contains("is used by", why);
            // A gameplay key on the same chord is another scope and does not meet the map keys.
            Keybinds.Register(_owner, "fourth.mod", "Act", File("fourth"), LeftCtrlShift, "", () => { });
            Assert.True(KeybindArbiter.Current().FindProgenitor("fourth.mod", "Act")!.Active);
        }
    }
}
