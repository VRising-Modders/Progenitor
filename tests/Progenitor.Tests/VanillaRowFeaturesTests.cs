using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of the tooltip rows added for Satisvampory: a row's indent, the key-hint row (which chord it
/// names and which glyphs that chord becomes) and the footer slot rule.
/// </summary>
public class VanillaRowFeaturesTests
{
    private sealed class Bind : IKeybindInfo
    {
        internal Bind(string plugin, string id, KeyChord chord, KeyChord pad = default)
        {
            PluginId = plugin;
            Id = id;
            Chord = chord;
            GamepadChord = pad;
        }

        public string PluginId { get; }

        public string Id { get; }

        public string Description => "";

        public KeyChord Chord { get; }

        public KeyChord DefaultChord => Chord;

        public KeyChord GamepadChord { get; }
    }

    // ---------------------------------------------------------------- the row

    [Fact]
    public void A_row_indent_is_clamped_and_part_of_equality()
    {
        Assert.Equal(0, new VanillaRow("a").Indent);
        Assert.Equal(1, new VanillaRow("a", LabelTone.Normal, 1).Indent);
        Assert.Equal(VanillaRow.MaxIndent, new VanillaRow("a", LabelTone.Normal, 99).Indent);
        Assert.Equal(0, new VanillaRow("a") { Indent = -3 }.Indent);
        Assert.Equal(2, new VanillaRow("a") { Indent = 2 }.Indent);
        Assert.NotEqual(new VanillaRow("a"), new VanillaRow("a", LabelTone.Normal, 1));
        Assert.Equal(new VanillaRow("a", LabelTone.Ok, 1), new VanillaRow("a", LabelTone.Ok) { Indent = 1 });
        Assert.Equal("> a", new VanillaRow("a", LabelTone.Normal, 1).ToString());
        Assert.Equal(">> a [ok]", new VanillaRow("a", LabelTone.Ok, 2).ToString());
    }

    [Fact]
    public void A_key_hint_row_carries_its_key_label_and_hold()
    {
        var row = VanillaRow.KeyHint("Alt", "Show storage", hold: true);
        Assert.True(row.IsKeyHint);
        Assert.Equal("Alt", row.Key);
        Assert.Equal("Show storage", row.Text);
        Assert.True(row.Hold);
        Assert.Equal(LabelTone.Normal, row.Tone);
        Assert.Equal("Hold [Alt] Show storage", row.ToString());
        Assert.False(new VanillaRow("Alt").IsKeyHint);
        Assert.NotEqual(VanillaRow.KeyHint("Alt", "x"), VanillaRow.KeyHint("Alt", "x", hold: true));
        Assert.NotEqual(VanillaRow.KeyHint("Alt", "x"), new VanillaRow("x"));
        Assert.Equal(1, (VanillaRow.KeyHint("Alt", "x") with { Indent = 1 }).Indent);
    }

    // ---------------------------------------------------------------- the chord

    [Fact]
    public void A_bind_id_names_the_plugins_bind_and_follows_the_controller()
    {
        var binds = new List<IKeybindInfo>
        {
            new Bind("example.recipes", "storage", KeyChord.Alt, KeyChord.Gamepad(UiKey.GamepadNorth)),
            new Bind("example.recipes", "off", KeyChord.None),
            new Bind("other.mod", "storage", new KeyChord(UiKey.F2)),
        };

        Assert.Equal(KeyChord.Alt, VanillaKeyHint.Chord("storage", "example.recipes", binds, false, out bool fromBind));
        Assert.True(fromBind);
        Assert.Equal(KeyChord.Gamepad(UiKey.GamepadNorth), VanillaKeyHint.Chord("storage", "example.recipes", binds, true, out _));
        Assert.Equal(new KeyChord(UiKey.F2), VanillaKeyHint.Chord("storage", "other.mod", binds, true, out _));
        Assert.Equal(KeyChord.None, VanillaKeyHint.Chord("off", "example.recipes", binds, false, out fromBind));
        Assert.True(fromBind);

        // Not a bind of this plugin: the key as written.
        Assert.Equal(KeyChord.Alt, VanillaKeyHint.Chord("Alt", "example.recipes", binds, false, out fromBind));
        Assert.False(fromBind);
        Assert.Equal(new KeyChord(UiKey.F, KeyModifiers.Shift), VanillaKeyHint.Chord("Shift + F", "x", null, false, out _));
        Assert.Equal(KeyChord.None, VanillaKeyHint.Chord("  ", "x", binds, false, out _));
    }

    [Fact]
    public void A_lone_modifier_is_its_own_cap()
    {
        var p = VanillaKeyHint.Parts(KeyChord.Alt, "Alt", "Open Legend", hold: false);
        Assert.Equal("<Keyboard>/leftAlt", p.FirstPath);
        Assert.Equal("", p.SecondPath);
        Assert.Equal("", p.Prefix);
        Assert.False(p.Gamepad);
        Assert.Equal("[" + KeyChord.Alt.DisplayText + "] Open Legend", p.Text);
    }

    [Fact]
    public void A_key_with_one_modifier_is_two_glyphs_and_a_hold_says_hold()
    {
        var p = VanillaKeyHint.Parts(new KeyChord(UiKey.F, KeyModifiers.Shift), "", "Share", hold: true);
        Assert.Equal("<Keyboard>/leftShift", p.FirstPath);
        Assert.Equal("<Keyboard>/f", p.SecondPath);
        Assert.Equal(VanillaKeyHint.HoldWord, p.Prefix);
        Assert.StartsWith("Hold [", p.Text);
    }

    [Fact]
    public void Mouse_and_controller_keys_have_their_paths()
    {
        Assert.Equal("<Mouse>/leftButton", VanillaKeyHint.Parts(new KeyChord(UiKey.MouseLeft), "", "Set", false).FirstPath);
        var pad = VanillaKeyHint.Parts(KeyChord.Gamepad(UiKey.GamepadSouth, UiKey.GamepadLeftShoulder), "", "Pick", false);
        Assert.True(pad.Gamepad);
        Assert.Equal("<Gamepad>/leftShoulder", pad.FirstPath);
        Assert.Equal("<Gamepad>/buttonSouth", pad.SecondPath);
        var one = VanillaKeyHint.Parts(KeyChord.Gamepad(UiKey.GamepadNorth), "", "Pick", false);
        Assert.Equal("<Gamepad>/buttonNorth", one.FirstPath);
        Assert.Equal("", one.SecondPath);
    }

    [Fact]
    public void Several_modifiers_or_an_unknown_key_are_text()
    {
        var multi = VanillaKeyHint.Parts(new KeyChord(UiKey.F, KeyModifiers.Ctrl | KeyModifiers.Shift), "", "Do", false);
        Assert.False(multi.HasGlyphs);
        Assert.True(multi.HasKey);

        var written = VanillaKeyHint.Parts(KeyChord.None, "Wheel", "Zoom", false);
        Assert.False(written.HasGlyphs);
        Assert.Equal("Wheel", written.KeyText);
        Assert.Equal("[Wheel] Zoom", written.Text);

        var none = VanillaKeyHint.Parts(KeyChord.None, "", "Zoom", false);
        Assert.False(none.HasKey);
        Assert.Equal("Zoom", none.Text);
    }

    [Fact]
    public void A_glyph_word_names_the_sprite_or_the_text()
    {
        Assert.Equal("sprite:Icon_Key_Alt", VanillaKeyHint.GlyphWord("Icon_Key_Alt", "Alt"));
        Assert.Equal("text:Alt", VanillaKeyHint.GlyphWord(null, "Alt"));
        Assert.Equal("text:Alt", VanillaKeyHint.GlyphWord("", "Alt"));
    }

    // ---------------------------------------------------------------- footer slot

    private static readonly string[] Column =
    {
        "TooltipDescFrame", "Tooltip_RecipeRequiredItems", "BottomInfoLayout", "ErrorText", "InputContexts",
    };

    [Fact]
    public void Body_rows_at_the_end_go_before_the_footers()
    {
        Assert.Equal(-1, VanillaRecipeTooltip.SlotBeforeFooters(Column.Length, i => Column[i]));

        var names = new List<string>(Column)
        {
            "progenitor-ui-a.mod-InventoryTooltip-tooltip",
            "progenitor-ui-a.mod-InventoryTooltip-footer",
            "progenitor-ui-b.mod-InventoryTooltip-footer",
        };
        Assert.Equal(6, VanillaRecipeTooltip.SlotBeforeFooters(names.Count, i => names[i]));

        // A footer only counts in the trailing run of Progenitor containers.
        var early = new List<string> { "progenitor-ui-x-RecipeTooltip-footer", "ErrorText", "InputContexts" };
        Assert.Equal(-1, VanillaRecipeTooltip.SlotBeforeFooters(early.Count, i => early[i]));
        Assert.Equal(-1, VanillaRecipeTooltip.SlotBeforeFooters(0, i => ""));
    }
}
