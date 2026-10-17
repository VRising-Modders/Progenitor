using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS3 polish (user report 2026-09-30, Core 2026.9.76, title screen): Escape during a key capture or
    /// on a dialog must not close the Mods workspace; the edit bar must not draw over the list, stay short,
    /// and the toolbar must end before the description column; reset and clear must explain themselves.
    /// Game-free; nothing here ran in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private void Escape()
        {
            // One physical Escape: Bootstrap forwards 27 then 17 (UiInputContext).
            UiApi.PressKeyForTests(27, down: true);
            UiApi.PressKeyForTests(17, down: true);
        }

        // ---- 1. Escape during a capture cancels the capture only --------------------------------

        [Fact]
        public void Escape_during_a_Keybinds_tab_capture_cancels_it_and_never_closes_the_workspace()
        {
            ResetArt();
            long now = 10_000;
            Keybinds.Clock = () => now;
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            string id = ModRowId("a.mod");

            // The panel hook sees the press first: nothing is hidden; the capture's pump then cancels.
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(id) + "/" + KeybindsPane.CellKey, out _));
            Assert.True(Keybinds.EscapeBelongsToCapture);
            Escape();
            Assert.True(ModsMenu.IsOpen);
            Assert.True(Keybinds.Capturing, "the hook does not end the capture; its own pump does");
            _keys.Next = new KeyChord(UiKey.Escape);
            _keys.Frame();
            Assert.False(Keybinds.Capturing);
            Assert.Equal("", Pane().Editing);
            Assert.True(ModsMenu.IsOpen);

            // The pump sees it first: the same press reaching the hook afterwards still hides nothing.
            Pane().BeginEdit(id);
            _keys.Next = new KeyChord(UiKey.Escape);
            _keys.Frame();
            Assert.False(Keybinds.Capturing);
            now += 50;
            Escape();
            Assert.True(ModsMenu.IsOpen);
            Assert.Contains(_log.Infos, l => l.Contains("left to the key capture; no panel hidden"));

            // A later Escape, with no capture, is the ordinary close.
            now += Keybinds.EscapeGraceMs + 1;
            Assert.False(Keybinds.EscapeBelongsToCapture);
            Escape();
            Assert.False(ModsMenu.IsOpen);
        }

        [Fact]
        public void Escape_during_a_mod_page_or_settings_rebind_does_not_close_the_workspace()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            Assert.True(Keybinds.BeginRebind("a.mod", "Open"));
            Assert.True(Keybinds.EscapeBelongsToCapture);
            Escape();
            Assert.True(ModsMenu.IsOpen);
            Keybinds.CancelRebind();
        }

        [Fact]
        public void Escape_on_a_dialog_inside_the_workspace_answers_the_dialog_only()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Rebind("a.mod", "Open", F7);
            OpenKeybindsTab();
            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.FooterKey + "/" + KeybindsPane.CellResetAll, out _));
            Assert.NotNull(UiDialog.Current);
            Escape();
            Assert.Null(UiDialog.Current);
            Assert.True(ModsMenu.IsOpen, "the dialog closed; the workspace behind it stays");
            Assert.Equal(F7, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.Contains(_log.Infos, l => l.Contains("answered the open dialog; no panel hidden"));
            // The next Escape, with no dialog, closes the workspace as before.
            Escape();
            Assert.False(ModsMenu.IsOpen);
        }

        // ---- 2. the top: short edit bar, no overlap, toolbar inside the list pane ----------------

        [Fact]
        public void The_edit_bar_is_one_short_line_and_the_full_refusal_is_in_the_description()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            OpenKeybindsTab();
            string id = ModRowId("a.mod");
            Pane().BeginEdit(id);
            string Bar() => _runtime.CellRow(KeybindsPane.EditKey)!.Cells().Single(c => c.Key == KeybindsPane.CellEditText).Text;
            Assert.EndsWith(" · Open: press a key", Bar());
            _keys.Next = F8;
            _keys.Frame();
            // Requested 2026-10-10: a used key is taken on Save; the full sentence is in the description.
            Assert.EndsWith(" · Open: F8", Bar());
            Assert.StartsWith("F8 is used by b.mod (Close). Save moves it to Open; Close will have no key.", Tab().GetProperty("description").GetProperty("warning").GetString());
            Assert.StartsWith("F8 is used by b.mod (Close).", Tab().GetProperty("edit").GetProperty("status").GetString());
            Pane().CaptureForBridge(id, new KeyChord(UiKey.F7, KeyModifiers.Ctrl));
            Assert.EndsWith(" · Open: Ctrl+F7", Bar());
            Assert.Equal("x · y: no key", KeybindsTabLayout.EditBarText("x · y", false, KeyChord.None));
            Pane().CancelEdit();
        }

        [Theory]
        [InlineData(1236f)]
        [InlineData(1000f)]
        [InlineData(800f)]
        [InlineData(640f)]
        public void No_top_row_overlaps_another_or_the_list_and_the_toolbar_ends_inside_the_list_pane(float rowWidth)
        {
            float bar = UnityUiRuntimeMetrics.RowHeight + 6f;
            foreach (var heights in new[] { new[] { bar }, new[] { bar, bar } })
            {
                float chrome = MenuPanelLayout.TopChromeHeight(heights);
                for (int i = 0; i < heights.Length; i++)
                {
                    float top = MenuPanelLayout.TopRowOffset(heights, i);
                    float bottom = top + heights[i];
                    if (i + 1 < heights.Length) Assert.True(bottom <= MenuPanelLayout.TopRowOffset(heights, i + 1));
                    Assert.True(bottom <= chrome, "the list starts below every shown top row");
                }
            }

            Assert.True(MenuPanelLayout.TopChromeHeight(new[] { bar, bar }) - MenuPanelLayout.TopChromeHeight(new[] { bar }) >= bar,
                "showing the edit bar pushes the list down by at least its height");
            Assert.Equal(0f, MenuPanelLayout.TopChromeHeight(Array.Empty<float>()));

            // The runtime uses the measured chrome and places only the shown top rows.
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            string layout = SourcePin.MethodBody(runtime, "private void RunLayout(");
            SourcePin.AssertUncommented(layout, "MenuPanelLayout.TopChromeHeight(topHeights) + headerChrome");
            SourcePin.AssertUncommented(layout, "if (_rows[i].Pin == MenuRowPin.Top && RowActive(_rows[i]))");

            // The toolbar's cells stay inside the list pane's row (so none can reach the description column).
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var cells = Pane().ToolbarCells;
            var placed = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec("probe", cells, rightInset: HeadBlockLayout.RightColumnInset));
            foreach (var p in placed.Where(p => p.Visible))
            {
                Assert.True(p.Left >= -rowWidth * .5f - .5f, p.Key + " starts outside the row at " + rowWidth);
                Assert.True(p.Right <= rowWidth * .5f - HeadBlockLayout.RightColumnInset + .5f, p.Key + " ends past the row at " + rowWidth);
            }
            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j]);
            Assert.Contains(placed, p => p.Key == KeybindsPane.CellJumpToMod && p.Visible);
            Assert.Contains(placed, p => p.Key == KeybindsPane.CellSearch && p.Visible);

            // The edit bar shown and hidden: its cells stay inside the row too.
            var edit = _runtime.CellRow(KeybindsPane.EditKey)!;
            var editPlaced = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec("probe", edit.Cells(), rightInset: HeadBlockLayout.RightColumnInset));
            foreach (var p in editPlaced.Where(p => p.Visible))
                Assert.True(p.Right <= rowWidth * .5f - HeadBlockLayout.RightColumnInset + .5f, p.Key);
        }

        // ---- 3. reset and Unbind explain themselves ---------------------------------------------

        [Fact]
        public void Crossing_the_gap_between_rows_never_flashes_the_selected_row_and_an_edit_keeps_the_column()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            OpenKeybindsTab();
            string a = ModRowId("a.mod"), b = ModRowId("b.mod");
            string Row() => Tab().GetProperty("description").GetProperty("row").GetString()!;
            long now = 1000;
            var clock = KeybindsPane.ClockMs;
            KeybindsPane.ClockMs = () => now;
            try
            {
                Pane().Select(a);
                Pane().Hover(b, entered: true);
                Assert.Equal(b, Row());
                // User report 2026-10-10: the pointer leaves b for the next row; within the grace b stays described.
                Pane().Hover(b, entered: false);
                now += KeybindsPane.HoverLeaveGraceMs - 1;
                Pane().Tick();
                Assert.Equal(b, Row());
                // Out of the list: after the grace the selected row is back.
                now += 2;
                Pane().Tick();
                Assert.Equal(a, Row());

                // During an edit, hovering another row leaves the edit's row in the column.
                Pane().BeginEdit(a);
                Pane().Hover(b, entered: true);
                Assert.Equal(a, Row());
                Pane().CancelEdit();
                Assert.Equal(b, Row());
            }
            finally { KeybindsPane.ClockMs = clock; }
        }

        [Fact]
        public void Reset_and_Unbind_explain_themselves_on_hover_and_say_why_when_dimmed()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            Assert.True(Keybinds.Rebind("a.mod", "Open", new KeyChord(UiKey.F7, KeyModifiers.Ctrl)));
            OpenKeybindsTab();
            string changed = ModRowId("a.mod"), plain = ModRowId("b.mod");
            var cells = RowOf(changed).Cells();
            Assert.True(cells.Single(c => c.Key == KeybindsPane.CellReset).Hover);
            Assert.True(cells.Single(c => c.Key == KeybindsPane.CellClear).Hover);
            Assert.Equal(KeybindsTabLayout.UnbindCaption2, cells.Single(c => c.Key == KeybindsPane.CellClear).Text);

            string Hint() => Tab().GetProperty("description").GetProperty("hint").GetString()!;
            Assert.True(UiCellRowHandle.TryHoverCell(NameRowOf(changed), KeybindsPane.CellReset, entered: true));
            Assert.Equal("Reset to default: F9.", Hint());
            Assert.Equal(changed, Pane().HoveredRow);
            Assert.True(UiCellRowHandle.TryHoverCell(NameRowOf(changed), KeybindsPane.CellReset, entered: false));
            Assert.Equal("", Hint());
            UiCellRowHandle.TryHoverCell(NameRowOf(changed), KeybindsPane.CellClear, entered: true);
            Assert.Equal(KeybindsTabLayout.HintUnbind, Hint());

            // Dimmed, with the reason.
            Assert.Equal(CellTone.Disabled, RowOf(plain).Cells().Single(c => c.Key == KeybindsPane.CellReset).Tone);
            UiCellRowHandle.TryHoverCell(NameRowOf(plain), KeybindsPane.CellReset, entered: true);
            Assert.Equal(KeybindsTabLayout.HintAlreadyDefault, Hint());
            Assert.True(Keybinds.Rebind("b.mod", "Close", KeyChord.None));
            Pane().Build(UiApi.Panel("probe", _owner, SettingsPanel.PluginId), 1200f);
            var unbound = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == "b.mod");
            Assert.Equal(KeybindsTabLayout.HintAlreadyUnbound, KeybindsTabLayout.GutterHint(unbound, KeybindsPane.CellClear));
            Assert.Equal("", KeybindsTabLayout.GutterHint(unbound, KeybindsPane.CellKey));
        }

        [Fact]
        public void The_game_reset_icon_replaces_the_reset_caption_and_the_caps_stay_aligned()
        {
            ResetArt();
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var mod = Pane().Model!.ModRows().Single();
            var game = Pane().Model!.Sections.SelectMany(s => s.Rows).Single(r => r.IsGame);
            var modCells = KeybindsTabLayout.RowCells(mod, mod.PrimaryText, false, resetIcon: true);
            var reset = modCells.Single(c => c.Key == KeybindsPane.CellReset);
            Assert.Equal(CellSkin.ResetIcon, reset.Skin);
            Assert.Equal("", reset.Text);
            Assert.Equal(KeybindsTabLayout.GutterCellWidth, reset.Width.Value);
            var gameCells = KeybindsTabLayout.RowCells(game, game.PrimaryText, false, resetIcon: true);
            float Right(IReadOnlyList<CellSpec> c, string key) => MenuPanelLayout.CellColumns(1200f, c).Single(p => p.Key == key).Right;
            Assert.Equal(Right(modCells, KeybindsPane.CellKey2), Right(gameCells, KeybindsPane.CellKey2), 1);
            var kitCells = KeybindsTabLayout.RowCells(mod, mod.PrimaryText, false, resetIcon: false);
            Assert.Equal(KeybindsTabLayout.ResetCaption, kitCells.Single(c => c.Key == KeybindsPane.CellReset).Text);

            Assert.Equal("Icon_Reset", KeybindArtLadder.ResetIconSpriteName);
            string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindArtDonors.cs");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void ReadEntryImages("),
                "spriteName == KeybindArtLadder.ResetIconSpriteName");
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            string paint = SourcePin.MethodBody(runtime, "private static bool PaintResetIcon(");
            SourcePin.AssertUncommented(paint, "KeybindArtDonors.TryResetIcon(out var sprite, out var color)");
            SourcePin.AssertUncommented(paint, "KeybindsTabLayout.DisabledIconAlpha");
            // Game-free there is no donor: the readback says kit, and the gutter shows the Reset caption.
            Assert.Equal("kit", ((Dictionary<string, object?>)KeybindArtLadder.Describe()["resetIcon"]!)["source"]);
            Assert.Equal(KeybindsTabLayout.ResetCaption, RowOf(mod.Id).Cells().Single(c => c.Key == KeybindsPane.CellReset).Text);
        }

        // ---- live P2: the game's FullscreenMenu must not close on an Escape we took ---------------

        [Fact]
        public void The_menu_guard_holds_Back_and_Menu_Close_while_a_capture_or_a_dialog_owns_Escape()
        {
            // The hold: a capture waiting or just ended on Escape, or a dialog open; kept while that Escape is down.
            Assert.True(MenuHotkeyGuard.NextCloseHold(captureOwnsEscape: true, dialogOpen: false, escapeHeld: false, wasHeld: false));
            Assert.True(MenuHotkeyGuard.NextCloseHold(false, dialogOpen: true, false, false));
            Assert.True(MenuHotkeyGuard.NextCloseHold(false, false, escapeHeld: true, wasHeld: true));
            Assert.False(MenuHotkeyGuard.NextCloseHold(false, false, escapeHeld: true, wasHeld: false));
            Assert.False(MenuHotkeyGuard.NextCloseHold(false, false, escapeHeld: false, wasHeld: true));

            // The flag is separate from the hotkey set (which still never takes the close key) and released with focus.
            MenuHotkeyGuard.SetCloseHeld(true);
            Assert.True(MenuHotkeyGuard.CloseHeld);
            Assert.DoesNotContain(17, MenuHotkeyGuard.BlockedActions());
            Assert.Equal(new[] { 17, 1004 }, InputMaskPlan.BlockedCloseActions());
            Assert.Equal(true, ModsMenu.FocusStatus(null, false)["closeHeld"]);
            MenuHotkeyGuard.Release();
            Assert.False(MenuHotkeyGuard.CloseHeld);

            // The native side: the 498 guard (ahead of FullscreenMenuMapper at 502) takes the close actions
            // in both lists while held, from the same live array as the 599 recipe; the pump writes the flag.
            string blocker = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameInputBlocker.cs");
            string guard = SourcePin.MethodBody(blocker, "private static void MaskGuard(");
            SourcePin.AssertUncommented(guard, "bool closeHeld = MenuHotkeyGuard.CloseHeld;");
            SourcePin.AssertUncommented(guard, "int[] closes = InputMaskPlan.BlockedCloseActions();");
            SourcePin.AssertUncommented(guard, "state.BlockKeyDown((ButtonInputAction)closes[i]);");
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            SourcePin.AssertUncommented(SourcePin.MethodBody(runtime, "private static void PumpTypedKeys("),
                // NAV1: also while B on a workspace page means back to the list.
                "Keybinds.EscapeBelongsToCapture, UiDialog.Current is { IsOpen: true } || UiChoiceHandle.AnyOpen || UiNav.HoldsGameClose,");
            Assert.True(UiInputCapture.HotkeyGuardOrder < UiInputCapture.HudMenuOrder);
        }

        [Fact]
        public void The_close_hold_follows_a_capture_and_a_dialog_through_one_press()
        {
            ResetArt();
            long now = 5_000;
            Keybinds.Clock = () => now;
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Rebind("a.mod", "Open", F7);
            OpenKeybindsTab();
            bool held = false;
            bool Step(bool escapeDown) => held = MenuHotkeyGuard.NextCloseHold(
                Keybinds.EscapeBelongsToCapture, UiDialog.Current is { IsOpen: true }, escapeDown, held);

            Assert.False(Step(false));
            Pane().BeginEdit(ModRowId("a.mod"));
            Assert.True(Step(false), "held from the frame the capture starts, before any Escape");
            _keys.Next = new KeyChord(UiKey.Escape);
            _keys.Frame();
            Assert.True(Step(true), "the Escape that canceled it");
            now += Keybinds.EscapeGraceMs + 100;
            Assert.True(Step(true), "still down: its release must not reach the menu either");
            Assert.False(Step(false));

            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.FooterKey + "/" + KeybindsPane.CellResetAll, out _));
            Assert.True(Step(false), "a dialog is open");
            Escape();
            Assert.True(Step(true));
            Assert.False(Step(false));
        }

        // ---- live: the row highlight keeps the game's tint --------------------------------------

        [Fact]
        public void The_row_band_is_the_games_row_tint_and_never_a_white_band()
        {
            // Live 2026-09-30, second failed attempt at the borrowed glow: the band is a solid fill, its
            // brightness capped whatever the source. MENUKIT2 M1: the fill is the game's row tint (the
            // Options entries' ButtonTarget hue), no longer the kit's gold selection.
            var band = KeybindsTabLayout.HighlightBand;
            Assert.Equal((0.163f, 0.395f, 0.585f), (band.R, band.G, band.B));
            Assert.Equal(Progenitor.Ui.GameArt.GameSettingsLook.FlatBandAlpha, band.A);
            Assert.NotEqual(ModsListLayout.RowFill(selected: true), band);
            Assert.True(KeybindsTabLayout.BandIsDim(band.R, band.G, band.B, band.A),
                "max channel x alpha " + Math.Max(band.R, Math.Max(band.G, band.B)) * band.A);
            Assert.True(band.A <= 0.6f);
            Assert.True(Math.Max(band.R, Math.Max(band.G, band.B)) * band.A <= 0.45f * 0.6f);
            // What the live defect drew is rejected by the same rule.
            Assert.False(KeybindsTabLayout.BandIsDim(1f, 1f, 1f, 1f));
            Assert.False(KeybindsTabLayout.BandIsDim(1f, 1f, 1f, 0.35f));

            // The borrowed top gradient is only a faint thin rule.
            var top = KeybindsTabLayout.HighlightTopColor(1f, 1f, 1f, 1f);
            Assert.Equal(KeybindsTabLayout.HighlightTopMaxAlpha, top.A);
            // ... in the game's lit gradient tint (M1), not white.
            Assert.Equal((0.688f, 0.883f, 0.953f), (top.R, top.G, top.B));
            Assert.Equal(0.1f * 0.502f, KeybindsTabLayout.HighlightTopColor(1f, 1f, 1f, 0.1f).A, 4);
            Assert.True(KeybindsTabLayout.HighlightTopMaxHeight <= 3f);

            // The readback says what was used.
            var read = KeybindArtLadder.HighlightBandReadback();
            Assert.Equal("kit", read["source"]);
            Assert.Equal(false, read["borrowed"]);
            Assert.EndsWith("from " + KeybindsTabLayout.HighlightBandSource, (string)read["tint"]!);
            Assert.Equal("kit", ((Dictionary<string, object?>)KeybindArtLadder.Describe()["highlight"]!)["source"]);

            // The runtime: the band's sprite is cleared and its color written last; the glow is never
            // borrowed; the top rule is the capped color.
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            string select = SourcePin.MethodBody(runtime, "public void SetSelected(bool selected)");
            int sprite = select.IndexOf("_highlight!.sprite = null;", StringComparison.Ordinal);
            int color = select.IndexOf("_highlight.color = ToColor(KeybindsTabLayout.HighlightBand);", StringComparison.Ordinal);
            Assert.True(sprite >= 0 && color > sprite, "the band color is written after the sprite");
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(select, @"_highlight\.color\s*=").Count);
            Assert.DoesNotContain("TryHighlight(", select, StringComparison.Ordinal);
            SourcePin.AssertUncommented(select, "KeybindArtDonors.TryHighlightTop(out top, out topColor)");
            string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindArtDonors.cs");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "internal static void Prepare()"), "if (i == (int)PartId.Highlight)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "internal static bool TryHighlightTop("),
                "KeybindsTabLayout.HighlightTopColor(c.r, c.g, c.b, c.a)");
        }

        // ---- live: no list row behind the pinned top rows ---------------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_list_viewport_starts_below_every_pinned_top_row(bool editBarShown)
        {
            float bar = UnityUiRuntimeMetrics.RowHeight + 6f;
            var heights = editBarShown ? new[] { bar, bar } : new[] { bar };
            float chrome = MenuPanelLayout.TopChromeHeight(heights);
            var placed = heights.Select((h, i) => (MenuPanelLayout.TopRowOffset(heights, i), h)).ToList();
            float inset = MenuPanelLayout.ViewportTopInset(0f, chrome, placed);
            foreach (var (top, h) in placed) Assert.True(inset >= top + h, "a list row could draw behind a pinned row");

            // A pinned row placed lower than the chrome says (a stale or taller row) still pushes the viewport down.
            var taller = new List<(float, float)>(placed) { (chrome + 10f, bar) };
            Assert.True(MenuPanelLayout.ViewportTopInset(0f, chrome, taller) >= chrome + 10f + bar);
            Assert.Equal(40f + chrome, MenuPanelLayout.ViewportTopInset(40f, chrome, null));

            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            string fit = SourcePin.MethodBody(runtime, "private void FitChromeInsets(");
            SourcePin.AssertUncommented(fit, "WorkspaceHostLayout.ListViewportTop(TitleBand, topChrome, PlacedPinnedTopRows())");
            SourcePin.AssertUncommented(runtime, "TryAdd<RectMask2D>(go);");
        }
    }
}
