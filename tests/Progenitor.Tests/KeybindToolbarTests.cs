using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// Requests 2026-09-30 (Keybinds tab screenshot): the mods' keys before the game's, each under a
    /// group header with a stronger separator than between mod sections; a toolbar in three visibly
    /// separate groups; the "Open mod page" icon right after the mod's name; and the tab opening on Mod
    /// keys. Built from existing kit primitives only. Game-free; nothing here ran in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private void TwoModsAndTheGame()
        {
            RegisterMod("zeta.mod", "Zeta");
            RegisterMod("alpha.mod", "Alpha");
            UseGame(GameIn("AutoMove", new KeyChord(UiKey.F), GameKeybindCatalog.Movement),
                GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General));
            Keybinds.Register(_owner, "zeta.mod", "Open", File("zeta"), F8, "", () => { });
            Keybinds.Register(_owner, "alpha.mod", "Show", File("alpha"), F9, "", () => { });
        }

        private List<string> LiveRowKeys() => _runtime.CellRows.Where(r => !r.Released).Select(r => r.Text).ToList();

        // ---- 1. order: Mod keys, then Game keys ----------------------------------------------

        [Fact]
        public void Mod_keys_come_first_then_the_games_categories_each_under_a_group_header()
        {
            ResetArt();
            TwoModsAndTheGame();
            using var zeta = ModsMenu.Register(_owner, "zeta.mod", "Zeta");
            using var alpha = ModsMenu.Register(_owner, "alpha.mod", "Alpha");
            OpenKeybindsTab();
            var pane = Pane();
            Assert.Equal(new[] { "mod:alpha.mod", "mod:zeta.mod", "game:movement", "game:general" },
                pane.Model!.Sections.Select(s => s.Id).ToArray());

            // The rows are built in that order, each group under its header.
            var keys = LiveRowKeys();
            int modGroup = keys.IndexOf(KeybindsPane.GroupKeyPrefix + "mods");
            int gameGroup = keys.IndexOf(KeybindsPane.GroupKeyPrefix + "game");
            int firstMod = keys.IndexOf(((IUiCellRow)pane.HeadingOf("mod:alpha.mod")!).Key);
            int firstGame = keys.IndexOf(((IUiCellRow)pane.HeadingOf("game:movement")!).Key);
            Assert.True(modGroup >= 0 && modGroup < firstMod && firstMod < gameGroup && gameGroup < firstGame);
            Assert.Equal(KeybindsTabLayout.ModGroupTitle, pane.GroupHeaderOf("mods")!.Cells.Single().Text);
            Assert.Equal(KeybindsTabLayout.GameGroupTitle, pane.GroupHeaderOf("game")!.Cells.Single().Text);

            // Readback and jump targets follow the same order.
            var tab = Tab();
            Assert.Equal("mod:alpha.mod", tab.GetProperty("sections")[0].GetProperty("id").GetString());
            var targets = tab.GetProperty("toolbar").GetProperty("jumpTargets").EnumerateArray().Select(t => t.GetProperty("target").GetString()).ToList();
            Assert.Equal(new[] { "mods", "game", "mod:alpha.mod", "mod:zeta.mod" }, targets);
            Assert.Equal("mod:alpha.mod", tab.GetProperty("rows")[0].GetProperty("section").GetString());

            // The Game group carries the note on its own muted line.
            Assert.Equal("game", pane.NotedSection);
        }

        // ---- 2. the toolbar's groups -------------------------------------------------------------

        [Fact]
        public void The_toolbar_has_a_filter_choice_quiet_section_jumps_and_a_mod_picker()
        {
            var cells = KeybindsTabLayout.ToolbarCells("", KeybindsFilter.Conflicts, 2, KeybindsPane.JumpMods);
            Assert.Equal(new[]
            {
                KeybindsPane.CellSearch, KeybindsTabLayout.CellFilterGap, KeybindsTabLayout.CellShow,
                KeybindsPane.CellAll, KeybindsPane.CellConflicts, KeybindsPane.CellChanged,
                KeybindsTabLayout.CellJumpGap, KeybindsPane.CellModJump, KeybindsPane.CellGameJump, KeybindsPane.CellJumpToMod,
            }, cells.Select(c => c.Key).ToArray());
            CellSpec C(string key) => cells.Single(c => c.Key == key);

            // One three-way choice: the chosen one in the selected accent, the other two muted.
            Assert.Equal(CellTone.Gold, C(KeybindsPane.CellConflicts).Tone);
            Assert.Equal(CellTone.Muted, C(KeybindsPane.CellAll).Tone);
            Assert.Equal(CellTone.Muted, C(KeybindsPane.CellChanged).Tone);
            Assert.Equal("Conflicts 2", C(KeybindsPane.CellConflicts).Text);
            Assert.Equal(KeybindsTabLayout.ShowCaption, C(KeybindsTabLayout.CellShow).Text);

            // Quiet jumps: small captions with an arrow; the current section's is bright, the other muted.
            Assert.StartsWith("↓", C(KeybindsPane.CellModJump).Text);
            Assert.Equal(CellTone.Normal, C(KeybindsPane.CellModJump).Tone);
            Assert.Equal(CellTone.Muted, C(KeybindsPane.CellGameJump).Tone);
            Assert.Equal(KeybindsTabLayout.MarkerFontSize, C(KeybindsPane.CellModJump).FontSize);

            // The picker: a bar with a chevron (the game's key-cap sprite), never dropped.
            Assert.Equal(CellSkin.KeyCap, C(KeybindsPane.CellJumpToMod).Skin);
            Assert.EndsWith("▼", C(KeybindsPane.CellJumpToMod).Text);
            Assert.False(C(KeybindsPane.CellJumpToMod).Optional);
            Assert.False(C(KeybindsPane.CellSearch).Optional);

            // The drawn tab: the filter re-tones in place; a jump makes its section current.
            ResetArt();
            TwoModsAndTheGame();
            OpenKeybindsTab();
            var pane = Pane();
            var bar = _runtime.CellRow(KeybindsPane.ToolbarKey)!;
            pane.SetFilter(KeybindsFilter.Changed);
            Assert.Equal(CellTone.Gold, bar.Cells().Single(c => c.Key == KeybindsPane.CellChanged).Tone);
            Assert.Equal(CellTone.Muted, bar.Cells().Single(c => c.Key == KeybindsPane.CellAll).Tone);
            pane.SetFilter(KeybindsFilter.All);
            Assert.True(UiCellRowHandle.TryInvoke(KeybindsPane.ToolbarKey + "/" + KeybindsPane.CellGameJump, out _));
            Assert.Equal(KeybindsPane.JumpGame, pane.CurrentJump);
            Assert.Equal(CellTone.Normal, bar.Cells().Single(c => c.Key == KeybindsPane.CellGameJump).Tone);
            Assert.Equal(CellTone.Muted, bar.Cells().Single(c => c.Key == KeybindsPane.CellModJump).Tone);
            Assert.Equal("game", Tab().GetProperty("toolbar").GetProperty("currentJump").GetString());
        }

        [Theory]
        [InlineData(1236f)]
        [InlineData(1000f)]
        [InlineData(800f)]
        [InlineData(640f)]
        public void The_toolbar_groups_stay_apart_and_inside_the_row(float rowWidth)
        {
            var cells = KeybindsTabLayout.ToolbarCells("", KeybindsFilter.All, 0, KeybindsPane.JumpMods);
            var placed = MenuPanelLayout.CellColumns(rowWidth, new CellRowSpec("probe", cells, rightInset: HeadBlockLayout.RightColumnInset));
            var visible = placed.Where(p => p.Visible).ToList();
            foreach (var p in visible)
            {
                Assert.True(p.Left >= -rowWidth * .5f - .5f, p.Key);
                Assert.True(p.Right <= rowWidth * .5f - HeadBlockLayout.RightColumnInset + .5f, p.Key);
            }
            for (int i = 0; i < placed.Length; i++)
                for (int j = i + 1; j < placed.Length; j++)
                    Assert.False(CellLayout.Overlap(placed[i], placed[j]), placed[i] + " overlaps " + placed[j]);
            Assert.Contains(visible, p => p.Key == KeybindsPane.CellSearch);
            Assert.Contains(visible, p => p.Key == KeybindsPane.CellJumpToMod);
            Assert.Contains(visible, p => p.Key == KeybindsPane.CellAll);
            if (rowWidth >= 1236f)
            {
                // Every group, and the gaps that set them apart, at the design width.
                Assert.Equal(cells.Count, visible.Count);
                float At(string key) => visible.Single(p => p.Key == key).Left;
                float Right(string key) => visible.Single(p => p.Key == key).Right;
                Assert.True(At(KeybindsPane.CellModJump) - Right(KeybindsPane.CellChanged) >= KeybindsTabLayout.GroupGap + CellLayout.Gap);
                Assert.True(At(KeybindsPane.CellAll) - Right(KeybindsPane.CellSearch) >= KeybindsTabLayout.GroupGap);
            }
        }

        // ---- 3. the page icon right after the mod's name -----------------------------------------

        [Fact]
        public void Open_mod_page_is_an_icon_right_after_the_mod_name_with_hover_help()
        {
            ResetArt();
            TwoModsAndTheGame();
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F7, "", () => { });
            OpenKeybindsTab();
            var heading = (IUiCellRow)Pane().HeadingOf("mod:a.mod")!;
            var keys = heading.Cells.Select(c => c.Key).ToList();
            Assert.Equal(keys.IndexOf(KeybindsPane.CellTitle) + 1, keys.IndexOf(KeybindsPane.CellOpenPage));
            var icon = heading.Cells.Single(c => c.Key == KeybindsPane.CellOpenPage);
            Assert.Equal(KeybindsTabLayout.OpenModPageGlyph, icon.Text);
            Assert.Equal(CellAlign.Left, icon.Align);
            // Live 2026-09-30: a plain inline glyph (text, no button chrome), muted, about one space after
            // the name: the title is its estimate times a small slack, so the glyph follows it closely.
            Assert.Equal(CellKind.Text, icon.Kind);
            Assert.Equal(CellTone.Muted, icon.Tone);
            var title = heading.Cells.Single(c => c.Key == KeybindsPane.CellTitle);
            float est = CellLayout.EstimateTextWidth("Atlas", KeybindsTabLayout.CategoryFontSize);
            Assert.True(title.Width.Value <= est * 1.2f + 4f, "the glyph is not pushed far from a short name");
            Assert.True(KeybindsTabLayout.TitleFits(icon.FontSize, KeybindsTabLayout.ModHeadingHeight, icon.VAlign));
            Assert.Equal(CellAlign.Right, heading.Cells.Single(c => c.Key == KeybindsPane.CellCount).Align);
            Assert.DoesNotContain(heading.Cells, c => c.Text == KeybindsTabLayout.OpenModPageCaption);

            // Hovering the heading brightens the glyph and says what it does; a click opens the page.
            Assert.True(UiCellRowHandle.TryHover(heading.Key, entered: true));
            Assert.Equal(CellTone.Normal, heading.Cells.Single(c => c.Key == KeybindsPane.CellOpenPage).Tone);
            var d = Tab().GetProperty("description");
            Assert.Equal("Atlas", d.GetProperty("heading").GetString());
            Assert.Equal(KeybindsTabLayout.OpenModPageHint, d.GetProperty("hint").GetString());
            Assert.True(UiCellRowHandle.TryHover(heading.Key, entered: false));
            Assert.Equal(CellTone.Muted, heading.Cells.Single(c => c.Key == KeybindsPane.CellOpenPage).Tone);
            Assert.Equal(KeybindsTabLayout.DescriptionTitle, Tab().GetProperty("description").GetProperty("heading").GetString());
            Assert.True(UiCellRowHandle.TryInvoke(heading.Key, out _));
            Assert.Equal(ModsTab.Mods, ModsMenu.SelectedTabForTests);
        }

        // ---- default focus: Mod keys ---------------------------------------------------------------

        [Fact]
        public void The_tab_opens_on_Mod_keys_and_a_mod_link_still_lands_on_its_mod()
        {
            ResetArt();
            TwoModsAndTheGame();
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F7, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            Assert.Equal(KeybindsPane.JumpMods, pane.CurrentJump);
            Assert.Equal("mods", pane.LastJump!.Target);
            Assert.Same(RuntimeOf(pane.GroupHeaderOf("mods")), _reveals[^1].First);
            Assert.Equal(CellTone.Normal, _runtime.CellRow(KeybindsPane.ToolbarKey)!.Cells().Single(c => c.Key == KeybindsPane.CellModJump).Tone);

            // A link from the Mods page names a mod: that mod, not the default.
            ModsMenu.OpenInKeybinds("zeta.mod");
            ModsMenu.SelectTab("mods");
            ModsMenu.OpenInKeybinds("zeta.mod");
            Assert.Equal("mod:zeta.mod", Pane().LastJump!.Section);

            // A rebuild after a save keeps the scroll: no default jump on the new pane.
            string id = Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == "a.mod").Id;
            Pane().CaptureForBridge(id, new KeyChord(UiKey.K, KeyModifiers.Ctrl));
            Pane().SaveEdit();
            Assert.Null(Pane().LastJump);

            // Coming back to the tab starts at Mod keys again (the default wins over a remembered scroll).
            ModsMenu.SelectTab("mods");
            ModsMenu.SelectTab("keybinds");
            Assert.Equal("mods", Pane().LastJump!.Target);

            string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.Keybinds.cs");
            SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void BuildKeybindsPanes"), "else if (!keep) _keybindsPane?.OpenOnDefault();");
        }

        // ---- separators: the spacing hierarchy ------------------------------------------------

        [Fact]
        public void The_group_divider_is_stronger_than_the_space_between_mods_which_is_more_than_a_row_gap()
        {
            Assert.True(KeybindsTabLayout.GroupSpaceAbove > KeybindsTabLayout.ModSpaceAbove);
            Assert.True(KeybindsTabLayout.ModSpaceAbove > PanelLayout.RowGap);
            Assert.True(KeybindsTabLayout.GroupSpaceAbove >= 24f && KeybindsTabLayout.GroupSpaceAbove <= 32f);
            Assert.True(KeybindsTabLayout.ModSpaceAbove >= 8f && KeybindsTabLayout.ModSpaceAbove <= 12f);
            Assert.True(KeybindsTabLayout.GroupSpaceAbove + KeybindsTabLayout.GroupHeaderHeight > KeybindsTabLayout.ModHeadingHeight);
            // Live 2026-09-30: every title is drawn in its row (a 28 px title bottom-aligned in one tall
            // row was not drawn at all): the group title fills its own row; headings fit bottom-aligned.
            Assert.True(KeybindsTabLayout.TitleFits(KeybindsTabLayout.GroupFontSize, KeybindsTabLayout.GroupHeaderHeight, CellVAlign.Middle));
            Assert.False(KeybindsTabLayout.TitleFits(KeybindsTabLayout.GroupFontSize, 74f, CellVAlign.Bottom), "the live defect");
            Assert.True(KeybindsTabLayout.TitleFits(KeybindsTabLayout.CategoryFontSize, KeybindsTabLayout.ModHeadingHeight, CellVAlign.Bottom));
            Assert.True(KeybindsTabLayout.TitleFits(KeybindsTabLayout.CategoryFontSize, KeybindsTabLayout.CategoryRowHeight, CellVAlign.Bottom));
            Assert.True(KeybindsTabLayout.ModHeadingHeight > KeybindsTabLayout.CategoryRowHeight);
            Assert.True(KeybindsTabLayout.GroupFontSize > KeybindsTabLayout.CategoryFontSize);

            // No borrowed ornament (game-free): the kit divider follows each group header; the title sits
            // at the row's bottom, so the space is above it; mod headings are taller than game ones.
            ResetArt();
            TwoModsAndTheGame();
            RegisterMod("a.mod", "Atlas");
            using var reg = ModsMenu.Register(_owner, "a.mod", "Atlas");
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F7, "", () => { });
            OpenKeybindsTab();
            var pane = Pane();
            foreach (var g in new[] { "mods", "game" })
            {
                var header = pane.GroupHeaderOf(g)!;
                var caption = header.Cells.Single();
                Assert.NotEqual(CellVAlign.Bottom, caption.VAlign);
                Assert.Equal(KeybindsTabLayout.GroupFontSize, caption.FontSize);
                Assert.True(KeybindsTabLayout.TitleFits(caption.FontSize, RowHeightOf(header.Key), caption.VAlign));
                // The caption cell has its text and a real width when placed.
                Assert.False(string.IsNullOrEmpty(caption.Text));
                var placed = MenuPanelLayout.CellColumns(1236f, new CellRowSpec("probe", header.Cells, rightInset: header.RightInset));
                Assert.True(placed.Single(p => p.Key == KeybindsPane.CellGroupTitle).Width
                    >= CellLayout.EstimateTextWidth(caption.Text, caption.FontSize));
                var readCaption = ((List<object?>)UiCellRowHandle.DescribeVisible(header.Key)!["cells"]!)
                    .Cast<Dictionary<string, object?>>().Single(c => (string?)c["key"] == KeybindsPane.CellGroupTitle);
                Assert.Equal(caption.Text, readCaption["text"]);
                // The space above the group is its own row, about 30 px.
                Assert.Equal(KeybindsTabLayout.GroupSpaceAbove, RowHeightOf(KeybindsPane.GroupSpaceKeyPrefix + g));
                var read = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content"))).RootElement.EnumerateArray()
                    .Single(w => w.TryGetProperty(KeybindsPane.RowGroup, out var r) && r.GetProperty("id").GetString() == g)
                    .GetProperty(KeybindsPane.RowGroup);
                Assert.Equal("kit", read.GetProperty("divider").GetString());
            }

            string modHeading = ((IUiCellRow)pane.HeadingOf("mod:a.mod")!).Key;
            string gameHeading = ((IUiCellRow)pane.HeadingOf("game:movement")!).Key;
            Assert.True(RowHeightOf(modHeading) > RowHeightOf(gameHeading));
            Assert.Equal(KeybindsTabLayout.GroupHeaderHeight, RowHeightOf(KeybindsPane.GroupKeyPrefix + "mods"));

            // With the game's ornament borrowed, the group divider is that image, full width.
            var sprite = new object();
            KeybindArtLadder.PrepareHook = () => KeybindArtLadder.Use(new KeybindArt(
                KeybindArt.Part.Kit, KeybindArt.Part.Kit,
                new KeybindArt.Part(GameArtSource.Resident, KeybindArt.DonorOptionsDetails, "Divider", sprite)));
            ModsMenu.SelectTab("mods");
            ModsMenu.SelectTab("keybinds");
            var divider = _runtime.CellRow(KeybindsPane.GroupDividerKeyPrefix + "game")!;
            Assert.Same(sprite, divider.Images[KeybindsPane.CellGroupDivider]);
            Assert.Equal(1f, divider.Cells().Single().Width.Value);
            ResetArt();
        }

        /// <summary>A visible cell row's height as its readback reports it.</summary>
        private static float RowHeightOf(string key) =>
            Convert.ToSingle(UiCellRowHandle.DescribeVisible(key)!["height"], System.Globalization.CultureInfo.InvariantCulture);
    }
}
