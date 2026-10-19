using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// PANELKEY1: panel keys (<c>ctx.Keybinds.RegisterWhileShown</c>). A panel key fires only while its panel is shown;
    /// while that panel holds the input (<see cref="UiCaptureMode.WhileShown"/>) the game's gameplay actions never meet it
    /// (Space next to Respawn is no conflict, not even a warning), other mods' panel keys meet it by the first-installed
    /// rule, and a panel that does not hold the input leaves the game's keys in the clash as before. Also: the key-conflict
    /// dialog is raised only for a clash in the key's own scope, and only where the Mods menu or the title screen can take
    /// it, never over gameplay. Game-free, on <see cref="KeybindTabTests"/>' fakes. Not run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord Space = new(UiKey.Space);

        /// <summary>A panel whose shown state and input mode a test sets.</summary>
        private sealed class ScopePanel : IUiPanel
        {
            public ScopePanel(string pluginId, UiCaptureMode mode = UiCaptureMode.WhileShown)
            {
                PluginId = pluginId;
                CaptureMode = mode;
            }

            public string Text { get; set; } = "";
            public bool Visible { get; set; } = true;
            public bool Enabled => true;
            public bool IsDisposed { get; private set; }
            public UiHandleState State { get; set; } = UiHandleState.Built;
            public string PluginId { get; }
            public string RootName => "test";
            public UiCaptureMode CaptureMode { get; set; }
            public IUiButton Button(string text) => throw new NotSupportedException();
            public IUiToggle Toggle(string text, bool initial = false) => throw new NotSupportedException();
            public IUiLabel Label(string text) => throw new NotSupportedException();
            public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();
            public IUiInput Input(string text, string initial = "") => throw new NotSupportedException();
            public void Show() => State = UiHandleState.Shown;
            public void Hide() => State = UiHandleState.Built;

            public void Dispose()
            {
                IsDisposed = true;
                State = UiHandleState.Disposed;
            }
        }

        private IKeybindHandle RegisterPanel(string owner, string id, IUiPanel panel, KeyChord chord, Action? press = null,
            string? name = "Object preview", Action? held = null, Action? release = null) =>
            Keybinds.RegisterWhileShown(_owner, owner, id, File(owner), panel, chord, "Keeps the preview open.",
                press ?? (() => { }), held, release, name);

        // ---- the arbiter ---------------------------------------------------------------------------

        [Fact]
        public void A_panel_key_that_holds_the_input_shares_Space_with_Respawn_without_any_conflict()
        {
            UseGame(Game("Respawn", Space));
            RegisterPanel("satisvampory", "PreviewKeepOpen", new ScopePanel("satisvampory"), Space);
            var v = KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!;
            Assert.Equal(KeybindScope.Panel, v.Claim.Scope);
            Assert.Equal("Object preview", v.Claim.Panel);
            Assert.True(v.Active);
            Assert.False(v.InConflict);
            Assert.Equal("", v.Reason);
            Assert.False(KeybindArbiter.ScopesMeet(Game("Respawn", Space), KeybindScope.Panel));
            Assert.False(KeybindArbiter.GameWins(Game("Respawn", Space), KeybindScope.Panel));
            // The game's row does not list it either, and no problem or dialog comes of it.
            var respawn = KeybindArbiter.Current().Verdicts.Single(x => x.Claim.Source == KeybindSource.Game);
            Assert.False(respawn.InConflict);
            Assert.Empty(KeybindConflictPrompt.Find(KeybindArbiter.Current(), null));
            Assert.Equal(0, KeybindArbiter.Current().ConflictCount);
        }

        [Fact]
        public void The_same_Space_key_as_a_plain_gameplay_bind_still_loses_to_Respawn()
        {
            UseGame(Game("Respawn", Space));
            Keybinds.Register(_owner, "satisvampory", "PreviewKeepOpen", File("satisvampory"), Space, "", () => { }, allowWhileCaptured: true);
            var v = KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!;
            Assert.False(v.Active);
            Assert.Contains("Respawn", v.Reason);
        }

        [Fact]
        public void Two_mods_panel_keys_on_one_chord_follow_the_first_installed_rule()
        {
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            UseGame(Game("Respawn", Space));
            RegisterPanel("later.mod", "Keep", new ScopePanel("later.mod"), Space, name: "Later panel");
            RegisterPanel("first.mod", "Pin", new ScopePanel("first.mod"), Space, name: "First panel");
            var picture = KeybindArbiter.Current();
            var winner = picture.FindProgenitor("first.mod", "Pin")!;
            var loser = picture.FindProgenitor("later.mod", "Keep")!;
            Assert.True(winner.Active);
            Assert.False(loser.Active);
            Assert.True(loser.Enforced);
            Assert.Equal(KeybindArbiter.RuleInstall, loser.Rule);
            Assert.DoesNotContain(loser.Others, o => o.Source == KeybindSource.Game);
            // A real clash in the scope: the losing mod gets its dialog.
            var c = Assert.Single(KeybindConflictPrompt.Find(picture, null));
            Assert.Equal("later.mod", c.PluginId);
            Assert.Contains("while Later panel is open", KeybindConflictPrompt.WhereOf(loser.Claim));
        }

        [Theory]
        [InlineData(UiCaptureMode.WhenHovered)]
        [InlineData(UiCaptureMode.Never)]
        public void A_panel_that_does_not_hold_the_input_leaves_the_game_key_conflict_as_before(UiCaptureMode mode)
        {
            UseGame(Game("Respawn", Space));
            RegisterPanel("satisvampory", "PreviewKeepOpen", new ScopePanel("satisvampory", mode), Space);
            var v = KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!;
            Assert.Equal(KeybindScope.Gameplay, v.Claim.Scope);
            Assert.Equal("Object preview", v.Claim.Panel);
            Assert.False(v.Active);
            Assert.Equal(KeybindArbiter.RuleGame, v.Rule);
            Assert.Contains("Respawn", v.Reason);
        }

        [Fact]
        public void Changing_the_panel_input_mode_while_it_is_open_moves_the_key_between_scopes()
        {
            UseGame(Game("Respawn", Space));
            var panel = new ScopePanel("satisvampory");
            RegisterPanel("satisvampory", "PreviewKeepOpen", panel, Space);
            Assert.True(KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!.Active);
            panel.CaptureMode = UiCaptureMode.Never;
            _keys.Frame();
            Assert.False(KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!.Active);
            panel.CaptureMode = UiCaptureMode.WhileShown;
            _keys.Frame();
            Assert.True(KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!.Active);
        }

        [Fact]
        public void The_game_keys_a_holding_panel_lets_through_still_stop_a_panel_key()
        {
            var enter = new KeyChord(UiKey.Enter);
            var menuEnter = GameInMode("Enter", enter, GameKeybindCatalog.InMenus);
            Assert.True(KeybindArbiter.ScopesMeet(menuEnter, KeybindScope.Panel));
            Assert.True(KeybindArbiter.GameWins(menuEnter, KeybindScope.Panel));
            var rotate = new KeybindClaim(KeybindSource.Game, KeybindArbiter.GameOwnerId, "RotateCamera", "Rotate Camera", F7,
                category: GameKeybindCatalog.Camera);
            Assert.True(KeybindArbiter.GameWins(rotate, KeybindScope.Panel));
            UseGame(menuEnter);
            RegisterPanel("a.mod", "Confirm", new ScopePanel("a.mod"), enter);
            Assert.False(KeybindArbiter.Current().FindProgenitor("a.mod", "Confirm")!.Active);
        }

        [Fact]
        public void An_ordinary_mods_key_setting_meets_a_panel_key_but_a_Progenitor_gameplay_key_shares_it()
        {
            var cfg = Mod("old.mod", "Toggle", F8, KeybindSource.Cfg);
            Assert.True(KeybindArbiter.ScopesMeet(cfg, KeybindScope.Panel, KeybindSource.Progenitor));
            Assert.False(KeybindArbiter.ScopesMeet(Mod("a.mod", "Act", F8), KeybindScope.Panel));
            Assert.True(KeybindArbiter.ScopesMeet(Mod("a.mod", "Act", F8), KeybindScope.Gameplay));
        }

        // ---- the live gate -------------------------------------------------------------------------

        [Fact]
        public void A_panel_key_fires_only_while_its_panel_is_shown_and_never_after_it_is_destroyed()
        {
            UseGame(Game("Respawn", Space));
            var panel = new ScopePanel("satisvampory");
            int presses = 0;
            RegisterPanel("satisvampory", "PreviewKeepOpen", panel, Space, () => presses++);

            _keys.Down(Space); _keys.Frame(); _keys.Up(UiKey.Space);
            Assert.Equal(0, presses);
            var hidden = Keybinds.SimulatePress("satisvampory", "PreviewKeepOpen").Single();
            Assert.False(hidden.Fired);
            Assert.Equal(Keybinds.PanelClosedReason("Object preview"), hidden.Reason);

            panel.Show();
            _keys.Down(Space); _keys.Frame(); _keys.Up(UiKey.Space);
            Assert.Equal(1, presses);
            Assert.True(Keybinds.SimulatePress("satisvampory", "PreviewKeepOpen").Single().Fired);
            Assert.Equal(2, presses);

            panel.Hide();
            _keys.Down(Space); _keys.Frame(); _keys.Up(UiKey.Space);
            Assert.Equal(2, presses);

            panel.Show();
            panel.Dispose();
            _keys.Down(Space); _keys.Frame(); _keys.Up(UiKey.Space);
            Assert.Equal(2, presses);
            Assert.False(Keybinds.SimulatePress("satisvampory", "PreviewKeepOpen").Single().Fired);
            // A destroyed panel keeps its last mode, so the dead key does not turn into a Respawn conflict.
            var after = KeybindArbiter.Current().FindProgenitor("satisvampory", "PreviewKeepOpen")!;
            Assert.Equal(KeybindScope.Panel, after.Claim.Scope);
            Assert.False(after.InConflict);
        }

        [Fact]
        public void A_held_panel_key_is_released_when_its_panel_hides()
        {
            var panel = new ScopePanel("a.mod");
            panel.Show();
            int held = 0, released = 0;
            RegisterPanel("a.mod", "Spin", panel, F8, press: null, held: () => held++, release: () => released++);
            _keys.Down(F8); _keys.Frame();
            _keys.Frame();
            Assert.Equal(1, held);
            panel.Hide();
            _keys.Frame();
            Assert.Equal(1, released);
        }

        [Fact]
        public void A_Progenitor_gameplay_key_on_the_chord_stands_aside_only_while_the_panel_is_shown()
        {
            var panel = new ScopePanel("satisvampory");
            int panelPresses = 0, gameplayPresses = 0;
            RegisterPanel("satisvampory", "PreviewKeepOpen", panel, F8, () => panelPresses++);
            Keybinds.Register(_owner, "other.mod", "Open", File("other"), F8, "", () => gameplayPresses++, allowWhileCaptured: true);
            var picture = KeybindArbiter.Current();
            Assert.True(picture.FindProgenitor("satisvampory", "PreviewKeepOpen")!.Active);
            Assert.True(picture.FindProgenitor("other.mod", "Open")!.Active);

            _keys.Down(F8); _keys.Frame(); _keys.Up(UiKey.F8);
            Assert.Equal((0, 1), (panelPresses, gameplayPresses));
            panel.Show();
            _keys.Down(F8); _keys.Frame(); _keys.Up(UiKey.F8);
            Assert.Equal((1, 1), (panelPresses, gameplayPresses));
            var yielded = Keybinds.SimulatePress("other.mod", "Open").Single();
            Assert.False(yielded.Fired);
            Assert.Contains("has this key while Object preview is open", yielded.Reason);
        }

        // ---- lifecycle -----------------------------------------------------------------------------

        [Fact]
        public void A_panel_key_goes_with_its_handle_or_its_plugin_and_a_second_registration_replaces_the_first()
        {
            var first = new ScopePanel("a.mod");
            var handle = RegisterPanel("a.mod", "Keep", first, Space);
            Assert.Equal(1, Keybinds.Count);
            var second = new ScopePanel("a.mod");
            second.Show();
            int presses = 0;
            var again = RegisterPanel("a.mod", "Keep", second, Space, () => presses++);
            Assert.True(handle.IsDisposed);
            Assert.Equal(1, Keybinds.Count);
            Assert.True(Keybinds.SimulatePress("a.mod", "Keep").Single().Fired);
            Assert.Equal(1, presses);

            Assert.Equal(1, Keybinds.RemoveAll(_owner));
            Assert.True(again.IsDisposed);
            Assert.Equal(0, Keybinds.Count);
            Assert.Null(KeybindArbiter.Current().FindProgenitor("a.mod", "Keep"));
        }

        [Fact]
        public void A_panel_key_takes_the_panel_title_when_no_name_is_given()
        {
            RegisterPanel("a.mod", "Keep", new ScopePanel("a.mod"), Space, name: null);
            Assert.Equal(Keybinds.DefaultPanelName, KeybindArbiter.Current().FindProgenitor("a.mod", "Keep")!.Claim.Panel);
            Assert.Throws<ArgumentNullException>(() =>
                Keybinds.RegisterWhileShown(_owner, "a.mod", "X", File("a.mod"), null!, Space, "", () => { }));
        }

        // ---- the Keybinds tab and the key hint -----------------------------------------------------

        [Fact]
        public void The_Keybinds_tab_groups_a_panel_key_under_its_panel_and_says_where_it_works()
        {
            UseGame(Game("Respawn", Space));
            RegisterPanel("satisvampory", "PreviewKeepOpen", new ScopePanel("satisvampory"), Space);
            OpenKeybindsTab();
            var section = Pane().Model!.Sections.Single(s => s.OwnerId == "satisvampory" && s.Title == "While Object preview is open");
            var row = Assert.Single(section.Rows);
            Assert.True(row.Active);
            Assert.True(row.Editable);
            var read = row.Readback(true);
            Assert.Equal("panel", read["scope"]);
            Assert.Equal("While Object preview is open", read["scopeText"]);
            Assert.Equal("Object preview", read["panel"]);
            Assert.Equal(true, read["panelHoldsInput"]);
            var info = KeybindsTabLayout.Describe(row, "selected").Info;
            Assert.Contains("Works while Object preview is open.", info);
            Assert.Contains(KeybindsTabLayout.PanelHoldsInputNote, info);
            Assert.Equal("", row.ConflictText);
        }

        [Fact]
        public void A_panel_key_that_does_not_hold_the_input_says_so_in_the_Keybinds_tab()
        {
            RegisterPanel("a.mod", "Keep", new ScopePanel("a.mod", UiCaptureMode.Never), F8, name: "Tracker");
            OpenKeybindsTab();
            var row = Pane().Model!.Sections.Single(s => s.Title == "While Tracker is open").Rows.Single();
            Assert.Contains(KeybindsTabLayout.PanelSharesInputNote, KeybindsTabLayout.Describe(row, "selected").Info);
            Assert.Equal(false, row.Readback(true)["panelHoldsInput"]);
        }

        [Fact]
        public void A_button_key_hint_shows_a_panel_keys_current_key()
        {
            RegisterPanel("satisvampory", "PreviewKeepOpen", new ScopePanel("satisvampory"), Space);
            Assert.Equal(Space.DisplayText, UiKeyHint.Resolve("PreviewKeepOpen", "satisvampory", Keybinds.Snapshot(), null));
            Assert.True(Keybinds.Rebind("satisvampory", "PreviewKeepOpen", F8));
            Assert.Equal(F8.DisplayText, UiKeyHint.Resolve("PreviewKeepOpen", "satisvampory", Keybinds.Snapshot(), null));
        }

        // ---- the key-conflict dialog for scoped keys -----------------------------------------------

        [Fact]
        public void Scoped_keys_raise_a_conflict_dialog_only_for_a_clash_in_their_own_scope()
        {
            var f = new KeyChord(UiKey.F);
            var g = new KeyChord(UiKey.G);
            UseGame(Game("Interact", f), Game("Respawn", Space), Game("UseItem", g), GameInMode("MapMarker", F9, GameKeybindCatalog.InTheMap));
            // Out of scope: a map key and a holding panel key on a gameplay action's chord are no clash.
            Keybinds.RegisterScoped(_owner, "map.mod", "Ping", File("map"), f, "", () => { }, null, null,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);
            RegisterPanel("panel.mod", "Keep", new ScopePanel("panel.mod"), Space);
            // In scope: a map key on the game's map key, a build key on a gameplay action (building keeps gameplay live).
            Keybinds.RegisterScoped(_owner, "map2.mod", "Mark", File("map2"), F9, "", () => { }, null, null,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map);
            Keybinds.RegisterScoped(_owner, "build.mod", "Copy", File("build"), g, "", () => { }, null, null,
                allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Build);
            var cases = KeybindConflictPrompt.Find(KeybindArbiter.Current(), null);
            Assert.Equal(new[] { "build.mod", "map2.mod" }, cases.Select(c => c.PluginId).OrderBy(x => x));
            Assert.DoesNotContain(cases, c => c.PluginId == "map.mod" || c.PluginId == "panel.mod");
        }

        [Fact]
        public void The_conflict_dialog_waits_for_the_Mods_menu_or_the_title_screen_and_never_shows_over_gameplay()
        {
            Assert.False(KeybindConflictPrompt.ScreenReadyFor(modsMenuOpen: false, titleModsEntryUp: false));
            Assert.True(KeybindConflictPrompt.ScreenReadyFor(modsMenuOpen: true, titleModsEntryUp: false));
            Assert.True(KeybindConflictPrompt.ScreenReadyFor(modsMenuOpen: false, titleModsEntryUp: true));
            string source = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindConflictPrompt.cs");
            string ready = SourcePin.MethodBody(source, "private static bool DefaultScreenReady");
            Assert.DoesNotContain("ChatFocus", ready);
        }

        [Fact]
        public void A_deferred_conflict_is_asked_once_the_Mods_menu_opens()
        {
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            bool menuOpen = false;
            KeybindConflictPrompt.ScreenReady = () => KeybindConflictPrompt.ScreenReadyFor(menuOpen, false);
            Keybinds.Register(_owner, "first.mod", "A", File("first"), F8, "", () => { });
            Keybinds.Register(_owner, "later.mod", "B", File("later"), F8, "", () => { });
            for (int i = 0; i < 5; i++) _keys.Frame();
            Assert.Null(UiDialog.Current);
            menuOpen = true;
            _keys.Frame();
            Assert.NotNull(UiDialog.Current);
        }
    }
}
