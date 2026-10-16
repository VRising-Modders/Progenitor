using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests
{
    /// <summary>
    /// HUB1: the tab-history keys (Alt+Left, Alt+Right) are Core keybinds of their own Window scope, checked against
    /// gameplay keys, beside the workspace's Menu-scope Navigate back and forward; and a window's tab history only takes a
    /// back or forward it can act on, so the Mods workspace's history keeps the rest. On <see cref="KeybindTabTests"/>'
    /// fakes. Nothing here has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        [Fact]
        public void The_tab_history_keys_are_window_keybinds_on_Alt_and_the_arrows_checked_against_gameplay_keys()
        {
            var keys = NavKeys();
            Assert.Equal(6, keys.Length);
            var picture = KeybindArbiter.Current();
            var back = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId)!;
            var forward = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateForwardKeysId)!;
            Assert.Equal(new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt), back.Claim.Chord);
            Assert.Equal(new KeyChord(UiKey.RightArrow, KeyModifiers.Alt), forward.Claim.Chord);
            Assert.Equal(KeybindScope.Window, back.Claim.Scope);
            Assert.True(back.Active);
            // The workspace's own keys stay menu keys.
            Assert.Equal(KeybindScope.Menu, picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackId)!.Claim.Scope);

            // A window key meets a gameplay mod key on its chord (it fires in play), unlike a menu key.
            Keybinds.Register(_owner, "a.mod", "Dodge", File("a"), new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt), "", () => { });
            var dodge = KeybindArbiter.Current().FindProgenitor("a.mod", "Dodge")!;
            Assert.Contains(dodge.Others, o => o.OwnerId == SettingsPanel.PluginId && o.ActionId == ModsMenu.NavigateBackKeysId);

            Assert.True(Keybinds.TryRebind(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId, new KeyChord(UiKey.F6), Keybinds.RebindCheck.Full, out _));
            Assert.Contains(Keybinds.Snapshot(), b => b.Id == ModsMenu.NavigateBackKeysId && b.Chord == new KeyChord(UiKey.F6));
        }

        [Fact]
        public void The_window_mouse_keys_share_the_menu_keys_buttons_without_a_clash_and_meet_the_games_mouse_keys()
        {
            UseGame(AutoRun());
            var keys = NavKeys();
            Assert.Equal(6, keys.Length);
            var picture = KeybindArbiter.Current();
            var back = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.WindowTabBackMouseId)!;
            var forward = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.WindowTabForwardMouseId)!;
            Assert.Equal(MouseBack, back.Claim.Chord);
            Assert.Equal(MouseForward, forward.Claim.Chord);
            Assert.Equal(KeybindScope.Window, back.Claim.Scope);
            // The workspace's Navigate back on the same button: both active, no clash between them.
            Assert.True(back.Active);
            Assert.True(picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackId)!.Active);
            Assert.DoesNotContain(back.Others, o => o.ActionId == ModsMenu.NavigateBackId);
            // The game's Auto Run holds the forward button in play: the window mouse key meets it and the game wins (reported).
            Assert.False(forward.Active);
            Assert.Equal(KeybindArbiter.RuleGame, forward.Rule);
            // The workspace's Navigate forward is a menu key and does not meet the gameplay Auto Run.
            Assert.True(picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateForwardId)!.Active);
        }

        [Fact]
        public void A_window_mouse_key_that_lost_to_the_game_fires_only_while_its_window_holds_the_input()
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
                var forward = KeybindArbiter.Current().FindProgenitor(SettingsPanel.PluginId, ModsMenu.WindowTabForwardMouseId)!;
                // The Keybinds tab keeps reporting the clash with Auto Run.
                Assert.False(forward.Active);
                Assert.True(KeybindArbiter.LostToGame(SettingsPanel.PluginId, ModsMenu.WindowTabForwardMouseId));
                Assert.Contains("except while the window holds the input", KeybindsTabLayout.WindowScopeNote);

                // Hovered, the game's input not blocked: Auto Run keeps the button, the window does not move.
                Keybinds.WindowHoldsInput = () => false;
                _keys.Down(MouseForward);
                _keys.Frame();
                _keys.Up(UiKey.MouseForward);
                Assert.Equal(0, tabs.Selected);

                // The window holds the input (the full mask blocks the game's button actions, Auto Run included):
                // the window key acts.
                Keybinds.WindowHoldsInput = () => true;
                _keys.Frame();
                _keys.Down(MouseForward);
                _keys.Frame();
                _keys.Up(UiKey.MouseForward);
                Assert.Equal(1, tabs.Selected);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        [Fact]
        public void The_exception_is_only_for_a_window_key_beaten_by_the_game_not_by_a_mod()
        {
            NavKeys();
            // An earlier-ranked rival on Alt+Left would be a mod's, never the game's: no exception for it.
            Assert.False(KeybindArbiter.LostToGame(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId));
            Assert.False(KeybindArbiter.LostToGame(SettingsPanel.PluginId, ModsMenu.NavigateBackId));
            // With no window shown nothing holds the input for a window key.
            Assert.False(Keybinds.WindowHoldsInput());
        }

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

                // In play (no menu open): the window mouse key steps the window once.
                Assert.False(ModsMenu.MenuContextOpen);
                _keys.Down(MouseBack);
                _keys.Frame();
                _keys.Up(UiKey.MouseBack);
                Assert.Equal(1, tabs.Selected);

                // The Mods menu open, the pointer over the window: the menu key steps the window, the window mouse key
                // stands aside, so the same press moves one tab, not two, and the workspace does not move.
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
                Assert.False(ModsMenu.NavigateWindowMouse(back: true));
                Assert.Equal(1, tabs.Selected);
            }
            finally
            {
                window.Dispose();
                UiTabHistoryRoute.ResetForTests();
            }
        }

        [Fact]
        public void A_window_scope_meets_gameplay_and_map_keys_but_not_menu_keys()
        {
            Assert.True(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Gameplay)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Menu), Mod(KeybindScope.Window)));
            Assert.True(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Map)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Photo)));
            Assert.False(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Menu), Mod(KeybindScope.Gameplay)));
        }

        private static KeybindClaim Mod(KeybindScope scope) =>
            new(KeybindSource.Progenitor, "x", "y", "Y", new KeyChord(UiKey.F6), scope: scope);

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
    }
}
