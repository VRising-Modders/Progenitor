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
            Assert.Equal(4, keys.Length);
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
        public void A_window_scope_meets_gameplay_menu_and_map_keys()
        {
            Assert.True(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Window), Mod(KeybindScope.Gameplay)));
            Assert.True(KeybindArbiter.ScopesMeet(Mod(KeybindScope.Menu), Mod(KeybindScope.Window)));
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
