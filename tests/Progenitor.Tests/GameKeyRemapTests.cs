using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS9 (user report 2026-10-10: changing the game's own keys in the Mods Keybinds tab did nothing). The
    /// game rows were read-only by design, so a click on a game key cap only put a note in the description
    /// column: a plain key, a Shift chord and a mouse button all did nothing. These tests drive the tab against
    /// a fake game: its slots are control paths with the game's defaults, the reader builds the claims from
    /// them exactly as <c>GameKeybindReader</c> does (<see cref="GameKeybindCatalog.Claim"/>), and the writer
    /// stands in for <c>GameKeybindWriter</c> (override path, or remove the override). Game-free; the Unity
    /// writer itself has not run.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        /// <summary>A fake game: (action, slot) to an effective path, over the shipped defaults.</summary>
        private sealed class FakeGameKeys
        {
            internal readonly Dictionary<(string Action, int Slot), string?> Defaults = new();
            internal readonly Dictionary<(string Action, int Slot), string?> Overrides = new();
            internal readonly List<GameKeyWrite> Writes = new();
            internal readonly HashSet<(string Action, int Slot)> Missing = new();
            internal bool Available = true;
            internal string? SaveError;

            internal void Ship(string action, string? primary, string? secondary = null)
            {
                Defaults[(action, 0)] = primary;
                Defaults[(action, 1)] = secondary;
            }

            internal string? Effective(string action, int slot) =>
                Overrides.TryGetValue((action, slot), out var o) ? o : Defaults.TryGetValue((action, slot), out var d) ? d : null;

            internal GameKeybindSnapshot Read()
            {
                if (!Available) return GameKeybindSnapshot.Unavailable(KeybindArbiter.GameUnavailableNote);
                var claims = new List<KeybindClaim>();
                foreach (var action in Defaults.Keys.Select(k => k.Action).Distinct())
                {
                    Assert.True(GameKeyRemap.TryEntry(action, out var entry), action);
                    for (int slot = 0; slot < 2; slot++)
                    {
                        string? path = Effective(action, slot);
                        Defaults.TryGetValue((action, slot), out var shipped);
                        claims.Add(GameKeybindCatalog.Claim(entry, entry.FallbackName.Length > 0 ? entry.FallbackName : action, slot,
                            null, string.IsNullOrEmpty(path) ? null : path, "", "", defaultKnown: true, null, shipped,
                            slotExists: !Missing.Contains((action, slot))));
                    }
                }

                return new GameKeybindSnapshot(true, claims, "");
            }

            internal GameKeyWriteResult Write(GameKeyWrite w)
            {
                Writes.Add(w);
                string action = w.Entry.Action;
                // As GameKeybindWriter: no keyboard binding for the slot, nothing written.
                if (Missing.Contains((action, w.Slot))) return new GameKeyWriteResult(false, GameKeyRemap.NoSlotReason);
                if (w.Reset) Overrides.Remove((action, w.Slot));
                else Overrides[(action, w.Slot)] = w.Path;
                if (SaveError != null) return new GameKeyWriteResult(true, GameKeyRemap.NotSavedStatus(SaveError), Effective(action, w.Slot), saved: false);
                return new GameKeyWriteResult(true, w.Reset ? GameKeyRemap.ResetStatus : GameKeyRemap.SavedStatus, Effective(action, w.Slot));
            }
        }

        private FakeGameKeys UseFakeGame()
        {
            var game = new FakeGameKeys();
            game.Ship("MoveUp", "<Keyboard>/w");
            game.Ship("MoveDown", "<Keyboard>/s");
            game.Ship("MoveLeft", "<Keyboard>/a");
            game.Ship("Interact", "<Keyboard>/f");
            game.Ship("WeaponSkill1", "<Keyboard>/q");
            game.Ship("AutoMove", "<Mouse>/forwardButton");
            game.Ship("Primary", "<Mouse>/leftButton");
            game.Ship("CliffJump", "<Keyboard>/leftShift");
            game.Ship("MapZoom", "<Mouse>/scroll/y");
            KeybindArbiter.GameSource = game.Read;
            GameKeyRemap.Writer = game.Write;
            return game;
        }

        private void Capture(KeyChord chord)
        {
            _keys.Next = chord;
            _keys.Frame();
        }

        private static string EditStatus() => Tab().GetProperty("edit").GetProperty("status").GetString() ?? "";

        private static KeybindsRowModel GameRow(string action) =>
            Pane().Model!.Sections.SelectMany(s => s.Rows).Single(r => r.IsGame && r.Claim.ActionId == action);

        // ---- the cause: nothing could change a game key --------------------------------------------------

        [Fact]
        public void Without_a_writer_a_game_cap_still_only_explains_and_starts_no_capture()
        {
            ResetArt();
            var game = UseFakeGame();
            GameKeyRemap.Writer = null;
            OpenKeybindsTab();
            string row = GameRowId("Interact");
            Assert.False(GameRow("Interact").Editable);
            Pane().PressPrimary(row);
            Assert.False(Keybinds.Capturing);
            Assert.Equal("", Pane().Editing);
            Assert.Equal(KeybindsTabLayout.GameCapNote, Pane().LastStatus);
            Assert.Empty(game.Writes);
        }

        // ---- case 2: a plain single key takes effect -----------------------------------------------------

        [Fact]
        public void A_plain_key_on_a_movement_direction_is_written_to_its_composite_part_and_read_back()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            string up = GameRowId("MoveUp");
            Assert.True(GameRow("MoveUp").Editable);
            Assert.Equal("W", GameRow("MoveUp").PrimaryText);

            Pane().PressPrimary(up);
            Assert.True(Keybinds.Capturing);
            Assert.Equal(up, Pane().Editing);
            Assert.Equal(0, Pane().EditingSlot);
            Capture(new KeyChord(UiKey.I));
            Assert.Equal("I", Tab().GetProperty("edit").GetProperty("pending").GetString());
            Assert.StartsWith("I is free. Save to use it for Move Up", EditStatus());
            Pane().SaveEdit();

            // The write names the analog axis and its positive half, so the writer overrides that composite part only.
            var w = Assert.Single(game.Writes);
            Assert.Equal("MoveY", w.Entry.Analog);
            Assert.True(w.Entry.Positive);
            Assert.Equal(0, w.Slot);
            Assert.Equal("<Keyboard>/i", w.Path);
            Assert.Contains(_log.Infos, l => l.Contains("keybinds game MoveUp#1 -> I: saved (" + GameKeyRemap.SavedStatus + ")"));

            // The game's keys are read again: the row shows I, reads changed, and W is free for the game's other rows.
            Assert.Equal("I", GameRow("MoveUp").PrimaryText);
            Assert.True(GameRow("MoveUp").Changed);
            Assert.True(GameRow("MoveUp").CanReset);
            Assert.Equal("The game's default is W.", GameRow("MoveUp").GameDefaultSentence);
            Assert.Equal("<Keyboard>/s", game.Effective("MoveDown", 0));

            // Reset puts the game's default back (both slots go back to the shipped bindings).
            Pane().ResetRow(up);
            Assert.Equal(new[] { 0, 1 }, game.Writes.Skip(1).Select(x => x.Slot).ToArray());
            Assert.All(game.Writes.Skip(1), x => Assert.True(x.Reset));
            Assert.Equal("W", GameRow("MoveUp").PrimaryText);
            Assert.False(GameRow("MoveUp").Changed);
        }

        [Fact]
        public void The_second_slot_is_its_own_edit_and_the_bridge_names_it_with_hash_2()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            string interact = GameRowId("Interact");
            Pane().PressSecondary(interact);
            Assert.Equal(1, Pane().EditingSlot);
            Assert.Equal(KeybindsTabLayout.CapturingText, RowOf(interact).Cells().Single(c => c.Key == KeybindsPane.CellKey2).Text);
            Assert.Equal("F", RowOf(interact).Cells().Single(c => c.Key == KeybindsPane.CellKey).Text);
            Capture(new KeyChord(UiKey.G));
            Pane().SaveEdit();
            Assert.Equal(1, Assert.Single(game.Writes).Slot);
            Assert.Equal("<Keyboard>/g", game.Effective("Interact", 1));
            Assert.Equal("<Keyboard>/f", game.Effective("Interact", 0));

            // The bridge: keys op=edit on the secondary claim's key starts the second slot.
            Assert.True(Pane().BeginEdit(interact + "#2"));
            Assert.Equal(1, Pane().EditingSlot);
            Pane().CancelEdit();
        }

        // ---- case 1: modifier chords and mouse buttons ---------------------------------------------------

        [Fact]
        public void A_chord_with_a_modifier_is_refused_with_a_reason_and_the_capture_keeps_waiting()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            Pane().PressPrimary(GameRowId("WeaponSkill1"));
            Capture(new KeyChord(UiKey.X, KeyModifiers.Shift));
            Assert.True(Keybinds.Capturing);
            Assert.Null(Pane().Pending);
            Assert.StartsWith(GameKeyRemap.ModifierReason(new KeyChord(UiKey.X, KeyModifiers.Shift)), EditStatus());
            Assert.EndsWith(KeybindsTabLayout.StillWaitingSuffix, EditStatus());
            Assert.Equal("warn", Tab().GetProperty("description").GetProperty("warningTone").GetString());

            // The bridge's capture is refused the same way, and nothing reaches the game.
            Assert.True(Pane().CaptureForBridge(GameRowId("WeaponSkill1"), new KeyChord(UiKey.X, KeyModifiers.Ctrl)));
            Assert.Null(Pane().Pending);
            Assert.Empty(game.Writes);
            Pane().CancelEdit();
        }

        [Fact]
        public void Mouse_buttons_middle_back_forward_are_written_and_bare_left_right_are_refused()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            string skill = GameRowId("WeaponSkill1");

            Pane().PressPrimary(skill);
            // A bare left or right click is never a key (KEYS8): the capture does not take it, and it keeps waiting.
            Capture(new KeyChord(UiKey.MouseLeft));
            Assert.Null(Pane().Pending);
            Assert.True(Keybinds.Capturing);
            Capture(new KeyChord(UiKey.MouseRight));
            Assert.Null(Pane().Pending);
            Assert.True(Keybinds.Capturing);
            // The guard refuses them too, for the bridge's capture.
            Assert.NotNull(KeybindsPane.RefusalFor(GameRow("WeaponSkill1").Claim, new KeyChord(UiKey.MouseLeft), reset: false));

            // The forward button belongs to Auto Run: Save would take it, naming it.
            Capture(new KeyChord(UiKey.MouseForward));
            Assert.NotNull(Pane().Pending);
            Assert.StartsWith("Mouse Forward is used by the game (", EditStatus());
            Assert.Contains(". Save moves it to ", EditStatus());
            Pane().CancelEdit();
            Pane().PressPrimary(skill);

            Capture(new KeyChord(UiKey.MouseMiddle));
            Assert.NotNull(Pane().Pending);
            Pane().SaveEdit();
            Assert.Equal("<Mouse>/middleButton", Assert.Single(game.Writes).Path);
            Assert.Equal(new KeyChord(UiKey.MouseMiddle), GameRow("WeaponSkill1").Claim.Chord);
        }

        [Fact]
        public void A_key_another_game_action_or_a_mod_has_is_taken_on_save()
        {
            ResetArt();
            var game = UseFakeGame();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            Pane().PressPrimary(GameRowId("MoveUp"));
            // Requested 2026-10-10: a used key is taken, not refused: the preview names what loses it, Cancel keeps all.
            Capture(new KeyChord(UiKey.F));
            Assert.Equal(new KeyChord(UiKey.F), Pane().Pending);
            Assert.Equal("F is used by the game (Interact). Save moves it to Move Up; Interact will have no key.", EditStatus());
            Pane().CancelEdit();
            Pane().PressPrimary(GameRowId("MoveUp"));
            Capture(F9);
            Assert.Equal(F9, Pane().Pending);
            Assert.StartsWith("F9 is used by a.mod (Open). Save moves it to Move Up; Open will have no key.", EditStatus());
            Pane().CancelEdit();
            Pane().PressPrimary(GameRowId("MoveUp"));
            // The key it already has is a no-op preview, never a write.
            Capture(new KeyChord(UiKey.W));
            Assert.Equal("W is already this key.", EditStatus());
            Pane().CancelEdit();
            Assert.Empty(game.Writes);

            // Save takes F9: the mod's bind is unbound first, then the game key is written.
            Pane().PressPrimary(GameRowId("MoveUp"));
            Capture(F9);
            Pane().SaveEdit();
            Assert.False(Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord.IsBound);
            Assert.Equal("<Keyboard>/f9", game.Effective("MoveUp", 0));
            Assert.Equal("Took F9 from a.mod (Open), which has no key now.", Pane().Notes[GameRowId("MoveUp")]);
        }

        [Fact]
        public void A_key_back_on_a_default_the_game_shares_takes_nothing_but_another_key_on_it_takes_all()
        {
            ResetArt();
            var game = UseFakeGame();
            game.Ship("TravelSkill", "<Keyboard>/space");
            game.Ship("DropItem", "<Keyboard>/space");
            OpenKeybindsTab();
            var space = new KeyChord(UiKey.Space);
            Pane().PressPrimary(GameRowId("DropItem"));
            Capture(new KeyChord(UiKey.K));
            Pane().SaveEdit();

            // Back to its own default, which the game ships shared with Travel Skill: a warning, nothing taken.
            Pane().PressPrimary(GameRowId("DropItem"));
            Capture(space);
            Assert.Empty(Pane().TakeFromClaims);
            Assert.StartsWith("Space is also used by the game (", EditStatus());
            Pane().SaveEdit();
            Assert.Equal("<Keyboard>/space", game.Effective("TravelSkill", 0));
            Assert.Equal("<Keyboard>/space", game.Effective("DropItem", 0));

            // Another action on Space takes it from both.
            Pane().PressPrimary(GameRowId("Interact"));
            Capture(space);
            Assert.Equal(2, Pane().TakeFromClaims.Count);
            Pane().SaveEdit();
            Assert.Equal("", game.Effective("TravelSkill", 0));
            Assert.Equal("", game.Effective("DropItem", 0));
            Assert.Equal("<Keyboard>/space", game.Effective("Interact", 0));
        }

        [Fact]
        public void The_reset_arrow_is_live_during_a_change_and_drops_it()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            string left = GameRowId("MoveLeft");
            Assert.Equal(CellTone.Disabled, RowOf(left).Cells().Single(c => c.Key == KeybindsPane.CellReset).Tone);
            Pane().PressPrimary(left);
            Capture(new KeyChord(UiKey.J));
            Assert.Equal(CellTone.Normal, RowOf(left).Cells().Single(c => c.Key == KeybindsPane.CellReset).Tone);
            Pane().HoverCell(left, KeybindsPane.CellReset, entered: true);
            Assert.Equal(KeybindsTabLayout.HintCancelToDefault("A"), Tab().GetProperty("description").GetProperty("hint").GetString());
            Pane().ResetRow(left);
            Assert.Equal("", Pane().Editing);
            Assert.Null(Pane().Pending);
            Assert.Empty(game.Writes);
            Assert.Equal("A", GameRow("MoveLeft").PrimaryText);
        }

        [Fact]
        public void A_moved_key_can_go_back_to_its_default_even_where_a_screen_only_game_key_shares_it()
        {
            ResetArt();
            var game = UseFakeGame();
            game.Ship("MoveCameraForward", "<Keyboard>/w");
            OpenKeybindsTab();
            Pane().PressPrimary(GameRowId("MoveUp"));
            Capture(new KeyChord(UiKey.I));
            Pane().SaveEdit();
            Pane().PressPrimary(GameRowId("MoveUp"));
            Capture(new KeyChord(UiKey.W));
            Assert.Equal(new KeyChord(UiKey.W), Pane().Pending);
            Pane().SaveEdit();
            Assert.Equal("<Keyboard>/w", game.Effective("MoveUp", 0));
            Assert.Equal("W", GameRow("MoveUp").PrimaryText);
        }

        // ---- review follow-ups -------------------------------------------------------------------------------

        [Fact]
        public void A_key_on_the_other_direction_of_an_axis_is_taken_so_the_axis_never_cancels()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            Pane().PressPrimary(GameRowId("MoveUp"));
            Capture(new KeyChord(UiKey.I));
            Pane().SaveEdit();

            // Requested 2026-10-10: Move Down on I takes it from Move Up, so one key never drives both ways.
            var i = new KeyChord(UiKey.I);
            Pane().PressPrimary(GameRowId("MoveDown"));
            Capture(i);
            Assert.Equal(i, Pane().Pending);
            Assert.Equal("I is used by the game (Move Up). Save moves it to Move Down; Move Up will have no key.", EditStatus());
            Pane().SaveEdit();
            Assert.Equal("<Keyboard>/i", game.Effective("MoveDown", 0));
            Assert.Equal("", GameRow("MoveUp").PrimaryText);
            Assert.Equal("I", GameRow("MoveDown").PrimaryText);
            Assert.Equal("Took I from the game (Move Up), which has no key now.", Pane().Notes[GameRowId("MoveDown")]);
            Pane().Select(GameRowId("MoveUp"));
            Assert.Contains("I moved to Move Down; this action has no key now.", Tab().GetProperty("description").GetProperty("info").GetString());
        }

        [Fact]
        public void Reset_writes_both_slots_and_skips_a_second_slot_the_game_does_not_have()
        {
            ResetArt();
            var game = UseFakeGame();
            game.Ship("TravelSkill", "<Keyboard>/space");
            game.Missing.Add(("TravelSkill", 1));
            OpenKeybindsTab();

            // Both slots changed, both reset.
            Pane().PressPrimary(GameRowId("Interact"));
            Capture(new KeyChord(UiKey.G));
            Pane().SaveEdit();
            Pane().PressSecondary(GameRowId("Interact"));
            Capture(new KeyChord(UiKey.H));
            Pane().SaveEdit();
            game.Writes.Clear();
            Pane().ResetRow(GameRowId("Interact"));
            Assert.Equal(new[] { 0, 1 }, game.Writes.Select(w => w.Slot).ToArray());
            Assert.Equal("F", GameRow("Interact").PrimaryText);
            Assert.Equal("", GameRow("Interact").SecondaryText);
            Assert.Contains(_log.Infos, l => l.Contains("keybinds game Interact#1 -> default: saved (" + GameKeyRemap.ResetStatus + ")"));

            // No second keyboard binding: the second cap explains, no capture starts, and Reset writes only the first slot.
            string travel = GameRowId("TravelSkill");
            Assert.Equal(CellTone.Disabled, GameRow("TravelSkill").SecondaryTone);
            Pane().PressSecondary(travel);
            Assert.False(Keybinds.Capturing);
            Assert.Equal(GameKeyRemap.NoSlotReason, Pane().LastStatus);
            Assert.False(Pane().BeginEdit(travel + "#2"));
            Pane().PressPrimary(travel);
            Capture(new KeyChord(UiKey.V));
            Pane().SaveEdit();
            game.Writes.Clear();
            Pane().ResetRow(GameRowId("TravelSkill"));
            var only = Assert.Single(game.Writes);
            Assert.Equal(0, only.Slot);
            Assert.True(only.Reset);
            Assert.Contains(_log.Infos, l => l.Contains("keybinds game TravelSkill#1 -> default: saved (" + GameKeyRemap.ResetStatus + ")"));
            Assert.Equal("Space", GameRow("TravelSkill").PrimaryText);
        }

        [Fact]
        public void A_writer_that_throws_never_reports_the_key_as_unchanged_and_a_failed_save_says_applied()
        {
            ResetArt();
            var game = UseFakeGame();
            Assert.True(GameKeyRemap.TryEntry("Interact", out _));
            var claim = GameRow0("Interact", game);

            GameKeyRemap.Writer = w => { game.Write(w); throw new InvalidOperationException("save exploded"); };
            var thrown = GameKeyRemap.Store(claim, 0, new KeyChord(UiKey.G), reset: false);
            Assert.False(thrown.Ok);
            Assert.Equal(GameKeyRemap.WriteErrorStatus("save exploded"), thrown.Status);
            Assert.DoesNotContain("could not be changed", thrown.Status);
            Assert.Contains(_log.Warnings, l => l.Contains("save exploded"));
            // The game's keys are read again, so the row shows what the game has (G was applied before the throw).
            Assert.Equal("<Keyboard>/g", game.Effective("Interact", 0));
            Assert.Equal(new KeyChord(UiKey.G), KeybindArbiter.GameSnapshot().Claims.Single(c => c.ActionId == "Interact" && c.Slot == 0).Chord);

            // A save failure after the key is applied: ok, applied, not saved.
            GameKeyRemap.Writer = game.Write;
            game.SaveError = "disk full";
            var notSaved = GameKeyRemap.Store(claim, 0, new KeyChord(UiKey.J), reset: false);
            Assert.True(notSaved.Ok);
            Assert.False(notSaved.Saved);
            Assert.StartsWith("Applied for this session, but the game could not save its controls (disk full).", notSaved.Status);
        }

        private static KeybindClaim GameRow0(string action, FakeGameKeys game) =>
            game.Read().Claims.Single(c => c.ActionId == action && c.Slot == 0);

        // ---- which rows, and when -------------------------------------------------------------------------

        [Fact]
        public void Only_the_Bindings_Menu_rows_are_editable_and_only_in_a_world()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            Assert.True(GameRow("Interact").Editable);
            Assert.True(GameRow("CliffJump").Editable);
            // A screen-only map key is not on the game's Bindings Menu: read-only, with the reason.
            Assert.False(GameRow("MapZoom").Editable);
            Pane().PressPrimary(GameRowId("MapZoom"));
            Assert.False(Keybinds.Capturing);
            Assert.Equal(GameKeyRemap.OnlyInGameMenuReason, Pane().LastStatus);
            ModsMenu.Close();

            // At the title screen the game's keys come from the last read and cannot be written.
            game.Available = false;
            KeybindArbiter.Invalidate(game: true);
            OpenKeybindsTab();
            string interact = GameRowId("Interact");
            Pane().PressPrimary(interact);
            Assert.False(Keybinds.Capturing);
            Assert.Equal(GameKeyRemap.OutsideWorldReason, Pane().LastStatus);
            Assert.False(Pane().BeginEdit(interact));
            Assert.Empty(game.Writes);
        }

        [Fact]
        public void Unbind_clears_the_primary_slot_with_an_empty_path_as_the_games_Clear_does()
        {
            ResetArt();
            var game = UseFakeGame();
            OpenKeybindsTab();
            string interact = GameRowId("Interact");
            Assert.True(GameRow("Interact").CanClear);
            Pane().ClearRow(interact);
            var w = Assert.Single(game.Writes);
            Assert.Equal(0, w.Slot);
            Assert.Equal("", w.Path);
            Assert.Equal("", GameRow("Interact").PrimaryText);
            Assert.True(GameRow("Interact").Changed);
        }

        // ---- the path mapping -------------------------------------------------------------------------------

        [Fact]
        public void Every_capturable_key_maps_to_a_control_path_the_reader_reads_back_as_the_same_chord()
        {
            foreach (UiKey key in Enum.GetValues(typeof(UiKey)))
            {
                if (key == UiKey.None || key is UiKey.MouseLeft or UiKey.MouseRight) continue;
                // PAD1: a controller button is never a game keyboard slot.
                if (KeyChord.IsGamepadKey(key))
                {
                    Assert.False(GameKeyRemap.TryPath(new KeyChord(key), out _), key.ToString());
                    Assert.Equal(GameKeyRemap.GamepadReason, GameKeyRemap.Refusal(KeyChord.Gamepad(key)));
                    continue;
                }
                var chord = new KeyChord(key);
                Assert.True(GameKeyRemap.TryPath(chord, out string path), key.ToString());
                Assert.True(GameKeybindCatalog.TryChord(null, path, out var back), key + " -> " + path);
                Assert.Equal(chord, back);
            }

            Assert.True(GameKeyRemap.TryPath(new KeyChord(UiKey.Digit1), out var one));
            Assert.Equal("<Keyboard>/1", one);
            Assert.True(GameKeyRemap.TryPath(new KeyChord(UiKey.LeftBracket), out var bracket));
            Assert.Equal("<Keyboard>/leftBracket", bracket);
            Assert.True(GameKeyRemap.TryPath(new KeyChord(UiKey.MouseBack), out var back5));
            Assert.Equal("<Mouse>/backButton", back5);
            Assert.False(GameKeyRemap.TryPath(new KeyChord(UiKey.F, KeyModifiers.Alt), out _));
            Assert.NotNull(GameKeyRemap.Refusal(new KeyChord(UiKey.F, KeyModifiers.Shift)));
            Assert.Null(GameKeyRemap.Refusal(new KeyChord(UiKey.F)));
        }

        [Fact]
        public void A_game_row_reads_its_default_and_changed_state_from_the_paths()
        {
            Assert.True(GameKeyRemap.TryEntry("Interact", out var entry));
            var same = GameKeybindCatalog.Claim(entry, "Interact", 0, null, "<Keyboard>/f", "F", "", true, null, "<Keyboard>/F");
            Assert.False(same.Modified);
            Assert.Equal("F", same.DefaultText);
            var moved = GameKeybindCatalog.Claim(entry, "Interact", 0, null, "<Keyboard>/g", "G", "", true, null, "<Keyboard>/f");
            Assert.True(moved.Modified);
            var unknown = GameKeybindCatalog.Claim(entry, "Interact", 0, null, "<Keyboard>/g", "G");
            Assert.False(unknown.Modified);
            Assert.Equal("", unknown.DefaultText);
        }
    }
}
