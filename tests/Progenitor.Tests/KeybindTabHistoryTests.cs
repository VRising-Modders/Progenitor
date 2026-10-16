using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// HUB1: the keyboard's back and forward (Alt+Left, Alt+Right) are Core menu-scope keybinds beside the mouse's, in the
    /// Keybinds tab and remappable, on <see cref="KeybindTabTests"/>' fakes. Nothing here has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        [Fact]
        public void The_keyboard_back_and_forward_are_menu_keybinds_on_Alt_and_the_arrows()
        {
            var keys = NavKeys();
            Assert.Equal(4, keys.Length);
            var picture = KeybindArbiter.Current();
            var back = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId)!;
            var forward = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateForwardKeysId)!;
            Assert.Equal(new KeyChord(UiKey.LeftArrow, KeyModifiers.Alt), back.Claim.Chord);
            Assert.Equal(new KeyChord(UiKey.RightArrow, KeyModifiers.Alt), forward.Claim.Chord);
            Assert.Equal(KeybindScope.Menu, back.Claim.Scope);
            Assert.True(back.Active);
            Assert.True(Keybinds.TryRebind(SettingsPanel.PluginId, ModsMenu.NavigateBackKeysId, new KeyChord(UiKey.F6), Keybinds.RebindCheck.Full, out _));
            Assert.Contains(Keybinds.Snapshot(), b => b.Id == ModsMenu.NavigateBackKeysId && b.Chord == new KeyChord(UiKey.F6));
        }
    }
}
