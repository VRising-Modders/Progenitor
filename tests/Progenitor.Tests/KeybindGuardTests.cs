using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS2 (requested 2026-09-29: "with keybinds make sure there are no conflicts / don't allow it to
    /// conflict with the OS"): the reserved-chord table and its enforcement at registration, cfg load,
    /// remap and cfg write; the Windows key during a capture; remaps that would clash with the game, a
    /// Progenitor bind or a cfg key; the same-chord no-op; Swap; reset-to-default refusals; the reserved-key
    /// problem; and the Conflicts header. Game-free, on the Keybinds tab's fixtures; nothing here has run in
    /// the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        /// <summary>Every row of the table, as (chord text, who).</summary>
        public static IEnumerable<object[]> ReservedRows() =>
            ReservedChords.Table.Select(r => new object[] { r.Chord.ToString(), r.By.ToString() });

        private static KeyChord Chord(string text)
        {
            Assert.True(KeyChord.TryParse(text, out var chord), text);
            return chord;
        }

        // ---- the table ------------------------------------------------------------------------

        [Fact]
        public void The_reserved_table_names_Windows_and_Steam_with_distinct_reasons()
        {
            Assert.Equal("Alt+F4 closes the window in Windows. Pick another key.", ReservedChords.Reason(new KeyChord(UiKey.F4, KeyModifiers.Alt)));
            Assert.Equal("Shift+Tab opens the Steam overlay. Pick another key.", ReservedChords.Reason(new KeyChord(UiKey.Tab, KeyModifiers.Shift)));
            Assert.Equal("F12 takes a Steam screenshot. Pick another key.", ReservedChords.Reason(new KeyChord(UiKey.F12)));
            Assert.Equal("Ctrl+Shift+Esc opens Task Manager in Windows. Pick another key.",
                ReservedChords.Reason(new KeyChord(UiKey.Escape, KeyModifiers.Ctrl | KeyModifiers.Shift)));
            Assert.Equal("Alt+Print Screen copies the active window in Windows. Pick another key.",
                ReservedChords.Reason(new KeyChord(UiKey.PrintScreen, KeyModifiers.Alt)));

            foreach (var row in ReservedChords.Table)
            {
                string reason = ReservedChords.Reason(row.Chord)!;
                if (row.By == ReservedBy.Windows)
                {
                    Assert.Contains("Windows", reason);
                    Assert.DoesNotContain("Steam", reason);
                }
                else
                {
                    Assert.Equal(ReservedBy.Steam, row.By);
                    Assert.Contains("Steam", reason);
                    Assert.DoesNotContain("Windows", reason);
                }
            }

            // The one whole-key rule: the Windows key with any modifiers.
            Assert.Equal(ReservedBy.Windows, ReservedChords.ReservedByOf(new KeyChord(UiKey.LeftMeta, KeyModifiers.Ctrl)));

            // Print Screen: only what Windows takes (plain, Alt, Ctrl, Alt+Shift for High Contrast).
            Assert.True(ReservedChords.IsReserved(new KeyChord(UiKey.PrintScreen, KeyModifiers.Ctrl)));
            Assert.Equal("Alt+Shift+Print Screen turns High Contrast on or off in Windows. Pick another key.",
                ReservedChords.Reason(new KeyChord(UiKey.PrintScreen, KeyModifiers.Alt | KeyModifiers.Shift)));
            Assert.False(ReservedChords.IsReserved(new KeyChord(UiKey.PrintScreen, KeyModifiers.Shift)));
            Assert.False(ReservedChords.IsReserved(new KeyChord(UiKey.PrintScreen, KeyModifiers.Ctrl | KeyModifiers.Shift)));

            // Mouse Keys (Left Alt+Left Shift+Num Lock; chords are side-agnostic, so either side).
            Assert.Equal("Alt+Shift+NumLock turns Mouse Keys on or off in Windows. Pick another key.",
                ReservedChords.Reason(new KeyChord(UiKey.NumLock, KeyModifiers.Alt | KeyModifiers.Shift)));

            // Near misses are free: only exact chords are reserved.
            foreach (var free in new[] { "F4", "F5 + Alt", "Tab", "F12 + Shift", "Escape", "Space", "Delete + Ctrl", "Tab + Ctrl", "NumLock" })
                Assert.False(ReservedChords.IsReserved(Chord(free)), free);
        }

        // ---- registration and cfg load ----------------------------------------------------------

        [Theory]
        [MemberData(nameof(ReservedRows))]
        public void A_reserved_default_is_registered_unbound_logged_once_and_raised_as_a_problem(string chordText, string by)
        {
            var chord = Chord(chordText);
            var handle = Keybinds.Register(_owner, "r.mod", "Go", File("r"), chord, "", () => { });
            Assert.False(handle.Chord.IsBound);

            string name = ReservedChords.NameOf(Enum.Parse<ReservedBy>(by));
            string line = ReservedChords.RegistrationLog("r.mod", chord, Enum.Parse<ReservedBy>(by));
            Assert.Equal("r.mod's key " + ReservedChords.Display(chord) + " is reserved by " + name + "; pick another key in the Keybinds tab.", line);
            Assert.Single(_log.Warnings, l => l.Contains(line));

            var problem = Assert.Single(ModProblems.For("r.mod"));
            Assert.Equal(ModProblemKinds.KeybindReserved, problem.Kind);
            Assert.Equal(ModProblemSeverity.Warning, problem.Severity);
            Assert.False(problem.IsConflict);
            Assert.Equal("Its default key " + ReservedChords.Display(chord) + " is reserved by " + name + ", so it is unbound.", problem.Sentence);
            Assert.Equal(ReservedChords.ProblemWhatToDo, problem.WhatToDo);
            Assert.True(problem.Dismissible);

            // The row says so too.
            OpenKeybindsTab();
            var row = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == "r.mod");
            Assert.StartsWith(problem.Sentence, row.HelpLine);
            Assert.Equal(name.ToLowerInvariant(), row.Reserved);
        }

        [Theory]
        [MemberData(nameof(ReservedRows))]
        public void A_reserved_cfg_value_falls_back_to_the_default_or_to_unbound(string chordText, string by)
        {
            var chord = Chord(chordText);
            string name = ReservedChords.NameOf(Enum.Parse<ReservedBy>(by));
            var file = File("c");
            var entry = file.Bind(Keybinds.Section, "Go", chord.ToString(), "");
            var handle = Keybinds.Register(_owner, "c.mod", "Go", file, F9, "", () => { });
            Assert.Equal(F9, handle.Chord);
            var problem = Assert.Single(ModProblems.For("c.mod"));
            Assert.Equal(ReservedChords.Display(chord) + " is reserved by " + name + "; your key was reset to F9.", problem.Sentence);

            // Written back once: the next load reads F9, logs nothing and raises nothing.
            Assert.Equal("F9", entry.Value);
            handle.Dispose();
            int warnings = _log.Warnings.Count;
            var again = Keybinds.Register(_owner, "c.mod", "Go", file, F9, "", () => { });
            Assert.Equal(F9, again.Chord);
            Assert.Empty(ModProblems.For("c.mod"));
            Assert.Equal(warnings, _log.Warnings.Count);

            // Both reserved: unbound, and the cfg says None.
            var file2 = File("c2");
            var entry2 = file2.Bind(Keybinds.Section, "Go", chord.ToString(), "");
            var altF4 = new KeyChord(UiKey.F4, KeyModifiers.Alt);
            var both = Keybinds.Register(_owner, "c2.mod", "Go", file2, chord == altF4 ? new KeyChord(UiKey.F12) : altF4, "", () => { });
            Assert.False(both.Chord.IsBound);
            Assert.EndsWith("; your key was unbound.", Assert.Single(ModProblems.For("c2.mod")).Sentence);
            Assert.Equal("None", entry2.Value);
        }

        [Fact]
        public void A_reserved_key_problem_can_be_dismissed()
        {
            var file = File("c");
            file.Bind(Keybinds.Section, "Go", "F12", "");
            Keybinds.Register(_owner, "c.mod", "Go", file, F9, "", () => { });
            var problem = Assert.Single(ModProblems.For("c.mod"));
            Assert.Equal("F12 is reserved by Steam; your key was reset to F9.", problem.Sentence);
            Assert.True(problem.Dismissible);
            Assert.True(problem.Readback()["dismissible"] is true);

            // The page line carries Dismiss.
            var specs = ModPagePane.ProblemRowSpecs(new[] { problem }, new[] { problem.Line }, false, false, 1200f);
            Assert.Contains(specs[0].Cells, c => c.Key == ModPagePane.CellDismiss);

            Assert.True(ModProblems.Dismiss(problem));
            Assert.Empty(ModProblems.For("c.mod"));
            Assert.Null(Keybinds.ReservedNoteOf("c.mod", "Go"));
            Assert.Equal(F9, Keybinds.Snapshot().Single().Chord);

            // A conflict is not dismissible.
            Assert.False(ModProblems.Dismiss(new ModProblem(ModProblemSeverity.Warning, ModProblemKinds.KeybindConflict, "x.mod", "s", "w")));
        }

        [Fact]
        public void An_ordinary_mods_reserved_key_is_logged_once_per_process()
        {
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Close", new KeyboardShortcut("F4 + LeftAlt"), "");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            for (int i = 0; i < 3; i++)
            {
                Assert.Single(ModProblems.For("plain.mod"), p => p.Kind == ModProblemKinds.KeybindReserved);
                KeybindArbiter.Invalidate();
            }

            Assert.Single(_log.Warnings, l => l.Contains("Ui: key setting plain.mod Hotkeys/Close") && l.Contains("Logged once per process"));
        }

        [Fact]
        public void The_reserved_problem_clears_when_the_player_picks_a_key()
        {
            Keybinds.Register(_owner, "r.mod", "Go", File("r"), new KeyChord(UiKey.F4, KeyModifiers.Alt), "", () => { });
            Assert.Single(ModProblems.For("r.mod"));
            Assert.Equal("Key Alt+F4 reserved by Windows", ModProblems.For("r.mod")[0].ShortLine);
            Assert.True(Keybinds.Rebind("r.mod", "Go", F7));
            Assert.Empty(ModProblems.For("r.mod"));
            Assert.Null(Keybinds.ReservedNoteOf("r.mod", "Go"));
        }

        // ---- remap and cfg write ------------------------------------------------------------------

        [Theory]
        [MemberData(nameof(ReservedRows))]
        public void A_reserved_chord_is_refused_by_rebind_and_by_our_cfg_writes(string chordText, string by)
        {
            var chord = Chord(chordText);
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            RebindResult? raised = null;
            Keybinds.RebindFinished += r => raised = r;
            Assert.False(Keybinds.TryRebind("a.mod", "Go", chord, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal(ReservedChords.Reason(chord), why);
            Assert.Equal(RebindOutcome.Refused, raised!.Value.Outcome);
            Assert.Equal(F9, Keybinds.Snapshot().Single().Chord);
            // Even the unchecked path never stores a reserved chord.
            Assert.False(Keybinds.Rebind("a.mod", "Go", chord, checkConflicts: false));
            Assert.Contains(by == "Steam" ? "Steam" : "Windows", why);

            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Open", new KeyboardShortcut("F9"), "");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            SettingsCatalog.AllowForeignEdits = true;
            var claim = SettingsCatalog.KeyClaims(sweep: false).Single();
            var result = SettingsCatalog.WriteKey(claim, chord);
            Assert.Equal(SettingWriteState.Refused, result.State);
            Assert.Equal(ReservedChords.Reason(chord), result.Error);
        }

        [Fact]
        public void A_reserved_key_is_refused_by_the_tab_and_the_capture_keeps_waiting()
        {
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            string rowId = pane.Model!.ModRows().Single().Id;
            Assert.True(pane.BeginEdit(rowId));
            _keys.Next = new KeyChord(UiKey.F4, KeyModifiers.Alt);
            _keys.Frame();
            Assert.True(Keybinds.Capturing);
            Assert.True(pane.Capturing);
            Assert.Null(pane.Pending);
            var edit = JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe())).RootElement.GetProperty("edit");
            Assert.Equal("Alt+F4 closes the window in Windows. Pick another key. Press another key, or Escape to cancel.",
                edit.GetProperty("status").GetString());

            // Another key is taken normally.
            _keys.Next = F7;
            _keys.Frame();
            Assert.Equal(F7, pane.Pending);
            pane.SaveEdit();
            Assert.Equal(F7, Keybinds.Snapshot().Single().Chord);

            // The bridge's capture gets the same answer (Steam this time).
            pane = Pane();
            Assert.True(pane.CaptureForBridge(rowId, new KeyChord(UiKey.Tab, KeyModifiers.Shift)));
            Assert.Null(pane.Pending);
            Assert.StartsWith("Shift+Tab opens the Steam overlay.", JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe()))
                .RootElement.GetProperty("edit").GetProperty("status").GetString());
            Assert.True(pane.Capturing);
        }

        // ---- the Windows key --------------------------------------------------------------------

        [Fact]
        public void The_Windows_key_during_a_capture_is_refused_and_never_fires_a_bind()
        {
            var keys = new UiKeybindTests.FakeKeyInput();
            Keybinds.SetSourceForTests(keys);
            int fired = 0;
            Keybinds.Register(_owner, "a.mod", "Demo", File("a"), F9, "", () => fired++);

            // The rebind flow (the settings panel, the mod page's Keys row): Win+G is refused, the flow waits.
            var results = new List<RebindResult>();
            var finished = new List<RebindResult>();
            Keybinds.RebindRefused += r => results.Add(r);
            Keybinds.RebindFinished += r => { results.Add(r); finished.Add(r); };
            Assert.True(Keybinds.BeginRebind("a.mod", "Demo"));
            keys.PressPhysical(UiKey.G, ModifierSides.LeftMeta);
            Keybinds.Pump();
            var refused = Assert.Single(results);
            Assert.Equal(RebindOutcome.Refused, refused.Outcome);
            Assert.True(refused.StillCapturing);
            Assert.Equal(ReservedChords.WindowsKeyHeldReason, refused.Reason);
            Assert.Equal("Demo", Keybinds.RebindingId);

            // The Windows key on its own (the source reports it as the main key) is refused as well.
            keys.PressPhysical(UiKey.LeftMeta, ModifierSides.LeftMeta);
            Keybinds.Pump();
            Assert.Equal(2, results.Count);
            Assert.True(results[1].StillCapturing);
            Assert.Equal("Demo", Keybinds.RebindingId);

            // Mid-flow refusals go to RebindRefused only: a pre-KEYS2 RebindFinished subscriber sees one result, at the end.
            Assert.Empty(finished);

            keys.NextChord = F7;
            Keybinds.Pump();
            Assert.Null(Keybinds.RebindingId);
            Assert.Equal(RebindOutcome.Bound, results[2].Outcome);
            Assert.Equal(RebindOutcome.Bound, Assert.Single(finished).Outcome);
            Assert.Equal(F7, Keybinds.Snapshot().Single().Chord);

            // The tab's capture: the same refusal, still waiting.
            KeyCaptureResult? got = null;
            Keybinds.BeginCapture(r => got = r);
            keys.PressPhysical(UiKey.F8, ModifierSides.RightMeta | ModifierSides.LeftCtrl);
            Keybinds.Pump();
            Assert.Equal(RebindOutcome.Refused, got!.Value.Outcome);
            Assert.True(got.Value.StillCapturing);
            Assert.True(Keybinds.Capturing);
            Keybinds.CancelCapture();

            // A bound key pressed with a Windows key held is Windows' shortcut: the bind does not fire.
            Assert.False(F7.MatchesHeld(ModifierSides.LeftMeta));
            keys.PressPhysical(UiKey.F7, ModifierSides.LeftMeta);
            Keybinds.Pump();
            Assert.Equal(0, fired);
            keys.PressPhysical(UiKey.F7, ModifierSides.None);
            Keybinds.Pump();
            Assert.Equal(1, fired);
        }

        [Fact]
        public void A_cfg_key_on_the_Windows_key_or_a_reserved_chord_warns_on_its_row_and_raises_a_problem()
        {
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Close", new KeyboardShortcut("F4 + LeftAlt"), "");
            cfg.Bind("Hotkeys", "Snap", new KeyboardShortcut("S + LeftWindows"), "");
            cfg.Bind("Hotkeys", "Overlay", new KeyboardShortcut("Tab + LeftShift"), "");
            cfg.Bind("Hotkeys", "Fine", new KeyboardShortcut("F6"), "");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            OpenKeybindsTab();
            var rows = Pane().Model!.ModRows().Where(r => r.Claim.OwnerId == "plain.mod").ToDictionary(r => r.Claim.ActionId);
            Assert.StartsWith(ReservedChords.WindowsRowWarning, rows["Hotkeys/Close"].HelpLine);
            Assert.StartsWith(ReservedChords.WindowsRowWarning, rows["Hotkeys/Snap"].HelpLine);
            Assert.False(rows["Hotkeys/Snap"].Claim.Comparable);
            Assert.StartsWith(ReservedChords.SteamRowWarning, rows["Hotkeys/Overlay"].HelpLine);
            Assert.Equal("steam", rows["Hotkeys/Overlay"].Reserved);
            Assert.Equal("", rows["Hotkeys/Fine"].ReservedText);

            var problems = ModProblems.For("plain.mod").Where(p => p.Kind == ModProblemKinds.KeybindReserved).ToList();
            Assert.Equal(3, problems.Count);
            Assert.Contains(problems, p => p.Sentence == "Its key Alt+F4 is reserved by Windows, so it may not reach the mod.");
            Assert.Contains(problems, p => p.Sentence == "Its key Shift+Tab is used by Steam, so it may not reach the mod.");
            Assert.Contains(problems, p => p.Sentence == "Its key S + LeftWindows is reserved by Windows, so it may not reach the mod.");
        }

        // ---- no conflicting remaps ----------------------------------------------------------------

        [Fact]
        public void A_remap_onto_a_game_key_is_refused_mode_only_ones_included()
        {
            UseGame(Game("Interact", new KeyChord(UiKey.E)),
                new KeybindClaim(KeybindSource.Game, KeybindArbiter.GameOwnerId, "Building_Place", "Place", F8,
                    category: GameKeybindCatalog.General, mode: "while building"));
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });

            Assert.False(Keybinds.TryRebind("a.mod", "Go", new KeyChord(UiKey.E), Keybinds.RebindCheck.Full, out var why));
            Assert.Equal("E is used by the game (Interact). Pick another key.", why);
            Assert.False(Keybinds.TryRebind("a.mod", "Go", F8, Keybinds.RebindCheck.Full, out why));
            Assert.Equal("F8 is used by the game (Place, while building). Pick another key.", why);
            Assert.Equal(F9, Keybinds.Snapshot().Single().Chord);
        }

        [Fact]
        public void A_remap_onto_another_Progenitor_bind_is_refused_the_same_mods_included()
        {
            KeybindArbiter.NameOf = id => id == "sample.mod" ? "Progenitor Sample" : null;
            Keybinds.Register(_owner, "sample.mod", "NetPing", File("s"), F8, "", () => { });
            Keybinds.Register(_owner, "sample.mod", "Other", File("s2"), F7, "", () => { });
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });

            Assert.False(Keybinds.TryRebind("a.mod", "Go", F8, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal("F8 is used by Progenitor Sample (Net ping). Pick another key.", why);
            Assert.False(Keybinds.TryRebind("sample.mod", "Other", F8, Keybinds.RebindCheck.Full, out why));
            Assert.Equal("F8 is used by Progenitor Sample (Net ping). Pick another key.", why);

            // The rebind flow's capture refuses it and keeps waiting.
            var results = new List<RebindResult>();
            Keybinds.RebindRefused += r => results.Add(r);
            Keybinds.BeginRebind("a.mod", "Go");
            _keys.Next = F8;
            _keys.Frame();
            Assert.True(Assert.Single(results).StillCapturing);
            Assert.Equal("Go", Keybinds.RebindingId);
            Keybinds.CancelRebind();
        }

        [Fact]
        public void A_remap_onto_a_cfg_mods_key_is_refused_and_a_cfg_key_onto_a_used_key_is_refused_too()
        {
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "OpenPanel", new KeyboardShortcut("F8 + LeftControl"), "");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            SettingsCatalog.AllowForeignEdits = true;
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });

            var ctrlF8 = new KeyChord(UiKey.F8, KeyModifiers.Ctrl);
            Assert.False(Keybinds.TryRebind("a.mod", "Go", ctrlF8, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal("F8 + Ctrl is used by plain.mod (Open panel). Pick another key.", why);

            // The tab's save of the cfg key onto the Progenitor bind's F9 is refused before anything is written.
            OpenKeybindsTab();
            var pane = Pane();
            var cfgRow = pane.Model!.ModRows().Single(r => r.Claim.Source == KeybindSource.Cfg);
            Assert.True(pane.BeginEdit(cfgRow.Id));
            Assert.True(pane.CaptureForBridge(cfgRow.Id, F9));
            // Requested 2026-10-10: the capture offers to take F9 from a.mod; a direct store without the take
            // is still refused before anything is written.
            Assert.Equal(F9, pane.Pending);
            Assert.Equal("progenitor:a.mod/Go", Assert.Single(pane.TakeFromClaims).Key);
            Assert.Null(pane.SwapWith);
            Assert.False(pane.Store(cfgRow, F9));
            Assert.Equal("F9 is used by a.mod (Go). Pick another key.", pane.LastStatus);
            Assert.Equal("F8 + LeftControl", ((ConfigEntry<KeyboardShortcut>)cfg[new ConfigDefinition("Hotkeys", "OpenPanel")]).Value.Text);
        }

        [Fact]
        public void Choosing_the_key_a_bind_already_has_is_a_no_op_even_in_a_conflict()
        {
            KeybindArbiter.InstallTimeOf = id => id == "early.mod" ? Ledger(9) : Ledger(1);
            Keybinds.Register(_owner, "early.mod", "Go", File("e"), F9, "", () => { });
            Keybinds.Register(_owner, "late.mod", "Go", File("l"), F9, "", () => { });
            Assert.True(Keybinds.TryRebind("late.mod", "Go", F9, Keybinds.RebindCheck.Full, out var why));
            Assert.Null(why);

            OpenKeybindsTab();
            var pane = Pane();
            var loser = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "late.mod");
            Assert.True(loser.Inactive);
            Assert.True(pane.CaptureForBridge(loser.Id, F9));
            Assert.Equal(F9, pane.Pending);
            Assert.EndsWith("is already this key.", JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe()))
                .RootElement.GetProperty("edit").GetProperty("status").GetString());
        }

        [Fact]
        public void Swap_exchanges_two_Progenitor_keys_after_one_confirm_and_saves_both()
        {
            var fileA = File("a");
            var fileB = File("b");
            Keybinds.Register(_owner, "a.mod", "Open", fileA, F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", fileB, F8, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            string rowId = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod").Id;
            Assert.True(pane.BeginEdit(rowId));
            _keys.Next = F8;
            _keys.Frame();
            Assert.Equal("b.mod", pane.SwapWith!.OwnerId);

            // One confirm: the framework dialog; its Yes swaps.
            pane.PressSwap();
            var dialog = UiDialog.Current;
            Assert.NotNull(dialog);
            Assert.Equal(KeybindsPane.SwapDialogTag, dialog!.Tag);
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            dialog.Close();
            Assert.True(pane.ConfirmSwap());
            Assert.Equal("Swapped.", pane.LastStatus);
            Assert.Equal(F8, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "b.mod").Chord);
            Assert.Equal("F8", ((ConfigEntry<string>)fileA[new ConfigDefinition(Keybinds.Section, "Open")]).Value);
            Assert.Equal("F9", ((ConfigEntry<string>)fileB[new ConfigDefinition(Keybinds.Section, "Close")]).Value);
            Assert.Equal(0, Pane().Model!.ConflictCount);
        }

        [Fact]
        public void Swap_is_not_offered_when_the_other_bind_could_not_take_this_key()
        {
            // b.mod's new key would be F9, which the game has: no swap, just the refusal.
            UseGame(Game("Interact", F9));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F7, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            Assert.True(Keybinds.Rebind("a.mod", "Open", F9, checkConflicts: false));
            Assert.False(Keybinds.Swap("a.mod", "Open", "b.mod", "Close", dryRun: true, out var why));
            Assert.Equal("F9 is used by the game (Interact). Pick another key.", why);

            OpenKeybindsTab();
            var pane = Pane();
            var row = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod");
            Assert.True(pane.CaptureForBridge(row.Id, F8));
            Assert.Null(pane.SwapWith);
            Assert.False(pane.ConfirmSwap());
        }

        [Fact]
        public void The_settings_field_refuses_a_chord_with_an_empty_part()
        {
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            var keysRow = SettingsCatalog.Rows().Single(r => r.PluginId == "a.mod" && r.Kind == SettingsEditorKind.Keybind);

            Assert.False(keysRow.TryWrite("Ctrl++", out string error));
            Assert.Equal(KeyChord.EmptyPartReason, error);
            Assert.False(keysRow.TryWrite("F8 +", out error));
            Assert.Equal(KeyChord.EmptyPartReason, error);
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
        }

        [Fact]
        public void Reset_to_a_default_that_now_clashes_is_refused_everywhere()
        {
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            Assert.True(Keybinds.Rebind("a.mod", "Open", F7));
            Assert.True(Keybinds.Rebind("b.mod", "Close", F9));
            const string Expected = "Its default, F9, is now used by b.mod (Close). Unbind it or pick another key.";

            Assert.False(Keybinds.TryRebind("a.mod", "Open", F9, Keybinds.RebindCheck.Reset, out var why));
            Assert.Equal(Expected, why);

            // The Keybinds tab's « .
            OpenKeybindsTab();
            var pane = Pane();
            string rowId = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod").Id;
            pane.ResetRow(rowId);
            Assert.Equal(Expected, pane.LastStatus);
            Assert.Equal(F7, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);

            // The mod page's Keys row reset and the settings row's text write.
            var keysRow = SettingsCatalog.Rows().Single(r => r.PluginId == "a.mod" && r.Kind == SettingsEditorKind.Keybind);
            var result = ModSettingsLayout.Reset(keysRow);
            Assert.Equal(SettingWriteState.Refused, result.State);
            Assert.Equal(Expected, result.Error);
            Assert.False(keysRow.TryWrite("F9", out string error));
            Assert.Equal("F9 is used by b.mod (Close). Pick another key.", error);

            // The picker (the mod page's key field).
            var panel = Progenitor.Ui.Ui.Panel("Keys", _owner, "example.ui.test");
            var picker = UiPrimitives.KeybindPicker(panel, "Open", "a.mod", "Open");
            Assert.False(picker.ResetToDefault());
            Assert.Equal(Expected, picker.ConflictNotice);
            Assert.Equal(F7, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);

            // Reset all mod keys resets what it can and never stores a conflict: Atlas's default is still
            // taken until b.mod goes back to its own.
            Pane().ConfirmResetAll();
            Assert.Equal(0, Pane().Model!.ConflictCount);

            // A reserved default is refused the same way.
            Keybinds.Register(_owner, "w.mod", "Go", File("w"), new KeyChord(UiKey.F4, KeyModifiers.Alt), "", () => { });
            Assert.False(Keybinds.TryRebind("w.mod", "Go", new KeyChord(UiKey.F4, KeyModifiers.Alt), Keybinds.RebindCheck.Reset, out why));
            Assert.Equal("Its default, Alt+F4, closes the window in Windows. Unbind it or pick another key.", why);
        }

        // ---- the Conflicts view -----------------------------------------------------------------

        [Fact]
        public void The_Conflicts_view_heads_with_the_inactive_count_and_the_losers_key_cell_opens_capture()
        {
            Assert.Equal("3 keys are inactive because of conflicts. Pick new keys to turn them back on.", KeybindsTabLayout.ConflictsHeader(3));
            Assert.Equal("1 key is inactive because of a conflict. Pick a new key to turn it back on.", KeybindsTabLayout.ConflictsHeader(1));
            Assert.Equal("", KeybindsTabLayout.ConflictsHeader(0));

            KeybindArbiter.InstallTimeOf = id => id == "early.mod" ? Ledger(9) : Ledger(1);
            Keybinds.Register(_owner, "early.mod", "Go", File("e"), F9, "", () => { });
            Keybinds.Register(_owner, "late.mod", "Go", File("l"), F9, "", () => { });
            Keybinds.Register(_owner, "late.mod", "Zoom", File("l2"), F8, "", () => { });
            Keybinds.Register(_owner, "early.mod", "Zoom", File("e2"), F8, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            Assert.Equal(2, pane.Model!.InactiveCount);
            Assert.Equal("", pane.ConflictsHeaderText);
            pane.SetFilter(KeybindsFilter.Conflicts);
            Assert.Equal("2 keys are inactive because of conflicts. Pick new keys to turn them back on.", pane.ConflictsHeaderText);
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(pane.Describe())))
                Assert.True(doc.RootElement.GetProperty("conflictsHeader").GetProperty("visible").GetBoolean());

            // One click on the loser's key cell opens the capture for it.
            var loser = pane.Model.ModRows().First(r => r.Inactive);
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(loser.Id) + "/" + KeybindsPane.CellKey, out _));
            Assert.True(pane.Capturing);
            Assert.Equal(loser.Id, pane.Editing);
            _keys.Next = F7;
            _keys.Frame();
            pane.SaveEdit();
            Assert.Equal(1, Pane().Model!.InactiveCount);

            pane = Pane();
            pane.SetFilter(KeybindsFilter.All);
            Assert.Equal("", pane.ConflictsHeaderText);
        }

        // ---- review fixes (live check 2026-09-29) --------------------------------------------------

        [Fact]
        public void A_capture_refused_again_and_again_still_ends_after_30_seconds()
        {
            long now = 1000;
            Keybinds.Clock = () => now;
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            var finished = new List<RebindResult>();
            Keybinds.RebindFinished += r => finished.Add(r);
            Assert.True(Keybinds.BeginRebind("a.mod", "Go"));
            for (int i = 0; i < 3; i++)
            {
                now += 9000;
                _keys.Next = new KeyChord(UiKey.F4, KeyModifiers.Alt);
                _keys.Frame();
                Assert.Equal("Go", Keybinds.RebindingId);
            }

            now += 4000;
            _keys.Frame();
            Assert.Null(Keybinds.RebindingId);
            var end = Assert.Single(finished);
            Assert.Equal(RebindOutcome.Cancelled, end.Outcome);
            Assert.Equal(Keybinds.CaptureMaxReason, end.Reason);
        }

        [Theory]
        [InlineData(KeyModifiers.None)]
        [InlineData(KeyModifiers.Ctrl)]
        [InlineData(KeyModifiers.Alt)]
        [InlineData(KeyModifiers.Ctrl | KeyModifiers.Shift)]
        public void Escape_with_any_modifiers_cancels_the_capture(KeyModifiers mods)
        {
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            var finished = new List<RebindResult>();
            var refused = new List<RebindResult>();
            Keybinds.RebindFinished += r => finished.Add(r);
            Keybinds.RebindRefused += r => refused.Add(r);
            Keybinds.BeginRebind("a.mod", "Go");
            _keys.Next = new KeyChord(UiKey.Escape, mods);
            _keys.Frame();
            Assert.Null(Keybinds.RebindingId);
            Assert.Equal(RebindOutcome.Cancelled, Assert.Single(finished).Outcome);
            Assert.Empty(refused);

            // Win+Esc too, and the tab's capture.
            var keys = new UiKeybindTests.FakeKeyInput();
            Keybinds.SetSourceForTests(keys);
            KeyCaptureResult? got = null;
            Keybinds.BeginCapture(r => got = r);
            keys.PressPhysical(UiKey.Escape, ModifierSides.LeftMeta);
            Keybinds.Pump();
            Assert.Equal(RebindOutcome.Cancelled, got!.Value.Outcome);
            Assert.False(Keybinds.Capturing);
        }

        [Fact]
        public void At_the_title_screen_a_remap_is_checked_against_the_last_game_keys_read_in_a_world()
        {
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            var e = new KeyChord(UiKey.E);

            // Nothing remembered yet: the key is allowed, and the preview says the game was not checked.
            Assert.Equal(KeybindRemapGuard.GameUncheckedNote, KeybindRemapGuard.GameCheckNote());

            // In a world the game's keys are read (and remembered, in memory and in the file).
            KeybindGameCache.Directory = () => _dir;
            UseGame(Game("Interact", e));
            KeybindArbiter.Current();
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(_dir, KeybindGameCache.FileName)));

            // Back at the title screen.
            KeybindArbiter.GameSource = null;
            Assert.False(KeybindArbiter.Current().GameAvailable);
            Assert.Equal("", KeybindRemapGuard.GameCheckNote());
            Assert.False(Keybinds.TryRebind("a.mod", "Go", e, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal("E is used by the game (Interact), as last read in a world today. Pick another key.", why);

            // After a restart: read back from the file, with its age.
            KeybindGameCache.ResetForTests();
            KeybindGameCache.Directory = () => _dir;
            KeybindGameCache.UtcNow = () => DateTime.UtcNow.AddDays(3);
            Assert.False(Keybinds.TryRebind("a.mod", "Go", e, Keybinds.RebindCheck.Full, out why));
            Assert.Equal("E is used by the game (Interact), as last read in a world 3 days ago. Pick another key.", why);
            Assert.True(Keybinds.TryRebind("a.mod", "Go", F7, Keybinds.RebindCheck.Full, out _));
        }

        [Fact]
        public void A_check_that_cannot_run_refuses_the_key()
        {
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            var row = pane.Model!.ModRows().Single();
            KeybindRemapGuard.PictureSource = () => throw new InvalidOperationException("test: no picture");

            Assert.False(Keybinds.TryRebind("a.mod", "Go", F7, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal(KeybindRemapGuard.CheckFailedReason, why);
            Assert.Equal(KeybindRemapGuard.CheckFailedReason, KeybindsPane.RefusalFor(row.Claim, F7, reset: false)!.Reason);
            Assert.Equal(KeybindRemapGuard.CheckFailedReason,
                Keybinds.CaptureRefusal(Keybinds.Snapshot().Single(), F7, ModifierSides.None));
            Assert.Equal(F9, Keybinds.Snapshot().Single().Chord);
        }

        [Fact]
        public void The_mod_pages_greyed_row_undo_goes_through_the_guard()
        {
            string view = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSettingsView.cs");
            SourcePin.AssertUncommented(view, "Keybinds.TryRebind(row.PluginId, row.Key, previous, Keybinds.RebindCheck.Full, out var undoRefused)");
            foreach (var file in System.IO.Directory.GetFiles(System.IO.Path.Combine(SourcePin.RepoRoot(), "src"), "*.cs", System.IO.SearchOption.AllDirectories))
                Assert.DoesNotContain("checkConflicts: false", System.IO.File.ReadAllText(file));
        }

        [Fact]
        public void A_bind_that_cannot_be_saved_is_never_swapped()
        {
            var fileA = File("a");
            fileA.Bind(Keybinds.Section, "Open", 5, "");
            Keybinds.Register(_owner, "a.mod", "Open", fileA, F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            Assert.False(Keybinds.Swap("a.mod", "Open", "b.mod", "Close", dryRun: true, out var why));
            Assert.Equal(Keybinds.SwapNotSavableReason, why);
            Assert.False(Keybinds.Swap("a.mod", "Open", "b.mod", "Close", dryRun: false, out _));
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);

            OpenKeybindsTab();
            var pane = Pane();
            var row = pane.Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod");
            Assert.True(pane.CaptureForBridge(row.Id, F8));
            Assert.Null(pane.SwapWith);
        }

        [Fact]
        public void A_swap_whose_save_fails_puts_both_keys_and_their_notes_back()
        {
            var fileB = File("b");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), new KeyChord(UiKey.F4, KeyModifiers.Alt), "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", fileB, F8, "", () => { });
            Assert.NotNull(Keybinds.ReservedNoteOf("a.mod", "Open"));
            fileB.SettingChanged += (_, _) => throw new System.IO.IOException("test: disk full");

            Assert.False(Keybinds.Swap("a.mod", "Open", "b.mod", "Close", dryRun: false, out var why));
            Assert.StartsWith("The keys could not be saved", why);
            Assert.False(Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord.IsBound);
            Assert.Equal(F8, Keybinds.Snapshot().Single(b => b.PluginId == "b.mod").Chord);
            Assert.NotNull(Keybinds.ReservedNoteOf("a.mod", "Open"));
            Assert.Single(ModProblems.For("a.mod"));
        }

        [Fact]
        public void A_rebind_is_revalidated_inside_the_lock()
        {
            Keybinds.Register(_owner, "a.mod", "Go", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Other", File("b"), F7, "", () => { });
            int builds = 0;
            // Another bind takes F8 after the picture read the registry and before the rebind stores.
            KeybindArbiter.CfgSource = () =>
            {
                if (++builds == 1) Keybinds.Rebind("b.mod", "Other", F8, checkConflicts: false);
                return Array.Empty<KeybindClaim>();
            };

            Assert.False(Keybinds.TryRebind("a.mod", "Go", F8, Keybinds.RebindCheck.Full, out var why));
            Assert.Equal("F8 is used by b.mod (Other). Pick another key.", why);
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
        }

        [Fact]
        public void A_swap_is_revalidated_inside_the_lock()
        {
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            Keybinds.Register(_owner, "c.mod", "Go", File("c"), F7, "", () => { });
            Keybinds.Register(_owner, "d.mod", "Go", File("d"), new KeyChord(UiKey.F6), "", () => { });
            int builds = 0;
            KeybindArbiter.CfgSource = () =>
            {
                builds++;
                if (builds == 1) Keybinds.Rebind("d.mod", "Go", new KeyChord(UiKey.F5), checkConflicts: false);
                if (builds == 2) Keybinds.Rebind("c.mod", "Go", F8, checkConflicts: false);
                return Array.Empty<KeybindClaim>();
            };

            Assert.False(Keybinds.Swap("a.mod", "Open", "b.mod", "Close", dryRun: false, out var why));
            Assert.Equal(Keybinds.ChangedMeanwhileReason, why);
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.Equal(F8, Keybinds.Snapshot().Single(b => b.PluginId == "b.mod").Chord);
        }

        [Fact]
        public void The_bridge_capture_defaults_to_the_row_being_edited_and_errors_clearly_otherwise()
        {
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var ex = Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("capture", null, "F7"));
            Assert.Contains("args.target", ex.Message);
            Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("capture", "progenitor:nobody/none", "F7"));

            var pane = Pane();
            string rowId = pane.Model!.ModRows().Single().Id;
            Assert.True(pane.BeginEdit(rowId));
            ModsMenu.KeysOp("capture", null, "F7");
            Assert.Equal(F7, pane.Pending);
        }
    }
}
