using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS4: mouse buttons as keys, menu-scoped keybinds, and back/forward in the Mods workspace on the
    /// mouse's back and forward buttons. Game-free, on <see cref="KeybindTabTests"/>' fakes. Nothing here
    /// has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static readonly KeyChord MouseBack = new(UiKey.MouseBack);
        private static readonly KeyChord MouseForward = new(UiKey.MouseForward);
        private readonly object _navOwner = new();

        private IKeybindHandle[] NavKeys() => ModsMenu.RegisterNavigationKeys(_navOwner, File("core-ui"));

        private static KeybindClaim AutoRun() =>
            GameKeybindCatalog.Claim(GameKeybindCatalog.Listed.Single(e => e.Action == "AutoMove"), "Auto Run", 0, null,
                "<Mouse>/forwardButton", "Mouse Button 4");

        // ---- mouse buttons as keys -------------------------------------------------------------------

        [Fact]
        public void The_middle_back_and_forward_mouse_buttons_are_keys_by_name_and_by_the_games_words()
        {
            Assert.True(KeyChord.TryParse("MouseBack", out var back) && back == MouseBack);
            Assert.True(KeyChord.TryParse("Mouse Button 4", out var fwd) && fwd == MouseForward);
            Assert.True(KeyChord.TryParse("Mouse Button 5 + Ctrl", out var ctrlBack) && ctrlBack == new KeyChord(UiKey.MouseBack, KeyModifiers.Ctrl));
            Assert.True(KeyChord.TryParse("Scroll Wheel", out var middle) && middle.Key == UiKey.MouseMiddle);
            Assert.Equal("MouseForward", MouseForward.ToString());
            // KEYS8: the left and right buttons are keys too (with a modifier); the game's own left-button
            // bindings are still not read as claims (below).
            Assert.True(KeyChord.TryParse("Left Mouse", out var left) && left.Key == UiKey.MouseLeft);
            Assert.True(MouseBack.IsSafeWhileTyping);

            // The game's mouse bindings read as keys now: Auto Run holds the forward button.
            var autoRun = AutoRun();
            Assert.True(autoRun.Comparable);
            Assert.Equal(MouseForward, autoRun.Chord);
            Assert.Equal("Mouse Button 4", autoRun.ChordText);
            Assert.True(KeybindArbiter.GameWins(autoRun));
            Assert.False(GameKeybindCatalog.TryChord(null, "<Mouse>/leftButton", out _));
            Assert.Equal(new KeyChord(UiKey.MouseMiddle), GameKeybindCatalog.TryChord(null, "<Mouse>/middleButton", out var m) ? m : KeyChord.None);
        }

        // ---- scopes ----------------------------------------------------------------------------------

        [Fact]
        public void A_menu_key_never_meets_a_gameplay_key_and_meets_other_menu_keys_and_the_games_menu_keys()
        {
            UseGame(AutoRun(), Game("Interact", new KeyChord(UiKey.F6)),
                GameKeybindCatalog.Claim(GameKeybindCatalog.OtherKeys.Single(e => e.Action == "Enter"), "Enter", 0, null, "<Keyboard>/f7", ""));
            NavKeys();
            var picture = KeybindArbiter.Current();
            // Auto Run holds the forward button in gameplay; the menu's Navigate forward is active beside it.
            var forward = picture.FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateForwardId)!;
            Assert.True(forward.Active);
            Assert.False(forward.InConflict);
            Assert.Equal(KeybindScope.Window, forward.Claim.Scope);

            // A gameplay mod key on the forward button loses to Auto Run; the menu key is not involved.
            Keybinds.Register(_owner, "a.mod", "Dash", File("a"), MouseForward, "", () => { });
            var dash = KeybindArbiter.Current().FindProgenitor("a.mod", "Dash")!;
            Assert.False(dash.Active);
            Assert.Equal(KeybindArbiter.RuleGame, dash.Rule);
            Assert.DoesNotContain(dash.Others, o => o.OwnerId == SettingsPanel.PluginId && o.ActionId == ModsMenu.NavigateForwardId);
            // HUB2: no Core key on the forward button meets a gameplay key, so only the game is reported beside the mod key.
            Assert.DoesNotContain(dash.Others, o => o.OwnerId == SettingsPanel.PluginId);

            // Remapping: the menu key may take a gameplay game key's chord, never another menu key's or a game menu key's.
            Assert.True(Keybinds.TryRebind(SettingsPanel.PluginId, ModsMenu.NavigateBackId, new KeyChord(UiKey.F6), Keybinds.RebindCheck.Full, out _));
            Assert.True(KeybindArbiter.Current().FindProgenitor(SettingsPanel.PluginId, ModsMenu.NavigateBackId)!.Active);
            Assert.False(Keybinds.TryRebind(SettingsPanel.PluginId, ModsMenu.NavigateBackId, MouseForward, Keybinds.RebindCheck.Full, out string? why));
            Assert.StartsWith("Mouse Forward is used by ", why);
            Assert.EndsWith(" (Navigate forward). Pick another key.", why);
            Assert.False(Keybinds.TryRebind(SettingsPanel.PluginId, ModsMenu.NavigateBackId, F7, Keybinds.RebindCheck.Full, out why));
            Assert.StartsWith("F7 is used by the game (Enter, in menus).", why);
            // A gameplay key may not take the forward button (Auto Run), whatever the menu does with it.
            Keybinds.Register(_owner, "b.mod", "Go", File("b"), F9, "", () => { });
            Assert.False(Keybinds.TryRebind("b.mod", "Go", MouseForward, Keybinds.RebindCheck.Full, out why));
            // KEYS5: in words, with the game's own name for the same button.
            Assert.StartsWith("Mouse Forward (the game's Mouse Button 4) is used by the game (Auto Run)", why);
        }

        [Fact]
        public void Two_menu_keys_on_one_chord_follow_the_winner_rule_and_a_game_menu_key_stops_a_menu_key()
        {
            UseGame(GameKeybindCatalog.Claim(GameKeybindCatalog.OtherKeys.Single(e => e.Action == "Back"), "Back", 0, null, "<Keyboard>/f7", ""));
            var picture = KeybindArbiter.Compute(
                KeybindArbiter.ReadGame().Claims,
                new[]
                {
                    new KeybindClaim(KeybindSource.Progenitor, "a.mod", "One", "One", F9, scope: KeybindScope.Menu),
                    new KeybindClaim(KeybindSource.Progenitor, "b.mod", "Two", "Two", F9, scope: KeybindScope.Menu),
                    new KeybindClaim(KeybindSource.Progenitor, "c.mod", "Three", "Three", F9),
                    new KeybindClaim(KeybindSource.Progenitor, "d.mod", "Four", "Four", F7, scope: KeybindScope.Menu),
                    new KeybindClaim(KeybindSource.Progenitor, "e.mod", "Five", "Five", F7),
                },
                Array.Empty<KeybindClaim>());
            Assert.True(picture.FindProgenitor("a.mod", "One")!.Active);
            Assert.False(picture.FindProgenitor("b.mod", "Two")!.Active);
            var three = picture.FindProgenitor("c.mod", "Three")!;
            Assert.True(three.Active);
            Assert.False(three.InConflict);
            // The game's Back works in menus: it stops a menu key and only warns a gameplay key.
            Assert.False(picture.FindProgenitor("d.mod", "Four")!.Active);
            var five = picture.FindProgenitor("e.mod", "Five")!;
            Assert.True(five.Active);
            Assert.StartsWith("Also used by the game in menus (Back)", five.Reason);
        }

        // ---- navigation keys and the pump ---------------------------------------------------------------

        [Fact]
        public void The_navigation_keys_are_Progenitor_keys_on_the_mouse_buttons_listed_in_the_Keybinds_tab()
        {
            NavKeys();
            OpenKeybindsTab();
            var rows = Pane().Model!.ModRows().Where(r => r.Claim.OwnerId == SettingsPanel.PluginId).ToList();
            var back = rows.Single(r => r.Claim.ActionId == ModsMenu.NavigateBackId);
            var forward = rows.Single(r => r.Claim.ActionId == ModsMenu.NavigateForwardId);
            Assert.Equal("Navigate back", back.Name);
            Assert.Equal("Navigate forward", forward.Name);
            Assert.Equal("Mouse Back", back.Claim.ChordText);
            Assert.Equal("Mouse Forward", forward.Claim.ChordText);
            Assert.Equal(new KeyChord(UiKey.MouseMiddle).DisplayText, "Middle Mouse");
            Assert.True(KeyChord.TryParse("Mouse Back", out var parsedBack) && parsedBack == MouseBack);
            Assert.Equal("window", back.Readback(true)["scope"]);
            Assert.True(back.Editable);
            Assert.Contains(KeybindsTabLayout.WindowScopeNote, KeybindsTabLayout.Describe(back, "selected").Info);
        }

        [Fact]
        public void A_menu_key_fires_only_while_the_workspace_is_open()
        {
            int fired = 0;
            Keybinds.RegisterScoped(_owner, "a.mod", "Menu", File("a"), F9, "", () => fired++, null, null,
                allowWhileCaptured: true, allowWhenChatUnknown: false, KeybindScope.Menu);
            Assert.False(ModsMenu.IsOpen);
            _keys.Down(F9);
            _keys.Frame();
            _keys.Up(UiKey.F9);
            Assert.Equal(0, fired);
            Assert.Equal(Keybinds.MenuClosedReason, Keybinds.SimulatePress("a.mod", "Menu").Single().Reason);

            ModsMenu.Open();
            _keys.Down(F9);
            _keys.Frame();
            Assert.Equal(1, fired);
        }

        [Fact]
        public void The_mouse_back_button_is_captured_as_the_new_key_while_a_capture_waits_and_does_not_navigate()
        {
            NavKeys();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            OpenKeybindsTab();
            int steps = ModsMenu.NavForTests.Count;
            Pane().BeginEdit("progenitor:a.mod/Open");
            _keys.Next = MouseBack;
            _keys.Down(MouseBack);
            _keys.Frame();
            var edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            // The capture takes it: a.mod/Open is a gameplay key, and Navigate back is a menu key, so it is free.
            Assert.Equal("Mouse Back", edit["pending"]);
            Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
            Assert.Equal(steps, ModsMenu.NavForTests.Count);
        }

        // ---- the history ----------------------------------------------------------------------------------

        [Fact]
        public void The_history_works_like_a_browser()
        {
            var h = new WorkspaceNavHistory();
            var a = new NavStep(ModsTab.Mods, page: "overview");
            var b = new NavStep(ModsTab.Keybinds);
            var c = new NavStep(ModsTab.Preferences, section: "General");
            var d = new NavStep(ModsTab.InstallMods);
            Assert.Null(h.Back());
            Assert.True(h.Record(a));
            Assert.False(h.Record(a));
            h.Record(b);
            h.Record(c);
            Assert.Equal(b, h.Back());
            Assert.Equal(a, h.Back());
            Assert.Null(h.Back());
            Assert.Equal(b, h.Forward());
            // A new place after going back drops the forward entries.
            h.Record(d);
            Assert.False(h.CanForward);
            Assert.Equal(3, h.Count);
            Assert.Equal(b, h.Back());
            Assert.Equal(a, h.Back());
            h.Clear();
            Assert.Equal(0, h.Count);
            for (int i = 0; i < WorkspaceNavHistory.Max + 10; i++) h.Record(new NavStep(ModsTab.Preferences, section: "S" + i));
            Assert.Equal(WorkspaceNavHistory.Max, h.Count);
        }

        [Fact]
        public void Back_and_forward_restore_tabs_mods_pages_and_sections_and_back_at_the_start_does_not_close()
        {
            NavKeys();
            RegisterMod("a.mod", "Alpha");
            using var menu = ModsMenu.Register(_owner, "a.mod", "Alpha", "A mod.");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            ModsMenu.Open();
            ModsMenu.OpenMod("a.mod");
            ModsMenu.OpenInKeybinds("a.mod");
            ModsMenu.OpenModPageKeys("a.mod");
            ModsMenu.SelectTab("configuration");
            var state = ModsMenu.NavState();
            var steps = ((List<object?>)state["steps"]!).Cast<Dictionary<string, object?>>().ToList();
            Assert.Equal(new[] { "mods", "mods", "keybinds", "mods", "preferences" }, steps.Select(s => (string)s["tab"]!).ToArray());
            Assert.Equal("overview", steps[0]["page"]);
            Assert.Equal("mod", steps[1]["page"]);
            Assert.Equal("a.mod", steps[1]["plugin"]);
            Assert.Equal("mod:a.mod", steps[2]["section"]);
            Assert.Equal("keys", steps[3]["section"]);

            // Back through the mouse button, as the pump reads it: the mod page's Keys section.
            _keys.Down(MouseBack);
            _keys.Frame();
            _keys.Up(UiKey.MouseBack);
            Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
            Assert.Equal(3, ModsMenu.NavState()["index"]);
            Assert.True(ModsMenu.NavigateBack());
            Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
            Assert.Equal("mod:a.mod", Pane().LastJump!.Section);
            Assert.True(ModsMenu.NavigateBack());
            Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
            Assert.Equal("a.mod", ModsMenu.NavState()["current"] is Dictionary<string, object?> cur ? cur["plugin"] : null);
            Assert.True(ModsMenu.NavigateBack());
            Assert.Equal("overview", ((Dictionary<string, object?>)ModsMenu.NavState()["current"]!)["page"]);
            // At the start: nothing happens, and the workspace stays open.
            Assert.False(ModsMenu.NavigateBack());
            Assert.True(ModsMenu.IsOpen);
            Assert.Equal(0, ModsMenu.NavState()["index"]);

            // Forward again, through the bridge op (the keybind path).
            var op = (Dictionary<string, object?>)ModsMenu.NavigateOp(back: false);
            Assert.Equal(true, ((Dictionary<string, object?>)((List<object?>)op["dispatch"]!).Single()!)["fired"]);
            Assert.Equal("a.mod", ((Dictionary<string, object?>)ModsMenu.NavState()["current"]!)["plugin"]);
            // A new place after going back drops the forward entries.
            ModsMenu.SelectTab("install");
            Assert.Equal(false, ModsMenu.NavState()["canForward"]);

            // A dialog on screen: ignored.
            UiDialog.OpenContent(t => UiApi.Panel(t, _owner, "x.mod"), new DialogContent { Shape = UiDialogShape.Notice, Title = "N", MainCaption = "OK" }, "t", "notice", "k");
            Assert.False(ModsMenu.NavigateBack());
            Assert.StartsWith("back: ignored (a dialog is open)", (string)ModsMenu.NavState()["last"]!);
            UiDialog.CloseCurrent();

            // KEYS6: closing keeps the history (it is still readable), and the keys do nothing while closed.
            int kept = (int)ModsMenu.NavState()["count"]!;
            ModsMenu.Close();
            Assert.Equal(kept, ModsMenu.NavState()["count"]);
            Assert.Equal(Keybinds.WindowClosedReason, ((Dictionary<string, object?>)((List<object?>)((Dictionary<string, object?>)ModsMenu.NavigateOp(back: true))["dispatch"]!).Single()!)["reason"]);
        }

        [Fact]
        public void The_bridge_nav_actions_need_no_open_workspace()
        {
            Assert.Contains("nav_back", ModsUiArgs.Actions);
            Assert.Contains("nav_forward", ModsUiArgs.Actions);
            Assert.Contains("nav_state", ModsUiArgs.Actions);
            Assert.False(ModsUiArgs.RequiresOpenWorkspace("nav_back"));
            Assert.False(ModsUiArgs.RequiresOpenWorkspace("nav_state"));
        }
    }
}
