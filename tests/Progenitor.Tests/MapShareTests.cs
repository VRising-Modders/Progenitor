using System.Collections.Generic;
using Progenitor.Chat;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Map share (<see cref="MapShareLogic"/>, <see cref="MapShareClick"/>, <see cref="ChatInsertPlan"/>): a click on the
/// open map puts <c>[loc:x,z]</c> for that spot in the chat input. Game-free.
/// </summary>
public class MapShareTests
{
    // ---- pointer on the map -> world -> token -------------------------------------------------

    /// <summary>
    /// A world-to-map matrix as the game's map has one: world X and Z scaled into the texture's rect, centered on
    /// (cx, cz), world Z on the rect's Y (column-major, Unity order).
    /// </summary>
    private static float[] WorldToMap(float scale, float cx, float cz)
    {
        var m = new float[16];
        m[0] = scale;            // x' = scale * x ...
        m[12] = -scale * cx;     // ... - scale * cx
        m[9] = scale;            // y' = scale * z ...
        m[13] = -scale * cz;     // ... - scale * cz
        m[6] = 1f;               // z' = y (map plane depth)
        m[15] = 1f;
        return m;
    }

    [Fact]
    public void A_map_point_converts_to_world_and_to_the_token()
    {
        var m = WorldToMap(0.25f, -1000f, -2000f);
        Assert.True(MapTransformMath.TryWorldToAnchored(m, -1234.4f, -1876.6f, out float ax, out float ay));
        Assert.True(MapTransformMath.TryAnchoredToWorld(m, ax, ay, out float wx, out float wz));
        Assert.Equal(-1234.4f, wx, 2);
        Assert.Equal(-1876.6f, wz, 2);
        Assert.Equal("[loc:-1234,-1877]", MapShareLogic.Token(wx, wz));
    }

    [Fact]
    public void A_map_without_data_has_no_world_point()
    {
        Assert.False(MapTransformMath.TryAnchoredToWorld(new float[16], 10f, 10f, out _, out _));
    }

    [Theory]
    [InlineData(12.4f, -7.6f, "[loc:12,-8]")]
    [InlineData(0f, 0f, "[loc:0,0]")]
    [InlineData(-3000.2f, 2500.9f, "[loc:-3000,2501]")]
    public void The_token_is_whole_meters(float x, float z, string expected) =>
        Assert.Equal(expected, MapShareLogic.Token(x, z));

    // ---- the draft ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "[loc:5,6]")]
    [InlineData("", "[loc:5,6]")]
    [InlineData("meet at", "meet at [loc:5,6]")]
    [InlineData("meet at ", "meet at [loc:5,6]")]
    [InlineData("/w Bob ", "/w Bob [loc:5,6]")]
    public void The_token_goes_after_the_draft_with_a_space(string? draft, string expected) =>
        Assert.Equal(expected, MapShareLogic.Draft(draft, 5f, 6f));

    // ---- the click --------------------------------------------------------------------------------

    [Fact]
    public void A_click_shares_the_pressed_point()
    {
        var click = new MapShareClick();
        Assert.True(click.Press(true, true, 500f, 400f, 120f, -340f));
        Assert.True(click.Release(true, true, 504f, 397f, out float x, out float z));
        Assert.Equal(120f, x);
        Assert.Equal(-340f, z);
        Assert.False(click.Armed);
    }

    [Fact]
    public void A_drag_pans_and_shares_nothing()
    {
        var click = new MapShareClick();
        click.Press(true, true, 500f, 400f, 120f, -340f);
        Assert.False(click.Release(true, true, 530f, 400f, out _, out _));
    }

    [Fact]
    public void A_press_off_the_map_or_without_data_shares_nothing()
    {
        var click = new MapShareClick();
        Assert.False(click.Press(false, true, 0f, 0f, 1f, 1f));
        Assert.False(click.Release(true, true, 0f, 0f, out _, out _));
        Assert.False(click.Press(true, false, 0f, 0f, 1f, 1f));
        Assert.False(click.Release(true, true, 0f, 0f, out _, out _));
        Assert.False(click.Press(true, true, 0f, 0f, float.NaN, 1f));
    }

    [Fact]
    public void A_release_after_the_map_closed_shares_nothing()
    {
        var click = new MapShareClick();
        click.Press(true, true, 10f, 10f, 1f, 2f);
        Assert.False(click.Release(false, true, 10f, 10f, out _, out _));
    }

    [Fact]
    public void A_release_with_no_pointer_reading_still_shares_the_press()
    {
        var click = new MapShareClick();
        click.Press(true, true, 10f, 10f, 1f, 2f);
        Assert.True(click.Release(true, false, 0f, 0f, out float x, out float z));
        Assert.Equal((1f, 2f), (x, z));
    }

    [Fact]
    public void A_second_release_does_not_share_again()
    {
        var click = new MapShareClick();
        click.Press(true, true, 10f, 10f, 1f, 2f);
        Assert.True(click.Release(true, true, 10f, 10f, out _, out _));
        Assert.False(click.Release(true, true, 10f, 10f, out _, out _));
    }

    // ---- the key ------------------------------------------------------------------------------------

    [Fact]
    public void The_default_key_is_shift_left_mouse_and_allowed_on_the_map()
    {
        Assert.Equal(new KeyChord(UiKey.MouseLeft, KeyModifiers.Shift), MapShareLogic.DefaultKey);
        Assert.Null(MouseChords.Refusal(MapShareLogic.DefaultKey, KeybindScope.Map));
        Assert.False(ReservedChords.IsReserved(MapShareLogic.DefaultKey));
    }

    [Fact]
    public void The_game_admin_teleport_chord_is_not_the_share_key()
    {
        // Ctrl + Shift + left click is the game's admin map teleport; a keybind matches its modifiers exactly.
        var teleport = new KeyChord(UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift);
        Assert.NotEqual(teleport, MapShareLogic.DefaultKey);
    }

    // ---- the key tip ------------------------------------------------------------------------------

    [Fact]
    public void The_tip_for_the_default_key_shows_the_mouse_glyph_after_shift()
    {
        var hint = MapShareLogic.Hint(MapShareLogic.DefaultKey);
        Assert.Equal(MapShareGlyph.MouseLeft, hint.Glyph);
        Assert.Equal("Shift +", hint.Prefix);
        Assert.Equal("Share Location", hint.Action);
        Assert.Equal("Shift + [LMB] Share Location", hint.Text);
    }

    [Fact]
    public void The_tip_follows_a_remap()
    {
        Assert.Equal("Ctrl + Alt + [RMB] Share Location",
            MapShareLogic.Hint(new KeyChord(UiKey.MouseRight, KeyModifiers.Ctrl | KeyModifiers.Alt)).Text);
        var key = MapShareLogic.Hint(new KeyChord(UiKey.K, KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.Equal(MapShareGlyph.None, key.Glyph);
        Assert.Equal("Ctrl + Shift + K", key.KeyText);
        Assert.Equal("[Ctrl + Shift + K] Share Location", key.Text);
        Assert.Equal("[Middle Mouse] Share Location", MapShareLogic.Hint(new KeyChord(UiKey.MouseMiddle)).Text);
    }

    [Fact]
    public void The_tip_shows_only_on_the_open_map_with_a_key()
    {
        Assert.True(MapShareLogic.ShouldShowHint(true, true, MapShareLogic.DefaultKey));
        Assert.False(MapShareLogic.ShouldShowHint(true, false, MapShareLogic.DefaultKey));
        Assert.False(MapShareLogic.ShouldShowHint(false, true, MapShareLogic.DefaultKey));
        Assert.False(MapShareLogic.ShouldShowHint(true, true, KeyChord.None));
    }

    private static MapHintEntry E(string action, string sprite = "", bool icon = false, bool keyText = false, bool active = true) =>
        new(active, action, icon, sprite, keyText);

    /// <summary>The map's row: Player Position · Pan Map · Zoom In/Out · Add/Remove Marker · Open Legend · Close Map.</summary>
    private static List<MapHintEntry> MapRow() => new()
    {
        E("Player Position", keyText: true),
        E("Pan Map", "Icon_LMB_Hold", icon: true),
        E("Zoom In/Out", "Icon_MouseWheel", icon: true),
        E("Add/Remove Marker", "Icon_RMB1", icon: true),
        E("Open Legend", keyText: true),
        E("Close Map", keyText: true),
    };

    [Fact]
    public void A_mouse_tip_copies_an_entry_with_that_glyph()
    {
        Assert.Equal(1, MapShareLogic.PickDonor(MapRow(), MapShareGlyph.MouseLeft));
        Assert.Equal(3, MapShareLogic.PickDonor(MapRow(), MapShareGlyph.MouseRight));
    }

    [Fact]
    public void A_mouse_tip_with_no_such_glyph_finds_no_donor()
    {
        var row = new List<MapHintEntry> { E("Close Map", keyText: true), E("Pan Map", "Icon_LMB1", icon: false) };
        Assert.Equal(-1, MapShareLogic.PickDonor(row, MapShareGlyph.MouseLeft));
    }

    [Fact]
    public void A_text_tip_copies_a_keyboard_entry_first()
    {
        Assert.Equal(0, MapShareLogic.PickDonor(MapRow(), MapShareGlyph.None));
        var glyphsOnly = new List<MapHintEntry> { E("Pan Map", "Icon_LMB1", icon: true) };
        Assert.Equal(0, MapShareLogic.PickDonor(glyphsOnly, MapShareGlyph.None));
    }

    [Fact]
    public void A_shown_entry_is_copied_before_a_pooled_hidden_one()
    {
        var row = new List<MapHintEntry> { E("Old", "Icon_LMB1", icon: true, active: false), E("Pan Map", "Icon_LMB1", icon: true) };
        Assert.Equal(1, MapShareLogic.PickDonor(row, MapShareGlyph.MouseLeft));
    }

    [Fact]
    public void The_tip_goes_after_the_marker_entry()
    {
        Assert.Equal(3, MapShareLogic.PlaceAfter(MapRow()));
        var noMarker = new List<MapHintEntry> { E("A"), E("B"), E("C", active: false) };
        Assert.Equal(1, MapShareLogic.PlaceAfter(noMarker));
        Assert.Equal(-1, MapShareLogic.PlaceAfter(new List<MapHintEntry>()));
    }

    [Theory]
    [InlineData(3, 4, 4)]
    [InlineData(3, 6, 4)]
    [InlineData(3, 0, 3)]
    public void The_sibling_index_lands_right_after(int after, int tip, int expected) =>
        Assert.Equal(expected, MapShareLogic.SiblingAfter(after, tip));

    // ---- the chat opens after the map closes ---------------------------------------------------

    [Fact]
    public void An_insert_after_the_menu_waits_for_it_then_opens_the_chat()
    {
        Assert.Equal(ChatInsertStep.Wait, ChatInsertPlan.Next(false, true, true, 1));
        Assert.Equal(ChatInsertStep.OpenChat, ChatInsertPlan.Next(false, true, false, 2));
        Assert.Equal(ChatInsertStep.Wait, ChatInsertPlan.Next(false, false, false, 1));
        Assert.Equal(ChatInsertStep.Apply, ChatInsertPlan.Next(true, false, false, 2));
    }

    [Fact]
    public void An_insert_gives_up_when_the_menu_or_the_chat_never_opens()
    {
        Assert.Equal(ChatInsertStep.Drop, ChatInsertPlan.Next(false, true, true, ChatInsertPlan.MenuFrames + 1));
        Assert.Equal(ChatInsertStep.Drop, ChatInsertPlan.Next(false, false, false, ChatInsertPlan.FocusFrames + 1));
    }

    [Fact]
    public void An_insert_with_the_chat_already_open_lands_at_once()
    {
        Assert.Equal(ChatInsertStep.Apply, ChatInsertPlan.Next(true, true, false, 1));
        Assert.Equal(ChatInsertStep.Apply, ChatInsertPlan.Next(true, false, false, 1));
    }
}
