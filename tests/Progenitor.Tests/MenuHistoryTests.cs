using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS7: the game's full-screen menu tabs in the one back/forward history the Mods workspace keeps.
    /// Game-free: a fake <see cref="IGameMenuProbe"/> stands in for the menu, and a tab click on it behaves as
    /// the game's does (selecting a game tab while the in-game Mods tab is shown closes the workspace).
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private sealed class FakeGameMenu : IGameMenuProbe
        {
            internal readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Selected = new();
            internal FakeInner? Inner;

            public bool MenuOpen { get; set; }

            public string CurrentTab { get; set; } = "";

            public bool IsTabAvailable(string tab) =>
                GameMenuHistory.TopTabs.Contains(tab, StringComparer.OrdinalIgnoreCase) && !Hidden.Contains(tab);

            /// <summary>
            /// Live-like (KEYS7 live 2026-09-30): a click lands over the next frames, not at once. The first
            /// frame closes the in-game workspace and shows the tab the menu returns to; the next shows the tab.
            /// </summary>
            internal bool Async;

            /// <summary>Clicks that never land (the menu ignores them).</summary>
            internal bool Deaf;

            private string _clicked = "";
            private int _stage;
            private bool _opening;

            internal string ReturnTab = "System";

            public bool SelectTab(string tab)
            {
                if (!MenuOpen || !IsTabAvailable(tab)) return false;
                Selected.Add(tab);
                if (Deaf) return true;
                if (Async)
                {
                    _clicked = tab;
                    _stage = 0;
                    return true;
                }

                // The native route closes the workspace when the menu moves off the MODS tab.
                if (ModsMenu.IsOpen) ModsMenu.Close();
                CurrentTab = tab;
                return true;
            }

            public bool OpenMenu()
            {
                if (Async)
                {
                    _opening = true;
                    return true;
                }

                MenuOpen = true;
                if (CurrentTab.Length == 0) CurrentTab = "System";
                return true;
            }

            /// <summary>One frame of the game's menu.</summary>
            internal void Advance()
            {
                if (_opening)
                {
                    _opening = false;
                    MenuOpen = true;
                    if (CurrentTab.Length == 0) CurrentTab = ReturnTab;
                    return;
                }

                if (_clicked.Length == 0) return;
                if (_stage++ == 0)
                {
                    if (ModsMenu.IsOpen) ModsMenu.Close();
                    CurrentTab = ReturnTab;
                    return;
                }

                CurrentTab = _clicked;
                _clicked = "";
            }

            public IMenuInnerTabs? InnerTabs => Inner;

            public IMenuInnerTabs? InnerTabsFor(string tab) =>
                Inner != null && string.Equals(tab, "SpellBook", StringComparison.OrdinalIgnoreCase) ? Inner : null;

            /// <summary>The player clicking a game tab (or a hotkey that lands on one).</summary>
            internal void Click(string tab)
            {
                MenuOpen = true;
                if (ModsMenu.IsOpen) ModsMenu.Close();
                CurrentTab = tab;
            }
        }

        private sealed class FakeInner : IMenuInnerTabs
        {
            public string Name => "FakeSchools";

            public int Current { get; set; }

            public int Count { get; set; } = 4;

            internal readonly List<int> Selected = new();

            public bool Select(int index)
            {
                Selected.Add(index);
                Current = index;
                return true;
            }
        }

        private static string Place(Dictionary<string, object?> s) =>
            (string)s["tab"]! == "game"
                ? "game:" + s["game"] + ((int)s["inner"]! >= 0 ? "#" + s["inner"] : "")
                : Where(s);

        private static List<string> Places() =>
            ((List<object?>)ModsMenu.NavState()["steps"]!).Cast<Dictionary<string, object?>>().Select(Place).ToList();

        private static string PlaceNow() => Place((Dictionary<string, object?>)ModsMenu.NavState()["current"]!);

        /// <summary>One frame: the game's menu moves on, then the pump (the history's observer) runs.</summary>
        private void F(FakeGameMenu game, int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                game.Advance();
                _keys.Frame();
            }
        }

        private FakeGameMenu InGame()
        {
            var menu = new FakeGameMenu();
            GameMenuHistory.Probe = menu;
            NativeModsEntry.SetOwnedForTests("top-tab", curtain: false, observers: 0);
            return menu;
        }

        [Fact]
        public void Game_tabs_and_Mods_views_are_one_history_walked_in_order_both_ways()
        {
            NavKeys();
            var game = InGame();
            game.Click("VampirePowers");
            _keys.Frame();
            game.Click("SpellBook");
            _keys.Frame();
            // The in-game MODS tab: the workspace's view is the place.
            ModsMenu.Open();
            _keys.Frame();
            Assert.Equal(new[] { "game:VampirePowers", "game:SpellBook", "mods//overview/#" }, Places());

            // Back leaves the workspace through the game's own tab click.
            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            Assert.Equal("SpellBook", game.Selected.Last());
            Assert.False(ModsMenu.IsOpen);
            Assert.Equal("game:SpellBook", PlaceNow());
            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            Assert.Equal("game:VampirePowers", PlaceNow());
            Assert.Equal(0, ModsMenu.NavState()["index"]);

            // Forward to the Mods view: the workspace opens (the Open Mods menu path) and its view comes back.
            Assert.True(ModsMenu.NavigateForward());
            _keys.Frame();
            Assert.True(ModsMenu.NavigateForward());
            _keys.Frame();
            _keys.Frame();
            Assert.True(ModsMenu.IsOpen);
            Assert.Equal("mods//overview/#", PlaceNow());
            Assert.Equal(2, ModsMenu.NavState()["index"]);
            Assert.Equal(3, ModsMenu.NavState()["count"]);

            // Back, then a new tab: the forward entries go.
            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            game.Click("Social");
            _keys.Frame();
            Assert.Equal(new[] { "game:VampirePowers", "game:SpellBook", "game:Social" }, Places());
            Assert.Equal(false, ModsMenu.NavState()["canForward"]);
        }

        // ---- KEYS7 live fixes: restores arrive over several frames ----------------------------------

        private FakeGameMenu LiveTrace()
        {
            NavKeys();
            var game = InGame();
            game.Async = true;
            foreach (var tab in new[] { "Social", "VampirePowers", "SpellBook" })
            {
                game.Click(tab);
                F(game);
            }

            ModsMenu.Open();
            F(game);
            Assert.Equal(new[] { "game:Social", "game:VampirePowers", "game:SpellBook", "mods//overview/#" }, Places());
            return game;
        }

        [Fact]
        public void Back_from_the_Mods_view_to_a_game_tab_and_Forward_again_move_the_index_and_add_nothing()
        {
            var game = LiveTrace();
            Assert.True(ModsMenu.NavigateBack());
            F(game, 5);
            Assert.False(ModsMenu.IsOpen);
            Assert.Equal("SpellBook", game.CurrentTab);
            Assert.Equal(2, ModsMenu.NavState()["index"]);
            Assert.Equal(4, ModsMenu.NavState()["count"]);
            Assert.Null(ModsMenu.NavState()["pending"]);

            Assert.True(ModsMenu.NavigateBack());
            F(game, 5);
            Assert.Equal("VampirePowers", game.CurrentTab);
            Assert.Equal(1, ModsMenu.NavState()["index"]);

            Assert.True(ModsMenu.NavigateForward());
            F(game, 5);
            Assert.Equal(2, ModsMenu.NavState()["index"]);
            Assert.True(ModsMenu.NavigateForward());
            F(game, 5);
            Assert.True(ModsMenu.IsOpen);
            Assert.Equal(3, ModsMenu.NavState()["index"]);
            Assert.Equal(new[] { "game:Social", "game:VampirePowers", "game:SpellBook", "mods//overview/#" }, Places());
            Assert.False(ModsMenu.NavigateForward());
        }

        [Fact]
        public void A_real_navigation_after_Back_truncates_the_forward_stack()
        {
            var game = LiveTrace();
            Assert.True(ModsMenu.NavigateBack());
            F(game, 5);
            game.Click("Social");
            F(game, 2);
            Assert.Equal(new[] { "game:Social", "game:VampirePowers", "game:SpellBook", "game:Social" }, Places());
            Assert.Equal(3, ModsMenu.NavState()["index"]);
            Assert.Equal(false, ModsMenu.NavState()["canForward"]);
        }

        [Fact]
        public void A_restore_that_never_lands_gives_way_to_the_players_next_tab()
        {
            var game = LiveTrace();
            game.Deaf = true;
            Assert.True(ModsMenu.NavigateBack());
            F(game, 3);
            Assert.NotNull(ModsMenu.NavState()["pending"]);
            // The player clicks a tab themselves.
            game.Click("Map");
            F(game, ModsMenu.NavSettleGraceFrames + 2);
            Assert.Null(ModsMenu.NavState()["pending"]);
            Assert.Equal("game:Map", Places()[^1]);
            Assert.Equal(false, ModsMenu.NavState()["canForward"]);
        }

        [Fact]
        public void The_menu_opened_by_a_tab_click_records_its_tab_and_every_switch_from_the_first_frame()
        {
            NavKeys();
            var game = InGame();
            game.Async = true;
            Assert.False(game.MenuOpen);
            try
            {
                // A Core generation in a world reads the world as 0 until the epoch watch catches up.
                game.Click("VampirePowers");
                F(game);
                game.Click("SpellBook");
                F(game);
                Assert.Equal(new[] { "game:VampirePowers", "game:SpellBook" }, Places());
                Progenitor.WorldState.ClientWorldEpoch.Note(9191); // the watch notes the world it is already in
                F(game);
                Assert.Equal(new[] { "game:VampirePowers", "game:SpellBook" }, Places());
                Assert.Equal(0, ModsMenu.NavState()["clearedCount"]);
                // A real world change afterwards still clears.
                Progenitor.WorldState.ClientWorldEpoch.Note(9292);
                F(game);
                Assert.Equal(ModsMenu.NavClearedWorldChange, ModsMenu.NavState()["clearedReason"]);
            }
            finally { Progenitor.WorldState.ClientWorldEpoch.Forget(); }
        }

        [Fact]
        public void A_tab_the_game_hides_is_skipped_and_dropped()
        {
            NavKeys();
            var game = InGame();
            foreach (var tab in new[] { "Emotes", "Map", "Social" })
            {
                game.Click(tab);
                _keys.Frame();
            }

            game.Hidden.Add("Map");
            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            Assert.Equal("game:Emotes", PlaceNow());
            Assert.Equal(new[] { "game:Emotes", "game:Social" }, Places());
            Assert.False(ModsMenu.NavigateBack());
        }

        [Fact]
        public void The_navigation_keys_work_on_any_game_menu_tab_and_never_in_plain_gameplay()
        {
            NavKeys();
            var game = InGame();
            Assert.False(ModsMenu.MenuContextOpen);
            Assert.Equal(Keybinds.MenuClosedReason, Keybinds.SimulatePress(SettingsPanel.PluginId, ModsMenu.NavigateBackId).Single().Reason);
            game.Click("Map");
            _keys.Frame();
            game.Click("VBlood");
            _keys.Frame();
            Assert.True(ModsMenu.MenuContextOpen);
            // The mouse's back button on the game's menu goes back a tab.
            _keys.Down(MouseBack);
            _keys.Frame();
            _keys.Up(UiKey.MouseBack);
            Assert.Equal("Map", game.CurrentTab);

            // The menu closes: the keys stop, the history stays; the game's hotkey reopens it on a tab.
            game.MenuOpen = false;
            _keys.Frame();
            Assert.False(ModsMenu.MenuContextOpen);
            Assert.Equal(2, ModsMenu.NavState()["count"]);
            game.Click("SpellBook");
            _keys.Frame();
            Assert.Equal(new[] { "game:Map", "game:SpellBook" }, Places());
        }

        [Fact]
        public void With_inner_tabs_on_a_step_keeps_the_inner_tab_and_restores_it_after_the_top_tab()
        {
            NavKeys();
            var game = InGame();
            GameMenuHistory.InnerTabsEnabled = true;
            game.Inner = new FakeInner { Current = 0 };
            game.Click("VampirePowers");
            _keys.Frame();
            game.Inner.Current = 2;
            _keys.Frame();
            game.Click("Social");
            game.Inner.Current = 0;
            _keys.Frame();
            Assert.Equal(new[] { "game:VampirePowers#0", "game:VampirePowers#2", "game:Social#0" }, Places());

            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            Assert.Equal("VampirePowers", game.CurrentTab);
            Assert.Equal(2, game.Inner.Selected.Last());
            Assert.Equal("game:VampirePowers#2", PlaceNow());

            // Off (the default), the inner tab is not read.
            GameMenuHistory.InnerTabsEnabled = false;
            Assert.Equal(-1, (int)((Dictionary<string, object?>)ModsMenu.MenuTabState()["place"]!)["inner"]!);
        }

        /// <summary>
        /// KEYS7 layer 2 (live 2026-09-30): the Spellbook schools are read from the page's own mapper and selected
        /// through the handler the tab click runs; the full-screen mapper's gamepad inner-bar fields are gone.
        /// On by default since the user's live check (2026-09-30).
        /// </summary>
        [Fact]
        public void The_Spellbook_school_adapter_reads_the_pages_index_and_invokes_its_tab_handler_and_is_on_by_default()
        {
            string keybindHost = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
            Assert.Matches(@"InnerTabsConfigKey,\s*true,", keybindHost);
            string probe = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "FullscreenMenuProbe.cs");
            SourcePin.AssertUncommented(probe, "GameMenuHistory.InnerTabsEnabled && MenuOpen ? InnerTabsFor(CurrentTab) : null");
            SourcePin.AssertUncommented(probe, "Mapper()?._SelectedTabIndex ?? -1");
            SourcePin.AssertUncommented(probe, "var handler = view?.OnTabClicked;");
            SourcePin.AssertUncommented(probe, "handler.Invoke(index);");
            Assert.DoesNotContain("_SelectedInnerTab", probe);
            Assert.DoesNotContain("GamepadTabInnerNavigation", probe);
            Assert.DoesNotContain("TabDatas", probe);
            string host = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
            SourcePin.AssertUncommented(host, "GameMenuHistory.InnerTabsConfigKey,");
        }

        [Fact]
        public void An_unknown_inner_tab_is_the_same_place_so_turning_inner_tabs_on_adds_no_duplicate()
        {
            NavKeys();
            var game = InGame();
            game.Inner = new FakeInner { Current = 0 };
            game.Click("Social");
            _keys.Frame();
            game.Click("SpellBook");
            _keys.Frame();
            Assert.Equal(new[] { "game:Social", "game:SpellBook" }, Places());
            GameMenuHistory.InnerTabsEnabled = true;
            _keys.Frame();
            Assert.Equal(new[] { "game:Social", "game:SpellBook#0" }, Places());
            game.Inner.Current = 2;
            _keys.Frame();
            Assert.Equal(new[] { "game:Social", "game:SpellBook#0", "game:SpellBook#2" }, Places());

            var pages = (Dictionary<string, object?>)ModsMenu.MenuTabState()["pages"]!;
            Assert.Equal("no adapter", pages["Social"]);
            var spellbook = (Dictionary<string, object?>)pages["SpellBook"]!;
            Assert.Equal("FakeSchools", spellbook["adapter"]);
            Assert.Equal(2, spellbook["current"]);
        }

        [Fact]
        public void The_bridge_reads_the_game_menu_and_the_history_without_an_open_workspace()
        {
            var game = InGame();
            game.Click("Social");
            _keys.Frame();
            var state = ModsMenu.MenuTabState();
            Assert.Equal(true, state["menuOpen"]);
            Assert.Equal("Social", state["tab"]);
            Assert.Equal(false, state["workspaceOpen"]);
            Assert.Equal(true, ((Dictionary<string, object?>)state["available"]!)["Map"]);
            Assert.Contains("menu_tab_state", Progenitor.Dev.Ops.ModsUiArgs.Actions);
            Assert.Equal(new[] { "game:Social" }, Places());
        }
    }
}
