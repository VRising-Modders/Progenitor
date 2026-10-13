using System;
using System.IO;
using Progenitor.Bootstrap;
using Progenitor.Content;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;

namespace Progenitor.Tests
{
    /// <summary>
    /// BUTTONART1 (live 2026-10-06): after a Core swap in a world every Mods button drew as a flat dark-red
    /// rectangle. Two causes: the settings kit's menu button matched the HUD's own <c>Button_Normal</c> (a flat
    /// red 35x35 square) by name, and the copies made earlier in the process were destroyed with the old Core.
    /// These tests cover the ladder (live, owned copy, held, kept, resident, kit), the kept copy's round trip
    /// through Bootstrap's process-lifetime table, the button shape guard, and the neutral fallback.
    /// </summary>
    public class ButtonArtKeptTests
    {
        [Fact]
        public void Ladder_is_live_owned_cached_kept_resident_kit()
        {
            for (int bits = 0; bits < 32; bits++)
            {
                bool live = (bits & 1) != 0, owned = (bits & 2) != 0, cached = (bits & 4) != 0;
                bool kept = (bits & 8) != 0, resident = (bits & 16) != 0;
                var expected = live ? GameArtSource.Live
                    : owned ? GameArtSource.OwnedCopy
                    : cached ? GameArtSource.Cached
                    : kept ? GameArtSource.Kept
                    : resident ? GameArtSource.Resident
                    : GameArtSource.Kit;
                Assert.Equal(expected, GameArtLadder.Next(live, owned, cached, kept, resident));
            }
        }

        [Fact]
        public void Without_a_kept_copy_the_ladder_decides_as_before()
        {
            for (int bits = 0; bits < 16; bits++)
            {
                bool live = (bits & 1) != 0, owned = (bits & 2) != 0, cached = (bits & 4) != 0, resident = (bits & 8) != 0;
                Assert.Equal(GameArtLadder.Next(live, owned, cached, residentFound: resident),
                    GameArtLadder.Next(live, owned, cached, false, resident));
            }
        }

        [Fact]
        public void A_kept_copy_wins_over_a_name_match_after_a_swap_in_a_world()
        {
            // A swap in a world: no live donor, the new generation has no owned copy and holds nothing,
            // a copy was kept by the old generation and a loaded sprite of a matching name exists.
            Assert.Equal(GameArtSource.Kept, GameArtLadder.Next(false, false, false, true, true));
            // Nothing kept (the client started straight into a world): the loaded sprite, as before.
            Assert.Equal(GameArtSource.Resident, GameArtLadder.Next(false, false, false, false, true));
            // A live donor (the title, the game's Options page) still wins over everything.
            Assert.Equal(GameArtSource.Live, GameArtLadder.Next(true, true, true, true, true));
        }

        [Fact]
        public void Kept_is_borrowed_has_a_stable_name_and_logs_without_a_resident_reason()
        {
            Assert.True(GameArtLadder.Borrowed(GameArtSource.Kept));
            Assert.Equal("kept", GameArtLadder.Name(GameArtSource.Kept));
            Assert.Equal("kept", KeybindArtLadder.Name(GameArtSource.Kept));
            Assert.True(new KeybindArt.Part(GameArtSource.Kept).Borrowed);
            string line = SettingsArtLadder.FallThrough("menuButton", "menu found, no SimpleMenuButton_Normal", "none kept", "", GameArtSource.Kept);
            Assert.Equal("Settings kit: art menuButton live=menu found, no SimpleMenuButton_Normal cached=none kept -> kept", line);
        }

        [Fact]
        public void The_kept_entry_exists_after_a_swap_by_its_table_name_alone()
        {
            // Generation 1 (at the title) keeps the menu button's copy under this path.
            string path = GameArtKept.Path(SettingsArtDonorsTag, "menuButton", "SimpleMenuButton_Normal",
                (96f, 0f, 180f, 0f), 100f, sliced: true, (1f, 1f, 1f, 1f));
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
            string key = PrefabIconPath.FormatCacheKey(GameArtKept.PluginId, path, png);

            // Bootstrap's table splits the key and names its sprite "progenitor-icon-" + the file name.
            Assert.True(IconCachePolicy.TryParseKey(key, out string pluginId, out string storedPath, out string sha));
            Assert.Equal(GameArtKept.PluginId, pluginId);
            Assert.Equal(IconCachePolicy.HashBytes(png), sha);
            string iconName = "progenitor-icon-" + Path.GetFileNameWithoutExtension(IconCachePolicy.NormalizePath(storedPath));

            // Generation 2 (swapped in a world) has nothing of generation 1's but that sprite's name.
            Assert.True(GameArtKept.TryParse(iconName, out var entry));
            Assert.True(GameArtKept.IsFor(entry, SettingsArtDonorsTag, "menuButton"));
            Assert.False(GameArtKept.IsFor(entry, SettingsArtDonorsTag, "popupButton"));
            Assert.False(GameArtKept.IsFor(entry, KeybindArtDonorsTag, "menuButton"));
            Assert.Equal("simplemenubutton_normal", entry.Sprite);
            Assert.Equal((96f, 0f, 180f, 0f), entry.Border);
            Assert.True(entry.HasBorder);
            Assert.Equal(100f, entry.Ppu);
            Assert.True(entry.Sliced);
            Assert.Equal((1f, 1f, 1f, 1f), entry.Color);
        }

        [Fact]
        public void A_kept_key_cap_ornament_and_rule_keep_their_type_and_tint()
        {
            var rule = GameSettingsLook.HeaderRule;
            foreach (var (part, sprite, sliced, color) in new[]
            {
                ("keyCap", "SimpleMenuButton_Normal", true, (1f, 1f, 1f, 1f)),
                ("ornament", "Divider_Big_04", true, (1f, 1f, 1f, 0.6f)),
                ("rule", "Divider02_LeftToRight", false, rule),
            })
            {
                string path = GameArtKept.Path(KeybindArtDonorsTag, part, sprite, (12.5f, 0f, 12.5f, 0f), 200f, sliced, color);
                string name = "progenitor-icon-" + Path.GetFileNameWithoutExtension(IconCachePolicy.NormalizePath(path));
                Assert.True(GameArtKept.TryParse(name, out var e), name);
                Assert.True(GameArtKept.IsFor(e, KeybindArtDonorsTag, part));
                Assert.Equal(sliced, e.Sliced);
                Assert.Equal((12.5f, 0f, 12.5f, 0f), e.Border);
                Assert.Equal(200f, e.Ppu);
                // Kept to the byte (1/255).
                Assert.InRange(Math.Abs(color.Item1 - e.Color.R), 0f, 1f / 255f);
                Assert.InRange(Math.Abs(color.Item2 - e.Color.G), 0f, 1f / 255f);
                Assert.InRange(Math.Abs(color.Item3 - e.Color.B), 0f, 1f / 255f);
                Assert.InRange(Math.Abs(color.Item4 - e.Color.A), 0f, 1f / 255f);
            }
        }

        [Fact]
        public void Only_kept_names_parse()
        {
            Assert.False(GameArtKept.TryParse(null, out _));
            Assert.False(GameArtKept.TryParse("", out _));
            Assert.False(GameArtKept.TryParse("Button_Normal", out _));
            // Another plugin's buff icon in the same table.
            Assert.False(GameArtKept.TryParse("progenitor-icon-ember", out _));
            Assert.False(GameArtKept.TryParse("progenitor-icon-settings-art-menubutton~x~b0_0_0_0~p0~s~cffffffff", out _));
            Assert.False(GameArtKept.TryParse("progenitor-icon-settings-art-menubutton~x~b0_0_0~p100~s~cffffffff", out _));
            Assert.False(GameArtKept.TryParse("progenitor-icon-settings-art-menubutton~x~b0_0_0_0~p100~q~cffffffff", out _));
            Assert.False(GameArtKept.TryParse("progenitor-icon-settings-art-menubutton~x~b0_0_0_0~p100~s~cfff", out _));
            Assert.True(GameArtKept.TryParse("progenitor-icon-settings-art-menubutton~x~b0_0_0_0~p100~s~cffffffff", out _));
        }

        [Fact]
        public void The_path_has_one_extension_and_no_separator_in_its_segments()
        {
            string path = GameArtKept.Path("settings-art-", "menuButton", "Odd.Name~With/Bits", (1.5f, 0f, 2.25f, 0f), 133.33f, true, (0.5f, 0f, 0.096f, 1f));
            Assert.StartsWith(GameArtKept.Folder, path);
            Assert.Equal(path, path.ToLowerInvariant());
            string file = Path.GetFileName(path);
            Assert.Equal(".png", Path.GetExtension(file));
            Assert.Single(file.Split('.'), s => s.Length > 0 && s != "png");
            Assert.Equal(6, Path.GetFileNameWithoutExtension(file).Split(GameArtKept.Separator).Length);
            Assert.True(GameArtKept.TryParse(path, out var e));
            Assert.Equal("odd_name_with_bits", e.Sprite);
            Assert.Equal(133.33f, e.Ppu, 2);
        }

        [Fact]
        public void The_parts_own_sprite_is_preferred_among_kept_copies()
        {
            Assert.True(GameArtKept.Prefer(false, "", "button_normal", "SimpleMenuButton_Normal"));
            Assert.True(GameArtKept.Prefer(true, "button_normal", "simplemenubutton_normal", "SimpleMenuButton_Normal"));
            Assert.False(GameArtKept.Prefer(true, "simplemenubutton_normal", "button_normal", "SimpleMenuButton_Normal"));
            Assert.False(GameArtKept.Prefer(true, "button_normal", "other", "SimpleMenuButton_Normal"));
        }

        // ---- the button shape guard: no red HUD square ------------------------------------------------

        [Fact]
        public void The_huds_red_square_Button_Normal_is_never_a_game_button()
        {
            // Read from the asset bundles 2026-10-06 (UnityPy): the three Button_Normal sprites the game ships.
            Assert.False(SettingsArtLadder.ShapeFits(SettingsArtPart.MenuButton, 35f, 35f, (12f, 12f, 12f, 12f)));   // HUD, flat red
            Assert.False(SettingsArtLadder.ShapeFits(SettingsArtPart.MenuButton, 40f, 40f, (10f, 10f, 10f, 10f)));   // main-menu frame
            Assert.True(SettingsArtLadder.ShapeFits(SettingsArtPart.MenuButton, 152f, 50f, (48f, 0f, 88f, 0f)));      // Options Back button
            Assert.True(SettingsArtLadder.ShapeFits(SettingsArtPart.MenuButton, 314f, 103f, (96f, 0f, 180f, 0f)));    // SimpleMenuButton_Normal
            Assert.True(SettingsArtLadder.ShapeFits(SettingsArtPart.PopupButton, 116f, 42f, (40f, 0f, 70f, 0f)));     // SmallerButton_Normal
            Assert.False(SettingsArtLadder.ShapeFits(SettingsArtPart.PopupButtonHover, 35f, 35f, (12f, 12f, 12f, 12f)));
            Assert.False(SettingsArtLadder.ShapeFits(SettingsArtPart.MenuButton, 314f, 103f, (0f, 0f, 0f, 0f)));
            Assert.False(SettingsArtLadder.ShapeFits(SettingsArtPart.MenuButton, 0f, 0f, (96f, 0f, 180f, 0f)));
        }

        [Fact]
        public void Parts_that_are_not_buttons_are_not_shape_checked()
        {
            for (int i = 0; i < SettingsArtLadder.PartCount; i++)
            {
                var part = (SettingsArtPart)i;
                if (SettingsArtLadder.IsButton(part)) continue;
                Assert.True(SettingsArtLadder.ShapeFits(part, 24f, 24f, (8f, 8f, 8f, 8f)), part.ToString());
            }

            Assert.True(SettingsArtLadder.IsButton(SettingsArtPart.MenuButton));
            Assert.True(SettingsArtLadder.IsButton(SettingsArtPart.PopupButtonDisabled));
            Assert.False(SettingsArtLadder.IsButton(SettingsArtPart.ValueBox));
        }

        [Fact]
        public void A_bare_loaded_sprite_matches_primary_names_only()
        {
            // A donor menu may name the Back button's Button_Normal as the menu button...
            Assert.Equal(SettingsArtPart.MenuButton, SettingsArtLadder.PartOf("Button_Normal"));
            // ...but a loaded sprite found by name alone never is (three different Button_Normal ship).
            Assert.Null(SettingsArtLadder.PrimaryPartOf("Button_Normal"));
            Assert.Equal(SettingsArtPart.MenuButton, SettingsArtLadder.PrimaryPartOf("SimpleMenuButton_Normal"));
            Assert.Equal(SettingsArtPart.PopupButton, SettingsArtLadder.PrimaryPartOf("SmallerButton_Normal"));
            Assert.Null(SettingsArtLadder.PrimaryPartOf(null));
            for (int i = 0; i < SettingsArtLadder.PartCount; i++)
                Assert.Equal((SettingsArtPart)i, SettingsArtLadder.PrimaryPartOf(SettingsArtLadder.SpriteName((SettingsArtPart)i)));
        }

        [Fact]
        public void The_keybind_ornament_and_rule_names_are_the_ones_found_live()
        {
            // Live 2026-09-29 (K8): ornament=rebinding-details:Divider_Big_04, rule=rebinding-entry:Divider02_LeftToRight.
            Assert.Equal("Divider_Big_04", KeybindArtLadder.OrnamentSpriteName);
            Assert.Equal("Divider02_LeftToRight", KeybindArtLadder.RuleSpriteName);
            Assert.Equal(GameSettingsLook.SectionRule, KeybindArtLadder.RuleSpriteName);
        }

        [Fact]
        public void A_game_button_without_art_is_neutral_steel_never_red()
        {
            var fill = GameSettingsLook.NeutralButtonFill;
            var rim = GameSettingsLook.NeutralButtonRim;
            Assert.True(fill.B > fill.R && fill.G > fill.R, "fill must read blue-steel, not red");
            Assert.True(rim.B >= rim.R && rim.G >= rim.R, "rim must read steel, not red");
            Assert.Equal(1f, fill.A);
            // The game buttons' face (center texel rgb(23-24, 39-40, 57-59)) within a step.
            Assert.InRange(fill.R * 255f, 22f, 26f);
            Assert.InRange(fill.G * 255f, 38f, 42f);
            Assert.InRange(fill.B * 255f, 56f, 60f);
        }

        private const string SettingsArtDonorsTag = "settings-art-";
        private const string KeybindArtDonorsTag = "keybind-art-";
    }
}
