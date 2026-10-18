using System.Collections.Generic;
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
