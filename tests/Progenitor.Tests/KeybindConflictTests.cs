using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS4: the key-conflict dialog when a mod that loads wants a key something else has; the game's
    /// movement keys in the game-keys model (bug found live 2026-09-30: W was offered as free); and
    /// <c>keys op=capture</c> without a chord (bug found live 2026-09-30: it unbound the key). Game-free,
    /// on <see cref="KeybindTabTests"/>' fakes. Nothing here has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private const string SampleId = "progenitor.sample";
        private const string CompanionId = "progenitor.samplecompanion";
        private readonly object _sampleOwner = new();
        private readonly object _companionOwner = new();
        private ConfigFile? _sampleFile;

        private static readonly KeyChord W = new(UiKey.W);

        private void UseNames() =>
            KeybindArbiter.NameOf = id => id == SampleId ? "Progenitor Sample" : id == CompanionId ? "Progenitor Sample Companion" : id;

        /// <summary>The Sample (installed first) holds F8 for NetPing; the companion is the newcomer.</summary>
        private ConfigFile SampleHoldsF8()
        {
            UseNames();
            KeybindArbiter.InstallTimeOf = id => id == SampleId ? Ledger(9) : Ledger(1);
            _sampleFile = File("sample");
            Keybinds.Register(_sampleOwner, SampleId, "NetPing", _sampleFile, F8, "Ping the server.", () => { });
            _keys.Frame();
            return File("companion");
        }

        private void RegisterCompanion(ConfigFile file) =>
            Keybinds.Register(_companionOwner, CompanionId, "CompanionPing", file, F8, "Log a line.", () => { });

        private static KeyChord ChordOf(string plugin, string id) =>
            Keybinds.Snapshot().Single(b => b.PluginId == plugin && b.Id == id).Chord;

        private static UiDialog Shown() => UiDialog.Current ?? throw new InvalidOperationException("no dialog on screen");

        // ---- the dialog --------------------------------------------------------------------------

        [Fact]
        public void A_newcomer_that_loses_a_key_gets_one_dialog_naming_the_clash_and_the_choices()
        {
            var companion = SampleHoldsF8();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(companion);
            Assert.Null(UiDialog.Current); // nothing is shown inside the registration itself
            _keys.Frame();

            var dialog = Shown();
            Assert.Equal(KeybindConflictPrompt.DialogKind, dialog.Kind);
            Assert.Equal(CompanionId, dialog.Key);
            Assert.Equal("Key conflict: Progenitor Sample Companion", dialog.Content.Title);
            Assert.Equal("Companion ping wants F8, used by Progenitor Sample (Net ping). Companion ping is not active until you choose.",
                dialog.Content.Body);
            Assert.Equal(new[]
            {
                "Give F8: Progenitor Sample's Net ping becomes unbound.",
                "Leave unbound: Companion ping is unbound until you pick a key in Mods > Keybinds.",
                KeybindConflictPrompt.LaterLine,
            }, dialog.Content.Lines);
            Assert.Equal(new[] { "Choose new keys", "Give F8 to Progenitor Sample Companion", "Leave unbound" }, dialog.Content.Options);
            Assert.Equal("Later", dialog.Content.CancelCaption);
            Assert.Equal(UiDialogShape.Choose, dialog.Content.Shape);

            // The winner rule already decided: the newcomer is not active while the dialog waits.
            Assert.False(KeybindArbiter.Current().FindProgenitor(CompanionId, "CompanionPing")!.Active);

            // Readbacks: UiDialog.Describe carries the summary; keys op=conflict the whole dialog.
            var summary = (Dictionary<string, object?>)UiDialog.Describe()["keyConflict"]!;
            Assert.Equal(true, summary["open"]);
            Assert.Equal(CompanionId, summary["plugin"]);
            var read = (Dictionary<string, object?>)ModsMenu.KeysOp("conflict", null, null);
            var d = (Dictionary<string, object?>)read["dialog"]!;
            Assert.Equal("Key conflict: Progenitor Sample Companion", d["title"]);
            var clash = (Dictionary<string, object?>)((List<object?>)d["clashes"]!).Single()!;
            Assert.Equal("progenitor:" + CompanionId + "/CompanionPing", clash["row"]);
            Assert.Equal(true, clash["givable"]);
        }

        [Fact]
        public void Give_moves_the_key_to_the_newcomer_and_unbinds_the_holder_with_a_note()
        {
            var companion = SampleHoldsF8();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(companion);
            _keys.Frame();

            ModsMenu.KeysOp("conflict-choose", "give", null);

            Assert.Null(UiDialog.Current);
            Assert.Equal(KeyChord.None, ChordOf(SampleId, "NetPing"));
            Assert.Equal(F8, ChordOf(CompanionId, "CompanionPing"));
            var picture = KeybindArbiter.Current();
            Assert.True(picture.FindProgenitor(CompanionId, "CompanionPing")!.Active);
            Assert.Equal(0, picture.ConflictCount);
            Assert.Equal("Unbound: F8 was given to Progenitor Sample Companion (Companion ping).",
                KeybindConflictPrompt.NoteOf("progenitor:" + SampleId + "/NetPing"));
            Assert.Equal("None", _sampleFile!.Bind(Keybinds.Section, "NetPing", "F8").Value);

            OpenKeybindsTab();
            var row = Pane().Model!.FindRow("progenitor:" + SampleId + "/NetPing")!;
            Assert.StartsWith("Unbound: F8 was given to Progenitor Sample Companion", row.HelpLine);
        }

        [Fact]
        public void Leave_unbinds_the_newcomer_in_its_cfg_and_a_reload_does_not_ask_again()
        {
            var companion = SampleHoldsF8();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(companion);
            _keys.Frame();
            Assert.True(KeybindConflictPrompt.Choose(KeybindConflictPrompt.ChoiceLeave));

            Assert.Equal(KeyChord.None, ChordOf(CompanionId, "CompanionPing"));
            Assert.Equal(F8, ChordOf(SampleId, "NetPing"));
            Assert.Equal("None", companion.Bind(Keybinds.Section, "CompanionPing", "F8").Value);

            // A hot reload of the companion: its binds go and come back from the same cfg.
            Keybinds.RemoveAll(_companionOwner);
            RegisterCompanion(companion);
            _keys.Frame();
            _keys.Frame();
            Assert.Null(UiDialog.Current);
            Assert.Empty((List<object?>)KeybindConflictPrompt.Describe()["waiting"]!);
        }

        [Fact]
        public void Escape_is_Later_nothing_is_written_and_the_same_generation_does_not_ask_again()
        {
            var companion = SampleHoldsF8();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(companion);
            _keys.Frame();
            Assert.NotNull(UiDialog.Current);

            Assert.True(UiDialog.NotifyEscape());

            Assert.Null(UiDialog.Current);
            Assert.Equal(F8, ChordOf(CompanionId, "CompanionPing"));
            Assert.Equal(F8, ChordOf(SampleId, "NetPing"));
            Assert.False(KeybindArbiter.Current().FindProgenitor(CompanionId, "CompanionPing")!.Active);
            Assert.Equal("later", ((Dictionary<string, object?>)KeybindConflictPrompt.Describe()["last"]!)["choice"]);

            Keybinds.RemoveAll(_companionOwner);
            RegisterCompanion(companion);
            _keys.Frame();
            _keys.Frame();
            Assert.Null(UiDialog.Current);

            // A new Core generation (the next start) has no "later": it asks again.
            KeybindConflictPrompt.Reset();
            Keybinds.RemoveAll(_companionOwner);
            RegisterCompanion(companion);
            _keys.Frame();
            Assert.Equal(KeybindConflictPrompt.DialogKind, Shown().Kind);
        }

        [Fact]
        public void It_waits_for_a_capture_another_dialog_or_a_screen_and_is_never_shown_over_them()
        {
            var companion = SampleHoldsF8();
            bool ready = false;
            KeybindConflictPrompt.ScreenReady = () => ready;
            RegisterCompanion(companion);
            _keys.Frame();
            Assert.Null(UiDialog.Current);
            Assert.Equal(new List<object?> { CompanionId }, KeybindConflictPrompt.Describe()["waiting"]);

            ready = true;
            Keybinds.BeginCapture(_ => { });
            _keys.Frame();
            Assert.Null(UiDialog.Current);
            Keybinds.CancelCapture();

            var other = UiDialog.OpenContent(t => UiApi.Panel(t, _owner, "other.mod"),
                new DialogContent { Shape = UiDialogShape.Notice, Title = "Other", MainCaption = "OK" }, "t", "notice", "k");
            _keys.Frame();
            Assert.Same(other, UiDialog.Current);
            other.Close();
            _keys.Frame();
            Assert.Equal(KeybindConflictPrompt.DialogKind, Shown().Kind);
        }

        [Fact]
        public void A_key_the_game_holds_offers_no_Give()
        {
            UseNames();
            UseGame(Game("Interact", F8));
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(File("companion"));
            _keys.Frame();
            var dialog = Shown();
            Assert.Equal("Companion ping wants F8, used by the game (Interact). Companion ping is not active until you choose.", dialog.Content.Body);
            Assert.Equal(new[] { "Choose new keys", "Leave unbound" }, dialog.Content.Options);
            Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("conflict-choose", "give", null));
            Assert.NotNull(UiDialog.Current);
        }

        [Fact]
        public void A_newcomer_that_takes_a_key_from_an_installed_mod_asks_about_the_mod_that_lost_it()
        {
            UseNames();
            // The companion was installed first: when it loads, the Sample's NetPing stops being active.
            KeybindArbiter.InstallTimeOf = id => id == CompanionId ? Ledger(9) : Ledger(1);
            Keybinds.Register(_sampleOwner, SampleId, "NetPing", File("sample"), F8, "Ping the server.", () => { });
            _keys.Frame();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(File("companion"));
            _keys.Frame();
            var dialog = Shown();
            Assert.Equal("Key conflict: Progenitor Sample", dialog.Content.Title);
            Assert.Equal(SampleId, dialog.Key);
            Assert.Contains("Give F8 to Progenitor Sample", dialog.Content.Options);
        }

        [Fact]
        public void Choose_new_keys_opens_the_Keybinds_tab_with_the_row_in_capture()
        {
            var companion = SampleHoldsF8();
            KeybindConflictPrompt.ScreenReady = () => true;
            RegisterCompanion(companion);
            _keys.Frame();
            Assert.False(ModsMenu.IsOpen);

            ModsMenu.KeysOp("conflict-choose", "edit", null);

            Assert.True(ModsMenu.IsOpen);
            Assert.Equal(ModsTab.Keybinds, ModsMenu.SelectedTabForTests);
            Assert.Equal("progenitor:" + CompanionId + "/CompanionPing", Pane().Editing);
            Assert.True(Pane().Capturing);
            Assert.True(Keybinds.Capturing);
            Assert.Null(KeybindConflictPrompt.Describe()["editRequest"]);
            // Nothing was written: the player picks the key.
            Assert.Equal(F8, ChordOf(CompanionId, "CompanionPing"));
        }

        [Fact]
        public void The_conflict_ops_work_with_the_workspace_closed_and_the_bridge_needs_no_open_workspace()
        {
            Assert.False(ModsMenu.IsOpen);
            var read = (Dictionary<string, object?>)ModsMenu.KeysOp("conflict", null, null);
            Assert.Equal(false, read["open"]);
            Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("conflict-choose", "leave", null));
            Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("conflict-choose", "bogus", null));
            Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("read", null, null));
            Assert.False(ModsUiArgs.RequiresOpenWorkspace("keys", "conflict"));
            Assert.False(ModsUiArgs.RequiresOpenWorkspace("keys", "conflict-choose"));
            Assert.True(ModsUiArgs.RequiresOpenWorkspace("keys", "read"));
            Assert.True(ModsUiArgs.RequiresOpenWorkspace("keys"));
        }

        // ---- the game's movement keys (bug 2) -------------------------------------------------------

        private static KeybindClaim[] Movement() =>
            GameKeybindCatalog.MovementDirections
                .Select(e => GameKeybindCatalog.Claim(e, "", 0, null, "<Keyboard>/" + DefaultKeyOf(e.Action), ""))
                .ToArray();

        private static string DefaultKeyOf(string action) => action switch
        {
            "MoveUp" => "w",
            "MoveLeft" => "a",
            "MoveDown" => "s",
            _ => "d",
        };

        [Fact]
        public void The_game_keys_model_lists_the_four_movement_directions_as_the_Controls_page_does()
        {
            var movement = GameKeybindCatalog.Listed.Where(e => e.Category == GameKeybindCatalog.Movement).Select(e => e.Action).ToArray();
            Assert.Equal(new[] { "MoveUp", "MoveLeft", "MoveDown", "MoveRight", "AutoMove", "Walk" }, movement);
            var up = GameKeybindCatalog.MovementDirections[0];
            Assert.Equal(("MoveY", true), (up.Analog, up.Positive));
            Assert.Equal(new[] { "Move Up", "Move Left", "Move Down", "Move Right" },
                GameKeybindCatalog.MovementDirections.Select(e => GameKeybindCatalog.FallbackName(e.Action)).ToArray());

            var claims = Movement();
            Assert.Equal(new[] { "W", "A", "S", "D" }, claims.Select(c => c.Chord.ToString()).ToArray());
            Assert.All(claims, c => Assert.True(c.Comparable && c.Mode.Length == 0 && KeybindArbiter.GameWins(c)));

            // The reader reads them: an analog entry goes through the direction path, never the button enum.
            string reader = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameKeybindReader.cs");
            SourcePin.AssertUncommented(reader, "ReadDirection(system, asset, entry, kbm, claims);");
            string direction = SourcePin.MethodBody(reader, "private static void ReadDirection");
            SourcePin.AssertUncommented(direction, "system.GetBindingIndexInAction(action, slot == 0, true, !entry.WholeAxis && entry.Positive, kbm)");
            SourcePin.AssertUncommented(direction, "asset.FindAction(entry.Analog, false)");
        }

        [Theory]
        [InlineData(UiKey.W, "Move Up")]
        [InlineData(UiKey.A, "Move Left")]
        [InlineData(UiKey.S, "Move Down")]
        [InlineData(UiKey.D, "Move Right")]
        public void W_A_S_and_D_are_game_keys_and_a_remap_to_them_is_refused(UiKey key, string name)
        {
            UseGame(Movement());
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            var chord = new KeyChord(key);

            Assert.False(Keybinds.TryRebind("a.mod", "Open", chord, Keybinds.RebindCheck.Full, out string? why));
            Assert.Equal(key + " is used by the game (" + name + "). Pick another key.", why);
            Assert.Equal(F9, ChordOf("a.mod", "Open"));

            // The Keybinds tab's capture refuses it too, and keeps waiting.
            OpenKeybindsTab();
            ModsMenu.KeysOp("capture", "progenitor:a.mod/Open", key.ToString());
            var edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Null(edit["pending"]);
            Assert.StartsWith(key + " is used by the game (" + name + ")", (string)edit["status"]!);

            // A mod whose default is W loses it to the game.
            Keybinds.Register(_owner, "b.mod", "Go", File("b"), chord, "", () => { });
            Assert.False(KeybindArbiter.Current().FindProgenitor("b.mod", "Go")!.Active);
        }

        [Fact]
        public void The_title_screen_snapshot_file_keeps_the_movement_keys_and_still_refuses_W()
        {
            string dir = Path.Combine(_dir, "cache");
            KeybindGameCache.Directory = () => dir;
            KeybindGameCache.Remember(new GameKeybindSnapshot(true, Movement(), ""));
            string text = System.IO.File.ReadAllText(Path.Combine(dir, KeybindGameCache.FileName));
            Assert.Contains("MoveUp\tMove Up\tW\t", text);

            // The next start at the title screen: nothing in memory, the file is read.
            KeybindGameCache.ResetForTests();
            KeybindGameCache.Directory = () => dir;
            var cached = KeybindGameCache.Claims()!.Value.Claims;
            Assert.Equal(new[] { "MoveUp", "MoveLeft", "MoveDown", "MoveRight" }, cached.Select(c => c.ActionId).ToArray());
            Assert.Equal(W, cached[0].Chord);

            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            Assert.False(Keybinds.TryRebind("a.mod", "Open", W, Keybinds.RebindCheck.Full, out string? why));
            Assert.StartsWith("W is used by the game (Move Up), as last read in a world today.", why);
        }

        // ---- keys op=capture without a chord (bug 3) ---------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("None")]
        public void Capture_without_a_key_is_an_error_and_a_following_save_does_not_unbind(string? chord)
        {
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            OpenKeybindsTab();
            ModsMenu.KeysOp("edit", "progenitor:a.mod/Open", null);

            var e = Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("capture", "progenitor:a.mod/Open", chord));
            Assert.Contains("unbind", e.Message);
            Assert.Null(((Dictionary<string, object?>)Pane().Describe()["edit"]!)["pending"]);

            ModsMenu.KeysOp("save", null, null);
            Assert.Equal(F9, ChordOf("a.mod", "Open"));
            Assert.Equal(F7, ModsMenu.CaptureChordOrThrow("F7"));
        }

        // ---- modifiers alone (KEYS4 B) --------------------------------------------------------------

        [Theory]
        [InlineData("Shift")]
        [InlineData("LeftShift")]
        [InlineData("Ctrl + Alt")]
        public void Capturing_modifiers_alone_is_refused_not_turned_into_an_unbind(string chord)
        {
            Assert.True(KeyChord.IsModifiersOnly(chord, out var mods) && mods != KeyModifiers.None);
            Assert.False(KeyChord.IsModifiersOnly("F9 + Shift", out _));
            Assert.False(KeyChord.IsModifiersOnly("None", out _));

            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            OpenKeybindsTab();
            var e = Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("capture", "progenitor:a.mod/Open", chord));
            Assert.Contains("only modifiers", e.Message);

            // Whatever delivers a keyless capture to the pane (not only the bridge): refused, still capturing.
            Pane().BeginEdit("progenitor:a.mod/Open");
            Pane().OnCaptured(new KeyCaptureResult(RebindOutcome.Bound, KeyChord.None, null));
            var edit = (Dictionary<string, object?>)Pane().Describe()["edit"]!;
            Assert.Null(edit["pending"]);
            Assert.Equal(true, edit["capturing"]);
            Assert.StartsWith(KeybindsTabLayout.ModifiersOnlyReason, (string)edit["status"]!);
            ModsMenu.KeysOp("save", null, null);
            Assert.Equal(F9, ChordOf("a.mod", "Open"));
        }

        // ---- every game key the game can act on (KEYS4 A) ---------------------------------------------

        /// <summary>
        /// The keyboard-and-mouse actions of the live sweep (2026-09-30, game-input-asset.json) that the
        /// Controls page does not list. Each is in the catalog, except ActionBar9 (no ButtonInputAction).
        /// </summary>
        [Fact]
        public void Every_keyboard_and_mouse_action_of_the_live_sweep_is_listed_except_ActionBar9()
        {
            var sweep = new[]
            {
                "MoveX", "MoveY", "Back", "Enter", "TrackItem", "MapPan", "MapMarker", "SelectProgression", "MapZoom",
                "MapCenterPlayer", "MapToggleLegend", "CycleChat", "ZoomCamera", "MoveCameraX", "MoveCameraY",
                "RotateCameraX", "RotateCameraY", "MoveCameraZ", "Ability4", "TransferItem", "CancelCraft", "UseItem",
                "EquipItem", "RemoveBuff", "MoveItem", "EditItem", "StartCraft", "TrackRecipe", "QuickRouteItems",
                "Building_Exit", "Building_Pickup", "DebugMenu", "DisableAutoSnapPoints", "DisableAutoSnapGrid",
            };
            var listed = new HashSet<string>(GameKeybindCatalog.Listed.Select(e => e.IsAnalog ? e.Analog : e.Action));
            Assert.Empty(sweep.Where(a => !listed.Contains(a)));
            Assert.DoesNotContain(GameKeybindCatalog.Listed, e => e.Action == "ActionBar9");
            Assert.Equal(GameKeybindCatalog.Listed.Count, GameKeybindCatalog.Listed.Select(e => e.Action).Distinct().Count());
            // The page's own groups keep their order; the extras come after them.
            Assert.Equal(new[] { GameKeybindCatalog.Other, GameKeybindCatalog.Debug }, GameKeybindCatalog.CategoryOrder.Skip(7).ToArray());
            Assert.All(GameKeybindCatalog.OtherKeys, e => Assert.Contains(e.Category, new[] { GameKeybindCatalog.Other, GameKeybindCatalog.Debug }));
        }

        [Fact]
        public void An_other_game_key_warns_and_is_refused_as_a_remap_a_debug_key_never_conflicts_and_mouse_axes_are_listed_only()
        {
            GameKeybindCatalog.Entry Of(string action) => GameKeybindCatalog.OtherKeys.Single(e => e.Action == action);
            var map = GameKeybindCatalog.Claim(Of("MapCenterPlayer"), "Center Map on Player", 0, null, "<Keyboard>/f7", "");
            var debug = GameKeybindCatalog.Claim(Of("DebugMenu"), "Debug Menu", 0, null, "<Keyboard>/f11", "");
            var zoom = GameKeybindCatalog.Claim(Of("ZoomCamera"), "", 0, null, "<Mouse>/scroll/y", "");
            Assert.Equal(GameKeybindCatalog.InTheMap, map.Mode);
            Assert.False(KeybindArbiter.GameWins(map));
            Assert.False(debug.Comparable);
            Assert.Equal("F11", debug.ChordText);
            Assert.False(zoom.Comparable);
            Assert.Equal("Scroll Wheel", zoom.ChordText);
            Assert.Equal("Zoom Camera", zoom.DisplayName);
            UseGame(map, debug, zoom);

            // A mod key on the map's key stays active with the warning; a remap onto it is refused.
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F7, "Opens it.", () => { });
            var v = KeybindArbiter.Current().FindProgenitor("a.mod", "Open")!;
            Assert.True(v.Active);
            Assert.StartsWith("Also used by the game in the map (Center Map on Player)", v.Reason);
            Keybinds.Register(_owner, "b.mod", "Go", File("b"), F9, "", () => { });
            Assert.False(Keybinds.TryRebind("b.mod", "Go", F7, Keybinds.RebindCheck.Full, out string? why));
            Assert.Equal("F7 is used by the game (Center Map on Player, in the map). Pick another key.", why);

            // The debug key's F11 is free for a mod.
            Assert.True(Keybinds.TryRebind("b.mod", "Go", new KeyChord(UiKey.F11), Keybinds.RebindCheck.Full, out _));
            Assert.True(KeybindArbiter.Current().FindProgenitor("b.mod", "Go")!.Active);

            OpenKeybindsTab();
            var debugRow = Pane().Model!.FindRow("game:game/DebugMenu")!;
            Assert.Equal(GameKeybindCatalog.Debug, debugRow.Category);
            Assert.Contains(KeybindsTabLayout.DebugKeyNote, KeybindsTabLayout.Describe(debugRow, "selected").Info);
        }
    }
}
