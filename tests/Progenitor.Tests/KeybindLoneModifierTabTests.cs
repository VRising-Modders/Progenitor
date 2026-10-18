using System.Collections.Generic;
using BepInEx.Configuration;
using System.Linq;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// HOLDMOD1: lone-modifier hold keys against the game's own lone-modifier actions (the game's input asset binds
    /// Advanced Tooltip, Toggle Emote Wheel and Emotes Modifier to Left Alt, Toggle Action Wheel to Left Ctrl, Cliff Jump,
    /// Custom Split Item, Track Item and Shapeshift Modifier to Left Shift), the first-installed rule between mods, and
    /// the Keybinds tab's capture. Game-free, on <see cref="KeybindTabTests"/>' fakes. Nothing here has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static KeybindClaim GameAction(string action, string displayName, string path)
        {
            var entry = GameKeybindCatalog.Listed.Single(e => e.Action == action);
            return GameKeybindCatalog.Claim(entry, displayName, 0, null, path, "");
        }

        private void HoldBind(string plugin, string id, KeyChord chord, List<string>? log = null) =>
            Keybinds.Register(_owner, plugin, id, File(plugin), chord, "Show more while held.",
                () => log?.Add(plugin + " pressed"), () => { }, () => log?.Add(plugin + " released"));

        [Fact]
        public void The_games_lone_modifier_actions_read_as_lone_modifiers_and_share_the_key_with_a_mod_hold()
        {
            var tooltip = GameAction("AdvancedTooltip", "Advanced Tooltip", "<Keyboard>/leftAlt");
            var emotes = GameAction("ToggleEmoteWheel", "Toggle Emote Wheel", "<Keyboard>/leftAlt");
            var cliff = GameAction("CliffJump", "Cliff Jump", "<Keyboard>/leftShift");
            var wheel = GameAction("ToggleActionWheel", "Toggle Action Wheel", "<Keyboard>/leftCtrl");
            Assert.Equal(KeyChord.Alt, tooltip.Chord);
            Assert.Equal(KeyChord.Shift, cliff.Chord);
            Assert.Equal(KeyChord.Ctrl, wheel.Chord);
            Assert.True(tooltip.Comparable);
            Assert.True(KeybindArbiter.SharesLoneModifier(tooltip));
            Assert.False(KeybindArbiter.GameWins(tooltip));
            Assert.False(KeybindArbiter.GameWins(tooltip, KeybindScope.Gameplay));
            Assert.False(KeybindArbiter.GameWins(tooltip, KeybindScope.Map));
            UseGame(tooltip, emotes, cliff, wheel, Game("Interact", new KeyChord(UiKey.F)));

            // The mod's Alt hold stays active, says it shares Alt with the game, and fires on Alt.
            var log = new List<string>();
            HoldBind("a.mod", "Advanced", KeyChord.Alt, log);
            var v = KeybindArbiter.Current().FindProgenitor("a.mod", "Advanced")!;
            Assert.True(v.Active);
            Assert.Equal("Alt is also held by the game (Advanced Tooltip and Toggle Emote Wheel); both act on it.", v.Reason);
            _keys.Down(KeyChord.Alt);
            _keys.Frame();
            Assert.Equal(new[] { "a.mod pressed" }, log);

            // A remap onto a lone modifier the game reads is not refused and takes nothing from the game.
            HoldBind("b.mod", "Peek", F9);
            Assert.True(Keybinds.TryRebind("b.mod", "Peek", KeyChord.Shift, Keybinds.RebindCheck.Full, out string? why), why);
            Assert.Null(KeybindRemapGuard.HolderOf(KeybindSource.Progenitor, "c.mod", "X", KeyChord.Ctrl, null, out _));
            var take = new List<KeybindClaim>();
            Assert.Null(KeybindRemapGuard.TakeFrom(Mod("c.mod", "X", F8), KeyChord.Ctrl, take, out _));
            Assert.Empty(take);

            // An ordinary game key still wins, as before.
            HoldBind("d.mod", "Use", new KeyChord(UiKey.F));
            Assert.False(KeybindArbiter.Current().FindProgenitor("d.mod", "Use")!.Active);
        }

        /// <summary>
        /// Review of PR #102: an older mod's own <c>ModifierKey = LeftAlt</c> setting shares Alt with a Progenitor hold, as
        /// the game does, instead of making it inactive by install order. Shared claims are no conflict.
        /// </summary>
        [Fact]
        public void An_ordinary_mods_LeftAlt_setting_shares_Alt_and_is_not_counted_as_a_conflict()
        {
            var cfg = File("old.mod");
            cfg.Bind("Hotkeys", "ModifierKey", new KeyboardShortcut("LeftAlt"), "Hold for details.");
            SettingsCatalog.Register("old.mod", cfg, new object(), foreign: true);
            KeybindArbiter.InstallTimeOf = id => id == "old.mod" ? Ledger(30) : Ledger(1);
            UseGame(GameAction("AdvancedTooltip", "Advanced Tooltip", "<Keyboard>/leftAlt"));

            var log = new List<string>();
            HoldBind("a.mod", "Advanced", KeyChord.Alt, log);
            var claim = SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/ModifierKey");
            Assert.Equal(KeyChord.Alt, claim.Chord);
            Assert.True(KeybindArbiter.SharesLoneModifier(claim, Mod("a.mod", "Advanced", KeyChord.Alt)));
            Assert.False(KeybindArbiter.SharesLoneModifier(Mod("b.mod", "X", KeyChord.Alt), Mod("a.mod", "Advanced", KeyChord.Alt)));

            var picture = KeybindArbiter.Current();
            var mine = picture.FindProgenitor("a.mod", "Advanced")!;
            Assert.True(mine.Active);
            Assert.False(mine.InConflict);
            Assert.StartsWith("Alt is also held by the game (Advanced Tooltip) and old.mod (", mine.Reason);
            Assert.EndsWith("); both act on it.", mine.Reason);
            var theirs = picture.Find(claim.Key)!;
            Assert.True(theirs.Active);
            Assert.False(theirs.InConflict);
            Assert.StartsWith("Alt is also held by ", theirs.Reason);
            Assert.Empty(ModProblems.For("a.mod"));

            _keys.Down(KeyChord.Alt);
            _keys.Frame();
            Assert.Equal(new[] { "a.mod pressed" }, log);
            _keys.Up(UiKey.LeftAlt);
            _keys.Frame();

            // The tab: no conflict counted, the Conflicts view leaves the rows out, the muted note stays.
            OpenKeybindsTab();
            Assert.Equal(0, Pane().Model!.ConflictCount);
            var row = Pane().Model!.FindRow("progenitor:a.mod/Advanced")!;
            Assert.False(row.Conflict);
            Assert.False(Pane().Model!.FindRow(claim.Key)!.Conflict);
            Assert.Contains("both act on it", KeybindsTabLayout.Describe(row, "selected").Info + " " + KeybindsTabLayout.Describe(row, "selected").Warning);

            // A second Progenitor hold on Alt is a real clash, and is counted.
            HoldBind("b.mod", "Peek", KeyChord.Alt);
            OpenKeybindsTab();
            Assert.True(Pane().Model!.ConflictCount > 0);
            Assert.True(Pane().Model!.FindRow("progenitor:b.mod/Peek")!.Conflict);
            Assert.False(Pane().Model!.FindRow(claim.Key)!.Conflict);
        }

        /// <summary>Review of PR #102: an ordinary mod's key may act on a press, so its row never takes a lone modifier.</summary>
        [Fact]
        public void A_config_file_key_row_refuses_a_lone_modifier_except_its_own_default()
        {
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Toggle", new KeyboardShortcut("F7"), "Toggles it.");
            cfg.Bind("Hotkeys", "ModifierKey", new KeyboardShortcut("LeftAlt"), "Hold for details.");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            SettingsCatalog.AllowForeignEdits = true;
            var toggle = SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/Toggle");

            OpenKeybindsTab();
            Assert.True(Pane().BeginEdit(toggle.Key));
            Pane().OnCaptured(new KeyCaptureResult(RebindOutcome.Bound, KeyChord.Alt, null));
            var edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Null(edit["pending"]);
            Assert.StartsWith(KeybindsTabLayout.CfgLoneModifierReason(KeyChord.Alt), (string)edit["status"]!);
            ModsMenu.KeysOp("cancel", null, null);

            var refused = SettingsCatalog.WriteKey(toggle, KeyChord.Shift);
            Assert.Equal(SettingWriteState.Refused, refused.State);
            Assert.Equal(KeybindsTabLayout.CfgLoneModifierReason(KeyChord.Shift), refused.Error);

            // A reset puts back what the mod shipped, even a lone modifier.
            var modifier = SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/ModifierKey");
            Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.WriteKey(modifier, F8).State);
            modifier = SettingsCatalog.KeyClaims(sweep: false).Single(c => c.ActionId == "Hotkeys/ModifierKey");
            Assert.Equal(SettingWriteState.Accepted, SettingsCatalog.WriteKey(modifier, KeyChord.Alt).State);
        }

        [Fact]
        public void Two_mods_holding_Alt_follow_the_first_installed_rule()
        {
            UseNames();
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            var log = new List<string>();
            HoldBind("late.mod", "Advanced", KeyChord.Alt, log);
            HoldBind("first.mod", "Advanced", KeyChord.Alt, log);

            var picture = KeybindArbiter.Current();
            Assert.True(picture.FindProgenitor("first.mod", "Advanced")!.Active);
            var loser = picture.FindProgenitor("late.mod", "Advanced")!;
            Assert.False(loser.Active);
            Assert.StartsWith("Not active: Alt is used by first.mod, installed first.", loser.Reason);

            _keys.Down(KeyChord.Alt);
            _keys.Frame();
            Assert.Equal(new[] { "first.mod pressed" }, log);

            // Alt + X is a different key: no clash with either.
            Keybinds.Register(_owner, "x.mod", "AltX", File("x.mod"), new KeyChord(UiKey.X, KeyModifiers.Alt), "", () => { });
            Assert.True(KeybindArbiter.Current().FindProgenitor("x.mod", "AltX")!.Active);
        }

        [Fact]
        public void The_Keybinds_tab_takes_Alt_for_a_hold_key_and_refuses_it_for_a_press_key()
        {
            HoldBind("a.mod", "Peek", F9);
            Keybinds.Register(_owner, "a.mod", "Open", File("a.mod.open"), F8, "Opens it.", () => { });
            OpenKeybindsTab();

            // A press key: refused with the reason, still capturing, nothing stored.
            Assert.Equal(KeyChord.Alt, ModsMenu.CaptureChordOrThrow("Alt"));
            ModsMenu.KeysOp("capture", "progenitor:a.mod/Open", "Alt");
            var edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Null(edit["pending"]);
            Assert.Equal(true, edit["capturing"]);
            Assert.StartsWith(Keybinds.LoneModifierPressReason(KeyChord.Alt), (string)edit["status"]!);
            ModsMenu.KeysOp("cancel", null, null);
            Assert.Equal(F8, ChordOf("a.mod", "Open"));

            // A hold key: shown as "Alt" and saved as "Alt".
            ModsMenu.KeysOp("capture", "progenitor:a.mod/Peek", "Alt");
            edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Equal("Alt", edit["pending"]);
            ModsMenu.KeysOp("save", null, null);
            Assert.Equal(KeyChord.Alt, ChordOf("a.mod", "Peek"));
        }

        [Fact]
        public void The_Keybinds_tab_records_Alt_when_it_is_pressed_and_released_alone()
        {
            HoldBind("a.mod", "Peek", F9);
            OpenKeybindsTab();
            _keys.Mods = ModifierSides.None;
            Assert.True(Pane().BeginEdit("progenitor:a.mod/Peek"));
            Assert.Equal(KeybindsTabLayout.CaptureHelp, (string)((Dictionary<string, object?>)Pane().Describe()["edit"]!)["status"]!);
            _keys.Frame();

            _keys.Mods = ModifierSides.RightAlt;
            _keys.Frame();
            _keys.Frame();
            var edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Null(edit["pending"]);
            _keys.Mods = ModifierSides.None;
            _keys.Frame();
            edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Equal("Alt", edit["pending"]);

            // Alt held and another key pressed: that chord, not Alt.
            Assert.True(Pane().BeginEdit("progenitor:a.mod/Peek"));
            _keys.Frame();
            _keys.Mods = ModifierSides.LeftAlt;
            _keys.Frame();
            _keys.Next = new KeyChord(UiKey.Q, KeyModifiers.Alt);
            _keys.Frame();
            _keys.Mods = ModifierSides.None;
            _keys.Frame();
            edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Equal("Q + Alt", edit["pending"]);
        }
    }
}
