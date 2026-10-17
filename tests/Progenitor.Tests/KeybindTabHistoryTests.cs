using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests
{
    /// <summary>
    /// HUB2: Navigate back and forward (the mouse's back and forward buttons) and their Alt+Left / Alt+Right keys are
    /// Core keybinds of the Window scope: one action each, acting in menus (a window with tabs under the pointer first,
    /// then the workspace) and, outside menus, on a window with tabs only while it holds the input. They clash as menu
    /// keys do, so they never meet the game's gameplay actions (no key-conflict dialog for Auto Run or Interrupt Ability
    /// Cast), and outside menus they give way to a mod's gameplay key. On <see cref="KeybindTabTests"/>' fakes. Nothing
    /// here has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static KeybindClaim InterruptCast() =>
            new(KeybindSource.Game, KeybindArbiter.GameOwnerId, "InterruptCast", "Interrupt Ability Cast", MouseBack,
                chordText: "Mouse Button 5", category: GameKeybindCatalog.General);

        [Fact]
        public void The_navigation_keys_are_four_window_keys_and_the_separate_window_mouse_keys_are_gone()
        {
            var keys = NavKeys();
            Assert.Equal(4, keys.Length);
            var picture = KeybindArbiter.Current();
            foreach (var id in new[] { ModsMenu.NavigateBackId, ModsMenu.NavigateForwardId, ModsMenu.NavigateBackKeysId, ModsMenu.NavigateForwardKeysId })
            {
                var v = picture.FindProgenitor(SettingsPanel.PluginId, id)!;
                Assert.Equal(KeybindScope.Window, v.Claim.Scope);
                Assert.True(v.Active);
            }

            Assert.Equal(MouseBack, picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackId)!.Claim.Chord);
            Assert.Equal(new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt), picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId)!.Claim.Chord);
            Assert.DoesNotContain(Keybinds.Snapshot(), b => ModsMenu.RetiredNavigationKeyIds.Contains(b.Id));
        }

        [Fact]
        public void Against_the_games_gameplay_actions_on_the_mouse_buttons_there_is_no_conflict_and_no_dialog()
        {
            // The live case (2026-10-16): Interrupt Ability Cast on Mouse Button 5, Auto Run on Mouse Button 4.
            UseGame(InterruptCast(), AutoRun());
            KeybindConflictPrompt.ScreenReady = () => true;
            NavKeys();
            _keys.Frame();
            _keys.Frame();

            var picture = KeybindArbiter.Current();
            var back = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackId)!;
            var forward = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateForwardId)!;
            Assert.True(back.Active);
            Assert.False(back.InConflict);
            Assert.True(forward.Active);
            Assert.False(forward.InConflict);
            Assert.Equal("", back.Reason);
            // The game's rows do not list them either, and nothing counts as a conflict.
            Assert.Empty(picture.Find("game:game/AutoMove")!.Others);
            Assert.Equal(0, picture.ConflictCount);
            Assert.Empty(KeybindConflictPrompt.Find(picture, null));
            Assert.Null(UiDialog.Current);
            Assert.Empty((System.Collections.Generic.List<object?>)KeybindConflictPrompt.Describe()["waiting"]!);
            Assert.False(KeybindArbiter.ScopesMeet(AutoRun(), KeybindScope.Window));

            OpenKeybindsTab();
            var row = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == SettingsPanel.PluginId && r.Claim.ActionId == ModsMenu.NavigateForwardId);
            Assert.False(row.Conflict);
            Assert.True(row.Active);
            Assert.Equal("window", row.Readback(true)["scope"]);
            var described = KeybindsTabLayout.Describe(row, "selected");
            Assert.Equal("", described.Warning);
            Assert.Contains(KeybindsTabLayout.WindowScopeNote, described.Info);
        }

        [Fact]
        public void Outside_menus_a_navigation_key_acts_on_a_window_only_while_it_holds_the_input()
        {
            UseGame(AutoRun());
            NavKeys();
            var window = UiApi.Panel("Hub", _owner, "example.hub");
            try
            {
                window.GameWindow();
                var tabs = window.Tabs("hub", new[] { "A", "B", "C" }, new UiTabsOptions());
                window.Show();
                tabs.Selected = 1;
                Assert.True(tabs.Back());
                UiTabHistoryRoute.UnderPointer = p => ReferenceEquals(p, window);
                Assert.False(ModsMenu.MenuContextOpen);

                // Hovered, the game's input not blocked: Auto Run keeps the button, the window does not move.
                Keybinds.WindowHoldsInput = () => false;
                _keys.Down(MouseForward);
                _keys.Frame();
                _keys.Up(UiKey.MouseForward);
                Assert.Equal(0, tabs.Selected);
                Assert.Equal(Keybinds.WindowClosedReason, Keybinds.SimulatePress(SettingsPanel.PluginId, ModsMenu.NavigateForwardId).Single().Reason);

                // The window holds the input (the full mask blocks the game's actions, Auto Run included): Navigate forward acts.
                Keybinds.WindowHoldsInput = () => true;
                _keys.Frame();
                _keys.Down(MouseForward);
                _keys.Frame();
                _keys.Up(UiKey.MouseForward);
                Assert.Equal(1, tabs.Selected);

                // Alt+Left is the same action.
                _keys.Frame();
                _keys.Down(new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt));
                _keys.Frame();
                _keys.Up(UiKey.LeftArrow);
                Assert.Equal(0, tabs.Selected);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        [Fact]
        public void A_mods_gameplay_key_on_the_mouse_back_button_keeps_it_with_no_prompt_and_a_quiet_note()
        {
            KeybindConflictPrompt.ScreenReady = () => true;
            NavKeys();
            int fired = 0;
            Keybinds.Register(_owner, "a.mod", "Dodge", File("a"), MouseBack, "Dodge.", () => fired++, allowWhileCaptured: true);
            _keys.Frame();
            var picture = KeybindArbiter.Current();
            var dodge = picture.FindProgenitor("a.mod", "Dodge")!;
            var back = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackId)!;
            // Not a clash: both active, no prompt, nothing counted.
            Assert.True(dodge.Active);
            Assert.False(dodge.InConflict);
            Assert.True(back.Active);
            Assert.False(back.InConflict);
            Assert.Equal(0, picture.ConflictCount);
            Assert.Null(UiDialog.Current);
            Assert.Equal("a.mod", Assert.Single(KeybindArbiter.SharersOf(SettingsPanel.PluginId, ModsMenu.NavigateBackId)).OwnerId);

            OpenKeybindsTab();
            var row = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == SettingsPanel.PluginId && r.Claim.ActionId == ModsMenu.NavigateBackId);
            var described = KeybindsTabLayout.Describe(row, "selected");
            Assert.Equal("", described.Warning);
            Assert.Contains("a.mod's Dodge also uses Mouse Back; whenever it can act, it does and this key doesn't.", described.Info);
            ModsMenu.Close();

            var window = HubWindow(out var tabs);
            try
            {
                // The window holds the input and Dodge may run under capture: the press is Dodge's alone.
                Keybinds.WindowHoldsInput = () => true;
                PressMouseBack();
                Assert.Equal(1, fired);
                Assert.Equal(1, tabs.Selected);
                Assert.StartsWith("not-active: suppressed: a.mod's Dodge has this key and acts",
                    Keybinds.SimulatePress(SettingsPanel.PluginId, ModsMenu.NavigateBackId).Single().Reason);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        /// <summary>A shown game window with tabs A and B, B selected, holding the input, under the pointer.</summary>
        private IUiPanel HubWindow(out IUiTabs tabs)
        {
            var window = UiApi.Panel("Hub", _owner, "example.hub");
            window.GameWindow();
            window.CaptureMode = UiCaptureMode.WhileShown;
            tabs = window.Tabs("hub", new[] { "A", "B" }, new UiTabsOptions());
            window.Show();
            tabs.Selected = 1;
            UiTabHistoryRoute.UnderPointer = p => ReferenceEquals(p, window);
            return window;
        }

        private void PressMouseBack()
        {
            _keys.Frame();
            _keys.Down(MouseBack);
            _keys.Frame();
            _keys.Up(UiKey.MouseBack);
            _keys.Frame();
        }

        [Fact]
        public void A_mod_key_that_cannot_act_under_capture_leaves_the_press_to_the_window_key()
        {
            NavKeys();
            int fired = 0;
            // The default: a gameplay key does not run while a panel holds the input.
            Keybinds.Register(_owner, "a.mod", "Dodge", File("a"), MouseBack, "Dodge.", () => fired++);
            var window = HubWindow(out var tabs);
            try
            {
                Keybinds.WindowHoldsInput = () => true;
                Assert.True(UiInputCapture.IsFullyBlocking);
                PressMouseBack();
                // Exactly one of the two acts: the window steps, Dodge (suppressed by the capture) does not.
                Assert.Equal(0, fired);
                Assert.Equal(0, tabs.Selected);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        [Fact]
        public void A_mod_key_that_lost_to_the_game_leaves_the_press_to_the_window_key()
        {
            UseGame(InterruptCast());
            NavKeys();
            int fired = 0;
            Keybinds.Register(_owner, "a.mod", "Dodge", File("a"), MouseBack, "Dodge.", () => fired++, allowWhileCaptured: true);
            Assert.False(KeybindArbiter.Current().FindProgenitor("a.mod", "Dodge")!.Active);
            Assert.Empty(KeybindArbiter.SharersOf(SettingsPanel.PluginId, ModsMenu.NavigateBackId));
            var window = HubWindow(out var tabs);
            try
            {
                Keybinds.WindowHoldsInput = () => true;
                PressMouseBack();
                Assert.Equal(0, fired);
                Assert.Equal(0, tabs.Selected);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        [Fact]
        public void A_mods_menu_or_map_key_on_a_navigation_chord_keeps_it_with_no_dialog_and_wins_the_press()
        {
            KeybindConflictPrompt.ScreenReady = () => true;
            NavKeys();
            int menuFired = 0, mapFired = 0;
            Keybinds.RegisterScoped(_owner, "a.mod", "MenuBack", File("a"), MouseBack, "", () => menuFired++, null, null,
                allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Menu);
            Keybinds.RegisterScoped(_owner, "b.mod", "MapLeft", File("b"), new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt), "", () => mapFired++,
                null, null, allowWhileCaptured: true, allowWhenChatUnknown: true, KeybindScope.Map);
            _keys.Frame();
            var picture = KeybindArbiter.Current();
            Assert.True(picture.FindProgenitor("a.mod", "MenuBack")!.Active);
            Assert.False(picture.FindProgenitor("a.mod", "MenuBack")!.InConflict);
            Assert.True(picture.FindProgenitor("b.mod", "MapLeft")!.Active);
            Assert.True(picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId)!.Active);
            Assert.Equal(0, picture.ConflictCount);
            Assert.Empty(KeybindConflictPrompt.Find(picture, null));
            Assert.Null(UiDialog.Current);

            // In a menu (the map is a tab of the game's full-screen menu) each press is the mod's alone.
            Keybinds.MenuScopeOpen = () => true;
            Keybinds.MapScopeOpen = () => true;
            RegisterMod("c.mod", "Gamma");
            using var menu = ModsMenu.Register(_owner, "c.mod", "Gamma", "A mod.");
            ModsMenu.Open();
            ModsMenu.OpenMod("c.mod");
            int index = (int)ModsMenu.NavState()["index"]!;
            PressMouseBack();
            Assert.Equal(1, menuFired);
            Assert.Equal(index, ModsMenu.NavState()["index"]);
            _keys.Down(new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt));
            _keys.Frame();
            _keys.Up(UiKey.LeftArrow);
            Assert.Equal(1, mapFired);
            Assert.Equal(index, ModsMenu.NavState()["index"]);
        }

        [Fact]
        public void In_the_games_full_screen_menu_a_mods_gameplay_key_and_Navigate_back_never_both_fire()
        {
            NavKeys();
            int fired = 0;
            Keybinds.Register(_owner, "a.mod", "Dodge", File("a"), MouseBack, "Dodge.", () => fired++);
            // The game's full-screen menu (inventory, Spellbook): a menu the history covers, and no Progenitor capture.
            Keybinds.MenuScopeOpen = () => true;
            Assert.False(UiInputCapture.IsFullyBlocking);
            PressMouseBack();
            Assert.Equal(1, fired);
            Assert.StartsWith("not-active: suppressed: a.mod's Dodge has this key and acts",
                Keybinds.SimulatePress(SettingsPanel.PluginId, ModsMenu.NavigateBackId).Single().Reason);

            // With no menu and no window holding the input Navigate does not act at all.
            Keybinds.MenuScopeOpen = () => false;
            Keybinds.WindowHoldsInput = () => false;
            Assert.Equal(Keybinds.WindowClosedReason, Keybinds.SimulatePress(SettingsPanel.PluginId, ModsMenu.NavigateBackId).Single().Reason);
        }

        [Fact]
        public void A_window_scope_meets_no_other_mod_scope_and_meets_the_games_keys_as_a_menu_key()
        {
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Gameplay)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Menu), Mod(KeybindScope.Window)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Map)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Photo)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Chat)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Menu), Mod(KeybindScope.Gameplay)));
            Assert.False(KeybindArbiter.ScopesMeet(AutoRun(), KeybindScope.Window));
            var back = GameKeybindCatalog.Claim(GameKeybindCatalog.OtherKeys.Single(e => e.Action == "Back"), "Back", 0, null, "<Keyboard>/f6", "");
            Assert.True(KeybindArbiter.ScopesMeet(back, KeybindScope.Window));
            Assert.Equal(MouseChords.MenuClickReason, MouseChords.Refusal(new KeyChord(UiKey.MouseLeft, KeyModifiers.Ctrl), KeybindScope.Window));
        }

        private static KeybindClaim Mod(KeybindScope scope) =>
            new(KeybindSource.Progenitor, "x", "y", "Y", new KeyChord(UiKey.F6), scope: scope);

        [Fact]
        public void One_press_of_the_mouse_back_button_steps_a_window_once_in_play_and_over_the_menu()
        {
            NavKeys();
            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            var window = UiApi.Panel("Hub", _owner, "example.hub");
            try
            {
                window.GameWindow();
                var tabs = window.Tabs("hub", new[] { "A", "B", "C" }, new UiTabsOptions());
                window.Show();
                tabs.Selected = 1;
                tabs.Selected = 2;
                UiTabHistoryRoute.UnderPointer = p => ReferenceEquals(p, window);
                Keybinds.WindowHoldsInput = () => true;

                // In play (no menu open), the window holding the input: Navigate back steps the window once.
                Assert.False(ModsMenu.MenuContextOpen);
                _keys.Down(MouseBack);
                _keys.Frame();
                _keys.Up(UiKey.MouseBack);
                Assert.Equal(1, tabs.Selected);

                // The Mods menu open, the pointer over the window: the same key steps the window, not the workspace.
                ModsMenu.Open();
                ModsMenu.OpenMod("a.mod");
                int index = (int)ModsMenu.NavState()["index"]!;
                tabs.Selected = 2;
                _keys.Frame();
                _keys.Down(MouseBack);
                _keys.Frame();
                _keys.Up(UiKey.MouseBack);
                Assert.Equal(1, tabs.Selected);
                Assert.Equal(index, ModsMenu.NavState()["index"]);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        [Fact]
        public void A_window_at_the_end_of_its_tab_history_leaves_back_and_forward_to_the_workspace()
        {
            NavKeys();
            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            ModsMenu.Open();
            ModsMenu.OpenMod("a.mod");
            Assert.Equal(1, ModsMenu.NavState()["index"]);

            var window = UiApi.Panel("Hub", _owner, "example.hub");
            try
            {
                window.GameWindow();
                var tabs = window.Tabs("hub", new[] { "A", "B" }, new UiTabsOptions());
                window.Show();
                UiTabHistoryRoute.UnderPointer = p => ReferenceEquals(p, window);

                // The window's history has nothing before its first tab: the workspace goes back.
                Assert.True(ModsMenu.NavigateBack());
                Assert.Equal(0, ModsMenu.NavState()["index"]);

                // After a tab change the window takes Back, and the workspace stays where it is.
                tabs.Selected = 1;
                UiTabHistoryRoute.ResetCache();
                Assert.True(ModsMenu.NavigateBack());
                Assert.Equal(0, tabs.Selected);
                Assert.Equal(0, ModsMenu.NavState()["index"]);

                // A window that holds the input while the pointer is over the workspace: the workspace keeps the key.
                window.CaptureMode = UiCaptureMode.WhileShown;
                tabs.Selected = 1;
                UiTabHistoryRoute.UnderPointer = p => ModsMenu.IsWorkspacePanel(p);
                Assert.Null(UiTabHistoryRoute.Target());
                Assert.True(ModsMenu.NavigateForward());
                Assert.Equal(1, ModsMenu.NavState()["index"]);
                Assert.Equal(1, tabs.Selected);

                // With the pointer over no panel, the window holding the input takes it.
                UiTabHistoryRoute.UnderPointer = _ => false;
                Assert.Same(tabs, UiTabHistoryRoute.Target());
                Assert.True(ModsMenu.NavigateBack());
                Assert.Equal(0, tabs.Selected);
                Assert.Equal(1, ModsMenu.NavState()["index"]);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        // ---- migration of PR #42's separate window mouse keys ----------------------------------------------

        [Fact]
        public void The_retired_window_mouse_keys_are_dropped_from_the_saved_config_whatever_they_held()
        {
            // A player who saw the old dialog: "Leave unbound" wrote None for both; one was also rebound earlier.
            // (The test ConfigFile reads one "[Section] Key = value" row per line; the rows load as orphans, as BepInEx's do.)
            string path = Path.Combine(_dir, "core-old.cfg");
            System.IO.File.WriteAllText(path,
                "[Keybinds] WindowTabBackMouse = None\n[Keybinds] WindowTabForwardMouse = F7\n[Keybinds] NavigateBack = MouseBack\n");
            var file = new ConfigFile(path, saveOnInit: false);
            file.Reload();
            var keys = ModsMenu.RegisterNavigationKeys(_navOwner, file);
            Assert.Equal(4, keys.Length);

            string saved = System.IO.File.ReadAllText(path);
            Assert.DoesNotContain("WindowTabBackMouse", saved);
            Assert.DoesNotContain("WindowTabForwardMouse", saved);
            // Navigate back keeps the player's value and is the one mouse-back row.
            Assert.Contains("[Keybinds] NavigateBack = MouseBack", saved);
            Assert.Contains(_log.Infos, l => l.Contains("dropped 2 retired navigation key row(s)"));

            // No orphan rows survive a later save, and a second start finds nothing to drop.
            file.Save();
            Assert.DoesNotContain("WindowTab", System.IO.File.ReadAllText(path));
            var again = new ConfigFile(path, saveOnInit: false);
            again.Reload();
            Assert.Equal(0, ModsMenu.DropRetiredNavigationKeys(again));

            OpenKeybindsTab();
            Assert.DoesNotContain(Pane().Model!.ModRows(), r => r.Claim.ActionId.StartsWith("WindowTab", System.StringComparison.Ordinal));
        }

        [Fact]
        public void A_retired_key_still_live_from_an_earlier_Core_generation_is_dropped_too()
        {
            var file = File("core-live");
            file.Bind(Keybinds.Section, "WindowTabBackMouse", "None");
            Assert.Equal(1, ModsMenu.DropRetiredNavigationKeys(file));
            Assert.False(file.ContainsKey(new ConfigDefinition(Keybinds.Section, "WindowTabBackMouse")));
            Assert.DoesNotContain("WindowTabBackMouse", System.IO.File.ReadAllText(file.ConfigFilePath));

            // BepInEx's Remove throwing: the row is dropped from Entries anyway before the save.
            var stubborn = File("core-stubborn");
            stubborn.Bind(Keybinds.Section, "WindowTabForwardMouse", "F7");
            stubborn.RemoveThrows = true;
            Assert.Equal(1, ModsMenu.DropRetiredNavigationKeys(stubborn));
            Assert.False(stubborn.ContainsKey(new ConfigDefinition(Keybinds.Section, "WindowTabForwardMouse")));
            Assert.DoesNotContain("WindowTabForwardMouse", System.IO.File.ReadAllText(stubborn.ConfigFilePath));
        }
    }
}
