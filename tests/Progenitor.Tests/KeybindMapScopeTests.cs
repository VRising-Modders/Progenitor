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
        // General entries found on the native DefaultControls Inventory map.
        private static readonly string[] InventoryGeneralActions =
        {
            "DropItem", "SplitItem", "CustomSplitItem", "RepairItem", "SmartTakeItems",
            "AutoTransferItems", "SortItems",
        };

        [Fact]
        public void Every_general_row_has_its_audited_native_mode()
        {
            var coreActions = new[] { "Interact", "Interrupt", "Respawn", "CancelRevive", "Chat",
                "ToggleEmoteWheel", "PushToTalk", "AdvancedTooltip" };
            var general = GameKeybindCatalog.Listed.Where(e => e.Category == GameKeybindCatalog.General).ToArray();
            Assert.Equal(InventoryGeneralActions.Concat(coreActions).OrderBy(a => a),
                general.Select(e => e.Action).OrderBy(a => a));
            foreach (var entry in general)
            {
                var claim = GameKeybindCatalog.Claim(entry, entry.Action, 0, null, "<Mouse>/middleButton", "");
                bool inventory = InventoryGeneralActions.Contains(entry.Action);
                Assert.Equal(inventory ? GameKeybindCatalog.InInventory : "", claim.Mode);
                Assert.Equal(!inventory, KeybindArbiter.ScopesMeet(claim, KeybindScope.Build));
                Assert.Equal(!inventory && entry.Action != "PushToTalk", KeybindArbiter.GameWins(claim, KeybindScope.Build));
            }
        }

        private void RegisterBuild(string owner, string id, KeyChord chord, System.Action? press = null,
            System.Action? held = null, System.Action? release = null) =>
            Keybinds.RegisterScoped(_owner, owner, id, File(owner), chord, "", press ?? (() => { }), held, release,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Build);

        [Fact]
        public void Build_middle_mouse_shares_PTT_without_changing_gameplay_priority()
        {
            UseGame(Game("PushToTalk", Middle));
            RegisterBuild("a.mod", "Copy", Middle);
            var copy = KeybindArbiter.Current().FindProgenitor("a.mod", "Copy")!;
            Assert.True(copy.Active);
            Assert.Contains(copy.Others, o => o.ActionId == "PushToTalk");
            Assert.Contains("both remain active in Build mode", copy.Reason);
            Assert.DoesNotContain("EnforceGameWins is off", copy.Reason);
            Assert.True(KeybindArbiter.GameWins(Game("PushToTalk", Middle), KeybindScope.Gameplay));
        }

        [Theory]
        [InlineData("<Mouse>/middleButton", UiKey.MouseMiddle)]
        [InlineData("<Keyboard>/f8", UiKey.F8)]
        public void All_inventory_general_actions_do_not_conflict_with_build_even_when_rebound(string path, UiKey key)
        {
            var chord = new KeyChord(key);
            var inventory = InventoryGeneralActions.Select(action => GameKeybindCatalog.Claim(
                GameKeybindCatalog.Listed.Single(e => e.Action == action), action, 0, null, path, "")).ToArray();
            foreach (var claim in inventory)
            {
                Assert.Equal(GameKeybindCatalog.InInventory, claim.Mode);
                Assert.True(claim.Comparable);
                Assert.Equal(chord, claim.Chord);
                Assert.False(KeybindArbiter.ScopesMeet(claim, KeybindScope.Build));
                Assert.False(KeybindArbiter.GameWins(claim, KeybindScope.Build));
            }
            UseGame(inventory.Append(Game("PushToTalk", chord)).ToArray());
            int presses = 0;
            RegisterBuild("a.mod", "Copy", chord, () => presses++);
            var copy = KeybindArbiter.Current().FindProgenitor("a.mod", "Copy")!;
            Assert.True(copy.Active);
            Assert.DoesNotContain(copy.Others, c => InventoryGeneralActions.Contains(c.ActionId));
            Assert.Contains(copy.Others, c => c.ActionId == "PushToTalk");
            Keybinds.BuildScopeOpen = () => true;
            _keys.Down(chord); _keys.Frame();
            Assert.Equal(1, presses);
        }

        [Theory]
        [InlineData("Interact")]
        [InlineData("Interrupt")]
        [InlineData("Respawn")]
        [InlineData("CancelRevive")]
        [InlineData("Chat")]
        [InlineData("ToggleEmoteWheel")]
        [InlineData("AdvancedTooltip")]
        [InlineData("ActionBar1")]
        [InlineData("Building_Repair")]
        public void Inventory_classification_keeps_real_build_conflicts_blocking(string action)
        {
            var claim = GameKeybindCatalog.Claim(
                new GameKeybindCatalog.Entry(action, GameKeybindCatalog.General),
                action, 0, null, "<Mouse>/middleButton", "");
            Assert.True(KeybindArbiter.ScopesMeet(claim, KeybindScope.Build));
            Assert.True(KeybindArbiter.GameWins(claim, KeybindScope.Build));
            UseGame(claim);
            RegisterBuild("a.mod", "Copy", Middle);
            Assert.False(KeybindArbiter.Current().FindProgenitor("a.mod", "Copy")!.Active);
        }

        [Fact]
        public void Build_mode_is_a_named_UI_group_and_scope()
        {
            UseGame(Game("PushToTalk", Middle));
            RegisterBuild("a.mod", "Copy", Middle);
            OpenKeybindsTab();
            var section = Pane().Model!.Sections.Single(s => s.OwnerId == "a.mod" && s.Title == "Build mode");
            var row = Assert.Single(section.Rows);
            Assert.Equal("Copy", row.Claim.ActionId);
            Assert.Equal("build", row.Readback(true)["scope"]);
            Assert.Equal("Build mode", row.Readback(true)["scopeText"]);
            Assert.Contains(KeybindsTabLayout.BuildScopeNote, KeybindsTabLayout.Describe(row, "selected").Info);
        }

        [Fact]
        public void Build_conflicts_exclude_unrelated_modes_and_share_progenitor_gameplay_keys()
        {
            UseGame(GameInMode("MapMarker", F8, GameKeybindCatalog.InTheMap));
            KeybindArbiter.CfgSource = () => new[]
            {
                new KeybindClaim(KeybindSource.Cfg, "zz.cfg", "Thing", "Thing", F8, chordText: "F8"),
            };
            RegisterBuild("build.mod", "Copy", F8);
            RegisterMap("map.mod", "MapThing", F8);
            Keybinds.RegisterScoped(_owner, "photo.mod", "PhotoThing", File("photo"), F8, "", () => { }, null, null,
                false, true, KeybindScope.Photo);
            Keybinds.Register(_owner, "play.mod", "Action", File("play"), F8, "", () => { });
            var picture = KeybindArbiter.Current();
            var row = picture.FindProgenitor("build.mod", "Copy")!;
            // An ordinary mod's key setting still meets a build key; a Progenitor gameplay key shares it at press time
            // (zz.cfg sorts after play.mod, so play.mod keeps its gameplay chord against it).
            Assert.Contains(row.Others, c => c.OwnerId == "zz.cfg");
            Assert.DoesNotContain(row.Others, c => c.OwnerId == "play.mod");
            Assert.DoesNotContain(row.Others, c => c.ActionId == "MapMarker" || c.OwnerId == "map.mod" || c.OwnerId == "photo.mod");
            Assert.Contains(KeybindArbiter.BuildSharersOf("play.mod", "Action"), c => c.OwnerId == "build.mod");
        }

        [Theory]
        [InlineData("Building_Pickup", "while building")]
        [InlineData("Interact", "")]
        public void Build_scope_preserves_native_build_and_gameplay_conflicts(string action, string mode)
        {
            UseGame(Game("PushToTalk", Middle), GameInMode(action, Middle, mode));
            RegisterBuild("a.mod", "Copy", Middle);
            Assert.False(KeybindArbiter.Current().FindProgenitor("a.mod", "Copy")!.Active);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Build_and_gameplay_mod_keys_share_a_chord_whichever_was_installed_first(bool buildFirst)
        {
            UseGame();
            KeybindArbiter.InstallTimeOf = id => (id == "build.mod") == buildFirst ? Ledger(1) : Ledger(9);
            int build = 0, play = 0;
            bool open = false;
            Keybinds.BuildScopeOpen = () => open;
            RegisterBuild("build.mod", "Copy", F8, () => build++);
            Keybinds.Register(_owner, "play.mod", "Action", File("play"), F8, "", () => play++);
            var picture = KeybindArbiter.Current();
            Assert.True(picture.FindProgenitor("build.mod", "Copy")!.Active);
            Assert.True(picture.FindProgenitor("play.mod", "Action")!.Active);
            Assert.False(picture.FindProgenitor("play.mod", "Action")!.InConflict);
            PressF8();
            Assert.Equal((0, 1), (build, play));
            open = true;
            PressF8();
            Assert.Equal((1, 1), (build, play));
        }

        private void PressF8()
        {
            _keys.Frame();
            _keys.Down(F8);
            _keys.Frame();
            _keys.Up(F8.Key);
            _keys.Frame();
        }

        [Fact]
        public void Core_build_key_on_a_plugins_gameplay_key_wins_only_while_the_build_menu_is_open()
        {
            // Review finding: Core always outranks mods, so a static clash silenced the plugin's key everywhere.
            UseGame();
            KeybindConflictPrompt.ScreenReady = () => true;
            int copy = 0, dodge = 0;
            bool open = false;
            Keybinds.BuildScopeOpen = () => open;
            Keybinds.RegisterScoped(_owner, SettingsPanel.PluginId, "CopyBuilding", File("core"), F8, "", () => copy++,
                null, null, false, false, KeybindScope.Build);
            Keybinds.Register(_owner, "a.mod", "Dodge", File("a"), F8, "Dodge.", () => dodge++);
            _keys.Frame();
            _keys.Frame();

            var picture = KeybindArbiter.Current();
            var dodgeRow = picture.FindProgenitor("a.mod", "Dodge")!;
            Assert.True(dodgeRow.Active);
            Assert.False(dodgeRow.InConflict);
            Assert.False(picture.FindProgenitor(SettingsPanel.PluginId, "CopyBuilding")!.InConflict);
            Assert.Null(KeybindArbiter.BlockedReason("a.mod", "Dodge"));
            Assert.Empty(KeybindConflictPrompt.Find(picture, null));

            // Build menu closed: the plugin's key fires, Copy building does not.
            PressF8();
            Assert.Equal((0, 1), (copy, dodge));

            // Build menu open: Copy building fires and the plugin's key gives way on that press.
            open = true;
            PressF8();
            Assert.Equal((1, 1), (copy, dodge));
            Assert.StartsWith("not-active: suppressed: Progenitor's",
                Keybinds.SimulatePress("a.mod", "Dodge").Single().Reason);
            Assert.Equal(1, dodge);

            // Closed again: back to the plugin's key.
            open = false;
            PressF8();
            Assert.Equal((1, 2), (copy, dodge));
        }

        [Fact]
        public void Build_scope_fires_only_when_open_and_releases_when_closed()
        {
            UseGame(Game("PushToTalk", Middle));
            bool open = false;
            int pressed = 0, released = 0;
            Keybinds.BuildScopeOpen = () => open;
            RegisterBuild("a.mod", "Copy", Middle, () => pressed++, () => { }, () => released++);
            _keys.Down(Middle); _keys.Frame();
            _keys.Up(UiKey.MouseMiddle); _keys.Frame();
            Assert.Equal(0, pressed);
            Assert.Equal(Keybinds.BuildClosedReason, Keybinds.SimulatePress("a.mod", "Copy").Single().Reason);
            open = true;
            _keys.Down(Middle); _keys.Frame();
            Assert.Equal(1, pressed);
            open = false;
            _keys.Frame();
            Assert.Equal(1, released);
        }

        [Fact]
        public void Build_scope_probe_failure_is_closed_and_chat_still_blocks_activation()
        {
            UseGame();
            int pressed = 0;
            RegisterBuild("a.mod", "Copy", Middle, () => pressed++);
            Keybinds.BuildScopeOpen = () => throw new System.InvalidOperationException();
            Assert.Equal(Keybinds.BuildClosedReason, Keybinds.SimulatePress("a.mod", "Copy").Single().Reason);
            Keybinds.BuildScopeOpen = () => true;
            _keys.Chat = true;
            _keys.Down(Middle); _keys.Frame();
            Assert.Equal(0, pressed);
        }

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
