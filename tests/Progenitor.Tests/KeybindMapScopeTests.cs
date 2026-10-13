using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// MAPTP1: map-scope keybinds (<c>ctx.Keybinds.RegisterInMap</c> / <c>RegisterHoldInMap</c>). A map key
    /// fires only while the game's world map is open (the full-screen menu on its Map tab), meets only the
    /// game's map keys and other map keys, and releases a held key when the map closes. Game-free, on
    /// <see cref="KeybindTabTests"/>' fakes. Not run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord Middle = new(UiKey.MouseMiddle);

        private static KeybindClaim GameInMode(string action, KeyChord chord, string mode) =>
            new(KeybindSource.Game, KeybindArbiter.GameOwnerId, action, action, chord,
                category: GameKeybindCatalog.Other, mode: mode);

        private void RegisterMap(string owner, string id, KeyChord chord, System.Action? press = null,
            System.Action? held = null, System.Action? release = null) =>
            Keybinds.RegisterScoped(_owner, owner, id, File(owner), chord, "", press ?? (() => { }), held, release,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);

        [Fact]
        public void A_map_key_on_a_gameplay_game_key_stays_active_and_never_meets_gameplay_mod_keys()
        {
            var keyM = new KeyChord(UiKey.M);
            UseGame(Game("MapMenu", keyM));
            RegisterMap("a.mod", "MapThing", keyM);
            Keybinds.Register(_owner, "b.mod", "Wave", File("b"), keyM, "", () => { });

            var picture = KeybindArbiter.Current();
            var map = picture.FindProgenitor("a.mod", "MapThing")!;
            Assert.True(map.Active);
            Assert.False(map.InConflict);
            Assert.Equal(KeybindScope.Map, map.Claim.Scope);
            var wave = picture.FindProgenitor("b.mod", "Wave")!;
            Assert.False(wave.Active); // the game's M still wins over a gameplay mod key
            Assert.DoesNotContain(wave.Others, o => o.ActionId == "MapThing");
        }

        [Fact]
        public void Map_scope_rules_meet_what_is_live_in_the_full_screen_map()
        {
            var mapKey = GameInMode("MapMarker", F8, GameKeybindCatalog.InTheMap);
            var menuKey = GameInMode("Back", F8, GameKeybindCatalog.InMenus);
            var talk = Game("PushToTalk", Middle);
            var gameplay = Game("Interact", F8);

            // The game's map and menu keys are live in the map and stop a map key on an exact chord.
            Assert.True(KeybindArbiter.ScopesMeet(mapKey, KeybindScope.Map));
            Assert.True(KeybindArbiter.GameWins(mapKey, KeybindScope.Map));
            Assert.True(KeybindArbiter.ScopesMeet(menuKey, KeybindScope.Map));
            Assert.True(KeybindArbiter.GameWins(menuKey, KeybindScope.Map));
            // Push To Talk is read on every screen: reported, but both fire.
            Assert.True(KeybindArbiter.ScopesMeet(talk, KeybindScope.Map));
            Assert.False(KeybindArbiter.GameWins(talk, KeybindScope.Map));
            // A gameplay key is not live in the full-screen menu.
            Assert.False(KeybindArbiter.ScopesMeet(gameplay, KeybindScope.Map));
            Assert.False(KeybindArbiter.GameWins(gameplay, KeybindScope.Map));

            var a = new KeybindClaim(KeybindSource.Progenitor, "a", "A", "A", F8, scope: KeybindScope.Map);
            var b = new KeybindClaim(KeybindSource.Progenitor, "b", "B", "B", F8, scope: KeybindScope.Map);
            var c = new KeybindClaim(KeybindSource.Progenitor, "c", "C", "C", F8, scope: KeybindScope.Photo);
            var d = new KeybindClaim(KeybindSource.Progenitor, "d", "D", "D", F8);
            var e = new KeybindClaim(KeybindSource.Progenitor, "e", "E", "E", F8, scope: KeybindScope.Menu);
            Assert.True(KeybindArbiter.ScopesMeet(a, b));
            Assert.False(KeybindArbiter.ScopesMeet(a, c));
            Assert.False(KeybindArbiter.ScopesMeet(a, d));
            // The map is a tab of the game's full-screen menu, where menu keys are live too.
            Assert.True(KeybindArbiter.ScopesMeet(a, e));
            Assert.True(KeybindArbiter.ScopesMeet(e, a));
            Assert.False(KeybindArbiter.ScopesMeet(e, d));
        }

        [Fact]
        public void A_middle_mouse_map_key_is_reported_with_push_to_talk_and_stays_active()
        {
            UseGame(Game("PushToTalk", Middle));
            RegisterMap("a.mod", "TeleportHold", Middle);
            var map = KeybindArbiter.Current().FindProgenitor("a.mod", "TeleportHold")!;
            Assert.True(map.Active);
            Assert.Contains(map.Others, o => o.ActionId == "PushToTalk");
        }

        [Fact]
        public void An_exact_game_map_chord_wins_over_a_map_key()
        {
            UseGame(GameInMode("MapCenterPlayer", F8, GameKeybindCatalog.InTheMap));
            RegisterMap("a.mod", "MapThing", F8);
            var map = KeybindArbiter.Current().FindProgenitor("a.mod", "MapThing")!;
            Assert.False(map.Active);
        }

        [Fact]
        public void A_middle_mouse_map_hold_fires_only_with_the_map_open_and_releases_when_it_closes()
        {
            int pressed = 0, held = 0, released = 0;
            bool open = false;
            Keybinds.MapScopeOpen = () => open;
            RegisterMap("a.mod", "TeleportHold", Middle, () => pressed++, () => held++, () => released++);

            _keys.Down(Middle);
            _keys.Frame();
            _keys.Up(UiKey.MouseMiddle);
            _keys.Frame();
            Assert.Equal(0, pressed);
            Assert.Equal(Keybinds.MapClosedReason, Keybinds.SimulatePress("a.mod", "TeleportHold").Single().Reason);

            open = true;
            _keys.Down(Middle);
            _keys.Frame();
            Assert.Equal(1, pressed);
            _keys.Frame();
            Assert.True(held >= 1);
            Assert.Equal(0, released);

            // Closing the map under a held key releases it, so a hold-to-act gesture cancels there.
            open = false;
            _keys.Frame();
            Assert.Equal(1, released);
        }

        [Fact]
        public void The_map_scope_reads_the_full_screen_menu_on_its_Map_tab()
        {
            var menu = new FakeGameMenu();
            GameMenuHistory.Probe = menu;
            try
            {
                Assert.False(GameMenuHistory.SafeMapOpen());
                menu.MenuOpen = true;
                menu.CurrentTab = "VampirePowers";
                Assert.False(GameMenuHistory.SafeMapOpen());
                menu.CurrentTab = GameMenuHistory.MapTab;
                Assert.True(GameMenuHistory.SafeMapOpen());
                menu.MenuOpen = false;
                Assert.False(GameMenuHistory.SafeMapOpen());
            }
            finally
            {
                GameMenuHistory.Probe = NullGameMenuProbe.Instance;
            }
        }

        [Fact]
        public void The_keybinds_tab_labels_a_map_key()
        {
            RegisterMap("a.mod", "TeleportHold", Middle);
            OpenKeybindsTab();
            var row = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod" && r.Claim.ActionId == "TeleportHold");
            Assert.Equal("map", row.Readback(true)["scope"]);
            Assert.Contains(KeybindsTabLayout.MapScopeNote, KeybindsTabLayout.Describe(row, "selected").Info);
        }
    }
}
