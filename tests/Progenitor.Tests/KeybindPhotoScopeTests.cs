using System;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// Photo-scope keybinds: photo mode's fly-camera, HUD, pause and zoom keys go through the keybind
    /// API in their own scope, which fires only while photo mode is active and never meets a game key
    /// or another scope's key. Game-free, on <see cref="KeybindTabTests"/>' fakes. Not run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord KeyW = new(UiKey.W);

        [Fact]
        public void A_photo_key_on_a_game_movement_key_stays_active_and_meets_only_photo_keys()
        {
            UseGame(Game("MoveUp", KeyW));
            Keybinds.RegisterScoped(_owner, "core", "PhotoModeForward", File("core"), KeyW, "", () => { }, null, () => { },
                allowWhileCaptured: true, allowWhenChatUnknown: false, KeybindScope.Photo);
            Keybinds.Register(_owner, "a.mod", "Wave", File("a"), KeyW, "", () => { });

            var picture = KeybindArbiter.Current();
            var photo = picture.FindProgenitor("core", "PhotoModeForward")!;
            Assert.True(photo.Active);
            Assert.False(photo.InConflict);
            Assert.Equal(KeybindScope.Photo, photo.Claim.Scope);
            // The gameplay mod key on W still loses to the game's movement key, and never to the photo key.
            var wave = picture.FindProgenitor("a.mod", "Wave")!;
            Assert.False(wave.Active);
            Assert.DoesNotContain(wave.Others, o => o.ActionId == "PhotoModeForward");
        }

        [Fact]
        public void Photo_scope_rules_never_let_a_game_key_stop_or_meet_a_photo_key()
        {
            var game = Game("MoveUp", KeyW);
            Assert.False(KeybindArbiter.GameWins(game, KeybindScope.Photo));
            Assert.False(KeybindArbiter.ScopesMeet(game, KeybindScope.Photo));
            Assert.True(KeybindArbiter.ScopesMeet(game, KeybindScope.Gameplay));
            var a = new KeybindClaim(KeybindSource.Progenitor, "a", "A", "A", KeyW, scope: KeybindScope.Photo);
            var b = new KeybindClaim(KeybindSource.Progenitor, "b", "B", "B", KeyW, scope: KeybindScope.Photo);
            var c = new KeybindClaim(KeybindSource.Progenitor, "c", "C", "C", KeyW);
            Assert.True(KeybindArbiter.ScopesMeet(a, b));
            Assert.False(KeybindArbiter.ScopesMeet(a, c));
        }

        [Fact]
        public void A_photo_key_fires_and_releases_only_while_photo_mode_is_active()
        {
            int pressed = 0, released = 0;
            bool active = false;
            Keybinds.PhotoScopeOpen = () => active;
            Keybinds.RegisterScoped(_owner, "core", "PhotoModeForward", File("core"), KeyW, "", () => pressed++, null, () => released++,
                allowWhileCaptured: true, allowWhenChatUnknown: false, KeybindScope.Photo);

            _keys.Down(KeyW);
            _keys.Frame();
            _keys.Up(UiKey.W);
            _keys.Frame();
            Assert.Equal(0, pressed);
            Assert.Equal(Keybinds.PhotoInactiveReason, Keybinds.SimulatePress("core", "PhotoModeForward").Single().Reason);

            active = true;
            _keys.Down(KeyW);
            _keys.Frame();
            Assert.Equal(1, pressed);
            Assert.Equal(0, released);

            // Leaving photo mode mid-hold releases the key, so a held flight key never sticks.
            active = false;
            _keys.Frame();
            Assert.Equal(1, released);
        }

        [Fact]
        public void The_keybinds_tab_labels_a_photo_key()
        {
            Keybinds.RegisterScoped(_owner, "a.mod", "PhotoModeForward", File("a"), KeyW, "Fly.", () => { }, null, null,
                allowWhileCaptured: true, allowWhenChatUnknown: false, KeybindScope.Photo);
            OpenKeybindsTab();
            var row = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod" && r.Claim.ActionId == "PhotoModeForward");
            Assert.Equal("photo", row.Readback(true)["scope"]);
            Assert.Contains(KeybindsTabLayout.PhotoScopeNote, KeybindsTabLayout.Describe(row, "selected").Info);
        }
    }
}
