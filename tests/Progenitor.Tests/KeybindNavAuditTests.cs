using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS5 (user report 2026-09-30: Install Mods, then Configuration, then Progenitor settings, then the
    /// mouse's Back landed on Install Mods): every way the Mods workspace's view changes is one step of the
    /// back/forward history, taken by the one frame observer, and Back restores exactly the view before.
    /// Driven through the clicks a player makes (strip buttons, list rows, buttons, cell callbacks).
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static Dictionary<string, object?> NavNow() => (Dictionary<string, object?>)ModsMenu.NavState()["current"]!;

        private static string Where(Dictionary<string, object?> s) =>
            s["tab"] + "/" + s["plugin"] + "/" + s["page"] + "/" + s["pageId"] + "#" + s["section"];

        private static List<string> NavSteps() =>
            ((List<object?>)ModsMenu.NavState()["steps"]!).Cast<Dictionary<string, object?>>().Select(Where).ToList();

        /// <summary>Act, let one frame settle, and require exactly one new step that is the view now.</summary>
        private string Step(Action act, string expected)
        {
            int before = (int)ModsMenu.NavState()["count"]!;
            act();
            _keys.Frame();
            Assert.Equal(before + 1, (int)ModsMenu.NavState()["count"]!);
            string now = Where(NavNow());
            Assert.Equal(expected, now);
            Assert.Equal(now, NavSteps()[^1]);
            return now;
        }

        [Fact]
        public void Install_then_Configuration_then_the_Progenitor_link_then_Back_is_the_Configuration_main_view()
        {
            NavKeys();
            ModsMenu.BindModsConfig(File("mods-config"));
            ModsMenu.Open();
            _keys.Frame();
            Step(() => Assert.True(_runtime.FireClick("Install Mods")), "install///#");
            Step(() => Assert.True(_runtime.FireClick("Preferences")), "preferences//main/#");
            // Three homes: the purpose line's Server » opens the Server tab (one place).
            Step(() => Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.PurposeKey + "/" + SettingsLinks.CellServer, out _)),
                "server//main/#");
            Assert.False(SettingsPanel.IsMenuHostedOpen);

            // The mouse's Back button, as the pump reads it.
            _keys.Down(MouseBack);
            _keys.Frame();
            _keys.Up(UiKey.MouseBack);
            Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
            Assert.Equal("preferences//main/#", Where(NavNow()));
            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            Assert.Equal(ModsTab.InstallMods, ModsMenu.SelectedTabForTests);
            // Forward twice lands on the Server tab again (three homes: the purpose line's Server »).
            Assert.True(ModsMenu.NavigateForward());
            Assert.True(ModsMenu.NavigateForward());
            _keys.Frame();
            Assert.Equal(ModsTab.Server, ModsMenu.SelectedTabForTests);
            Assert.Equal("server//main/#", Where(NavNow()));
        }

        [Fact]
        public void Every_entry_point_is_one_step_and_Back_walks_them_all_in_order()
        {
            NavKeys();
            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            menu.Page("Info", p => p.Label("About Alpha."));
            UseGame(Game("Interact", new KeyChord(UiKey.E)));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            ModsMenu.BindModsConfig(File("mods-config"));
            ModsMenu.Open();
            _keys.Frame();
            var expected = new List<string> { Where(NavNow()) };
            Assert.Equal("mods//overview/#", expected[0]);

            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(ModsListLayout.RowKey("a.mod"), out _)), "mods/a.mod/mod/#"));
            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(
                ModsMenu.ModPage!.ButtonRow!.Key + "/" + ModPagePane.PageButtonKey(0), out _)), "mods/a.mod/custom/Info#"));
            expected.Add(Step(() => Assert.True(_runtime.FireClick("Back to Alpha")), "mods/a.mod/mod/#"));
            expected.Add(Step(() => ModsMenu.Select("a.mod", "settings", null), "mods/a.mod/settings-all/#"));
            expected.Add(Step(() => Assert.True(_runtime.FireClick("Overview / refresh")), "mods//overview/#"));
            expected.Add(Step(() => Assert.True(_runtime.FireClick("Install Mods")), "install///#"));
            expected.Add(Step(() => Assert.True(_runtime.FireClick("Preferences")), "preferences//main/#"));
            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(SettingsLinks.PurposeKey + "/" + SettingsLinks.CellServer, out _)),
                "server//main/#"));
            expected.Add(Step(() => Assert.True(_runtime.FireClick("Preferences")), "preferences//main/#"));
            // Three homes: a left-list item is a place of its own (the tab and the group).
            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(SectionNav.Key(ConfigurationPane.LocalServerGroup), out _)),
                "preferences//main/#" + ConfigurationPane.LocalServerGroup));
            expected.Add(Step(() => Assert.True(_runtime.FireClick("Keybinds")), "keybinds///#" + KeybindsPane.JumpMods));
            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellGameJump, out _)),
                "keybinds///#" + KeybindsPane.JumpGame));
            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(((IUiCellRow)Pane().HeadingOf("mod:a.mod")!).Key, out _)),
                "mods/a.mod/mod/#keys"));
            expected.Add(Step(() => Assert.True(UiCellRowHandle.TryInvoke(
                    ((IUiCellRow)ModsMenu.ModPage!.SettingsView!.KeysHeading!).Key + "/" + ModSettingsView.CellOpenKeybinds, out _)),
                "keybinds///#mod:a.mod"));
            // The bridge ops take the same clicks.
            var viaBridge = ModsMenu.KeysOp("open-page", "a.mod", null);
            Assert.Equal(true, viaBridge.GetType().GetProperty("clicked")!.GetValue(viaBridge));
            _keys.Frame();
            expected.Add("mods/a.mod/mod/#keys");
            Assert.Equal(expected, NavSteps());

            // Back through every step restores exactly that view (the view is read afresh each time).
            for (int i = expected.Count - 2; i >= 0; i--)
            {
                Assert.True(ModsMenu.NavigateBack(), "back to " + expected[i]);
                _keys.Frame();
                Assert.Equal(expected[i], Where(NavNow()));
                Assert.Equal(i, (int)ModsMenu.NavState()["index"]!);
            }

            Assert.False(ModsMenu.NavigateBack());
            Assert.True(ModsMenu.IsOpen);
        }

        // ---- KEYS6: the history outlives a close ------------------------------------------------------

        [Fact]
        public void Close_and_reopen_keeps_the_steps_and_Back_returns_to_the_view_before_the_close()
        {
            NavKeys();
            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            ModsMenu.BindModsConfig(File("mods-config"));
            ModsMenu.Open();
            _keys.Frame();
            Assert.True(UiCellRowHandle.TryInvoke(ModsListLayout.RowKey("a.mod"), out _));
            _keys.Frame();
            Assert.True(_runtime.FireClick("Preferences"));
            _keys.Frame();
            var beforeClose = NavSteps();
            Assert.Equal(new[] { "mods//overview/#", "mods/a.mod/mod/#", "preferences//main/#" }, beforeClose);

            ModsMenu.Close();
            _keys.Frame();
            var closed = ModsMenu.NavState();
            Assert.Equal(3, closed["count"]);
            Assert.Equal(false, closed["open"]);
            Assert.Equal(ModsMenu.NavClearedCoreReload, closed["clearedReason"]);

            // The workspace opens on the Mods overview: a new place after the kept ones.
            ModsMenu.Open();
            _keys.Frame();
            Assert.Equal(beforeClose.Append("mods//overview/#").ToList(), NavSteps());
            Assert.True(ModsMenu.NavigateBack());
            _keys.Frame();
            Assert.Equal(ModsTab.Preferences, ModsMenu.SelectedTabForTests);
            Assert.Equal("preferences//main/#", Where(NavNow()));
        }

        [Fact]
        public void Reopening_on_the_view_the_history_is_at_adds_no_duplicate()
        {
            NavKeys();
            ModsMenu.Open();
            _keys.Frame();
            Assert.Equal(new[] { "mods//overview/#" }, NavSteps());
            ModsMenu.Close();
            ModsMenu.Open();
            _keys.Frame();
            Assert.Equal(new[] { "mods//overview/#" }, NavSteps());
        }

        [Fact]
        public void A_world_change_clears_the_history_open_or_closed()
        {
            NavKeys();
            ModsMenu.BindModsConfig(File("mods-config"));
            ModsMenu.Open();
            _keys.Frame();
            Assert.True(_runtime.FireClick("Preferences"));
            _keys.Frame();
            ModsMenu.Close();
            Assert.Equal(2, ModsMenu.NavState()["count"]);
            try
            {
                Progenitor.WorldState.ClientWorldEpoch.Note(4242); // joined a world
                _keys.Frame();
                var state = ModsMenu.NavState();
                Assert.Equal(0, state["count"]);
                Assert.Equal(ModsMenu.NavClearedWorldChange, state["clearedReason"]);
                Assert.Equal(1, state["clearedCount"]);

                // Leaving the world (back to the title) clears what was recorded in it.
                ModsMenu.Open();
                _keys.Frame();
                Assert.Equal(1, ModsMenu.NavState()["count"]);
                Progenitor.WorldState.ClientWorldEpoch.Note(0);
                _keys.Frame();
                // Cleared, and the open view is the first place of the new world in the same frame.
                Assert.Equal(2, ModsMenu.NavState()["clearedCount"]);
                Assert.Equal(new[] { "mods//overview/#" }, NavSteps());

            }
            finally { Progenitor.WorldState.ClientWorldEpoch.Forget(); }
        }

        [Fact]
        public void Back_skips_and_drops_the_page_of_a_mod_that_was_unloaded()
        {
            NavKeys();
            RegisterMod("a.mod", "Alpha");
            RegisterMod("b.mod", "Beta");
            using var alpha = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            using var beta = ModsMenu.Register(_owner, "b.mod", "Beta", "B mod.");
            ModsMenu.BindModsConfig(File("mods-config"));
            ModsMenu.Open();
            _keys.Frame();
            Assert.True(UiCellRowHandle.TryInvoke(ModsListLayout.RowKey("b.mod"), out _));
            _keys.Frame();
            Assert.True(UiCellRowHandle.TryInvoke(ModsListLayout.RowKey("a.mod"), out _));
            _keys.Frame();
            Assert.True(_runtime.FireClick("Preferences"));
            _keys.Frame();
            Assert.Equal(new[] { "mods//overview/#", "mods/b.mod/mod/#", "mods/a.mod/mod/#", "preferences//main/#" }, NavSteps());

            // Alpha is unloaded: Back passes over its page to Beta's, and the history forgets it.
            Progenitor.Plugins.ModRegistry.NoteUnloaded("a.mod");
            Assert.True(ModsMenu.NavigateBack(), (string)ModsMenu.NavState()["last"]!);
            _keys.Frame();
            Assert.Equal("mods/b.mod/mod/#", Where(NavNow()));
            Assert.Equal(new[] { "mods//overview/#", "mods/b.mod/mod/#", "preferences//main/#" }, NavSteps());
            Assert.Equal(1, ModsMenu.NavState()["index"]);

            // Nothing valid behind the first place: nothing happens.
            Assert.True(ModsMenu.NavigateBack());
            Assert.False(ModsMenu.NavigateBack());
            Assert.True(ModsMenu.IsOpen);
        }

        [Fact]
        public void Gone_custom_pages_Keybinds_sections_and_Configuration_sections_are_not_valid_places()
        {
            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            menu.Page("Info", p => p.Label("About."));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Assert.True(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Mods, "a.mod", "custom", "Info")));
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Mods, "a.mod", "custom", "Gone")));
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Mods, "a.mod", "settings", "NoSuchGroup")));
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Mods, "nobody.mod", "mod")));
            Assert.True(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Keybinds, section: "mod:a.mod")));
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Keybinds, section: "mod:nobody.mod")));
            Assert.True(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Preferences, page: "main", section: ConfigurationPane.SourcesGroup)));
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Preferences, page: "main", section: "Retired section")));
            // Settings organization stage 3: the raw settings pane inside Configuration is no longer a place.
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Preferences, SettingsPanel.PluginId, "settings-all")));
            Assert.False(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Preferences, "nobody.mod", "settings-all")));
            Assert.True(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Preferences, page: "main", section: ConfigurationPane.ModsTabGroup)));
            Assert.True(ModsMenu.IsNavStepValid(new NavStep(ModsTab.Server, page: "main", section: ProgenitorPage.OperatorTitle)));
        }

        [Fact]
        public void A_raw_settings_page_from_a_mod_says_Back_to_mod_and_goes_there()
        {
            // Settings organization stage 3: the Configuration tab no longer opens a raw settings page (its link row goes
            // to Progenitor's page), so Back to Configuration is never drawn; from a mod it is still Back to mod.
            ModsMenu.BindModsConfig(File("mods-config"));
            ModsMenu.Open();
            Assert.True(_runtime.FireClick("Preferences"));
            Assert.False(_runtime.FireClick("Progenitor settings"));
            string BackCaption() => _runtime.CellRow(ModSettingsView.HeaderKey)!.Cells().Single(c => c.Key == ModSettingsView.CellBack).Text;

            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            Assert.True(_runtime.FireClick("Mods"));
            ModsMenu.Select("a.mod", "settings", null);
            Assert.Equal(ModSettingsView.BackCaption, BackCaption());
            Assert.True(UiCellRowHandle.TryInvoke(ModSettingsView.HeaderKey + "/" + ModSettingsView.CellBack, out _));
            _keys.Frame();
            Assert.Equal("a.mod", NavNow()["plugin"]);
            Assert.Equal("mod", NavNow()["page"]);
        }

        [Fact]
        public void Capture_mode_is_a_finite_list_so_its_row_is_a_dropdown()
        {
            Assert.Equal(new[] { "WhenHovered", "WhileShown", "Never" }, UiCaptureModes.Names);
            Assert.Equal(UiCaptureModes.DefaultName, UiCaptureModes.Names[0]);
            Assert.Equal(Enum.GetNames(typeof(UiCaptureMode)).OrderBy(n => n), UiCaptureModes.Names.OrderBy(n => n));
            string host = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
            SourcePin.AssertUncommented(host,
                "new ConfigDescription(UiCaptureModes.ConfigDescription, new AcceptableValueList<string>(UiCaptureModes.Names))");
        }
    }
}
