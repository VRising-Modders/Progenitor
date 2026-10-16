using System;

namespace Progenitor.Ui.GameArt;

/// <summary>
/// MENUKIT2: the game's own Options page settings controls, as read live by probe M1 (Steam client,
/// cold title, 2026-09-30; research <c>menu-kit-assets.md</c> section 6). Every prefab of
/// <c>OptionsPanel_Interface/Controls/Graphics/Sound/Accessibility</c> is one of five shared
/// <c>CustomGameSettings_*</c> prefabs (Checkbox, Label, Dropdown, Slider, Button).
///
/// <para>Sizes here are in the game prefabs' own canvas units. The game's settings canvas drew at
/// <see cref="GameCanvasScale"/> on the 1080-high reference capture, where the Mods workspace canvas
/// drew at 1 (M4 <c>canvasScale</c>), so <see cref="Kit"/> converts a game size to ours. Colors are the
/// donor's serialized values (image colors, <c>ButtonTarget</c> ColorTint states), before post-processing.</para>
/// </summary>
internal static class GameSettingsLook
{
    /// <summary>The game settings canvas's scale on the reference capture: a 58-unit row measured 70 px.</summary>
    internal const float GameCanvasScale = 70f / 58f;

    /// <summary>A game-unit size in the Mods workspace's units, rounded to a whole pixel.</summary>
    internal static float Kit(float gameUnits) =>
        float.IsNaN(gameUnits) ? 0f : (float)Math.Round(gameUnits * GameCanvasScale, MidpointRounding.AwayFromZero);

    // ---- text sizes (live review round C, 2026-09-30) --------------------------------------------------
    //
    // Text scaled like the geometry (x1.207) drew too large next to the game's: our 24-unit names read
    // about 26 px on the 3840x1080 capture against the game's about 20 px, the description body 28 against
    // 20, its title 36 against 30. The Mods workspace draws TMP about 1.08 px per unit there, so these are
    // the game's on-screen sizes over that ratio. Geometry (pitch, columns, bars) keeps the x1.207 scale.

    /// <summary>A setting's name (the game's about 20 px on screen).</summary>
    internal const float NameText = 18f;

    /// <summary>A section title, "General" / "HUD" (about 30 px).</summary>
    internal const float SectionTitleText = 28f;

    /// <summary>The description column's heading (about 30 px) and body (about 20 px).</summary>
    internal const float DetailsTitleText = 28f;
    internal const float DetailsBodyText = 18f;

    /// <summary>A value box's value and a dropdown's caption.</summary>
    internal const float ValueText = 18f;
    internal const float DropdownText = 17f;

    /// <summary>The page's small settings action row ("Settings", search, Reset all).</summary>
    internal const float ActionRowText = 18f;

    // ---- sprite names (M1 image sprites and selectable sprite states) ---------------------------------

    internal const string CheckboxNormal = "Checkbox_Normal";
    internal const string CheckboxHover = "Checkbox_Hover";
    internal const string CheckboxDisabled = "Checkbox_Disabled";
    internal const string Checkmark = "Checkmark_Normal";
    internal const string SliderTrack = "Slider_BG";
    internal const string SliderHandle = "Slider_Handle_Normal";
    internal const string SliderHandleHover = "Slider_Handle_Hover";
    internal const string SliderHandleDisabled = "Slider_Handle_Disabled";
    internal const string ValueBox = "SimpleBox_Normal";
    internal const string ValueBoxHover = "SimpleBox_Hover";
    internal const string ValueBoxDisabled = "SimpleBox_Disabled";
    internal const string DropdownBar = "DropdownButton_Normal";
    internal const string DropdownBarHover = "DropDownButton_Focus";
    internal const string DropdownBarDisabled = "DropdownButton_Disabled";
    internal const string DropdownArrow = "DropDownButton_Arrow";
    internal const string RowBand = "ListButton_Glow2";
    internal const string RowBandTop = "ListButton_TopGradient";
    internal const string ResetIcon = "Icon_Reset";
    internal const string SectionRule = "Divider02_LeftToRight";
    internal const string DetailsOrnament = "Divider_Big_02";
    internal const string MenuButton = "SimpleMenuButton_Normal";
    internal const string ScrollbarTrack = "ScrollBar_Bar";
    internal const string ScrollbarHandle = "ScrollBar_Handle";

    // ---- DIALOGS2: the game's popup (CommonPopup "GenericPopup", probe D1, 2026-10-01; resident and
    // live at the title and in a world) -------------------------------------------------------------

    /// <summary>The popup's panel: 128x128, border 32 all round, 9-sliced, white (the dark panel with the steel corner trim).</summary>
    internal const string PopupFrame = "Window_Box_Background";

    /// <summary>The popup's buttons: 116x42, border (40, 0, 70, 0), 9-sliced; sprite-swapped on hover, press, select and disable.</summary>
    internal const string PopupButton = "SmallerButton_Normal";
    internal const string PopupButtonHover = "SmallerButton_Normal_Hover";
    internal const string PopupButtonPressed = "SmallerButton_Pressed";
    internal const string PopupButtonDisabled = "SmallerButton_Disabled";

    /// <summary>
    /// WINDOWS1: the ornament the game draws either side of a window title. Named in the popup prefabs' images by probe D1
    /// (not used by the confirm itself); its size is read from the sprite, never assumed.
    /// </summary>
    internal const string WindowTitleOrnament = "Window_TitleSideOrnament";

    internal static readonly (float L, float B, float R, float T) PopupFrameBorder = (32f, 32f, 32f, 32f);
    internal static readonly (float L, float B, float R, float T) PopupButtonBorder = (40f, 0f, 70f, 0f);

    /// <summary>The blocker behind the popup: no sprite, black at 0.8, full screen.</summary>
    internal static readonly (float R, float G, float B, float A) PopupBlocker = (0f, 0f, 0f, 0.8f);

    /// <summary>Popup geometry (game units): 616 wide (padding 2); header 24 (DropShadow, 0.9, centered) with
    /// 12 above and below, then the 592x12 <c>Divider_Big_02</c> (the thin steel rule and its center ornament);
    /// content padding 24, body 22 (0.9, centered); button row padding (12, 12, 8, 8), spacing 12, two
    /// 256x42 buttons with 18-unit DropShadow captions (0.898).</summary>
    internal const float PopupWidth = 616f;
    internal const float PopupHeaderText = 24f;
    internal const float PopupBodyText = 22f;
    internal const float PopupButtonWidth = 256f;
    internal const float PopupButtonHeight = 42f;
    internal const float PopupButtonSpacing = 12f;
    internal const float PopupDividerWidth = 592f;
    internal const float PopupDividerHeight = 12f;
    internal const float PopupTextGrey = 0.9f;

    // ---- 9-slice borders (left, bottom, right, top) and pixels-per-unit multipliers ------------------

    internal static readonly (float L, float B, float R, float T) SliderTrackBorder = (12f, 0f, 12f, 0f);
    internal static readonly (float L, float B, float R, float T) ValueBoxBorder = (8f, 8f, 8f, 8f);
    internal static readonly (float L, float B, float R, float T) DropdownBarBorder = (38f, 0f, 72f, 0f);
    internal static readonly (float L, float B, float R, float T) ScrollbarBorder = (0f, 26f, 0f, 26f);
    internal static readonly (float L, float B, float R, float T) OrnamentBorder = (250f, 0f, 250f, 0f);
    internal static readonly (float L, float B, float R, float T) MenuButtonBorder = (96f, 0f, 180f, 0f);

    /// <summary><c>Image.pixelsPerUnitMultiplier</c> the game draws these sliced sprites with.</summary>
    internal const float ScrollbarPpuMultiplier = 3f;
    internal const float OrnamentPpuMultiplier = 2f;
    internal const float MenuButtonPpuMultiplier = 2f;
    internal const float RowBandTopPpuMultiplier = 2f;

    // ---- row geometry (CustomGameSettings_*; game units) ---------------------------------------------

    /// <summary>Every entry's <c>LayoutElement</c> min/preferred height.</summary>
    internal const float RowHeight = 58f;

    /// <summary>The name's left inset, and the share of the row it may take (<c>HeaderText</c> anchors 0..0.45).</summary>
    internal const float NameLeft = 16f;
    internal const float NameShare = 0.45f;
    internal const float NameFontSize = 20f;

    /// <summary>The control column starts at this share of the row (the slider and dropdown anchor at 0.45).</summary>
    internal const float ControlStartShare = 0.45f;

    /// <summary>The <c>Modified</c> bar at the row's left edge: 6 wide, full height.</summary>
    internal const float ModifiedBarWidth = 6f;

    /// <summary>Checkbox: a 24 square (<c>Checkbox_Normal</c>, 24x24), its mark 20 (<c>Checkmark_Normal</c>), centered 128 from the row's right.</summary>
    internal const float CheckboxSize = 24f;
    internal const float CheckmarkSize = 20f;
    internal const float CheckboxCenterFromRight = 128f;

    /// <summary>Reset: 20 square (<c>Icon_Reset</c>, 28x28), centered 32 from the right; the unused info button sits at 72.</summary>
    internal const float ResetSize = 20f;
    internal const float ResetCenterFromRight = 32f;

    /// <summary>Slider: 20 tall, from the control start to 256 short of the right; the track is its middle half (10).</summary>
    internal const float SliderHeight = 20f;
    internal const float SliderRightGap = 256f;
    internal const float SliderTrackHeight = 10f;

    /// <summary>The slider's handle: 20 square (<c>Slider_Handle_Normal</c>, a 29x29 diamond).</summary>
    internal const float SliderHandleSize = 20f;

    /// <summary>The slider's value box: 112x32 (<c>SimpleBox_Normal</c>), its right edge 112 from the row's right.</summary>
    internal const float ValueBoxWidth = 112f;
    internal const float ValueBoxHeight = 32f;
    internal const float ValueBoxRightGap = 112f;
    internal const float ValueFontSize = 20f;

    /// <summary>Dropdown: 42 tall, from the control start to 96 short of the right; caption 18, left 10; arrow 16x10 at 12 from its right.</summary>
    internal const float DropdownHeight = 42f;
    internal const float DropdownRightGap = 96f;
    internal const float DropdownFontSize = 18f;
    internal const float DropdownCaptionLeft = 10f;
    internal const float DropdownArrowWidth = 16f;
    internal const float DropdownArrowHeight = 10f;
    internal const float DropdownArrowRight = 12f;

    /// <summary>A row's button (<c>SimpleMenuButton</c>): 300x42, its right edge 96 from the row's right; caption 18.</summary>
    internal const float ButtonWidth = 300f;
    internal const float ButtonHeight = 42f;
    internal const float ButtonRightGap = 96f;

    // ---- section header (CustomGameSettings_Label) ---------------------------------------------------

    internal const float HeaderHeight = 48f;
    internal const float HeaderFontSize = 32f;

    /// <summary>The rule under the title: 1 high, along the bottom, over the left half.</summary>
    internal const float HeaderRuleHeight = 1f;
    internal const float HeaderRuleShare = 0.5f;

    // ---- details column (OptionsMenu Details*) ------------------------------------------------------

    internal const float DetailsWidth = 440f;
    internal const float DetailsHeadingFontSize = 30f;
    internal const float DetailsBodyFontSize = 20f;
    internal const float DetailsSpacing = 12f;

    /// <summary>The column's padding (left, right, top, bottom).</summary>
    internal static readonly (float L, float R, float T, float B) DetailsPadding = (8f, 8f, 12f, 0f);

    // ---- colors (r, g, b, a in 0..1) -----------------------------------------------------------------

    /// <summary>A row name's image color (0.906) times its ButtonTarget tint: 0.7 at rest (161 of 255), 1 hovered (231).</summary>
    internal const float NameBase = 0.906f;
    internal const float NameRestTint = 0.7f;
    internal const float NameHoverTint = 1f;
    internal const float NameSelectedTint = 0.961f;

    internal static float NameGrey(bool lit) => NameBase * (lit ? NameHoverTint : NameRestTint);

    /// <summary>The row band's ButtonTarget hue, and its alpha at rest (0), hovered/selected (0.102) and disabled.</summary>
    internal static readonly (float R, float G, float B) RowBandHue = (0.163f, 0.395f, 0.585f);
    internal const float RowBandRestAlpha = 0f;
    internal const float RowBandLitAlpha = 0.102f;

    /// <summary>The band image's own alpha (<c>ListButton_Glow2</c> drawn at 0.753), multiplied by the tint.</summary>
    internal const float RowBandImageAlpha = 0.753f;

    /// <summary>The band's top gradient (<c>ListButton_TopGradient</c>): at rest and hovered.</summary>
    internal static readonly (float R, float G, float B, float A) RowBandTopRest = (0.428f, 0.637f, 0.709f, 0.102f);
    internal static readonly (float R, float G, float B, float A) RowBandTopLit = (0.688f, 0.883f, 0.953f, 0.502f);

    /// <summary>The <c>Modified</c> bar's color.</summary>
    internal static readonly (float R, float G, float B, float A) ModifiedBar = (0.419f, 0.62f, 0.698f, 1f);

    /// <summary>The reset arrow: steel blue at rest, white hovered, dark gray disabled (the value is the default).</summary>
    internal static readonly (float R, float G, float B, float A) ResetRest = (0.361f, 0.48f, 0.6f, 1f);
    internal static readonly (float R, float G, float B, float A) ResetHover = (1f, 1f, 1f, 1f);
    internal static readonly (float R, float G, float B, float A) ResetDisabled = (0.25f, 0.25f, 0.25f, 1f);

    /// <summary>The slider's track (<c>Slider_BG</c> image color) and its fill (the same sprite, lighter).</summary>
    internal static readonly (float R, float G, float B, float A) SliderTrackColor = (0.128f, 0.135f, 0.15f, 1f);
    internal static readonly (float R, float G, float B, float A) SliderFillColor = (0.209f, 0.256f, 0.3f, 1f);

    /// <summary>The value box's text and the dropdown's caption and arrow: 0.7 gray.</summary>
    internal const float ValueGrey = 0.7f;

    /// <summary>The section title (0.906) and its rule (<c>Divider02_LeftToRight</c> tinted dark red).</summary>
    internal const float HeaderGrey = 0.906f;
    internal static readonly (float R, float G, float B, float A) HeaderRule = (0.5f, 0f, 0.096f, 1f);

    /// <summary>
    /// BUTTONART1: a game button drawn before (or without) the game's button art — a flat steel blue, the
    /// face of every game menu button (the center texel of <c>SimpleMenuButton_Normal</c>,
    /// <c>Button_Normal</c> 152x50 and <c>SmallerButton_Normal</c> is rgb(23-24, 39-40, 57-59), read from the
    /// asset bundles 2026-10-06) with the kit's steel frame tone, never the kit's dark red. The art replaces it
    /// as soon as it is ready.
    /// </summary>
    internal static readonly (float R, float G, float B, float A) NeutralButtonFill = (0.094f, 0.157f, 0.227f, 1f);

    internal static readonly (float R, float G, float B, float A) NeutralButtonRim = (0.212f, 0.239f, 0.267f, 1f);

    /// <summary>The details column: black at 0.322 behind it, a 0.9 heading and a 0.7 body.</summary>
    internal const float DetailsBackdropAlpha = 0.322f;
    internal const float DetailsHeadingGrey = 0.9f;
    internal const float DetailsBodyGrey = 0.7f;

    /// <summary>
    /// The row band's hovered color as the game composes it: the band image's alpha times the ButtonTarget's
    /// lit tint, in the tint's hue. Alpha 0.077 on the game's near-black ground (M1; the reference capture's
    /// band read brighter, rgb(16,32,45) at its top, because <c>ListButton_Glow2</c> is a gradient).
    /// </summary>
    internal static (float R, float G, float B, float A) RowBandLit() =>
        (RowBandHue.R, RowBandHue.G, RowBandHue.B, RowBandImageAlpha * RowBandLitAlpha);

    /// <summary>
    /// The row band drawn as one flat fill (no gradient sprite): the game's hue at
    /// <see cref="FlatBandAlpha"/>, chosen so the fill matches the gradient's measured top on a dark
    /// ground (the game's band reads rgb(16,32,45) at its top).
    ///
    /// <para>CONTROLS1 (2026-10-03): the game's UI blends in linear color space, so an alpha composes far
    /// brighter than the sRGB arithmetic the first value (0.3) assumed. Live 2026-10-03 a lit settings row at
    /// 0.3 read rgb(30,53,81) on the page's rgb(18,0,0) ground — exactly the linear blend, and a saturated
    /// blue next to the game's quiet band. In linear terms rgb(16,32,45) over black is about 0.1 of the hue.</para>
    /// </summary>
    internal const float FlatBandAlpha = 0.1f;

    // ---- CONTROLS1: the Options page's selector (CustomGameSettings_Selector, M1; Controls panel) ---------
    //
    // "Selector" (anchors 0.45..1, 40 tall) holds a SimpleBox_Normal backdrop inset 34 at each side, two 36x36
    // SimpleBox_Normal arrow buttons with a 20x20 "Arrow" (0.9), the value centered in TMP 20 at 0.7, and an
    // IndexIndicator under it: one 4-high bar per option, (0.419, 0.62, 0.698, 0.502), 3 under the bar. The
    // settings page composes its radio group's segments from these parts (SettingsControlLook).

    internal const float SelectorHeight = 40f;
    internal const float SelectorButtonSize = 36f;
    internal const float SelectorIndicatorHeight = 4f;
    internal static readonly (float R, float G, float B, float A) SelectorIndicator = (0.419f, 0.62f, 0.698f, 0.502f);

    internal static (float R, float G, float B, float A) FlatRowBand() => (RowBandHue.R, RowBandHue.G, RowBandHue.B, FlatBandAlpha);
}

/// <summary>
/// CONTROLS1 (2026-10-03): how each settings-grid control is drawn from the Options page's own parts, for
/// the controls the game has no prefab of. Game-free, so the choices are tested; the runtime
/// (<c>UnityUiRuntime.SettingsLook</c>) applies them.
///
/// <list type="bullet">
/// <item>A radio group's segments: each a <c>SimpleBox_Normal</c> frame (the selector's backdrop and arrow
/// buttons), <c>SimpleBox_Hover</c> on hover, a quiet caption and the chosen one at the lit 0.961, with the
/// selector's index bar (<see cref="GameSettingsLook.SelectorIndicator"/>) under each segment: bright under
/// the chosen one, dim under the rest.</item>
/// <item>A multi-select's (and a long radio's) options: the game's checkbox (<c>Checkbox_Normal</c>,
/// <c>Checkmark_Normal</c>) and a caption at the row name's grays.</item>
/// <item>Text fields: the value box, the text left at the dropdown caption's inset; numbers centered.</item>
/// <item>A read-only or info value: the value box's text size and gray, never bright white.</item>
/// </list>
/// </summary>
internal static class SettingsControlLook
{
    /// <summary>A segment's height (the selector's 40).</summary>
    internal static float SegmentHeight => GameSettingsLook.Kit(GameSettingsLook.SelectorHeight);

    /// <summary>The value box frame's tint on a segment: full on the chosen one, dimmed on the rest.</summary>
    internal static float SegmentFrameGrey(bool selected) => selected ? 1f : 0.62f;

    /// <summary>A segment's caption gray: quiet at rest; the chosen one is the lit name's 0.961.</summary>
    internal static float SegmentCaptionGrey(bool selected) => selected ? GameSettingsLook.NameSelectedTint : 0.55f;

    /// <summary>
    /// A segment's caption is the value text size (18) and shrinks to this before it is cut: four 14-character
    /// options do not fit at 18 on a normal row (about 126-135 units a segment at 1170-1236).
    /// </summary>
    internal const float SegmentMinText = 13f;

    /// <summary>The index bar under a segment: the game's indicator hue, opaque under the chosen one, dim under the rest.</summary>
    internal static (float R, float G, float B, float A) SegmentIndicator(bool selected)
    {
        var c = GameSettingsLook.SelectorIndicator;
        return (c.R, c.G, c.B, selected ? 1f : c.A * 0.35f);
    }

    /// <summary>The index bar's height (the game's 4) and its inset from the segment's sides and bottom.</summary>
    internal static float IndicatorHeight => GameSettingsLook.Kit(GameSettingsLook.SelectorIndicatorHeight);

    internal const float IndicatorInsetX = 8f;
    internal const float IndicatorInsetBottom = 5f;

    /// <summary>A segment's flat fill when the value box sprite is not ready (dark steel, lighter when chosen).</summary>
    internal static (float R, float G, float B, float A) SegmentKitFill(bool selected) =>
        selected ? (0.14f, 0.20f, 0.26f, 0.95f) : (0.05f, 0.07f, 0.09f, 0.95f);

    /// <summary>A list option's line height, its check box (the grid checkbox's 24) and mark (20), and caption size.</summary>
    internal const float ListLineHeight = 30f;

    internal static float ListBoxSize => GameSettingsLook.CheckboxSize;

    internal static float ListMarkSize => GameSettingsLook.CheckmarkSize;

    internal const float ListCaptionGap = 10f;

    internal static float ListCaptionText => GameSettingsLook.NameText;

    /// <summary>
    /// RADIO1: a radio list line's label: the chosen line bright (the header gray), the others dimmed well below the
    /// row name's resting gray so the choice reads at a glance.
    /// </summary>
    internal static float RadioCaptionGrey(bool chosen) => chosen ? GameSettingsLook.HeaderGrey : 0.5f;

    /// <summary>RADIO1: a radio line's explanation: the details body gray on the chosen line, dimmer on the others.</summary>
    internal static float RadioDescriptionGrey(bool chosen) => chosen ? GameSettingsLook.DetailsBodyGrey : 0.42f;

    /// <summary>A list option's caption: the row name's resting gray, its lit gray when checked.</summary>
    internal static float ListCaptionGrey(bool on) => GameSettingsLook.NameGrey(lit: on);

    /// <summary>Text in a value box or text field (and a read-only value): the value box's 0.7 lifted for reading.</summary>
    internal const float FieldTextGrey = GameSettingsLook.ValueGrey + 0.15f;

    /// <summary>A read-only value that reads <c>Not set</c>: muted below the value box's gray.</summary>
    internal const float NotSetGrey = GameSettingsLook.ValueGrey - 0.2f;

    internal const float FieldText = GameSettingsLook.ValueText;

    /// <summary>A text field's left text inset: the dropdown caption's.</summary>
    internal static float FieldTextInset => GameSettingsLook.Kit(GameSettingsLook.DropdownCaptionLeft);

    /// <summary>
    /// How a field's text aligns: a number (slider value, stepper, color hex) is centered like the game's
    /// value box; free text (a text field or a text area) starts at the left.
    /// </summary>
    internal static bool FieldCentered(string? runtimeKind) => runtimeKind is "slider-entry" or "slider" or "numeric" or "color";

    /// <summary>A text area's text starts at the top; every other field is middle-aligned.</summary>
    internal static bool FieldTopAligned(string? runtimeKind) => runtimeKind == "textarea";

    /// <summary>The color swatch beside a color's value box: a square the box's height less its frame.</summary>
    internal static float SwatchSize(float valueBoxHeight) => Math.Max(12f, valueBoxHeight - 8f);

    internal const float SwatchGap = 8f;

    /// <summary>
    /// Round 2: a control skinned before the settings kit's art arrived (it is prepared over idle frames) is
    /// tried again this often, for at most this long (about a minute at 60 fps).
    /// </summary>
    internal const int SkinRetryEveryFrames = 15;

    /// <summary>
    /// Round 3: a value box, stepper, segment or option check box answers the pointer with a color tint of
    /// its normal frame, never the game's hover sprite. <c>SimpleBox_Hover</c> is a bright warm 2-3 px rim
    /// (rgb(247,247,231) live) and <c>Checkbox_Hover</c> a heavier, larger frame; with the game in a
    /// background window the EventSystem's hover freezes where the last click was, so the clicked row's
    /// control kept that look indefinitely. At rest the frame is <see cref="QuietRestGrey"/> of itself,
    /// full under the pointer, and a quiet steel while it has the keyboard (<see cref="QuietFocus"/>).
    /// </summary>
    internal const float QuietRestGrey = 0.85f;

    internal static readonly (float R, float G, float B, float A) QuietFocus = (0.78f, 0.88f, 1f, 1f);

    internal const int SkinRetryForFrames = 3600;

    /// <summary>
    /// Round 2: the lit row band with the game's <c>ListButton_Glow2</c> gradient, composed as the game
    /// does — the image's 0.753 alpha times the lit tint's 0.102, in the tint's hue. Without the sprite the
    /// flat fill (<see cref="GameSettingsLook.FlatRowBand"/>) stands in.
    /// </summary>
    internal static (float R, float G, float B, float A) SpriteRowBand(bool lit)
    {
        var lighted = GameSettingsLook.RowBandLit();
        return (lighted.R, lighted.G, lighted.B, lit ? lighted.A : GameSettingsLook.RowBandRestAlpha);
    }
}
