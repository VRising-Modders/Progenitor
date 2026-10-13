#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ui.GameArt;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// MENUKIT2 phase B: the Mods settings page drawn like the game's own Options page.
/// Every settings-grid row is one line at the game's pitch
/// (<see cref="ModSettingsRowGrid"/>) with the game's row band and resting top gradient; a bool is the
/// game's checkbox; a slider is the game's track, diamond handle and value box; a finite choice is the
/// dropdown bar with its arrow; the reset arrow is <c>Icon_Reset</c>; section headings carry the red
/// rule; the help is in the description column at the page's right.
///
/// <para>Sprites come from <see cref="SettingsArtDonors"/> (owned copies of the Options page's sprites,
/// taken at the first workspace open, so they survive a world load). A part with no sprite draws the
/// kit's flat look in the same colors, so the page always draws.</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    private static Color Rgba((float R, float G, float B, float A) c) => new(c.R, c.G, c.B, c.A);

    private static Color Grey(float v, float a = 1f) => new(v, v, v, a);

    /// <summary>
    /// Paint <paramref name="image"/> with the borrowed <paramref name="part"/> tinted <paramref name="tint"/>;
    /// without it, no sprite and <paramref name="kit"/>. True when the game's sprite is drawn.
    /// </summary>
    private static bool PaintPart(Image? image, SettingsArtPart part, Color tint, Color kit)
    {
        if (image == null) return false;
        Sprite? sprite = null;
        Image.Type type = Image.Type.Simple;
        float ppu = 1f;
        bool borrowed = false;
        try { borrowed = SettingsArtDonors.TryPart(part, out sprite, out type, out ppu) && sprite != null; }
        catch { borrowed = false; }
        try
        {
            if (borrowed)
            {
                image.sprite = sprite;
                image.type = type;
                try { image.pixelsPerUnitMultiplier = ppu; } catch { /* older UI: default 1 */ }
                image.color = tint;
            }
            else
            {
                image.sprite = null;
                image.type = Image.Type.Simple;
                image.color = kit;
            }
        }
        catch { /* the image keeps what it had */ }
        return borrowed;
    }

    /// <summary>
    /// VANILLA1: a cell text field (the Install search) on the game's settings value box
    /// (<c>SimpleBox_Normal</c>, <c>SimpleBox_Hover</c> on hover). The kit's one-pixel outline is
    /// switched off only when the sprite drew; otherwise the kit field stays as it was painted.
    /// </summary>
    private static void PaintValueBoxField(GameObject control, TMP_InputField field)
    {
        if (control == null) return;
        Image? box = null;
        try { box = control.GetComponent<Image>(); } catch { box = null; }
        if (box == null) return;
        Color kit;
        try { kit = box.color; } catch { kit = Grey(0.06f, 0.95f); }
        if (!PaintPart(box, SettingsArtPart.ValueBox, Color.white, kit)) return;
        try
        {
            var outline = control.GetComponent<Outline>();
            if (outline != null) outline.enabled = false;
        }
        catch { /* */ }
        ApplySpriteSwap(field, SettingsArtPart.ValueBoxHover, null);
    }

    private static Sprite? PartSprite(SettingsArtPart part)
    {
        try { return SettingsArtDonors.TryPart(part, out var sprite, out _, out _) ? sprite : null; }
        catch { return null; }
    }

    // ---- the checkbox ---------------------------------------------------------------------------

    /// <summary>
    /// A settings-grid bool as the game's checkbox (<c>CustomGameSettings_Checkbox</c>, M1): the
    /// <c>Checkbox_Normal</c> frame (<c>Checkbox_Hover</c> when hovered, <c>Checkbox_Disabled</c> when
    /// grayed, by the Toggle's sprite swap) and the <c>Checkmark_Normal</c> mark shown when on. The Toggle
    /// carries the state and the click; the widget shows or hides the mark on every value write.
    /// </summary>
    private IRuntimeWidget? AddGridCheckbox(UnityPanel host, string text, bool initial)
    {
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("checkbox-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, ModSettingsRowGrid.NameFontSize, left: true);
            float size = ModSettingsRowGrid.CheckboxSize;
            var control = NewControl(go, "checkbox", columns, size, size);
            var frame = TryAdd<Image>(control);
            if (frame == null) { TryDestroyImmediate(control); return null; }
            bool framed = PaintPart(frame, SettingsArtPart.CheckboxFrame, Color.white, Grey(0.16f));
            if (!framed) ApplyFrame(frame, Grey(0.16f, 1f));
            try { frame.raycastTarget = true; } catch { /* */ }
            try { frame.preserveAspect = true; } catch { /* */ }

            var markGo = new GameObject("checkmark");
            markGo.transform.SetParent(control.transform, false);
            var mark = TryAdd<Image>(markGo);
            float markSize = ModSettingsRowGrid.CheckmarkSize;
            if (mark != null)
            {
                bool borrowedMark = PaintPart(mark, SettingsArtPart.CheckboxMark, Color.white, Grey(0.92f));
                if (!borrowedMark) markSize = Math.Max(6f, size * 0.35f);
                try { mark.raycastTarget = false; } catch { /* */ }
                try { mark.preserveAspect = true; } catch { /* */ }
            }

            try
            {
                var rt = markGo.GetComponent<RectTransform>() ?? markGo.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(markSize, markSize);
            }
            catch { /* */ }

            var toggle = TryAdd<Toggle>(control);
            if (toggle == null) { TryDestroyImmediate(control); return null; }
            try { toggle.targetGraphic = frame; } catch { /* */ }
            try { toggle.graphic = null; } catch { /* the widget shows the mark */ }
            try { toggle.group = null; } catch { /* */ }
            try { toggle.toggleTransition = Toggle.ToggleTransition.None; } catch { /* */ }
            ApplySpriteSwap(toggle, SettingsArtPart.CheckboxFrameHover, SettingsArtPart.CheckboxFrameDisabled);
            try { toggle.isOn = initial; } catch { /* */ }
            Activate(control);
            host.AddRow(go, label, hasControl: true, kind: "toggle", source: ControlSource.Built, control: control);
            var widget = new UnityCheckboxWidget(host, go, toggle.onValueChanged, mark);
            widget.Paint(initial);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddGridCheckbox threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// CONTROLS1 round 3: the quiet pointer answer for a value box, stepper, segment or option check box
    /// (<see cref="SettingsControlLook.QuietRestGrey"/>): a color tint of the normal frame on
    /// <paramref name="target"/>, the same geometry in every state. Clears any sprite swap.
    /// </summary>
    private static void ApplyQuietTint(Selectable? selectable, Graphic? target)
    {
        if (selectable == null) return;
        try
        {
            if (target != null) selectable.targetGraphic = target;
            var state = selectable.spriteState;
            state.highlightedSprite = null;
            state.selectedSprite = null;
            state.pressedSprite = null;
            selectable.spriteState = state;
            var c = selectable.colors;
            c.normalColor = Grey(SettingsControlLook.QuietRestGrey);
            c.highlightedColor = Color.white;
            c.pressedColor = Color.white;
            c.selectedColor = Rgba(SettingsControlLook.QuietFocus);
            c.disabledColor = Grey(SettingsControlLook.QuietRestGrey, 0.5f);
            c.colorMultiplier = 1f;
            c.fadeDuration = 0.1f;
            selectable.colors = c;
            selectable.transition = Selectable.Transition.ColorTint;
        }
        catch { /* the selectable keeps what it had */ }
    }

    /// <summary>
    /// The game's sprite swap on a selectable: the hover sprite on hover, when it is borrowed.
    ///
    /// <para>CONTROLS1 round 2: the selected state keeps the normal sprite. The game also gives "selected"
    /// the hover sprite, but its selection follows the gamepad/keyboard cursor; ours is the EventSystem's
    /// last click, which stays on a field after the pointer leaves. Live 2026-10-03 the clicked Notes and
    /// Example limit fields kept <c>SimpleBox_Hover</c>'s bright rim (rgb(247,247,231)) indefinitely, while
    /// the resting value boxes drew the game's own rgb(63,74,79) rim.</para>
    /// </summary>
    private static void ApplySpriteSwap(Selectable? selectable, SettingsArtPart hover, SettingsArtPart? disabled)
    {
        if (selectable == null) return;
        var hoverSprite = PartSprite(hover);
        if (hoverSprite == null) return;
        try
        {
            var state = selectable.spriteState;
            state.highlightedSprite = hoverSprite;
            state.selectedSprite = null;
            if (disabled is { } d && PartSprite(d) is { } disabledSprite) state.disabledSprite = disabledSprite;
            selectable.spriteState = state;
            selectable.transition = Selectable.Transition.SpriteSwap;
        }
        catch { /* the selectable keeps its color tint */ }
    }

    private sealed class UnityCheckboxWidget : UnityWidget
    {
        private readonly Image? _mark;

        public UnityCheckboxWidget(UnityPanel owner, GameObject go, object boolEvent, Image? mark)
            : base(owner, go, null, null, boolEvent, null, null, null)
        {
            _mark = mark;
        }

        protected override void AfterBoolWrite(bool value) => Paint(value);

        public void Paint(bool on)
        {
            if (_mark == null) return;
            try { _mark.enabled = on; } catch { /* */ }
        }
    }

    // ---- the other controls, skinned after they are built -------------------------------------------

    /// <summary>
    /// The game look for a settings-grid control, by its row kind. Kit controls keep their parts; only their
    /// art changes. Every skin is idempotent. CONTROLS1 round 2: true when every game part it wanted was
    /// drawn; false leaves the control on the kit look and the caller retries it
    /// (<see cref="RetrySkinLater"/>) as the settings kit's art arrives on later idle frames.
    /// </summary>
    private static bool SkinGridControl(string kind, GameObject? control)
    {
        if (control == null) return true;
        try
        {
            switch (kind)
            {
                case "toggle":
                    return SkinCheckbox(control);
                case "slider-entry":
                {
                    bool slider = SkinSlider(control);
                    return SkinField(control, kind) & slider;
                }
                case "slider":
                    // GRIDROW1: a plain slider on the grid takes the game's track and handle, as a slider field's does
                    // (it kept the kit's gold bar).
                    return SkinSlider(control);
                case "choice":
                    return SkinChoice(control);
                case "keybind":
                case "button":
                    // Round D: a row's button is the game's standard button (the owned key-cap copy of
                    // SimpleMenuButton_Normal), not a flat kit bar.
                    return SkinKeyCap(control);
                case "numeric":
                {
                    // CONTROLS1: the steppers are the selector's arrow buttons (SimpleBox frames).
                    bool field = SkinField(control, kind);
                    return SkinSteppers(control) & field;
                }
                case "color":
                case "input":
                case "textarea":
                    return SkinField(control, kind);
                case "radio":
                case "multiselect":
                    return SkinOptions(control);
                case "info":
                case "fact":
                    SkinInfoValue(control);
                    return true;
                default:
                    return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: settings skin (" + kind + ") threw: " + e.Message);
            return true;
        }
    }

    // ---- CONTROLS1 round 2: skins retried until the art arrives --------------------------------------

    private static readonly List<SkinRetry> SkinRetries = new();
    private static bool _skinRetryArmed;
    private static int _skinRetryFrame;

    private sealed class SkinRetry
    {
        internal GameObject Owner = null!;
        internal Func<bool> Attempt = null!;
        internal int Until;
        internal int Tries;
    }

    /// <summary>
    /// Try <paramref name="attempt"/> again every <see cref="SettingsControlLook.SkinRetryEveryFrames"/>
    /// frames until it reports complete, <paramref name="owner"/> is destroyed, or
    /// <see cref="SettingsControlLook.SkinRetryForFrames"/> pass. One entry per owner (a later call replaces
    /// it). Core-owned; dropped with the input pumps on workspace close and Core unload.
    /// </summary>
    private static void RetrySkinLater(GameObject? owner, Func<bool> attempt)
    {
        if (owner == null || attempt == null) return;
        for (int i = 0; i < SkinRetries.Count; i++)
        {
            if (!ReferenceEquals(SkinRetries[i].Owner, owner)) continue;
            SkinRetries[i].Attempt = attempt;
            return;
        }

        SkinRetries.Add(new SkinRetry { Owner = owner, Attempt = attempt, Until = _skinRetryFrame + SettingsControlLook.SkinRetryForFrames });
        if (_skinRetryArmed) return;
        _skinRetryArmed = true;
        Plugin.Frame += SkinRetryTick;
    }

    private static void SkinRetryTick()
    {
        _skinRetryFrame++;
        if (_skinRetryFrame % SettingsControlLook.SkinRetryEveryFrames != 0) return;
        for (int i = SkinRetries.Count - 1; i >= 0; i--)
        {
            var r = SkinRetries[i];
            bool done;
            if (!Alive(r.Owner) || _skinRetryFrame > r.Until) done = true;
            else
            {
                r.Tries++;
                try { done = r.Attempt(); }
                catch { done = true; }
            }

            if (done) SkinRetries.RemoveAt(i);
        }

        if (SkinRetries.Count == 0) ReleaseSkinRetries();
    }

    internal static void ReleaseSkinRetries()
    {
        SkinRetries.Clear();
        if (!_skinRetryArmed) return;
        _skinRetryArmed = false;
        try { Plugin.Frame -= SkinRetryTick; } catch { /* */ }
    }

    /// <summary>The settings grid's checkbox, painted again (idempotent): the frame and the mark, the mark's shown state kept.</summary>
    private static bool SkinCheckbox(GameObject control)
    {
        // Only the grid's own checkbox (AddGridCheckbox); an inline kit toggle keeps its look.
        var markGo = control.transform.Find("checkmark");
        if (markGo == null) return true;
        var frame = control.GetComponent<Image>();
        bool framed = PaintPart(frame, SettingsArtPart.CheckboxFrame, Color.white, Grey(0.16f));
        bool marked = true;
        {
            var mark = markGo.GetComponent<Image>();
            bool shown = false;
            try { shown = mark != null && mark.enabled; } catch { /* */ }
            marked = PaintPart(mark, SettingsArtPart.CheckboxMark, Color.white, Grey(0.92f));
            try
            {
                if (mark != null) mark.enabled = shown;
                var rt = markGo.GetComponent<RectTransform>();
                float size = marked ? ModSettingsRowGrid.CheckmarkSize : Math.Max(6f, ModSettingsRowGrid.CheckboxSize * 0.35f);
                if (rt != null) rt.sizeDelta = new Vector2(size, size);
            }
            catch { /* */ }
        }

        if (framed) ApplySpriteSwap(control.GetComponent<Toggle>(), SettingsArtPart.CheckboxFrameHover, SettingsArtPart.CheckboxFrameDisabled);
        return framed && marked;
    }

    /// <summary>
    /// The game's slider (<c>StunGUISlider</c>, M1): the <c>Slider_BG</c> track in its dark tint, the same
    /// sprite lighter as the fill, and the <c>Slider_Handle_Normal</c> diamond (hover sprite on hover).
    /// </summary>
    private static bool SkinSlider(GameObject control)
    {
        var slider = FindIn<Slider>(control);
        if (slider == null) return true;
        Image? fill = null, handle = null, track = null;
        try { fill = slider.fillRect != null ? slider.fillRect.GetComponent<Image>() : null; } catch { /* */ }
        try { handle = slider.handleRect != null ? slider.handleRect.GetComponent<Image>() : null; } catch { /* */ }
        try
        {
            var images = slider.GetComponentsInChildren<Image>(true);
            for (int i = 0; images != null && i < images.Length && track == null; i++)
            {
                var img = images[i];
                if (img == null) continue;
                if (fill != null && img.Pointer == fill.Pointer) continue;
                if (handle != null && img.Pointer == handle.Pointer) continue;
                track = img;
            }
        }
        catch { /* */ }

        float trackH = ModSettingsRowGrid.SliderTrackHeight;
        float handleS = ModSettingsRowGrid.SliderHandleSize;
        bool tracked = PaintPart(track, SettingsArtPart.SliderTrack, Rgba(GameSettingsLook.SliderTrackColor), Rgba(GameSettingsLook.SliderTrackColor));
        PaintPart(fill, SettingsArtPart.SliderTrack, Rgba(GameSettingsLook.SliderFillColor), Rgba(GameSettingsLook.SliderFillColor));
        bool diamond = PaintPart(handle, SettingsArtPart.SliderHandle, Color.white, Rgba(GameSettingsLook.ResetRest));
        if (handle != null) { try { handle.preserveAspect = diamond; } catch { /* */ } }
        // Our built slider's parts (named at build) take the game's heights; a cloned donor keeps its own.
        try { if (track != null && track.gameObject.name == "track") StretchWide(track.gameObject, 0f, trackH); } catch { /* */ }
        try
        {
            var fillArea = slider.fillRect != null ? slider.fillRect.parent : null;
            if (fillArea != null && fillArea.gameObject.name == "fill-area") StretchWide(fillArea.gameObject, handleS * 0.5f, trackH);
            var handleArea = slider.handleRect != null ? slider.handleRect.parent : null;
            if (handleArea != null && handleArea.gameObject.name == "handle-area")
            {
                StretchWide(handleArea.gameObject, handleS * 0.5f, handleS);
                DrivenRect(slider.handleRect!.gameObject, new Vector2(handleS, 0f));
            }
        }
        catch { /* */ }
        if (handle != null) ApplySpriteSwap(slider, SettingsArtPart.SliderHandleHover, null);
        return tracked && (diamond || handle == null);
    }

    /// <summary>
    /// A keybind as the Keybinds tab's key cap (the owned copy of the Bindings Menu's
    /// <c>SimpleMenuButton_Normal</c>, <see cref="KeybindArtDonors.TryKeyCap"/>), sized like a control
    /// (<see cref="SettingsControlShape.KeyCap"/>); the kit's frame when the cap is not ready.
    /// </summary>
    private static bool SkinKeyCap(GameObject control)
    {
        Image? background = null;
        try { background = control.GetComponent<Image>(); } catch { /* */ }
        if (background == null) return true;
        bool drawn = PaintGameButtonArt(background, control);
        // BUTTONART1: until the art arrives (the caller retries), the game buttons' steel face, not the kit's red.
        if (!drawn) PaintNeutralGameButton(background, control);
        try
        {
            var tmp = control.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                tmp.fontSize = GameSettingsLook.ValueText;
                tmp.color = Grey(0.9f);
            }
        }
        catch { /* */ }
        return drawn;
    }

    /// <summary>
    /// CONTROLS1: the game's standard button art (<c>SimpleMenuButton_Normal</c>) from either donor: the
    /// Keybinds tab's key cap, else the settings kit's own copy of the Options row button (live 2026-10-03
    /// the settings page's Reset all and Open in Keybinds drew the kit's flat box while the key caps on the
    /// same page drew the game's). False when neither is ready.
    /// </summary>
    internal static bool TryGameButtonArt(out Sprite? sprite, out Image.Type type, out Color color, out float ppu) =>
        TryGameButtonArt(out sprite, out type, out color, out ppu, out _);

    /// <summary>
    /// CONTROLS1 round 2: the donor order. <c>SimpleMenuButton_Normal</c> (the Options row button) from the
    /// Keybinds tab's key cap or the settings kit; else the game's popup button (<c>SmallerButton_Normal</c>,
    /// <c>CommonPopup</c>), which probe D1 found live and resident in a world as well as at the title.
    /// Live 2026-10-03 (Core hot-swapped in a world, Keybinds tab not opened) neither
    /// <c>SimpleMenuButton</c> donor drew: every row button kept the kit frame. <paramref name="donor"/>
    /// names the rung for the readback: <c>key-cap</c>, <c>menu-button</c>, <c>popup-button</c> or <c>kit</c>.
    /// </summary>
    internal static bool TryGameButtonArt(out Sprite? sprite, out Image.Type type, out Color color, out float ppu, out string donor)
    {
        ppu = 1f;
        donor = GameButtonDonorKeyCap;
        try
        {
            if (KeybindArtDonors.TryKeyCap(out sprite, out type, out color) && sprite != null) return true;
        }
        catch { /* the next donor */ }

        color = Color.white;
        donor = GameButtonDonorMenuButton;
        try
        {
            if (SettingsArtDonors.TryPart(SettingsArtPart.MenuButton, out sprite, out type, out ppu) && sprite != null) return true;
        }
        catch { /* */ }

        donor = GameButtonDonorPopupButton;
        try
        {
            if (SettingsArtDonors.TryPart(SettingsArtPart.PopupButton, out sprite, out type, out ppu) && sprite != null) return true;
        }
        catch { /* */ }

        donor = GameButtonDonorKit;
        sprite = null;
        type = Image.Type.Simple;
        ppu = 1f;
        return false;
    }

    internal const string GameButtonDonorKeyCap = "key-cap";
    internal const string GameButtonDonorMenuButton = "menu-button";
    internal const string GameButtonDonorPopupButton = "popup-button";
    internal const string GameButtonDonorKit = "kit";

    /// <summary>Paint <paramref name="image"/> with the game's button art and drop the kit's rim; false when no art.</summary>
    private static bool PaintGameButtonArt(Image image, GameObject owner)
    {
        if (image == null || !TryGameButtonArt(out var sprite, out var type, out var color, out var ppu, out var donor)) return false;
        try
        {
            image.sprite = sprite;
            image.type = type;
            image.color = color;
            try { image.pixelsPerUnitMultiplier = ppu; } catch { /* older UI */ }
            var rim = owner != null ? owner.GetComponent<Outline>() : null;
            if (rim != null) rim.enabled = false;
            if (donor == GameButtonDonorPopupButton && owner != null)
            {
                var button = owner.GetComponent<Button>();
                if (button != null)
                {
                    try { button.targetGraphic = image; } catch { /* */ }
                    ApplySpriteSwap(button, SettingsArtPart.PopupButtonHover, SettingsArtPart.PopupButtonDisabled);
                }
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// A value or text field as the game's value box (<c>SimpleBox_Normal</c>, 9-sliced; <c>SimpleBox_Hover</c>
    /// on hover). CONTROLS1: a number is centered like the game's value box; free text starts at the left at
    /// the dropdown caption's inset and is middle-aligned (a text area starts at the top), so a text field
    /// no longer reads as a centered caption pinned to the frame's top.
    /// </summary>
    private static bool SkinField(GameObject control, string kind)
    {
        var field = FindIn<TMP_InputField>(control);
        if (field == null) return true;
        Image? box = null;
        try { box = field.targetGraphic != null ? field.targetGraphic.TryCast<Image>() : null; }
        catch { box = null; }
        if (box == null)
        {
            try { box = field.GetComponent<Image>(); } catch { box = null; }
        }

        bool boxed = PaintPart(box, SettingsArtPart.ValueBox, Color.white, Grey(0.06f, 0.95f));
        if (boxed)
        {
            ApplyQuietTint(field, box);
            // The kit's one-pixel rim would draw a second frame around the game's.
            try { var rim = box!.GetComponent<Outline>(); if (rim != null) rim.enabled = false; } catch { /* */ }
        }

        bool centered = SettingsControlLook.FieldCentered(kind);
        bool top = SettingsControlLook.FieldTopAligned(kind);
        try
        {
            var tmp = field.textComponent != null ? field.textComponent.TryCast<TextMeshProUGUI>() : null;
            if (tmp != null)
            {
                tmp.color = Grey(SettingsControlLook.FieldTextGrey);
                tmp.fontSize = SettingsControlLook.FieldText;
                tmp.horizontalAlignment = centered ? HorizontalAlignmentOptions.Center : HorizontalAlignmentOptions.Left;
                tmp.verticalAlignment = top ? VerticalAlignmentOptions.Top : VerticalAlignmentOptions.Middle;
                if (!centered)
                {
                    float extra = Math.Max(0f, SettingsControlLook.FieldTextInset - InputPadX);
                    tmp.margin = new Vector4(extra, top ? 2f : 0f, extra, 0f);
                }

                // The text object fills its viewport, so the vertical alignment acts on the whole box.
                var rt = tmp.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
            }
        }
        catch { /* */ }
        return boxed || box == null;
    }

    /// <summary>
    /// CONTROLS1: a number's − and + steppers as the game selector's arrow buttons — <c>SimpleBox_Normal</c>
    /// squares (<c>SimpleBox_Hover</c> on hover) with a 0.9 caption; the kit's frame when the box is not ready.
    /// </summary>
    private static bool SkinSteppers(GameObject control)
    {
        bool all = true;
        try
        {
            for (int i = 0; i < control.transform.childCount; i++)
            {
                var child = control.transform.GetChild(i);
                if (child == null || child.gameObject.name != "stepper") continue;
                var image = child.GetComponent<Image>();
                if (PaintPart(image, SettingsArtPart.ValueBox, Color.white, Grey(0.10f, 0.95f)))
                {
                    try { var rim = child.GetComponent<Outline>(); if (rim != null) rim.enabled = false; } catch { /* */ }
                    ApplyQuietTint(child.GetComponent<Button>(), image);
                }
                else all = false;

                var tmp = child.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null)
                {
                    tmp.color = Grey(0.9f);
                    tmp.fontSize = SettingsControlLook.FieldText;
                    tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
                    tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                }
            }
        }
        catch { /* the kit steppers stay */ }
        return all;
    }

    /// <summary>
    /// CONTROLS1: a radio group or a multi-select drawn from the Options page's parts (see
    /// <see cref="SettingsControlLook"/>). A segmented option becomes a value-box segment with the selector's
    /// index bar under it; a list option's check box becomes the game's checkbox and checkmark. The state
    /// colors are written by <see cref="UnityOptionsWidget.Apply"/>.
    /// </summary>
    private static bool SkinOptions(GameObject container)
    {
        bool all = true;
        try
        {
            for (int i = 0; i < container.transform.childCount; i++)
            {
                var option = container.transform.GetChild(i);
                if (option == null || !option.gameObject.name.StartsWith("option-", StringComparison.Ordinal)) continue;
                // RADIO1: a radio list line draws its own circle and grays; it has no frame to skin.
                if (option.Find("radio-mark") != null) continue;
                var box = option.Find("check-box");
                bool drawn = box != null ? SkinListOption(option.gameObject, box.gameObject) : SkinSegment(option.gameObject);
                all &= drawn;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings options skin threw: " + e.Message); }
        return all;
    }

    internal const string SegmentIndicatorName = "segment-indicator";

    private static bool SkinSegment(GameObject option)
    {
        var image = option.GetComponent<Image>();
        // Idempotent: a retry keeps the state the options widget last painted (its index bar is opaque
        // under the chosen segment).
        var existing = option.transform.Find(SegmentIndicatorName);
        bool selected = false;
        try
        {
            var existingImage = existing != null ? existing.GetComponent<Image>() : null;
            selected = existingImage != null && existingImage.color.a > 0.99f;
        }
        catch { /* */ }

        var kit = SettingsControlLook.SegmentKitFill(selected);
        bool framed = PaintPart(image, SettingsArtPart.ValueBox, Grey(SettingsControlLook.SegmentFrameGrey(selected)), Rgba(kit));
        if (framed)
        {
            try { var rim = option.GetComponent<Outline>(); if (rim != null) rim.enabled = false; } catch { /* */ }
            ApplyQuietTint(option.GetComponent<Button>(), image);
        }

        // Segments sit apart like the selector's arrow buttons and its box.
        try
        {
            var rt = option.GetComponent<RectTransform>();
            if (rt != null)
            {
                bool first = rt.anchorMin.x <= 0.0001f;
                bool last = rt.anchorMax.x >= 0.9999f;
                float half = SettingsControlLook.IndicatorInsetX * 0.25f;
                rt.offsetMin = new Vector2(first ? 0f : half, rt.offsetMin.y);
                rt.offsetMax = new Vector2(last ? 0f : -half, rt.offsetMax.y);
            }
        }
        catch { /* */ }

        if (existing == null) try
        {
            var bar = new GameObject(SegmentIndicatorName);
            bar.transform.SetParent(option.transform, false);
            var barImage = TryAdd<Image>(bar);
            if (barImage != null)
            {
                barImage.sprite = null;
                barImage.raycastTarget = false;
                barImage.color = Rgba(SettingsControlLook.SegmentIndicator(false));
            }

            var rt = bar.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(SettingsControlLook.IndicatorInsetX, SettingsControlLook.IndicatorInsetBottom);
            rt.offsetMax = new Vector2(-SettingsControlLook.IndicatorInsetX,
                SettingsControlLook.IndicatorInsetBottom + SettingsControlLook.IndicatorHeight);
        }
        catch { /* the caption's gray still marks the choice */ }

        try
        {
            var tmp = option.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                tmp.fontSize = SettingsControlLook.FieldText;
                // Shrink a long caption before it is cut (four 14-character options on a normal row).
                tmp.fontSizeMin = SettingsControlLook.SegmentMinText;
                tmp.fontSizeMax = SettingsControlLook.FieldText;
                tmp.enableAutoSizing = true;
                tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                // Clear of the index bar.
                tmp.margin = new Vector4(4f, 0f, 4f, SettingsControlLook.IndicatorHeight);
            }
        }
        catch { /* */ }
        return framed;
    }

    private static bool SkinListOption(GameObject option, GameObject box)
    {
        float size = SettingsControlLook.ListBoxSize;
        var frame = box.GetComponent<Image>();
        bool framed = PaintPart(frame, SettingsArtPart.CheckboxFrame, Color.white, Grey(0.16f));
        try
        {
            if (frame != null) frame.preserveAspect = true;
            var rt = box.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(size, size);
        }
        catch { /* */ }

        var mark = box.transform.Find("check-mark");
        if (mark != null)
        {
            var markImage = mark.GetComponent<Image>();
            bool enabled = false;
            try { enabled = markImage != null && markImage.enabled; } catch { /* */ }
            bool borrowedMark = PaintPart(markImage, SettingsArtPart.CheckboxMark, Color.white, Grey(0.92f));
            float markSize = borrowedMark ? SettingsControlLook.ListMarkSize : Math.Max(6f, size * 0.35f);
            try
            {
                if (markImage != null)
                {
                    markImage.preserveAspect = true;
                    markImage.enabled = enabled;
                }

                var rt = mark.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = new Vector2(markSize, markSize);
                }
            }
            catch { /* */ }
        }

        // Round 3: the option's button tints the box (one frame geometry in every state, checked or not);
        // the game's Checkbox_Hover is a heavier, larger frame that made the hovered (or last-clicked)
        // option read as a different control.
        if (framed && frame != null) ApplyQuietTint(option.GetComponent<Button>(), frame);

        try
        {
            var tmp = option.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                tmp.fontSize = SettingsControlLook.ListCaptionText;
                tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                tmp.margin = new Vector4(2f + size + SettingsControlLook.ListCaptionGap, 0f, 4f, 0f);
            }
        }
        catch { /* */ }
        return framed;
    }

    /// <summary>A read-only value's gray: muted for <c>Not set</c> (decided 2026-10-06), else the value box's.</summary>
    internal static float InfoValueGrey(string? text) =>
        ModSettingsView.IsNotSetShown(text) ? SettingsControlLook.NotSetGrey : SettingsControlLook.FieldTextGrey;

    /// <summary>CONTROLS1: an info line's or a read-only row's value at the value box's text size and gray.</summary>
    private static void SkinInfoValue(GameObject control)
    {
        try
        {
            var tmp = control.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return;
            tmp.fontSize = SettingsControlLook.FieldText;
            tmp.color = Grey(InfoValueGrey(tmp.text));
            tmp.fontStyle = FontStyles.Normal;
        }
        catch { /* */ }
    }

    /// <summary>
    /// A finite choice as the game's dropdown bar (<c>DropdownButton_Normal</c>, M1): the value left in
    /// 0.7 gray, the <c>DropDownButton_Arrow</c> at the right. DROPDOWN1: a click anywhere on the bar, the chevron
    /// included, opens the list under it (<see cref="UnityChoiceWidget"/>); the selector's arrows are hidden or click-through
    /// here, so nothing on the bar steps the value.
    /// </summary>
    private static bool SkinChoice(GameObject control)
    {
        Image? bar = null;
        try { bar = control.GetComponent<Image>(); } catch { /* */ }
        bool barred = PaintPart(bar, SettingsArtPart.DropdownBar, Color.white, new Color(0.10f, 0.16f, 0.22f, 1f));
        bool chevroned = false;
        if (barred)
        {
            try { ApplySpriteSwap(control.GetComponent<Button>(), SettingsArtPart.DropdownBarHover, null); } catch { /* */ }
        }

        float arrowW = GameSettingsLook.Kit(GameSettingsLook.DropdownArrowWidth);
        float arrowH = GameSettingsLook.Kit(GameSettingsLook.DropdownArrowHeight);
        float arrowRight = GameSettingsLook.Kit(GameSettingsLook.DropdownArrowRight);
        try
        {
            var prev = control.transform.Find("choice-prev");
            if (prev != null) prev.gameObject.SetActive(false);
        }
        catch { /* */ }

        try
        {
            var next = control.transform.Find("choice-next");
            if (next != null)
            {
                var nextImage = next.GetComponent<Image>();
                if (nextImage != null)
                {
                    nextImage.sprite = null;
                    nextImage.color = new Color(0f, 0f, 0f, 0f);
                    // DROPDOWN1: the chevron is part of the bar: a click there opens the list like the rest of the bar.
                    nextImage.raycastTarget = false;
                }

                var nextButton = next.GetComponent<Button>();
                if (nextButton != null) nextButton.enabled = false;

                var chevronSprite = PartSprite(SettingsArtPart.DropdownArrow);
                chevroned = next.Find("chevron") != null;
                if (chevronSprite != null && !chevroned)
                {
                    chevroned = true;
                    var caption = next.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (caption != null) caption.gameObject.SetActive(false);
                    var chevronGo = new GameObject("chevron");
                    chevronGo.transform.SetParent(next, false);
                    var chevron = TryAdd<Image>(chevronGo);
                    if (chevron != null)
                    {
                        chevron.sprite = chevronSprite;
                        chevron.preserveAspect = true;
                        chevron.raycastTarget = false;
                        chevron.color = Grey(GameSettingsLook.ValueGrey);
                        var rt = chevronGo.GetComponent<RectTransform>();
                        rt.anchorMin = new Vector2(1f, 0.5f);
                        rt.anchorMax = new Vector2(1f, 0.5f);
                        rt.pivot = new Vector2(1f, 0.5f);
                        rt.anchoredPosition = new Vector2(-(arrowRight * 0.5f), 0f);
                        rt.sizeDelta = new Vector2(arrowW, arrowH);
                    }
                }
            }
        }
        catch { /* */ }

        try
        {
            var value = control.transform.Find("value-text");
            if (value != null)
            {
                AnchorInside(value.gameObject, 0f, 1f, GameSettingsLook.Kit(GameSettingsLook.DropdownCaptionLeft),
                    -(UiControlDraw.ChoiceArrowWidth + 4f), 0f);
                var tmp = value.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.horizontalAlignment = HorizontalAlignmentOptions.Left;
                    tmp.color = Grey(GameSettingsLook.ValueGrey + 0.15f);
                    tmp.fontSize = GameSettingsLook.DropdownText;
                    // The bar's caption stays left of the chevron at any length (shrinks, then an ellipsis).
                    FitInsideArea(tmp, GameSettingsLook.DropdownText, Math.Min(GameSettingsLook.DropdownText, UiControlDraw.ChoiceValueMinFontSize));
                }
            }
        }
        catch { /* */ }
        return barred && chevroned;
    }

    /// <summary>
    /// The reset link as the game's reset arrow (<c>Icon_Reset</c>): the button's own frame is cleared
    /// (still the click target) and the arrow drawn in it; the glyph stays only when no arrow is found.
    /// </summary>
    private static void SkinResetArrow(GameObject link, GameObject? caption)
    {
        Sprite? arrow = PartSprite(SettingsArtPart.ResetIcon);
        if (arrow == null)
        {
            try { if (KeybindArtDonors.TryResetIcon(out var keybindArrow, out _)) arrow = keybindArrow; }
            catch { arrow = null; }
        }

        if (arrow == null) return;
        try
        {
            var frame = link.GetComponent<Image>();
            if (frame != null)
            {
                frame.sprite = null;
                frame.color = new Color(0f, 0f, 0f, 0f);
                frame.raycastTarget = true;
            }

            if (caption != null) caption.SetActive(false);
            var iconGo = new GameObject("reset-arrow");
            iconGo.transform.SetParent(link.transform, false);
            Stretch(iconGo);
            var icon = TryAdd<Image>(iconGo);
            if (icon == null) return;
            icon.sprite = arrow;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            icon.color = Rgba(GameSettingsLook.ResetRest);
        }
        catch { /* the glyph link still works */ }
    }

    // ---- DIALOGS2 ----------------------------------------------------------------------------------------

    internal const string DialogTitleRuleName = "dialog-title-rule";

    /// <summary>
    /// The game's popup header rule under a dialog's title: <c>Divider_Big_02</c> (the thin steel rule and its
    /// small center ornament), across the content, at the bottom of the title row's reserved band. The kit's
    /// thin line when the art is not ready.
    /// </summary>
    private static void AddDialogTitleRule(GameObject titleRow)
    {
        try
        {
            var ruleGo = new GameObject(DialogTitleRuleName);
            ruleGo.transform.SetParent(titleRow.transform, false);
            var rule = TryAdd<Image>(ruleGo);
            if (rule == null) return;
            bool borrowed = PaintPart(rule, SettingsArtPart.DetailsOrnament, Color.white, new Color(0.36f, 0.48f, 0.6f, 0.6f));
            rule.raycastTarget = false;
            rule.type = Image.Type.Simple;
            var rt = ruleGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, DialogLayout.TitleRuleBottom);
            rt.sizeDelta = new Vector2(DialogLayout.TitleRuleWidth(), borrowed ? DialogLayout.TitleRuleHeight : 1f);
        }
        catch { /* the title stands without its rule */ }
    }

    // ---- IUiRuntime (MENUKIT2) ------------------------------------------------------------------------

    public void SetSettingsDescriptionMode(IRuntimePanel panel, bool on)
    {
        if (panel is UnityPanel host && host.Alive) host.SetDescriptionMode(on);
    }

    public void SetSettingsDescription(IRuntimePanel panel, string heading, string body, string notes, string tone)
    {
        if (panel is not UnityPanel host || !host.Alive) return;
        try { host.SetDescriptionText(heading ?? "", body ?? "", notes ?? "", tone ?? ""); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings description threw: " + e.Message); }
    }

    public Dictionary<string, object?>? DescribeSettingsDrawn(IRuntimePanel panel)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            TryGameButtonArt(out var sprite, out _, out _, out _, out var donor);
            d["buttonArt"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["donor"] = donor,
                ["sprite"] = sprite != null ? sprite.name : "",
            };
            d["pendingSkins"] = SkinRetries.Count;
            d["rows"] = host.DescribeDrawn();
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }

    public void SetGridRowLit(IRuntimeWidget row, bool lit)
    {
        if (row is not UnityWidget widget || !widget.Alive || widget.Owner is not { } host) return;
        host.SetGridRowLit(widget.GameObject, lit);
    }

    public IRuntimeWidget? GridRowPointer(IRuntimeWidget row)
    {
        if (row is not UnityWidget widget || !widget.Alive || widget.Owner is not { } host) return null;
        return host.GridPointerOf(widget.GameObject);
    }

    public void SetResetMark(IRuntimeWidget link, SettingsResetState state)
    {
        if (link is not UnityWidget widget || !widget.Alive) return;
        try
        {
            var icon = widget.GameObject.transform.Find("reset-arrow");
            var image = icon != null ? icon.GetComponent<Image>() : null;
            if (image != null)
            {
                image.color = Rgba(state == SettingsResetState.Changed ? GameSettingsLook.ResetRest : GameSettingsLook.ResetDisabled);
                return;
            }

            // The glyph fallback: the caption carries the same two tones.
            var tmp = widget.GameObject.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
                tmp.color = Rgba(state == SettingsResetState.Changed ? GameSettingsLook.ResetRest : GameSettingsLook.ResetDisabled);
        }
        catch { /* */ }
    }

    /// <summary>Round D: a destructive button (Uninstall): its caption in the danger tone.</summary>
    public void SetDestructive(IRuntimeWidget button)
    {
        if (button is not UnityWidget widget || !widget.Alive) return;
        try
        {
            // Round E (live: the captions stayed white): the grid button row's caption is the button's
            // own TMP (named "label" by the kit, like the row's name). The row's name is the row's direct
            // child; every deeper TMP is a caption.
            var row = widget.GameObject.transform;
            var tmps = widget.GameObject.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; tmps != null && i < tmps.Length; i++)
            {
                var tmp = tmps[i];
                if (tmp == null) continue;
                bool rowName = tmp.transform.parent != null && tmp.transform.parent.Pointer == row.Pointer
                    && widget.GameObject.transform.childCount > 1;
                if (rowName) continue;
                tmp.color = DangerCaption;
            }
        }
        catch { /* */ }
    }

    /// <summary>The destructive caption: the kit's red, lifted so it reads on the key cap.</summary>
    private static Color DangerCaption => new(0.93f, 0.36f, 0.36f, 1f);

    public void SetGameButton(IRuntimeWidget button)
    {
        if (button is not UnityWidget widget || !widget.Alive) return;
        var go = widget.GameObject;
        try
        {
            var image = go.GetComponent<Image>();
            if (image != null && !PaintGameButtonArt(image, go))
            {
                // BUTTONART1: no art yet (live 2026-10-06: the parked sidebar was built before a swap's idle
                // preparation, and its Close Mods kept the kit's dark red). A neutral steel face now, the art
                // when it arrives: retried on idle frames and again whenever a panel is shown.
                PaintNeutralGameButton(image, go);
                NoteGameButtonPending(image, go);
            }

            var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(true);
            for (int i = 0; tmps != null && i < tmps.Length; i++)
            {
                if (tmps[i] == null) continue;
                tmps[i].verticalAlignment = VerticalAlignmentOptions.Middle;
                tmps[i].horizontalAlignment = HorizontalAlignmentOptions.Center;
                tmps[i].color = Grey(0.9f);
                var rt = tmps[i].GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }
            }
        }
        catch { /* the kit button stays */ }
    }

    /// <summary>
    /// INLINEAPPLY1: what the button's face is drawn with now: the game's button art (the sprite the shared ladder resolves
    /// today, <see cref="TryGameButtonArt"/>), the pending flat steel face, or the kit's look.
    /// </summary>
    public string? ButtonArt(IRuntimeWidget button)
    {
        if (button is not UnityWidget widget || !widget.Alive) return null;
        try
        {
            var go = widget.GameObject;
            var image = go.GetComponent<Image>();
            if (image == null) return UiSettingsGrid.KitButtonStyle;
            for (int i = 0; i < PendingGameButtons.Count; i++)
                if (PendingGameButtons[i].Owner != null && PendingGameButtons[i].Owner.Pointer == go.Pointer)
                    return UiSettingsGrid.GameButtonPendingStyle;
            var drawn = image.sprite;
            if (drawn != null && TryGameButtonArt(out var sprite, out _, out _, out _, out _) && sprite != null && drawn.Pointer == sprite.Pointer)
                return UiSettingsGrid.GameButtonStyle;
            return UiSettingsGrid.KitButtonStyle;
        }
        catch { return null; }
    }

    // ---- BUTTONART1: game buttons drawn before their art was ready -----------------------------------

    private static readonly List<(GameObject Owner, Image Image)> PendingGameButtons = new();

    /// <summary>A game button with no art yet: the game buttons' flat steel face and frame, never the kit's dark red.</summary>
    private static void PaintNeutralGameButton(Image image, GameObject owner)
    {
        try
        {
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.color = Rgba(GameSettingsLook.NeutralButtonFill);
            var rim = owner != null ? owner.GetComponent<Outline>() : null;
            if (rim != null)
            {
                rim.enabled = true;
                rim.effectColor = Rgba(GameSettingsLook.NeutralButtonRim);
            }
        }
        catch { /* the kit button stays */ }
    }

    /// <summary>Paint the art when it arrives: retried on idle frames, and again at every panel show.</summary>
    private static void NoteGameButtonPending(Image image, GameObject owner)
    {
        if (image == null || owner == null) return;
        ForgetPendingGameButton(owner);
        PendingGameButtons.Add((owner, image));
        RetrySkinLater(owner, () =>
        {
            bool done = !Alive(owner) || PaintGameButtonArt(image, owner);
            if (done) ForgetPendingGameButton(owner);
            return done;
        });
    }

    private static void ForgetPendingGameButton(GameObject owner)
    {
        for (int i = PendingGameButtons.Count - 1; i >= 0; i--)
            if (ReferenceEquals(PendingGameButtons[i].Owner, owner)) PendingGameButtons.RemoveAt(i);
    }

    /// <summary>At a panel show: paint every pending game button whose art is ready now; forget the dead.</summary>
    private static void RepaintPendingGameButtons()
    {
        if (PendingGameButtons.Count == 0) return;
        for (int i = PendingGameButtons.Count - 1; i >= 0; i--)
        {
            var (owner, image) = PendingGameButtons[i];
            bool done;
            try { done = !Alive(owner) || image == null || PaintGameButtonArt(image, owner); }
            catch { done = true; }
            if (done) PendingGameButtons.RemoveAt(i);
        }
    }

    internal static void ReleasePendingGameButtons() => PendingGameButtons.Clear();

    public void SetRowChip(IRuntimeWidget row, string text)
    {
        if (row is not UnityWidget widget || !widget.Alive || widget.Owner is not { } host) return;
        host.SetRowChip(widget.GameObject, text ?? "");
    }

    public string? RowCaption(IRuntimeWidget row)
    {
        if (row is not UnityWidget widget || !widget.Alive || widget.Owner is not { } host) return null;
        return host.RowCaption(widget.GameObject);
    }

    public void AddSectionRule(IRuntimeWidget headingRow)
    {
        GameObject? go = headingRow switch
        {
            UnityCellRow cell => cell.GameObject,
            UnityWidget widget => widget.GameObject,
            _ => null,
        };
        if (go == null || !Alive(go)) return;
        try
        {
            if (go.transform.Find("section-rule") != null) return;
            // Round E: the kit's own heading rule spans the row; the game's rule takes its place.
            var kitRule = go.transform.Find("heading-rule");
            if (kitRule != null) kitRule.gameObject.SetActive(false);
            var ruleGo = new GameObject("section-rule");
            ruleGo.transform.SetParent(go.transform, false);
            var rule = TryAdd<Image>(ruleGo);
            if (rule == null) return;
            PaintPart(rule, SettingsArtPart.SectionRule, Rgba(GameSettingsLook.HeaderRule), Rgba(GameSettingsLook.HeaderRule));
            rule.raycastTarget = false;
            var rt = ruleGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(GameSettingsLook.HeaderRuleShare, 0f);
            rt.pivot = new Vector2(0f, 0f);
            rt.offsetMin = new Vector2(0f, 0f);
            rt.offsetMax = new Vector2(0f, ModSettingsRowGrid.HeaderRuleHeight);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: section rule threw: " + e.Message); }
    }

    // ---- the panel's side ------------------------------------------------------------------------------

    private sealed partial class UnityPanel
    {
        /// <summary>One grid row's game chrome: the band, the top gradient, its pointer events, and its lit state.</summary>
        private sealed class GridChrome
        {
            internal Image? Band;
            internal Image? Top;
            internal bool TopBorrowed;
            internal bool BandBorrowed;
            internal object? Enter;
            internal object? Exit;
            internal bool Lit;
            internal GameObject? Chip;
            internal string ChipText = "";
        }

        private readonly Dictionary<GameObject, GridChrome> _gridChrome = new();
        private bool _descriptionMode;
        private float _gridTop = -1f;
        private float _gridBottom;
        private GameObject? _column;
        private Image? _columnBack;
        private Image? _columnOrnament;
        private GameObject? _columnHeading;
        private GameObject? _columnBody;
        private GameObject? _columnNotes;
        private string _descHeading = "";
        private string _descBody = "";
        private string _descNotes = "";
        private string _descTone = "";
        private float _columnHeight;
        private bool _stickyArmed;

        /// <summary>The description column is drawn (the mode is on and the content is wide enough).</summary>
        internal bool ColumnShown => _descriptionMode && ModSettingsDescriptionLayout.HasColumn(FullRowWidth);

        /// <summary>A settings-grid row's width: the content, less the description column when it is shown.</summary>
        internal float GridWidth => ColumnShown ? ModSettingsDescriptionLayout.RowsWidth(FullRowWidth) : FullRowWidth;

        /// <summary>A plain content row placed after the first settings row while the column is shown sits beside it.</summary>
        private bool BesideColumn(in PanelRow row) =>
            !row.Grid && row.Pin == MenuRowPin.Content && ColumnShown
            && (_gridTop >= 0f || string.Equals(row.Kind, "heading", StringComparison.Ordinal));

        /// <summary>A grid row sits left-aligned with the content: its center moves left by half the column's share.</summary>
        internal float GridShiftX => -(FullRowWidth - GridWidth) * 0.5f;

        private const float GridHelpLineHeight = 18f;

        internal void SetDescriptionMode(bool on)
        {
            if (_descriptionMode == on) return;
            _descriptionMode = on;
            Relayout();
        }

        private bool IsGridRow(GameObject go)
        {
            for (int i = 0; i < _rows.Count; i++)
                if (ReferenceEquals(_rows[i].Go, go)) return _rows[i].Grid;
            return _grid;
        }

        /// <summary>A grid row's own help line is drawn only when there is no description column.</summary>
        private bool GridHelpShown(GameObject? go) =>
            go != null && !ColumnShown && _rowHelp.TryGetValue(go, out var help) && help != null;

        /// <summary>
        /// MENUKIT2: a new grid row gets the game's chrome — the row band (the hover and selection tint,
        /// also the row's pointer target) and the resting top gradient — its name the game's size and
        /// gray, and its control the game's skin.
        /// </summary>
        private void PrepareGridRow(GameObject go, GameObject? label, string kind, GameObject? control)
        {
            if (go == null) return;
            // Only the rows that are settings get the row chrome: not a heading, a note or the action row.
            if (!IsSettingRowKind(kind)) return;
            try
            {
                var chrome = new GridChrome();
                var bandGo = new GameObject("row-band");
                bandGo.transform.SetParent(go.transform, false);
                bandGo.transform.SetAsFirstSibling();
                var band = TryAdd<Image>(bandGo);
                if (band != null)
                {
                    band.sprite = null;
                    band.raycastTarget = true;
                    var rt = bandGo.GetComponent<RectTransform>();
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.offsetMin = new Vector2(0f, -ModSettingsRowGrid.BandOverhang);
                    rt.offsetMax = Vector2.zero;
                }

                var topGo = new GameObject("row-top");
                topGo.transform.SetParent(go.transform, false);
                topGo.transform.SetSiblingIndex(1);
                var top = TryAdd<Image>(topGo);
                if (top != null)
                {
                    chrome.TopBorrowed = PaintPart(top, SettingsArtPart.RowBandTop, Color.white, Color.white);
                    top.raycastTarget = false;
                    var rt = topGo.GetComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = new Vector2(0f, chrome.TopBorrowed ? ModSettingsRowGrid.TopStripHeight : 1f);
                }

                chrome.Band = band;
                chrome.Top = top;
                EnsurePointerEvents(go, out chrome.Enter, out chrome.Exit);
                _gridChrome[go] = chrome;
                PaintChrome(go, chrome);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings row chrome threw: " + e.Message); }

            // CONTROLS1 round 2: a part the settings kit has not prepared yet is drawn when it arrives.
            if (control != null && !SkinGridControl(kind, control))
            {
                var c = control;
                var k = kind;
                RetrySkinLater(c, () => SkinGridControl(k, c));
            }
        }

        /// <summary>
        /// CONTROLS1 round 2: what each settings row actually drew, read from the live objects (not from
        /// what the skin meant to draw): the row band, and every image under the control (sprite, color,
        /// type, pixels-per-unit, rim, sprite states) and its first text's color and size. A cell row (the
        /// settings header, a section's Open in Keybinds) reports its buttons and fields.
        /// </summary>
        internal List<Dictionary<string, object?>> DescribeDrawn()
        {
            var list = new List<Dictionary<string, object?>>();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Go == null || !UnityUiRuntime.Alive(row.Go)) continue;
                bool setting = IsSettingRowKind(row.Kind);
                bool cells = row.Kind is "cellrow" or "cellheader";
                if (!setting && !cells) continue;
                var d = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["kind"] = row.Kind,
                    ["name"] = TextOf(row.Label),
                    ["grid"] = row.Grid,
                };
                if (_gridChrome.TryGetValue(row.Go, out var chrome))
                {
                    d["band"] = DrawnImage(chrome.Band, row.Go);
                    d["lit"] = chrome.Lit;
                }

                var images = new List<object?>();
                var root = setting ? (row.Control ?? row.Go) : row.Go;
                try
                {
                    if (setting)
                    {
                        foreach (var img in root.GetComponentsInChildren<Image>(true))
                        {
                            if (images.Count >= 14) break;
                            images.Add(DrawnImage(img, row.Go));
                        }

                        var swatch = row.Go.transform.Find("swatch");
                        if (swatch != null) images.Add(DrawnImage(swatch.GetComponent<Image>(), row.Go));
                    }
                    else
                    {
                        foreach (var sel in root.GetComponentsInChildren<Selectable>(true))
                        {
                            if (images.Count >= 14) break;
                            var img = sel.targetGraphic != null ? sel.targetGraphic.TryCast<Image>() : sel.GetComponent<Image>();
                            var entry = DrawnImage(img ?? sel.GetComponent<Image>(), row.Go);
                            if (entry != null) entry["text"] = TextOf(sel.gameObject);
                            images.Add(entry);
                        }
                    }
                }
                catch (Exception e) { d["error"] = e.Message; }

                d["images"] = images;
                try
                {
                    var tmp = setting && row.Control != null ? row.Control.GetComponentInChildren<TextMeshProUGUI>(true) : null;
                    if (tmp != null)
                        d["text"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["color"] = ColorList(tmp.color),
                            ["size"] = Math.Round(tmp.fontSize, 1),
                            ["autoSize"] = tmp.enableAutoSizing,
                        };
                }
                catch { /* */ }

                list.Add(d);
            }

            return list;
        }

        private static string TextOf(GameObject? go)
        {
            if (go == null) return "";
            try
            {
                var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
                return tmp != null ? tmp.text ?? "" : "";
            }
            catch { return ""; }
        }

        private static List<object?> ColorList(Color c) => new()
        {
            Math.Round(c.r, 3), Math.Round(c.g, 3), Math.Round(c.b, 3), Math.Round(c.a, 3),
        };

        private static string SpriteName(Sprite? s)
        {
            try { return s != null ? s.name ?? "" : ""; }
            catch { return "?"; }
        }

        private static Dictionary<string, object?>? DrawnImage(Image? image, GameObject rowGo)
        {
            if (image == null) return null;
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            try
            {
                d["path"] = PathUnder(image.transform, rowGo.transform);
                d["sprite"] = SpriteName(image.sprite);
                // A SpriteSwap state draws through overrideSprite, not sprite: report what is on screen.
                try { d["override"] = SpriteName(image.overrideSprite); } catch { /* */ }
                d["color"] = ColorList(image.color);
                try { d["tint"] = ColorList(image.canvasRenderer.GetColor()); } catch { /* */ }
                d["type"] = image.type.ToString();
                d["enabled"] = image.enabled && image.gameObject.activeInHierarchy;
                try { d["ppu"] = Math.Round(image.pixelsPerUnitMultiplier, 2); } catch { /* */ }
                try
                {
                    var tex = image.sprite != null ? image.sprite.texture : null;
                    d["texture"] = image.sprite == null ? "" : tex == null ? "released" : "alive";
                }
                catch { d["texture"] = "?"; }
                var rim = image.GetComponent<Outline>();
                if (rim != null) d["rim"] = rim.enabled ? ColorList(rim.effectColor) : null;
                var sel = image.GetComponent<Selectable>();
                if (sel != null)
                {
                    var state = sel.spriteState;
                    d["transition"] = sel.transition.ToString();
                    d["hover"] = SpriteName(state.highlightedSprite);
                    d["selectedSprite"] = SpriteName(state.selectedSprite);
                }
            }
            catch (Exception e) { d["error"] = e.Message; }
            return d;
        }

        private static string PathUnder(Transform t, Transform root)
        {
            var parts = new List<string>();
            try
            {
                for (var cur = t; cur != null && cur.Pointer != root.Pointer && parts.Count < 8; cur = cur.parent)
                    parts.Insert(0, cur.gameObject.name);
            }
            catch { /* */ }
            return parts.Count == 0 ? "." : string.Join("/", parts);
        }

        internal static bool IsSettingRowKind(string? kind) => kind is "toggle" or "slider-entry" or "slider" or "choice"
            or "numeric" or "color" or "input" or "keybind" or "button" or "info" or "fact" or "textarea" or "radio" or "multiselect";

        private void PaintChrome(GameObject go, GridChrome chrome)
        {
            // CONTROLS1 round 2: the game's own band, ListButton_Glow2, composed as the game does (image
            // alpha 0.753 x lit tint 0.102). Live 2026-10-03 the flat fill at 0.1 measured rgb(26,28,46)
            // on the page's rgb(22,0,0) — the linear-space prediction — but still read as an even navy slab
            // across the row; the game's band is that sprite's gradient. Tried again on every paint until
            // the sprite is ready; the flat fill stands in until then.
            if (chrome.Band != null && !chrome.BandBorrowed)
                chrome.BandBorrowed = PaintPart(chrome.Band, SettingsArtPart.RowBand, new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0f));
            var hue = chrome.BandBorrowed ? SettingsControlLook.SpriteRowBand(chrome.Lit) : GameSettingsLook.FlatRowBand();
            try { if (chrome.Band != null) chrome.Band.color = new Color(hue.R, hue.G, hue.B, chrome.Lit ? hue.A : 0f); } catch { /* */ }
            try
            {
                if (chrome.Top != null)
                {
                    var t = chrome.Lit ? GameSettingsLook.RowBandTopLit : GameSettingsLook.RowBandTopRest;
                    // A flat 1-unit line (no gradient sprite) would be too loud at the gradient's alphas.
                    float a = chrome.TopBorrowed ? t.A : Math.Min(0.35f, t.A * 2.5f);
                    chrome.Top.color = new Color(t.R, t.G, t.B, a);
                }
            }
            catch { /* */ }
            PaintName(go, chrome.Lit);
        }

        private void PaintName(GameObject go, bool lit)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!ReferenceEquals(_rows[i].Go, go)) continue;
                var label = _rows[i].Label;
                if (label == null || ReferenceEquals(label, go)) return;
                try
                {
                    var tmp = label.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.color = Grey(GameSettingsLook.NameGrey(lit));
                }
                catch { /* */ }
                return;
            }
        }

        /// <summary>NAV1: whether <paramref name="go"/> is a grid row with the game's band (its lit state is the focus marker).</summary>
        internal bool HasRowBand(GameObject go) => go != null && _gridChrome.ContainsKey(go);

        internal void SetGridRowLit(GameObject go, bool lit)
        {
            if (go == null || !_gridChrome.TryGetValue(go, out var chrome) || chrome.Lit == lit) return;
            chrome.Lit = lit;
            PaintChrome(go, chrome);
        }

        internal IRuntimeWidget? GridPointerOf(GameObject go)
        {
            if (go == null || !_gridChrome.TryGetValue(go, out var chrome) || chrome.Enter == null) return null;
            // Only its two pointer events are read (to wire a slot); it is never released or registered.
            return new UnityWidget(null, go, null, null, null, null, chrome.Enter, chrome.Exit);
        }

        /// <summary>
        /// Live 2026-10-06: a read-only value written after the layout (the server's value arriving after the page was drawn,
        /// "Admin only · …" → "Admin only · PvP") left the chip where the shorter text ended, over the new text. A row with
        /// a chip shown is laid out again when its value text changes.
        /// </summary>
        internal void NoteRowValueChanged(GameObject go)
        {
            if (go == null || !_gridChrome.TryGetValue(go, out var chrome) || chrome.Chip == null || chrome.ChipText.Length == 0) return;
            Relayout();
        }

        /// <summary>The text of the caption drawn for row <paramref name="go"/> ("" when it has none or it is hidden), or null when the row is not this panel's.</summary>
        internal string? RowCaption(GameObject go)
        {
            if (go == null) return null;
            foreach (var row in _rows)
            {
                if (row.Go == null || row.Go.Pointer != go.Pointer) continue;
                try
                {
                    if (row.Label == null || !row.Label.activeInHierarchy) return "";
                    var tmp = row.Label.GetComponent<TextMeshProUGUI>();
                    return tmp != null ? tmp.text ?? "" : "";
                }
                catch { return null; }
            }

            return null;
        }

        internal void SetRowChip(GameObject go, string text)
        {
            if (go == null || !_gridChrome.TryGetValue(go, out var chrome)) return;
            if (string.IsNullOrEmpty(text))
            {
                if (chrome.Chip != null && chrome.ChipText.Length > 0)
                {
                    chrome.ChipText = "";
                    try { chrome.Chip.SetActive(false); } catch { /* */ }
                    Relayout();
                }
                return;
            }

            if (chrome.Chip == null || !UnityUiRuntime.Alive(chrome.Chip))
            {
                // Round D: a bordered chip (a dark fill and a thin steel rim), not loose text.
                var chipGo = new GameObject("row-chip");
                chipGo.transform.SetParent(go.transform, false);
                var fill = TryAdd<Image>(chipGo);
                if (fill != null)
                {
                    fill.sprite = null;
                    fill.color = new Color(0.06f, 0.09f, 0.12f, 0.92f);
                    fill.raycastTarget = false;
                }

                var rim = TryAdd<Outline>(chipGo);
                if (rim != null)
                {
                    try
                    {
                        rim.effectColor = Rgba(GameSettingsLook.ResetRest);
                        rim.effectDistance = new Vector2(1f, -1f);
                    }
                    catch { /* */ }
                }

                var caption = MakeLabel(chipGo, text, Vector2.zero, ChipFontSize, left: false);
                if (caption != null)
                {
                    caption.name = "chip-text";
                    Stretch(caption);
                    try
                    {
                        var tmp = caption.GetComponent<TextMeshProUGUI>();
                        if (tmp != null)
                        {
                            tmp.raycastTarget = false;
                            tmp.enableWordWrapping = false;
                            tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
                            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                        }
                    }
                    catch { /* */ }
                }

                chrome.Chip = chipGo;
            }

            try
            {
                var tmp = chrome.Chip.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null)
                {
                    tmp.text = text;
                    // Restart is a thing to do (the warning tone); the server's hand is information.
                    tmp.color = text == ModSettingsRowMarks.RestartChip ? ToColor(MenuPalette.Warn) : Grey(0.78f);
                }
            }
            catch { /* */ }
            bool changed = chrome.ChipText != text;
            chrome.ChipText = text;
            try { chrome.Chip.SetActive(true); } catch { /* */ }
            if (changed) Relayout();
        }

        /// <summary>The band and top gradient cover the row object and reach over the gap under it.</summary>
        private void PlaceGridChrome(in PanelRow row, float height)
        {
            // Anchored at build; nothing to move per pass. The name keeps its tone after a relayout.
            if (row.Go != null && _gridChrome.TryGetValue(row.Go, out var chrome)) PaintName(row.Go, chrome.Lit);
        }

        /// <summary>
        /// A grid row's name (the game's size, one line, cut with an ellipsis), its optional one-line help
        /// (narrow panes), the restart chip at the end of the name column, the reset arrow in the gutter,
        /// and a divider's rule.
        /// </summary>
        private void PlaceGridRowParts(in PanelRow row, in RowColumns columns, float total)
        {
            if (row.Go == null) return;
            var grid = ModSettingsRowGrid.For(GridWidth);
            if (string.Equals(row.Kind, "divider", StringComparison.Ordinal))
            {
                try
                {
                    var rule = row.Go.transform.Find("divider-rule");
                    if (rule != null) PlaceTop(rule.gameObject, 0f, total * 0.5f, grid.Right, 1f);
                }
                catch { /* */ }
                return;
            }

            bool helpShown = GridHelpShown(row.Go);
            float nameH = ModSettingsRowGrid.NameFontSize + 8f;
            float block = nameH + (helpShown ? GridHelpLineHeight : 0f);
            float blockTop = Math.Max(0f, (total - block) * 0.5f);
            if (_gridChrome.TryGetValue(row.Go, out var chrome) && chrome.Chip != null)
            {
                bool chipOn = false;
                try { chipOn = chrome.Chip.activeSelf; } catch { /* */ }
                if (chipOn)
                {
                    // Round D: immediately left of the value or control, as one right-aligned group.
                    float w = ModSettingsRowGrid.ChipWidth(CellLayout.EstimateTextWidth(chrome.ChipText, ChipFontSize));
                    float controlLeft = ControlLeftEdge(row, grid);
                    try
                    {
                        var rt = chrome.Chip.GetComponent<RectTransform>();
                        rt.anchorMin = new Vector2(0.5f, 0.5f);
                        rt.anchorMax = new Vector2(0.5f, 0.5f);
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.sizeDelta = new Vector2(w, ModSettingsRowGrid.ChipHeight);
                        rt.anchoredPosition = new Vector2(ModSettingsRowGrid.ChipCenterX(controlLeft, w), 0f);
                    }
                    catch { /* */ }
                }
            }

            if (row.Label != null && !ReferenceEquals(row.Label, row.Go))
            {
                try
                {
                    float w = Math.Max(1f, columns.LabelWidth);
                    var rt = row.Label.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        rt.anchorMin = new Vector2(0.5f, 0.5f);
                        rt.anchorMax = new Vector2(0.5f, 0.5f);
                        rt.pivot = new Vector2(0.5f, 0.5f);
                        rt.sizeDelta = new Vector2(w, nameH);
                        rt.anchoredPosition = new Vector2(columns.LabelX, total * 0.5f - blockTop - nameH * 0.5f);
                    }

                    var tmp = row.Label.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        tmp.fontSize = ModSettingsRowGrid.NameFontSize;
                        tmp.fontStyle = FontStyles.Normal;
                        tmp.enableWordWrapping = false;
                        tmp.overflowMode = TextOverflowModes.Ellipsis;
                        tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                        tmp.horizontalAlignment = HorizontalAlignmentOptions.Left;
                    }
                }
                catch { /* */ }
            }

            if (_rowHelp.TryGetValue(row.Go, out var help) && help != null)
            {
                try
                {
                    help.SetActive(helpShown);
                    if (helpShown)
                    {
                        // One line, cut with an ellipsis: the whole help is the readback's helpLine.
                        var helpTmp = help.GetComponent<TextMeshProUGUI>();
                        if (helpTmp != null)
                        {
                            helpTmp.enableWordWrapping = false;
                            helpTmp.overflowMode = TextOverflowModes.Ellipsis;
                        }

                        var rt = help.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            rt.anchorMin = new Vector2(0.5f, 0.5f);
                            rt.anchorMax = new Vector2(0.5f, 0.5f);
                            rt.pivot = new Vector2(0.5f, 0.5f);
                            rt.sizeDelta = new Vector2(columns.LabelWidth, GridHelpLineHeight);
                            rt.anchoredPosition = new Vector2(columns.LabelX,
                                total * 0.5f - blockTop - nameH - GridHelpLineHeight * 0.5f);
                        }
                    }
                }
                catch { /* */ }
            }

            if (_gutterLinks.TryGetValue(row.Go, out var link) && link != null)
            {
                float size = ModSettingsRowGrid.ResetSize;
                try
                {
                    var rt = link.GetComponent<RectTransform>() ?? link.AddComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = new Vector2(grid.FromCenter(grid.ResetCenterX), 0f);
                    rt.sizeDelta = new Vector2(size, size);
                }
                catch { /* */ }
            }
        }

        private const float ChipFontSize = 13f;

        /// <summary>
        /// The drawn left edge of a row's value or control, from the row's center: the control's rect, or for a
        /// right-aligned read-only value the start of its text.
        /// </summary>
        private static float ControlLeftEdge(in PanelRow row, in ModSettingsRowGrid grid)
        {
            float fallback = grid.FromCenter(grid.ControlLeft);
            if (row.Control == null) return fallback;
            try
            {
                var rt = row.Control.GetComponent<RectTransform>();
                if (rt == null) return fallback;
                float right = rt.anchoredPosition.x + rt.sizeDelta.x * 0.5f;
                float left = rt.anchoredPosition.x - rt.sizeDelta.x * 0.5f;
                if (string.Equals(row.Kind, "info", StringComparison.Ordinal))
                {
                    var tmp = row.Control.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (tmp != null)
                    {
                        // The text as it reads now (a value written after the last layout re-places the chip, below).
                        try { tmp.ForceMeshUpdate(); } catch { /* the preferred width may lag one frame */ }
                        float textW = tmp.preferredWidth;
                        if (textW > 0f) left = ModSettingsRowGrid.ReadOnlyValueLeft(right, rt.sizeDelta.x, textW);
                    }
                }

                if (string.Equals(row.Kind, "color", StringComparison.Ordinal))
                    left -= SettingsControlLook.SwatchGap + SettingsControlLook.SwatchSize(rt.sizeDelta.y);

                return left;
            }
            catch { return fallback; }
        }

        /// <summary>A grid control in its slot (<see cref="ModSettingsRowGrid.ControlSlot"/>), centered on the row.</summary>
        private void PlaceGameGridControl(in PanelRow row, RectTransform controlRt)
        {
            var grid = ModSettingsRowGrid.For(GridWidth);
            var shape = ModSettingsRowGrid.ShapeOfKind(row.Kind);
            float tall = 0f;
            try { tall = controlRt.rect.height; } catch { /* */ }
            if (row.Go != null && _minHeights.TryGetValue(row.Go, out float least)) tall = Math.Max(tall, least);
            (float Left, float Width, float Height) slot;
            switch (shape)
            {
                case SettingsControlShape.SliderWithValue:
                    // The container spans the track and the value box; its parts are split below.
                    slot = (grid.ControlLeft, grid.CompactRight - grid.ControlLeft, ModSettingsRowGrid.ValueBoxHeight);
                    break;
                case SettingsControlShape.ValueBox when string.Equals(row.Kind, "numeric", StringComparison.Ordinal):
                {
                    // A number keeps its steppers either side of the value box.
                    float w = Math.Min(grid.CompactRight - grid.ControlLeft, ModSettingsRowGrid.ValueBoxWidth + 2f * ModSettingsRowGrid.ValueBoxHeight);
                    slot = (grid.CompactRight - w, w, ModSettingsRowGrid.ValueBoxHeight);
                    break;
                }
                default:
                    slot = grid.ControlSlot(shape, tall);
                    break;
            }

            try
            {
                controlRt.anchorMin = new Vector2(0.5f, 0.5f);
                controlRt.anchorMax = new Vector2(0.5f, 0.5f);
                controlRt.pivot = new Vector2(0.5f, 0.5f);
                controlRt.anchoredPosition = new Vector2(grid.FromCenter(slot.Left + slot.Width * 0.5f), 0f);
                controlRt.sizeDelta = new Vector2(slot.Width, slot.Height);
            }
            catch { /* the control keeps its build-time placement */ }

            if (shape == SettingsControlShape.SliderWithValue && row.Control != null) SplitSliderEntry(row.Control, grid, slot.Width);
            if (string.Equals(row.Kind, "color", StringComparison.Ordinal) && row.Go != null) PlaceColorSwatch(row.Go, row.Control, grid, slot);
        }

        /// <summary>
        /// CONTROLS1: a color's swatch sits immediately left of its value box (it was left at the control
        /// column's start, far from the hex it shows), square at the box's height, framed in the game's
        /// value-box steel.
        /// </summary>
        private static void PlaceColorSwatch(GameObject rowGo, GameObject? control, in ModSettingsRowGrid grid, (float Left, float Width, float Height) box)
        {
            try
            {
                var swatch = rowGo.transform.Find("swatch");
                if (swatch == null || (control != null && swatch.gameObject.Pointer == control.Pointer)) return;
                float size = SettingsControlLook.SwatchSize(box.Height);
                var rt = swatch.GetComponent<RectTransform>();
                if (rt == null) return;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(size, size);
                rt.anchoredPosition = new Vector2(grid.FromCenter(box.Left - SettingsControlLook.SwatchGap - size * 0.5f), 0f);
                if (swatch.GetComponent<Outline>() == null)
                {
                    var rim = TryAdd<Outline>(swatch.gameObject);
                    if (rim != null)
                    {
                        rim.effectColor = new Color(0.25f, 0.29f, 0.31f, 1f);
                        rim.effectDistance = new Vector2(1f, -1f);
                    }
                }
            }
            catch { /* the swatch keeps its build-time place */ }
        }

        /// <summary>
        /// The track from the column's start, the value box at the compact edge (hidden on a narrow column). A plain slider
        /// (no value box) keeps a slider field's track width, so the two line up on one page (GRIDROW1).
        /// </summary>
        private static void SplitSliderEntry(GameObject container, in ModSettingsRowGrid grid, float width)
        {
            try
            {
                var slider = FindIn<Slider>(container);
                var field = FindIn<TMP_InputField>(container);
                bool box = grid.SliderHasValueBox && field != null;
                float boxW = ModSettingsRowGrid.SliderValueBoxWidth;
                float trackW = ModSettingsRowGrid.SliderTrackWidth(width, grid.SliderHasValueBox);
                if (slider != null)
                    AnchorInside(slider.gameObject, 0f, 0f, 0f, trackW, 0f);
                if (field != null)
                {
                    field.gameObject.SetActive(box);
                    if (box) AnchorInside(field.gameObject, 1f, 1f, -boxW, 0f, 0f);
                }
            }
            catch { /* the pair keeps its build-time split */ }
        }

        // ---- the description column ----------------------------------------------------------------

        private void BeginGridPass()
        {
            _gridTop = -1f;
            _gridBottom = 0f;
        }

        private void NoteGridRow(in PanelRow row, float top, float height)
        {
            if (_gridTop < 0f || top < _gridTop) _gridTop = top;
            _gridBottom = Math.Max(_gridBottom, top + height);
        }

        internal void SetDescriptionText(string heading, string body, string notes, string tone)
        {
            _descHeading = heading;
            _descBody = body;
            _descNotes = notes;
            _descTone = tone;
            if (ColumnShown) PlaceSettingsColumn();
        }

        /// <summary>
        /// The column: from the first grid row down, at the content's right, sized to its text, and kept in
        /// view while the rows scroll (<see cref="StickyColumnTick"/>). Hidden when there is none.
        /// </summary>
        private void PlaceSettingsColumn()
        {
            if (!ColumnShown || _gridTop < 0f)
            {
                if (_column != null) { try { _column.SetActive(false); } catch { /* */ } }
                ApplyScrollbarShift();
                return;
            }

            try
            {
                EnsureColumn();
                if (_column == null) return;
                _column.SetActive(true);
                float width = ModSettingsDescriptionLayout.ColumnWidth(FullRowWidth);
                var pad = ModSettingsDescriptionLayout.Padding;
                float inner = Math.Max(1f, width - pad.L - pad.R);
                float spacing = ModSettingsDescriptionLayout.Spacing;
                float y = pad.T;
                y = PlaceColumnText(_columnHeading, _descHeading, inner, y, spacing);
                if (_columnOrnament != null)
                {
                    float oh = Math.Max(4f, GameSettingsLook.Kit(12f) * 0.5f);
                    PlaceTop(_columnOrnament.gameObject, (pad.L - pad.R) * 0.5f, y, inner, oh);
                    y += oh + spacing;
                }

                y = PlaceColumnText(_columnBody, _descBody, inner, y, spacing);
                y = PlaceColumnText(_columnNotes, _descNotes, inner, y, spacing);
                try
                {
                    var tmp = _columnNotes != null ? _columnNotes.GetComponent<TextMeshProUGUI>() : null;
                    if (tmp != null)
                        tmp.color = _descTone == ModSettingsDescription.ToneWarn ? ToColor(MenuPalette.Warn) : Grey(0.55f);
                }
                catch { /* */ }

                _columnHeight = Math.Max(y + pad.B, 80f);
                float x = FullRowWidth * 0.5f - width * 0.5f;
                PlaceTop(_column, x, StickyTop(), width, _columnHeight);
                ArmSticky();
                ApplyScrollbarShift();
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings description column threw: " + e.Message); }
        }

        /// <summary>
        /// Round C: while the description column is shown, the scrollbar moves in from the window border to
        /// the gap between the rows and the column, as the game's sits between its list and its details.
        /// </summary>
        internal float ScrollbarShiftX =>
            ColumnShown && _gridTop >= 0f
                ? ModSettingsDescriptionLayout.ColumnWidth(FullRowWidth) + ModSettingsDescriptionLayout.ColumnGap * 0.5f
                : 0f;

        /// <summary>The scrollbar's top inset as the chrome fit last placed it (the viewport's top).</summary>
        internal float ScrollbarBaseTop = -1f;

        /// <summary>
        /// Round D (live: a thick bright bar from the window top past the status bar): while the column is
        /// shown the bar is thin (<see cref="ModSettingsDescriptionLayout.ScrollbarWidth"/>), a dark track with a
        /// gray handle, in the gap between the rows and the column, and it starts at the first settings row,
        /// so it never runs beside the status bar or the title. Off the settings page it is the kit's bar.
        /// Hidden when the content fits (the ScrollRect's AutoHide).
        /// </summary>
        private void ApplyScrollbarShift()
        {
            if (_scrollbar == null) return;
            try
            {
                var bar = _scrollbar.GetComponent<RectTransform>();
                if (bar == null) return;
                if (ScrollbarBaseTop < 0f) ScrollbarBaseTop = Math.Max(0f, -bar.offsetMax.y);
                float shift = ScrollbarShiftX;
                bool settings = shift > 0f;
                float width = settings ? ModSettingsDescriptionLayout.ScrollbarWidth : PanelLayout.ScrollbarWidth;
                float top = ScrollbarBaseTop + (settings ? Math.Max(0f, _gridTop) : 0f);
                float x = settings ? shift - ModSettingsDescriptionLayout.ScrollbarWidth * 0.5f : 0f;
                bar.offsetMin = new Vector2(-(PanelLayout.ScrollbarInset + width + x), bar.offsetMin.y);
                bar.offsetMax = new Vector2(-(PanelLayout.ScrollbarInset + x), -top);
                PaintScrollbar(settings);
            }
            catch { /* the bar stays at the border */ }
        }

        /// <summary>Round E: the game's scrollbar tones — a near-black track, a dark steel handle (its thumb reads rgb(28,42,54)).</summary>
        internal static Color ScrollTrack => new(0.015f, 0.02f, 0.025f, 0.9f);

        internal static Color ScrollHandle => new(0.16f, 0.22f, 0.27f, 1f);

        private void PaintScrollbar(bool settings)
        {
            try
            {
                var track = _scrollbar!.GetComponent<Image>();
                if (track != null)
                    track.color = settings ? ScrollTrack : new Color(1f, 1f, 1f, 0.10f);
                var sb = _scrollbar.GetComponent<Scrollbar>();
                var handle = sb != null && sb.handleRect != null ? sb.handleRect.GetComponent<Image>() : null;
                if (handle != null)
                    handle.color = settings ? ScrollHandle : new Color(1f, 1f, 1f, 0.45f);
            }
            catch { /* */ }
        }

        private float PlaceColumnText(GameObject? go, string text, float width, float y, float spacing)
        {
            if (go == null) return y;
            try
            {
                var tmp = go.GetComponent<TextMeshProUGUI>();
                if (tmp != null && tmp.text != text) tmp.text = text;
                bool any = !string.IsNullOrEmpty(text);
                go.SetActive(any);
                if (!any) return y;
                float h = Math.Max(1f, MeasureHelp(go, width, PanelLayout.MaxFixedRowHeight));
                PlaceTop(go, 0f, y, width, h);
                return y + h + spacing;
            }
            catch { return y; }
        }

        private void EnsureColumn()
        {
            if (_column != null && UnityUiRuntime.Alive(_column)) return;
            var parent = _content ?? GameObject;
            var go = new GameObject("settings-description");
            go.transform.SetParent(parent.transform, false);
            _columnBack = TryAdd<Image>(go);
            if (_columnBack != null)
            {
                _columnBack.sprite = null;
                _columnBack.color = new Color(0f, 0f, 0f, GameSettingsLook.DetailsBackdropAlpha);
                _columnBack.raycastTarget = false;
            }

            _columnHeading = ColumnLabel(go, "description-heading", ModSettingsDescriptionLayout.HeadingFontSize,
                GameSettingsLook.DetailsHeadingGrey, center: true);
            var ornamentGo = new GameObject("description-ornament");
            ornamentGo.transform.SetParent(go.transform, false);
            _columnOrnament = TryAdd<Image>(ornamentGo);
            if (_columnOrnament != null)
            {
                PaintPart(_columnOrnament, SettingsArtPart.DetailsOrnament, Color.white, Rgba(GameSettingsLook.HeaderRule));
                _columnOrnament.raycastTarget = false;
            }

            _columnBody = ColumnLabel(go, "description-body", ModSettingsDescriptionLayout.BodyFontSize, GameSettingsLook.DetailsBodyGrey, center: false);
            _columnNotes = ColumnLabel(go, "description-notes", ModSettingsDescriptionLayout.BodyFontSize * 0.85f, 0.55f, center: false);
            _column = go;
        }

        private static GameObject? ColumnLabel(GameObject parent, string name, float size, float grey, bool center)
        {
            var go = MakeLabel(parent, "", Vector2.zero, size, left: !center);
            if (go == null) return null;
            go.name = name;
            try
            {
                var tmp = go.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    tmp.color = Grey(grey);
                    // TEXTAREA1: the column shows other mods' cfg descriptions, setting values
                    // ("The default is X.") and server errors. Plain text, so a "<size=999>" in any of
                    // them stays characters instead of drawing over the page (Overflow mode).
                    tmp.richText = false;
                    tmp.enableWordWrapping = true;
                    tmp.overflowMode = TextOverflowModes.Overflow;
                    tmp.raycastTarget = false;
                    tmp.verticalAlignment = VerticalAlignmentOptions.Top;
                    tmp.horizontalAlignment = center ? HorizontalAlignmentOptions.Center : HorizontalAlignmentOptions.Left;
                }
            }
            catch { /* */ }
            return go;
        }

        /// <summary>The column's top in content units: the viewport's top while the rows scroll past, never above the first grid row nor below the last.</summary>
        private float StickyTop()
        {
            float scrolled = 0f;
            try
            {
                var rt = _content != null ? _content.GetComponent<RectTransform>() : null;
                if (rt != null) scrolled = Math.Max(0f, rt.anchoredPosition.y);
            }
            catch { /* */ }
            float lowest = Math.Max(_gridTop, _gridBottom - _columnHeight);
            return Math.Min(Math.Max(_gridTop, scrolled + 8f), lowest);
        }

        private void ArmSticky()
        {
            if (_stickyArmed) return;
            _stickyArmed = true;
            Plugin.Frame += StickyColumnTick;
        }

        /// <summary>Per frame while the column exists: one read of the content's scroll and, when it moved, one write.</summary>
        private void StickyColumnTick()
        {
            if (Released || !Alive || _column == null || !UnityUiRuntime.Alive(_column))
            {
                Plugin.Frame -= StickyColumnTick;
                _stickyArmed = false;
                return;
            }

            if (!ColumnShown || _gridTop < 0f) return;
            try
            {
                var rt = _column.GetComponent<RectTransform>();
                if (rt == null) return;
                float want = -StickyTop();
                if (Math.Abs(rt.anchoredPosition.y - want) < 0.5f) return;
                rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, want);
            }
            catch { /* */ }
        }
    }
}
#endif
