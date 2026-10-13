using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS3 live follow-up (2026-09-29, Steam client, Core 2026.9.75.1) and the review of 82baec90:
    /// compact key caps, one workspace footer, the bottom-right Reset all, no empty band, the game's keys
    /// at the title from the last in-world read, the game note on the first shown category, the inline
    /// conflict marker and the bridge hover through the row's slot. Game-free; nothing here ran in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        // ---- 3. compact key caps --------------------------------------------------------------

        [Fact]
        public void Key_caps_show_chords_modifiers_first_and_shrink_before_they_are_cut()
        {
            Assert.Equal("Ctrl+Shift+F12", KeybindsTabLayout.CapCaption("F12 + Ctrl + Shift"));
            Assert.Equal("Ctrl+Shift+Alt+K", KeybindsTabLayout.CapCaption("K + Alt + Shift + Ctrl"));
            Assert.Equal("Left Ctrl+E", KeybindsTabLayout.CapCaption("E + Left Ctrl"));
            Assert.Equal("Mouse Button 4", KeybindsTabLayout.CapCaption("Mouse Button 4"));
            Assert.Equal("", KeybindsTabLayout.CapCaption(null));
            Assert.Equal(MenuPanelLayout.HeaderButtonFontSize, KeybindsTabLayout.CapFontSize("W"));
            float longSize = KeybindsTabLayout.CapFontSize("Ctrl+Shift+Alt+Page Down");
            Assert.True(longSize < MenuPanelLayout.HeaderButtonFontSize);
            Assert.True(longSize >= KeybindsTabLayout.CapMinFontSize);
            Assert.True(CellLayout.EstimateTextWidth("Ctrl+Shift+F12", KeybindsTabLayout.CapFontSize("Ctrl+Shift+F12"))
                <= KeybindsTabLayout.KeyCapWidth - CellLayout.ButtonPad);

            ResetArt();
            var chord = new KeyChord(UiKey.F12, KeyModifiers.Ctrl | KeyModifiers.Shift);
            Keybinds.Register(_owner, "a.mod", "Big", File("a"), chord, "", () => { });
            OpenKeybindsTab();
            string id = ModRowId("a.mod");
            var cap = RowOf(id).Cells().Single(c => c.Key == KeybindsPane.CellKey);
            Assert.Equal("Ctrl+Shift+F12", cap.Text);
            Assert.Equal(KeybindsTabLayout.CapFontSize("Ctrl+Shift+F12"), cap.FontSize);
            var row = Tab().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);
            // The readback keeps the full text.
            Assert.Equal(chord.ToString(), row.GetProperty("primary").GetString());
            Assert.Equal("Ctrl+Shift+F12", row.GetProperty("primaryCaption").GetString());
        }

        // ---- 4. one footer; 5. Reset all at the bottom right; 6. no empty band ----------------

        [Fact]
        public void The_description_column_has_no_footer_and_Reset_all_is_a_placed_bottom_right_button()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            Assert.Equal("", ModsMenu.SidebarHeldForTests!.Footer);
            Assert.NotEqual("", ModsMenu.ContentHeldForTests!.Footer);
            string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
            SourcePin.AssertUncommented(ui, "panel.Footer = sidebar || mode == MenuPaneMode.Aside ? \"\"");

            var footer = _runtime.CellRow(KeybindsPane.FooterKey)!;
            var reset = footer.Cells().Single(c => c.Key == KeybindsPane.CellResetAll);
            var placed = MenuPanelLayout.CellColumns(1200f, footer.Cells());
            var at = placed.Single(p => p.Key == KeybindsPane.CellResetAll);
            Assert.Equal(KeybindsTabLayout.ButtonWidth(KeybindsTabLayout.ResetAllCaption), at.Width, 1);
            Assert.Equal(600f, at.Right, 1);

            // The runtime places a bottom-pinned cell row's cells (live: it drew as a wide bar), and a
            // hidden top row (the edit bar at rest) takes no band above the list.
            string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
            string bottom = SourcePin.MethodBody(runtime, "private void PlaceBottomRows(");
            SourcePin.AssertUncommented(bottom, "PlaceCellRowCells(row)");
            string layout = SourcePin.MethodBody(runtime, "private void RunLayout(");
            SourcePin.AssertUncommented(layout, "if (_rows[i].Pin == MenuRowPin.Top) { if (RowActive(_rows[i])) topPins++; }");
            Assert.False(_runtime.CellRow(KeybindsPane.EditKey)!.Visible);
            Assert.True(KeybindsTabLayout.CategoryRowHeight <= 40f);
        }

        // ---- 7. the game's keys at the title, from the last in-world read -----------------------

        [Fact]
        public void At_the_title_the_game_keys_come_from_the_last_in_world_read_with_a_note()
        {
            ResetArt();
            var saved = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
            KeybindGameCache.UtcNow = () => saved;
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General, description: "Use things"),
                new KeybindClaim(KeybindSource.Game, KeybindArbiter.GameOwnerId, "Primary", "Primary", KeyChord.None,
                    chordText: "Left Mouse", comparable: false, category: GameKeybindCatalog.Skills),
                GameIn("AutoMove", new KeyChord(UiKey.F), GameKeybindCatalog.Movement));
            KeybindArbiter.Invalidate(game: true);
            KeybindArbiter.Current();

            // Leave the world: the game's keys cannot be read any more.
            KeybindArbiter.GameSource = () => GameKeybindSnapshot.Unavailable(KeybindArbiter.GameUnavailableNote);
            OpenKeybindsTab();
            var tab = Tab();
            var ids = tab.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
            Assert.Equal(new[] { "game:movement", "game:skills-and-attacks", "game:general" }, ids);
            Assert.True(tab.GetProperty("gameFromCache").GetBoolean());
            Assert.Equal("As last read in a world today. " + KeybindsTabLayout.GameCapNote, tab.GetProperty("gameNote").GetString());
            var primary = tab.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("action").GetString() == "Primary");
            Assert.Equal("Left Mouse", primary.GetProperty("primary").GetString());
            var interact = tab.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("action").GetString() == "Interact");
            Assert.Equal("Use things.", interact.GetProperty("description").GetString());

            // Another day: the date.
            KeybindGameCache.UtcNow = () => saved.AddDays(3);
            Assert.StartsWith("As last read in a world on September 29, 2026", KeybindGameCache.ReadNote(saved));
        }

        [Fact]
        public void The_game_key_file_keeps_categories_descriptions_key_text_and_mouse_slots()
        {
            string dir = Path.Combine(_dir, "cache");
            KeybindGameCache.Directory = () => dir;
            var snapshot = new GameKeybindSnapshot(true, new[]
            {
                GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General, description: "Use things"),
                new KeybindClaim(KeybindSource.Game, KeybindArbiter.GameOwnerId, "Primary", "Primary", KeyChord.None,
                    chordText: "Left Mouse", comparable: false, category: GameKeybindCatalog.Skills),
            }, "");
            KeybindGameCache.Remember(snapshot);

            // A new process: memory empty, the file read back.
            KeybindGameCache.ResetForTests();
            KeybindGameCache.Directory = () => dir;
            var display = KeybindGameCache.DisplayClaims()!.Value.Claims;
            Assert.Equal(2, display.Count);
            var interact = display.Single(c => c.ActionId == "Interact");
            Assert.Equal(GameKeybindCatalog.General, interact.Category);
            Assert.Equal("Use things", interact.Help);
            Assert.True(interact.Comparable);
            var mouse = display.Single(c => c.ActionId == "Primary");
            Assert.Equal("Left Mouse", mouse.ChordText);
            Assert.False(mouse.Comparable);
            // The remap guard still compares keyboard keys only.
            Assert.Single(KeybindGameCache.Claims()!.Value.Claims);

            // A file written before KEYS3 (six fields) still loads its keyboard keys.
            System.IO.File.WriteAllText(Path.Combine(dir, KeybindGameCache.FileName),
                "saved\t2026-09-28T10:00:00.0000000Z\nInteract\tInteract\tE\t\t0\tGeneral\n");
            KeybindGameCache.ResetForTests();
            KeybindGameCache.Directory = () => dir;
            Assert.Single(KeybindGameCache.DisplayClaims()!.Value.Claims);
            KeybindGameCache.ResetForTests();
        }

        // ---- review M2: the game note and the Game keys jump follow what is shown ---------------

        [Fact]
        public void The_game_note_rides_on_the_first_shown_category_and_the_jump_keeps_the_search()
        {
            ResetArt();
            UseGame(GameIn("AutoMove", new KeyChord(UiKey.F), GameKeybindCatalog.Movement),
                GameIn("RotateCamera", new KeyChord(UiKey.K), GameKeybindCatalog.Camera));
            OpenKeybindsTab();
            var pane = Pane();
            // Requested 2026-09-30: the note sits under the "Game keys" group header, and stays while any
            // game category is shown (a search that hides Movement keeps it).
            string NotedOn() => pane.NotedSection;
            Assert.Equal("game", NotedOn());

            pane.ApplySearch("RotateCamera");
            Assert.False(pane.HeadingOf("game:movement")!.Visible);
            Assert.Equal("game", NotedOn());
            Assert.True(pane.GroupHeaderOf("game")!.Visible);
            Assert.True(pane.JumpTo("game"));
            Assert.Equal("game:camera", pane.LastJump!.Section);
            Assert.Equal("RotateCamera", pane.Query);

            // Nothing shown: the jump clears the search and lands on the first game section.
            pane.ApplySearch("nothing like this");
            Assert.True(pane.JumpTo("game"));
            Assert.Equal("", pane.Query);
            Assert.Equal("game:movement", pane.LastJump!.Section);
        }

        // ---- KEYS3 live: headings are never cut; the note has its own line ----------------------

        [Fact]
        public void No_category_heading_is_truncated_at_a_1236_px_row_and_the_note_has_its_own_line()
        {
            ResetArt();
            KeybindGameCache.UtcNow = () => new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
            var claims = GameKeybindCatalog.CategoryOrder
                .Select((c, i) => GameIn("Action" + i, new KeyChord(new[] { UiKey.F1, UiKey.F2, UiKey.F3, UiKey.F4, UiKey.F5, UiKey.F6, UiKey.F7, UiKey.F8, UiKey.F11 }[i]), c)).ToArray();
            UseGame(claims);
            KeybindArbiter.Invalidate(game: true);
            KeybindArbiter.Current();
            // At the title, from the snapshot: the longest note ("As last read in a world today. …").
            KeybindArbiter.GameSource = () => GameKeybindSnapshot.Unavailable(KeybindArbiter.GameUnavailableNote);
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            Assert.StartsWith("As last read in a world today.", pane.GameHeadingNote);
            foreach (var section in pane.Model!.Sections)
            {
                AssertHeadingTitleTakesTheRow(pane, section.Id);
                Assert.DoesNotContain(((IUiCellRow)pane.HeadingOf(section.Id)!).Cells, c => c.Text == pane.GameHeadingNote);
            }

            // The note is a line of its own, shown under the first game heading only.
            Assert.Equal("game", pane.NotedSection);
            var widgets = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"))).RootElement.EnumerateArray().ToList();
            Assert.Single(widgets, w => w.TryGetProperty("label", out var t) && t.GetString() == pane.GameHeadingNote);
        }

        /// <summary>
        /// KEYS3 live K13: "Movement" drew as "Movem…" because its cell was only as wide as the caption's
        /// estimate. The drawn title cell must take the whole 1236 px row left of the heading's own right
        /// cells, which is also well over the caption's estimated width at the heading size.
        /// </summary>
        private static void AssertHeadingTitleTakesTheRow(KeybindsPane pane, string sectionId)
        {
            const float row = 1236f;
            var heading = (IUiCellRow)pane.HeadingOf(sectionId)!;
            var title = heading.Cells.Single(c => c.Key == KeybindsPane.CellTitle);
            var placed = MenuPanelLayout.CellColumns(row, new CellRowSpec("probe", heading.Cells, rightInset: heading.RightInset));
            var visible = placed.Where(p => p.Visible).ToList();
            if (heading.Cells.Any(c => c.Key == KeybindsPane.CellOpenPage))
            {
                // Requested 2026-09-30: a mod heading with a page is its name, then the page icon right
                // after it; the name keeps the kit's measuring slack over its estimate, so it is not cut.
                var t = visible.Single(p => p.Key == KeybindsPane.CellTitle);
                var icon = visible.Single(p => p.Key == KeybindsPane.CellOpenPage);
                float est = CellLayout.EstimateTextWidth(title.Text, KeybindsTabLayout.CategoryFontSize);
                Assert.True(t.Width >= est * KeybindsTabLayout.HeadingTitleSlack, title.Text + " gets " + t.Width + " px for a " + est + " px caption");
                Assert.Equal(t.Right + CellLayout.Gap, icon.Left, 1);
                Assert.True(visible.Single(p => p.Key == KeybindsPane.CellCount).Left > icon.Right);
                return;
            }

            Assert.Equal(CellWidthMode.Fill, title.Width.Mode);
            float width = visible.Single(p => p.Key == KeybindsPane.CellTitle).Width;
            float others = visible.Where(p => p.Key != KeybindsPane.CellTitle).Sum(p => p.Width);
            float expected = row - heading.RightInset - others - (visible.Count - 1) * CellLayout.Gap;
            Assert.Equal(expected, width, 1);
            float caption = CellLayout.EstimateTextWidth(title.Text, KeybindsTabLayout.CategoryFontSize);
            Assert.True(width >= caption * 2f, title.Text + " gets " + width + " px for a " + caption + " px caption");
        }

        [Fact]
        public void In_a_world_the_first_game_heading_takes_the_row_too()
        {
            ResetArt();
            UseGame(GameIn("AutoMove", new KeyChord(UiKey.F), GameKeybindCatalog.Movement),
                GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General));
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            Assert.False(pane.Model!.GameFromCache);
            Assert.Equal("game", pane.NotedSection);
            foreach (var section in pane.Model.Sections) AssertHeadingTitleTakesTheRow(pane, section.Id);
            // Without the note (a search that hides nothing in Movement but the note's line is not the cause).
            pane.ApplySearch("AutoMove");
            AssertHeadingTitleTakesTheRow(pane, "game:movement");
        }

        // ---- KEYS3 live K8: an owned copy of the borrowed sprites ----------------------------------

        [Fact]
        public void The_owned_copy_plan_copies_a_plain_atlas_region_and_refuses_what_it_cannot_copy()
        {
            var none = (0f, 0f, 0f, 0f);
            // A 64x32 sprite at (128, 256) in a 1024x512 atlas.
            var plan = GameArtLadder.PlanCopy(64f, 32f, 1024, 512, (128f / 1024f, 256f / 512f, 192f / 1024f, 288f / 512f), none, rotated: false);
            Assert.True(plan.Ok, plan.Why);
            Assert.Equal((64, 32), (plan.Width, plan.Height));
            Assert.Equal(64f / 1024f, plan.ScaleX, 5);
            Assert.Equal(32f / 512f, plan.ScaleY, 5);
            Assert.Equal(128f / 1024f, plan.OffsetX, 5);
            Assert.Equal(256f / 512f, plan.OffsetY, 5);
            // A whole-texture sprite.
            Assert.True(GameArtLadder.PlanCopy(40f, 40f, 40, 40, (0f, 0f, 1f, 1f), none, false).Ok);

            Assert.Equal("rotated in its atlas", GameArtLadder.PlanCopy(64f, 32f, 1024, 512, (0f, 0f, 0.0625f, 0.0625f), none, true).Why);
            // Live K13: a packing-trimmed sprite (the glow) is padded back to its full rect.
            var trimmed = GameArtLadder.PlanCopy(64f, 32f, 1024, 512, (0f, 0f, 60f / 1024f, 30f / 512f), (3f, 2f, 1f, 0f), false);
            Assert.True(trimmed.Ok, trimmed.Why);
            Assert.True(trimmed.Padded);
            Assert.Equal((60, 30, 64, 32, 3, 2), (trimmed.Width, trimmed.Height, trimmed.FullWidth, trimmed.FullHeight, trimmed.DestX, trimmed.DestY));
            // Live K13: a thin, wide ornament (1664x16) is within the area limit.
            var wide = GameArtLadder.PlanCopy(1664f, 16f, 2048, 64, (0f, 0f, 1664f / 2048f, 16f / 64f), none, false);
            Assert.True(wide.Ok, wide.Why);
            Assert.False(wide.Padded);
            Assert.StartsWith("texture region 128x32",
                GameArtLadder.PlanCopy(64f, 32f, 1024, 512, (0f, 0f, 0.125f, 0.0625f), none, false).Why);
            Assert.Equal("no texture size", GameArtLadder.PlanCopy(64f, 32f, 0, 0, (0f, 0f, 1f, 1f), none, false).Why);
            Assert.StartsWith("size 2048x2048", GameArtLadder.PlanCopy(2048f, 2048f, 2048, 2048, (0f, 0f, 1f, 1f), none, false).Why);

            // The Unity side: a GPU copy into our own texture and sprite, every borrowed part, owned copies
            // destroyed on unload, and the copy preferred when drawing.
            string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindArtDonors.cs");
            string store = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "OwnedSprites.cs");
            string copy = SourcePin.MethodBody(store, "internal static Sprite? CopySprite(");
            SourcePin.AssertUncommented(copy, "Graphics.Blit(src, rt, new Vector2(plan.ScaleX, plan.ScaleY), new Vector2(plan.OffsetX, plan.OffsetY))");
            SourcePin.AssertUncommented(copy, "made.ReadPixels(new Rect(0f, 0f, plan.Width, plan.Height), plan.DestX, plan.DestY)");
            SourcePin.AssertUncommented(copy, "GL.Clear(false, true, new Color(0f, 0f, 0f, 0f))");
            SourcePin.AssertUncommented(copy, "RenderTexture.ReleaseTemporary(clear)");
            SourcePin.AssertUncommented(copy, "SpriteMeshType.FullRect, border)");
            SourcePin.AssertUncommented(copy, "RenderTexture.ReleaseTemporary(rt)");
            SourcePin.AssertUncommented(copy, "hideFlags = HideFlags.HideAndDontSave");
            SourcePin.AssertUncommented(SourcePin.MethodBody(store, "private void EnsureCopy("), "CopySprite(sprite, OwnedName(sprite), out var texture, out string why, keepReadable: true)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(store, "internal void Release()"), "DropOwnedCopy(i)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void Hold("), "Art.Hold(i, part.Sprite)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "internal static void Release()"), "Art.Release()");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static bool TryUse("), "if (OwnedUsable(i)) sprite = Art.Owned(i)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "internal static void Prepare()"),
                "KeybindArtLadder.Next(liveFound, ownedUsable, cachedUsable, kept, residentHit != null)");
        }

        // ---- review L1: a conflict is visible without hovering ----------------------------------

        [Fact]
        public void Conflict_and_reserved_rows_carry_a_short_inline_marker()
        {
            ResetArt();
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            Keybinds.Register(_owner, "first.mod", "Ping", File("first"), F9, "", () => { });
            Keybinds.Register(_owner, "second.mod", "Ping", File("second"), F9, "", () => { });
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Close", new KeyboardShortcut("F4 + LeftAlt"), "");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            OpenKeybindsTab();
            CellSpec Marker(string rowId) => RowOf(rowId).Cells().Single(c => c.Key == KeybindsTabLayout.CellMarker);
            var loser = Marker(ModRowId("second.mod"));
            Assert.True(loser.Visible);
            Assert.Equal(KeybindsTabLayout.MarkerNotActive, loser.Text);
            Assert.Equal(CellTone.Warn, loser.Tone);
            Assert.False(Marker(ModRowId("first.mod")).Visible);
            string close = Pane().Model!.ModRows().Single(r => r.Claim.ActionId == "Hotkeys/Close").Id;
            Assert.Equal(KeybindsTabLayout.MarkerReserved, Marker(close).Text);

            // The marker sits left of the caps and takes no width when hidden, so the caps stay aligned.
            var placed = MenuPanelLayout.CellColumns(1200f, RowOf(ModRowId("second.mod")).Cells());
            Assert.True(placed.Single(p => p.Key == KeybindsTabLayout.CellMarker).Right <= placed.Single(p => p.Key == KeybindsPane.CellKey).Left);
            var winnerPlaced = MenuPanelLayout.CellColumns(1200f, RowOf(ModRowId("first.mod")).Cells());
            Assert.Equal(placed.Single(p => p.Key == KeybindsPane.CellKey).Left, winnerPlaced.Single(p => p.Key == KeybindsPane.CellKey).Left, 1);
        }

        // ---- review L2: the bridge's hover goes through the row's pointer slot -------------------

        [Fact]
        public void The_bridge_hover_uses_the_rows_pointer_slot()
        {
            ResetArt();
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General));
            OpenKeybindsTab();
            string game = GameRowId("Interact");
            Assert.Equal(NameRowOf(game), Pane().NameRowKeyOf(game));
            ModsMenu.KeysOp("hover", game, null);
            Assert.Equal(game, Pane().HoveredRow);
            ModsMenu.KeysOp("hover", "", null);
            Assert.Equal("", Pane().HoveredRow);
            string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.Keybinds.cs");
            SourcePin.AssertUncommented(menu, "UiCellRowHandle.TryHover(pane.NameRowKeyOf(hoverRow), entered)");
        }
    }
}
