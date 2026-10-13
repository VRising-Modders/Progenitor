using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// KEYS3 (docs/mods-menu.md#keybinds-tab): the Keybinds tab restyled after the game's own Bindings
    /// Menu — the game's categories, two key caps per row, the description column with hover and
    /// selection, the gutter, and the donor ladder for the borrowed visuals with its kit fallback.
    /// Game-free: the fake UI runtime and fake game bindings of <see cref="KeybindTabTests"/>. Nothing
    /// here has run in the game.
    /// </summary>
    public sealed partial class KeybindTabTests
    {
        private static KeybindClaim GameIn(string action, KeyChord chord, string category, int slot = 0, string description = "") =>
            new(KeybindSource.Game, KeybindArbiter.GameOwnerId, action, action, chord, help: description, slot: slot, category: category);

        private static JsonElement Tab() => JsonDocument.Parse(JsonSerializer.Serialize(Pane().Describe())).RootElement;

        private CellRowTests.FakeCellRow RowOf(string rowId) => _runtime.CellRow(NameRowOf(rowId))!;

        private static string GameRowId(string action) =>
            Pane().Model!.Sections.SelectMany(s => s.Rows).Single(r => r.IsGame && r.Claim.ActionId == action).Id;

        private static string ModRowId(string owner) => Pane().Model!.ModRows().Single(r => r.Claim.OwnerId == owner).Id;

        /// <summary>The holder's name as the donor source declares it (the donor file is not in the test build).</summary>
        private static string KeybindArtDonorsHolderName =>
            UiOwnership.Prefix + "core-keybind-art";

        private static void ResetArt()
        {
            KeybindArtLadder.PrepareHook = null;
            KeybindArtLadder.Reset();
            GameKeybindCategories.Reset();
        }

        // ---- 1. the game's categories, in the game's order --------------------------------------

        [Fact]
        public void Game_keys_are_grouped_under_the_games_categories_in_the_games_order()
        {
            ResetArt();
            var claims = new[]
            {
                GameIn("Building_Place", new KeyChord(UiKey.F), GameKeybindCatalog.Building),
                GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General),
                GameIn("Mystery", new KeyChord(UiKey.K), "Other things"),
                GameIn("AutoMove", KeyChord.None, GameKeybindCatalog.Movement),
                GameIn("Chat", new KeyChord(UiKey.Enter), GameKeybindCatalog.General),
                GameIn("ActionBar1", new KeyChord(UiKey.Digit1), GameKeybindCatalog.ActionBar),
            };
            var model = KeybindsTabModel.Build(KeybindArbiter.Compute(claims, Array.Empty<KeybindClaim>(), Array.Empty<KeybindClaim>()), null);
            Assert.Equal(new[] { "game:movement", "game:general", "game:action-bar", "game:building", "game:other-things" },
                model.Sections.Select(s => s.Id).ToArray());
            Assert.Equal(new[] { "Movement", "General", "Action Bar", "Building", "Other things" }, model.Sections.Select(s => s.Title).ToArray());
            // Rows keep the reader's (the page's) order inside a category.
            Assert.Equal(new[] { "Interact", "Chat" }, model.Sections[1].Rows.Select(r => r.Claim.ActionId).ToArray());
            Assert.All(model.Sections, s => Assert.True(s.IsGame));
            Assert.Equal("game:skills-and-attacks", KeybindsTabLayout.GameCategorySectionId(GameKeybindCatalog.Skills));
            // The page's seven, then KEYS4's Other game keys and Debug keys (ours).
            Assert.Equal(9, GameKeybindCatalog.CategoryOrder.Count);
            Assert.Equal("LK_Category_SkillsAndAttacks", GameKeybindCatalog.CategoryKeyField(GameKeybindCatalog.Skills));

            // The game's own localized titles replace the catalog's words once read.
            GameKeybindCategories.Use(new Dictionary<string, string> { [GameKeybindCatalog.General] = "Allgemein", ["x"] = " " }, "live");
            Assert.Equal(GameKeybindCategories.SourceGame, GameKeybindCategories.Source);
            var localized = KeybindsTabModel.Build(KeybindArbiter.Compute(claims, Array.Empty<KeybindClaim>(), Array.Empty<KeybindClaim>()), null);
            Assert.Equal("Allgemein", localized.Sections[1].Title);
            Assert.Equal("Movement", localized.Sections[0].Title);
            // A later miss never forgets a good read.
            GameKeybindCategories.Use(new Dictionary<string, string>(), "resident");
            Assert.Equal("Allgemein", GameKeybindCategories.TitleOf(GameKeybindCatalog.General));
            Assert.Equal("live", GameKeybindCategories.From);

            // The drawn tab: one heading per category, the jump to "game" lands on the first one.
            UseGame(claims);
            OpenKeybindsTab();
            var sections = Tab().GetProperty("sections").EnumerateArray().ToList();
            Assert.Equal("game:movement", sections[0].GetProperty("id").GetString());
            Assert.Equal("Allgemein", sections[1].GetProperty("title").GetString());
            Assert.Equal("game", sections[1].GetProperty("titleSource").GetString());
            Assert.Equal(GameKeybindCatalog.General, sections[1].GetProperty("category").GetString());
            Assert.Equal(KeybindsTabLayout.GameGroupTitle, ((IUiCellRow)Pane().GroupHeaderOf("game")!).Cells.Single().Text);
            Assert.True(Pane().JumpTo("game"));
            Assert.Equal("game:movement", Pane().LastJump!.Section);
            ResetArt();
        }

        // ---- 2. primary and secondary caps ------------------------------------------------------

        [Fact]
        public void Each_row_has_a_primary_and_a_secondary_key_cap()
        {
            ResetArt();
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General),
                GameIn("Interact", new KeyChord(UiKey.F), GameKeybindCatalog.General, slot: 1),
                GameIn("Chat", new KeyChord(UiKey.Enter), GameKeybindCatalog.General),
                GameIn("Chat", KeyChord.None, GameKeybindCatalog.General, slot: 1));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "Opens it.", () => { });
            OpenKeybindsTab();

            string Cap(string rowId, string key) => RowOf(rowId).Cells().Single(c => c.Key == key).Text;
            CellTone Tone(string rowId, string key) => RowOf(rowId).Cells().Single(c => c.Key == key).Tone;
            Assert.Equal("E", Cap(GameRowId("Interact"), KeybindsPane.CellKey));
            Assert.Equal("F", Cap(GameRowId("Interact"), KeybindsPane.CellKey2));
            // An unbound slot is an empty cap, still live on a game row.
            Assert.Equal("", Cap(GameRowId("Chat"), KeybindsPane.CellKey2));
            Assert.Equal(CellTone.Normal, Tone(GameRowId("Chat"), KeybindsPane.CellKey2));
            // A mod key has one chord: the secondary cap is empty and disabled.
            Assert.Equal("F9", Cap(ModRowId("a.mod"), KeybindsPane.CellKey));
            // Decided 2026-09-30: a mod row has no second cap, only the space where it would be.
            Assert.DoesNotContain(RowOf(ModRowId("a.mod")).Cells(), c => c.Key == KeybindsPane.CellKey2);
            var space = RowOf(ModRowId("a.mod")).Cells().Single(c => c.Key == KeybindsTabLayout.CellSecondarySpace);
            Assert.Equal(CellKind.Muted, space.Kind);
            Assert.Equal("", space.Text);
            Assert.Equal(KeybindsTabLayout.KeyCapWidth, space.Width.Value);

            var rows = Tab().GetProperty("rows").EnumerateArray().ToList();
            var interact = rows.Single(r => r.GetProperty("action").GetString() == "Interact");
            Assert.Equal("E", interact.GetProperty("primary").GetString());
            Assert.Equal("F", interact.GetProperty("secondaryCap").GetString());
            Assert.True(interact.GetProperty("secondaryEnabled").GetBoolean());
            Assert.Equal("General", interact.GetProperty("categoryTitle").GetString());
            var open = rows.Single(r => r.GetProperty("owner").GetString() == "a.mod");
            Assert.Equal("F9", open.GetProperty("primary").GetString());
            Assert.False(open.TryGetProperty("secondaryCap", out _), "a mod row reports no second cap");
            Assert.False(open.TryGetProperty("secondaryCaption", out _));
            Assert.False(open.GetProperty("secondaryEnabled").GetBoolean());
        }

        // ---- 3. the read-only note on a game cap ------------------------------------------------

        [Fact]
        public void A_game_cap_click_shows_the_read_only_note_and_never_starts_a_capture()
        {
            ResetArt();
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General, description: "Interact with the world"));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            string game = GameRowId("Interact");

            foreach (var cap in new[] { KeybindsPane.CellKey, KeybindsPane.CellKey2 })
            {
                Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(game) + "/" + cap, out _));
                Assert.False(Keybinds.Capturing);
                Assert.Equal("", Pane().Editing);
                var d = Tab().GetProperty("description");
                Assert.Equal(game, d.GetProperty("row").GetString());
                Assert.Equal("selected", d.GetProperty("source").GetString());
                Assert.Equal("Interact", d.GetProperty("heading").GetString());
                Assert.Equal("Interact with the world.", d.GetProperty("body").GetString());
                Assert.Equal(KeybindsTabLayout.GameCapNote, d.GetProperty("info").GetString());
            }

            // A mod row has no second cap to click (decided 2026-09-30), and the op does nothing.
            string mod = ModRowId("a.mod");
            Assert.False(UiCellRowHandle.TryInvoke(NameRowOf(mod) + "/" + KeybindsPane.CellKey2, out _));
            Pane().PressSecondary(mod);
            Assert.False(Keybinds.Capturing);
            Assert.Equal(game, Pane().SelectedRow);
            Assert.Equal("Change game keys in Esc > Options > Controls.", KeybindsTabLayout.GameCapNote);
        }

        // ---- 4. capture from a mod's cap --------------------------------------------------------

        [Fact]
        public void A_mod_cap_click_starts_the_KEYS2_capture_and_a_refusal_shows_in_the_description()
        {
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Keybinds.Register(_owner, "b.mod", "Close", File("b"), F8, "", () => { });
            OpenKeybindsTab();
            string a = ModRowId("a.mod");
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(a) + "/" + KeybindsPane.CellKey, out _));
            Assert.True(Keybinds.Capturing);
            Assert.Equal(a, Pane().Editing);
            Assert.Equal(KeybindsTabLayout.CapturingText, RowOf(a).Cells().Single(c => c.Key == KeybindsPane.CellKey).Text);
            Assert.Equal(KeybindsTabLayout.CaptureHelp, Tab().GetProperty("description").GetProperty("warning").GetString());

            // A used key is pending with the take named (requested 2026-10-10), warn; the swap offer is unchanged.
            _keys.Next = F8;
            _keys.Frame();
            Assert.False(Keybinds.Capturing);
            Assert.Equal("b.mod", Tab().GetProperty("edit").GetProperty("swapWith").GetProperty("owner").GetString());
            var d = Tab().GetProperty("description");
            Assert.StartsWith("F8 is used by b.mod (Close). Save moves it to Open;", d.GetProperty("warning").GetString());
            Assert.Equal("warn", d.GetProperty("warningTone").GetString());
            Pane().CancelEdit();
            Assert.Equal("", Pane().Editing);

            // The bridge's cap press does the same.
            ModsMenu.KeysOp("press-key", a, null);
            Assert.True(Keybinds.Capturing);
            Pane().CancelEdit();
        }

        // ---- 5. conflict and reserved states in the description column --------------------------

        [Fact]
        public void Conflict_and_reserved_notes_are_in_the_description_and_hover_wins_over_selection()
        {
            ResetArt();
            KeybindArbiter.InstallTimeOf = id => id == "first.mod" ? Ledger(9) : Ledger(1);
            Keybinds.Register(_owner, "first.mod", "Ping", File("first"), F9, "Pings.", () => { });
            Keybinds.Register(_owner, "second.mod", "Ping", File("second"), F9, "", () => { });
            var cfg = File("plain.mod");
            cfg.Bind("Hotkeys", "Close", new KeyboardShortcut("F4 + LeftAlt"), "Closes it.");
            SettingsCatalog.Register("plain.mod", cfg, new object(), foreign: true);
            OpenKeybindsTab();
            string winner = ModRowId("first.mod"), loser = ModRowId("second.mod");

            // Nothing hovered or selected: the intro.
            var intro = Tab().GetProperty("description");
            Assert.Equal("none", intro.GetProperty("source").GetString());
            Assert.Equal(KeybindsTabLayout.DescriptionTitle, intro.GetProperty("heading").GetString());
            Assert.Equal(KeybindsTabLayout.DescriptionTitle, _runtime.CellRow(KeybindsPane.DescHeadingKey)!.Cells().Single().Text);

            // Select the winner (a row click): its muted note.
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(winner), out _));
            var d = Tab().GetProperty("description");
            Assert.Equal("Ping", d.GetProperty("heading").GetString());
            Assert.Equal("Pings.", d.GetProperty("body").GetString());
            Assert.StartsWith("Also bound by second.mod (not active there).", d.GetProperty("warning").GetString());
            Assert.Equal("muted", d.GetProperty("warningTone").GetString());
            Assert.True(RowOf(winner).Selected);

            // Hover the loser: it is described and highlighted instead; its name brightens.
            Assert.True(UiCellRowHandle.TryHover(NameRowOf(loser), entered: true));
            d = Tab().GetProperty("description");
            Assert.Equal("hover", d.GetProperty("source").GetString());
            Assert.Equal(loser, d.GetProperty("row").GetString());
            Assert.StartsWith("Not active: F9", d.GetProperty("warning").GetString());
            Assert.Equal("warn", d.GetProperty("warningTone").GetString());
            Assert.True(RowOf(loser).Selected);
            Assert.False(RowOf(winner).Selected);
            Assert.Equal(CellTone.Warn, RowOf(loser).Cells().Single(c => c.Key == KeybindsPane.CellKey).Tone);
            Assert.Equal("F9", RowOf(loser).Cells().Single(c => c.Key == KeybindsPane.CellKey).Text);
            Assert.Equal("Ping", _runtime.CellRow(KeybindsPane.DescHeadingKey)!.Cells().Single().Text);

            // The pointer leaves: back to the selected row.
            Assert.True(UiCellRowHandle.TryHover(NameRowOf(loser), entered: false));
            // User report 2026-10-10: the left row stays described for a short grace (the gap to the next row).
            Assert.Equal(loser, Tab().GetProperty("description").GetProperty("row").GetString());
            Pane().FlushHoverLeave();
            Assert.Equal(winner, Tab().GetProperty("description").GetProperty("row").GetString());
            Assert.True(RowOf(winner).Selected);
            Assert.False(RowOf(loser).Selected);

            // A reserved config-file key: the reserved sentence, warn, and the config-file note.
            string close = Pane().Model!.ModRows().Single(r => r.Claim.ActionId == "Hotkeys/Close").Id;
            ModsMenu.KeysOp("hover", close, null);
            d = Tab().GetProperty("description");
            Assert.StartsWith(ReservedChords.WindowsRowWarning, d.GetProperty("warning").GetString());
            Assert.Equal("warn", d.GetProperty("warningTone").GetString());
            Assert.Contains(KeybindsTabLayout.FromConfigFile, d.GetProperty("info").GetString());

            // Rows stay one line: no help under the name.
            Assert.Equal("Ping", RowOf(winner).Cells().Single(c => c.Key == KeybindsPane.CellName).Text);
        }

        // ---- 6. reset and clear in the right gutter ---------------------------------------------

        [Fact]
        public void Reset_and_clear_sit_in_the_right_gutter_after_the_caps()
        {
            ResetArt();
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General));
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            Assert.True(Keybinds.Rebind("a.mod", "Open", F7));
            OpenKeybindsTab();
            string mod = ModRowId("a.mod");
            var cells = RowOf(mod).Cells();
            var placed = MenuPanelLayout.CellColumns(1200f, cells);
            CellPlacement At(string key) => placed.Single(p => p.Key == key);
            Assert.True(At(KeybindsPane.CellKey).Right <= At(KeybindsTabLayout.CellSecondarySpace).Left);
            Assert.True(At(KeybindsTabLayout.CellSecondarySpace).Right <= At(KeybindsPane.CellReset).Left);
            Assert.True(At(KeybindsPane.CellReset).Right <= At(KeybindsPane.CellClear).Left);
            // KEYS3 polish: with no borrowed icon (game-free), reset is a small Reset button; clear is Unbind.
            Assert.Equal(KeybindsTabLayout.ResetCellWidth(resetIcon: false), At(KeybindsPane.CellReset).Width, 1);
            Assert.Equal(KeybindsTabLayout.ResetCaption, cells.Single(c => c.Key == KeybindsPane.CellReset).Text);
            Assert.Equal(KeybindsTabLayout.UnbindCaption2, cells.Single(c => c.Key == KeybindsPane.CellClear).Text);

            // The changed row says so in the column; reset from the gutter restores the default.
            ModsMenu.KeysOp("select", mod, null);
            Assert.Equal("The default is F9.", Tab().GetProperty("description").GetProperty("info").GetString());
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(mod) + "/" + KeybindsPane.CellReset, out _));
            Assert.Equal(F9, Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord);
            Assert.True(UiCellRowHandle.TryInvoke(NameRowOf(ModRowId("a.mod")) + "/" + KeybindsPane.CellClear, out _));
            Assert.False(Keybinds.Snapshot().Single(b => b.PluginId == "a.mod").Chord.IsBound);
            Assert.Equal("", RowOf(ModRowId("a.mod")).Cells().Single(c => c.Key == KeybindsPane.CellKey).Text);

            // A game row has no gutter buttons.
            Assert.DoesNotContain(RowOf(GameRowId("Interact")).Cells(), c => c.Key == KeybindsPane.CellReset || c.Key == KeybindsPane.CellClear);
        }

        // ---- 7. the donor ladder ----------------------------------------------------------------

        [Fact]
        public void The_art_ladder_tries_live_then_cached_then_resident_then_the_kit()
        {
            Assert.Equal(GameArtSource.Live, KeybindArtLadder.Next(true, true, true));
            Assert.Equal(GameArtSource.Cached, KeybindArtLadder.Next(false, true, true));
            Assert.Equal(GameArtSource.Resident, KeybindArtLadder.Next(false, false, true));
            Assert.Equal(GameArtSource.Kit, KeybindArtLadder.Next(false, false, false));
            Assert.True(KeybindArtLadder.CachedUsable(true, true));
            Assert.False(KeybindArtLadder.CachedUsable(true, false));
            Assert.False(KeybindArtLadder.CachedUsable(false, true));
            // One resident scan per build, three per generation.
            Assert.True(KeybindArtLadder.MayScanResident(0, int.MinValue, 10));
            Assert.False(KeybindArtLadder.MayScanResident(1, 10, 10));
            Assert.True(KeybindArtLadder.MayScanResident(2, 10, 11));
            Assert.False(KeybindArtLadder.MayScanResident(KeybindArtLadder.MaxResidentScans, 10, 11));
            Assert.Equal(new[] { "unresolved", "live", "cached", "resident", "kit", "owned-copy", "kept" },
                Enum.GetValues(typeof(GameArtSource)).Cast<GameArtSource>().Select(KeybindArtLadder.Name).ToArray());

            // Live K8: our owned copy comes right after the live donor, before the held original.
            Assert.Equal(GameArtSource.OwnedCopy, KeybindArtLadder.Next(false, ownedCopy: true, cachedUsable: true, residentFound: true));
            Assert.Equal(GameArtSource.Live, KeybindArtLadder.Next(true, ownedCopy: true, cachedUsable: true, residentFound: true));
            Assert.Equal(GameArtSource.Cached, KeybindArtLadder.Next(false, ownedCopy: false, cachedUsable: true, residentFound: true));
            Assert.Equal("Keybinds tab: art keyCap live=no donor menu in the scene -> owned-copy",
                KeybindArtLadder.FallThrough("keyCap", "no donor menu in the scene", "", "", GameArtSource.OwnedCopy));
            Assert.True(new KeybindArt.Part(GameArtSource.OwnedCopy, KeybindArt.DonorRebindingEntry, "SimpleMenuButton_Normal", null, "owned").Borrowed);

            // Live follow-up / review M1: the highlight is the named sprites seen live, never a name guess,
            // and a donor band that fades in from alpha 0 is drawn opaque rather than invisibly.
            Assert.Equal("ListButton_Glow2", KeybindArtLadder.HighlightSpriteName);
            Assert.Equal("ListButton_TopGradient", KeybindArtLadder.HighlightTopSpriteName);
            Assert.Equal("SimpleMenuButton_Normal", KeybindArtLadder.KeyCapSpriteName);
            Assert.Equal(1f, KeybindArtLadder.VisibleAlpha(0f));
            Assert.Equal(1f, KeybindArtLadder.VisibleAlpha(float.NaN));
            Assert.Equal(0.6f, KeybindArtLadder.VisibleAlpha(0.6f));
            string donorsSource = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindArtDonors.cs");
            string entry = SourcePin.MethodBody(donorsSource, "private static void ReadEntryImages(");
            SourcePin.AssertUncommented(entry, "spriteName == KeybindArtLadder.HighlightSpriteName");
            SourcePin.AssertUncommented(entry, "spriteName == KeybindArtLadder.HighlightTopSpriteName");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donorsSource, "private static Sprited? SpriteOf("),
                "color.a = KeybindArtLadder.VisibleAlpha(color.a)");
            Assert.DoesNotContain("LooksLikeHighlight", donorsSource);

            // The category underline: by object or sprite name under the header prefab.
            Assert.True(KeybindArtLadder.LooksLikeRule("Underline", ""));
            Assert.True(KeybindArtLadder.LooksLikeRule("Image", "Divider_Red"));
            Assert.False(KeybindArtLadder.LooksLikeRule("Background", "Panel_BG"));

            // Review L3: the scene is scanned once per world, again only while a part is missing.
            Assert.True(KeybindArtLadder.ShouldScanLive(allPartsHeld: true, lastScanEpoch: 1, epoch: 2, lastScanFrame: 10, frame: 11));
            Assert.False(KeybindArtLadder.ShouldScanLive(true, 2, 2, 10, 5000));
            Assert.False(KeybindArtLadder.ShouldScanLive(false, 2, 2, 10, 11));
            Assert.True(KeybindArtLadder.ShouldScanLive(false, 2, 2, 10, 10 + KeybindArtLadder.RescanFrames));

            // Live follow-up 1: every fall-through says why each rung failed.
            Assert.Equal("Keybinds tab: art keyCap live=no donor menu in the scene cached=kept sprite unloaded resident=scan budget spent this world -> kit",
                KeybindArtLadder.FallThrough("keyCap", "no donor menu in the scene", "kept sprite unloaded", "scan budget spent this world", GameArtSource.Kit));
            Assert.Equal("Keybinds tab: art keyCap live=not rescanned (kept from this world) cached=ok -> cached",
                KeybindArtLadder.FallThrough("keyCap", "not rescanned (kept from this world)", "", "", GameArtSource.Cached));

            // The source is logged once per change and read back per part.
            ResetArt();
            var art = new KeybindArt(
                new KeybindArt.Part(GameArtSource.Resident, KeybindArt.DonorOptionsButton, "Button_Steel"),
                KeybindArt.Part.Kit,
                new KeybindArt.Part(GameArtSource.Live, KeybindArt.DonorRebindingDetails, "Divider_Small", new object()));
            string? line = KeybindArtLadder.Use(art);
            Assert.Equal("Keybinds tab: art keyCap=resident(options-button:Button_Steel) highlight=game-row-tint highlightTop=kit ornament=live(rebinding-details:Divider_Small) rule=kit resetIcon=kit", line);
            Assert.Null(KeybindArtLadder.Use(art));
            Assert.Contains(_log.Infos, l => l.Contains("Keybinds tab: art keyCap=resident"));
            var read = KeybindArtLadder.Describe();
            Assert.Equal("resident", ((Dictionary<string, object?>)read["keyCap"]!)["source"]);
            Assert.Equal("options-button", ((Dictionary<string, object?>)read["keyCap"]!)["donor"]);
            Assert.Equal("kit", ((Dictionary<string, object?>)read["highlight"]!)["source"]);
            Assert.Equal("kit", ((Dictionary<string, object?>)read["highlightTop"]!)["source"]);
            Assert.Equal("kit", ((Dictionary<string, object?>)read["rule"]!)["source"]);

            // The Unity side walks the rungs in this order and takes the Bindings Menu before the Options page.
            string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "KeybindArtDonors.cs");
            string prepare = SourcePin.MethodBody(donors, "internal static void Prepare()");
            SourcePin.AssertUncommented(prepare, "KeybindArtLadder.Next(liveFound, ownedUsable, cachedUsable, kept, residentHit != null)");
            SourcePin.AssertUncommented(prepare, "KeybindArtLadder.ShouldScanLive(allHeld, _liveScanEpoch, epoch, _liveScanFrame, frame)");
            SourcePin.AssertUncommented(prepare, "LogFallThrough(KeybindArtLadder.FallThrough(");
            // Live follow-up 1: the cached rung holds the sprite on an owned DontDestroyOnLoad image (a
            // managed reference alone did not keep it loaded into the world), destroyed on unload.
            // MENUKIT2: the holder is the shared OwnedSprites store the donors delegate to.
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void Hold("), "Art.Hold(i, part.Sprite)");
            SourcePin.AssertUncommented(donors, "private static readonly OwnedSprites Art = new(HolderName, AssetTag, KeybindArtLadder.LogPrefix, PartNames)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "internal static void Release()"), "Art.Release()");
            string store = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "OwnedSprites.cs");
            string hold = SourcePin.MethodBody(store, "internal void Hold(");
            SourcePin.AssertUncommented(hold, "Object.DontDestroyOnLoad(go)");
            SourcePin.AssertUncommented(hold, "_held[i]!.sprite = sprite");
            SourcePin.AssertUncommented(SourcePin.MethodBody(store, "internal void Release()"), "DestroyOwned(_holder)");
            SourcePin.AssertUncommented(donors, "internal const string HolderName = UiOwnership.Prefix + \"core-keybind-art\"");
            Assert.True(UiOwnership.IsOwned(KeybindArtDonorsHolderName), "the Core-unload sweep also removes a leftover holder");
            // In a world the resident rung also finds the loaded sprites by name, and the Options menu's
            // Controls panel is reached through OptionsMenu.ControlsPanel.
            SourcePin.AssertUncommented(SourcePin.MethodBody(donors, "private static void ReadLoadedSprites("), "Resources.FindObjectsOfTypeAll(type)");
            string menus = SourcePin.MethodBody(donors, "private static void ReadMenus(");
            Assert.True(menus.IndexOf("menu.ControlsInputEntryPrefab", StringComparison.Ordinal)
                < menus.IndexOf("panel.ButtonPrefab", StringComparison.Ordinal));
            string unity = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "GameArtUnity.cs");
            SourcePin.AssertUncommented(SourcePin.MethodBody(unity, "internal static List<T> Loaded<T>()"), "Resources.FindObjectsOfTypeAll(type)");
            SourcePin.AssertUncommented(SourcePin.MethodBody(unity, "internal static List<T> Scene<T>()"), "Object.FindObjectsOfType(type, true)");
            string host = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UiKeybindHost.cs");
            SourcePin.AssertUncommented(host, "KeybindArtLadder.PrepareHook = KeybindArtDonors.Prepare");
            SourcePin.AssertUncommented(host, "KeybindArtLadder.PrepareHook = null");
            ResetArt();
        }

        // ---- 8. the fallback --------------------------------------------------------------------

        [Fact]
        public void Without_a_donor_every_part_falls_back_to_the_kit_and_a_borrowed_ornament_is_drawn()
        {
            // No client hook (a game-free host): the kit everywhere, the kit divider under the heading.
            ResetArt();
            Keybinds.Register(_owner, "a.mod", "Open", File("a"), F9, "", () => { });
            OpenKeybindsTab();
            var art = Tab().GetProperty("art");
            foreach (var part in new[] { "keyCap", "highlight", "ornament" })
                Assert.Equal("kit", art.GetProperty(part).GetProperty("source").GetString());
            Assert.Equal("catalog", art.GetProperty("categories").GetString());
            Assert.Null(_runtime.CellRow(KeybindsPane.DescOrnamentKey));

            // A hook that throws: the kit, and one warning.
            KeybindArtLadder.PrepareHook = () => throw new InvalidOperationException("boom");
            ModsMenu.SelectTab("mods");
            ModsMenu.SelectTab("keybinds");
            Assert.Equal("kit", Tab().GetProperty("art").GetProperty("keyCap").GetProperty("source").GetString());
            Assert.Contains(_log.Warnings, l => l.Contains("keybinds art donors threw: boom"));

            // A hook that borrowed the ornament: the image row carries the sprite.
            var sprite = new object();
            KeybindArtLadder.PrepareHook = () => KeybindArtLadder.Use(new KeybindArt(
                KeybindArt.Part.Kit, KeybindArt.Part.Kit,
                new KeybindArt.Part(GameArtSource.Resident, KeybindArt.DonorOptionsDetails, "Divider", sprite)));
            ModsMenu.SelectTab("mods");
            ModsMenu.SelectTab("keybinds");
            var ornament = _runtime.CellRow(KeybindsPane.DescOrnamentKey);
            Assert.NotNull(ornament);
            Assert.Same(sprite, ornament!.Images[KeybindsPane.CellOrnament]);
            Assert.Equal(CellSkin.Ornament, ornament.Cells().Single().Skin);
            Assert.Equal("resident", Tab().GetProperty("description").GetProperty("ornament").GetProperty("source").GetString());
            ResetArt();
        }

        // ---- the pane geometry: list left, description column right ------------------------------

        [Fact]
        public void The_list_and_the_description_column_split_the_frame()
        {
            foreach (float width in new[] { 1276f, 1664f, 2400f, 900f })
            {
                var frame = ModsWorkspaceFrame.Fallback(width + 200f, 1080f);
                var list = MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.ListWithAside);
                var aside = MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.Aside);
                MenuPanelLayout.AsideSplit(frame.Width, out _, out _, out float gap);
                Assert.Equal(frame.Width, list.Width + gap + aside.Width, 1);
                Assert.True(aside.X > list.X, "the column is on the right");
                Assert.Equal(list.X + list.Width * .5f + gap, aside.X - aside.Width * .5f, 1);
                Assert.True(aside.Width <= MenuPanelLayout.AsideMaxWidth + .01f);
                Assert.True(aside.Width <= (frame.Width - gap) * .4f + .01f);
            }

            Assert.Equal("list-with-aside", MenuPaneModes.Name(MenuPaneMode.ListWithAside));
            Assert.Equal("aside", MenuPaneModes.Name(MenuPaneMode.Aside));
            Assert.True(MenuPaneModes.Titleless(MenuPaneMode.ListWithAside));
            Assert.True(MenuPaneModes.Titleless(MenuPaneMode.Aside));
            Assert.Equal(MenuPaneMode.ListWithAside, KeybindsTabLayout.PaneMode);

            string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.Keybinds.cs");
            string build = SourcePin.MethodBody(menu, "private static void BuildKeybindsPanes");
            SourcePin.AssertUncommented(build, "Ui.ConfigureMenuPanel(panel, sidebar: false, KeybindsTabLayout.PaneMode)");
            SourcePin.AssertUncommented(build, "Ui.ConfigureMenuPanel(aside, sidebar: false, MenuPaneMode.Aside)");
            SourcePin.AssertUncommented(build, "Sidebar.Adopt(aside)");
            SourcePin.AssertUncommented(build, "pane.BuildDescription(aside)");
        }

        [Fact]
        public void Key_rows_report_hover_and_the_description_follows_the_bridge()
        {
            ResetArt();
            UseGame(GameIn("Interact", new KeyChord(UiKey.E), GameKeybindCatalog.General, description: "Use things"));
            OpenKeybindsTab();
            string game = GameRowId("Interact");
            var handle = (UiCellRowHandle)Pane().HeadingOf("game")!;
            // A heading does not report hover; a key row does (its spec asks for the pointer trigger).
            Assert.False(UiCellRowHandle.TryHover(handle.Key, entered: true));
            Assert.True(UiCellRowHandle.TryHover(NameRowOf(game), entered: true));
            Assert.Equal(game, Pane().HoveredRow);
            Assert.Equal("Use things.", Tab().GetProperty("description").GetProperty("body").GetString());
            var row = Tab().GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == game);
            Assert.True(row.GetProperty("current").GetBoolean());
            Assert.True(row.GetProperty("hovered").GetBoolean());
            ModsMenu.KeysOp("hover", "", null);
            Assert.Equal("", Pane().HoveredRow);
            Assert.Throws<ArgumentException>(() => ModsMenu.KeysOp("hover", "nobody", null));
        }
    }
}
