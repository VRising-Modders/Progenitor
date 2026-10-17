#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// The map share's key tip: one more entry in the game's own key-hint row at the bottom of the full-screen menu
/// (<c>FullscreenMenu.InputContexts</c>, the row that reads <i>Player Position · Pan Map · Zoom In/Out · Add/Remove
/// Marker · Open Legend · Close Map</i> on the Map tab), reading <i>Shift + [left mouse] Share Location</i> for the
/// default key, or the bound key's name for another key (<see cref="MapShareLogic.Hint"/>).
///
/// <para><b>The clone.</b> As Dye all's tip (<c>Progenitor.Dye.HintLabel</c>): a copy of one of the row's own
/// <c>InputContextEntry</c> objects (<see cref="MapShareLogic.PickDonor"/>), made under an inactive holder, with its
/// <c>InputContextEntry</c> and every <c>LocalizedText</c> destroyed and its other behaviours (bar texts, images and
/// layout) disabled, so nothing in the game drives it and the font, size, color and glyph are the game's. It sits
/// right after the marker entry (<see cref="MapShareLogic.PlaceAfter"/>). Nothing the game owns is written.</para>
///
/// <para><b>Lifetime.</b> Shown while the map is open with the key bound, hidden otherwise (the row is shared by every
/// tab of the menu). Rebuilt when the row is replaced or the key changes between a mouse glyph and text. Destroyed by
/// <see cref="Dispose"/> when Core uninstalls. <b>Not live-verified.</b></para>
/// </summary>
internal sealed class MapShareHint : IDisposable
{
    internal const string ObjectName = "progenitor-mapshare-hint";

    private GameObject? _go;
    private IntPtr _builtFor;
    /// <summary>The glyph the bound key asked for when the tip was built.</summary>
    private MapShareGlyph _builtGlyph;

    /// <summary>The row had no entry with that mouse glyph, so the tip names the button as text.</summary>
    private bool _asText;

    /// <summary>The bound key's tip the texts were last set for (<see cref="MapShareHintParts.Text"/>).</summary>
    private string _requestedText = "";

    private string _builtText = "";
    private TextMeshProUGUI? _prefix;
    private TextMeshProUGUI? _keyText;

    /// <summary>PAD1: the copy's glyph image, its second (combo) image and the text between them.</summary>
    private Image? _image;
    private Image? _comboImage;
    private TextMeshProUGUI? _comboText;
    private bool _warned;
    private bool _disposed;

    /// <summary>Whether the entry is built and showing (the dev bridge's readback).</summary>
    internal bool Shown
    {
        get
        {
            try { return !_disposed && Alive(_go) && _go!.activeInHierarchy; }
            catch { return false; }
        }
    }

    /// <summary>The tip as it reads (<see cref="MapShareHintParts.Text"/>), or empty before the first build.</summary>
    internal string Text => _builtText;

    /// <summary>Show the tip for <paramref name="chord"/> in the full-screen menu's key-hint row.</summary>
    internal void Show(KeyChord chord)
    {
        if (_disposed) return;
        try
        {
            if (!ModsWorkspaceChrome.TryGetFullscreenMenu(out var menu, out _) || menu == null) return;
            var row = menu.InputContexts;
            if (!Alive(row)) return;
            var parent = row.ContextsParent;
            if (!Alive(parent)) return;

            var parts = MapShareLogic.Hint(chord);
            if (!Alive(_go) || _builtFor != parent.Pointer || _builtGlyph != parts.Glyph)
            {
                DestroyObject(_go);
                _go = null;
                if (!Build(parent, parts)) return;
            }

            if (!string.Equals(_requestedText, parts.Text, StringComparison.Ordinal))
            {
                ApplyTexts(_asText ? AsText(parts) : parts);
                _requestedText = parts.Text;
            }
            Place(parent);
            if (!_go!.activeSelf) _go.SetActive(true);
        }
        catch (Exception e)
        {
            Warn("the key tip could not be shown: " + e.Message);
        }
    }

    /// <summary>Hide the tip (it stays built).</summary>
    internal void Hide()
    {
        if (!Alive(_go)) return;
        try
        {
            if (_go!.activeSelf) _go.SetActive(false);
        }
        catch { /* the row took it along */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyObject(_go);
        _go = null;
        _prefix = null;
        _keyText = null;
        _image = null;
        _comboImage = null;
        _comboText = null;
        _builtFor = IntPtr.Zero;
    }

    /// <summary>The row's game entries, in sibling order.</summary>
    private static List<(InputContextEntry Entry, MapHintEntry Info)> Entries(RectTransform parent)
    {
        var list = new List<(InputContextEntry, MapHintEntry)>();
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == null || child.name == ObjectName) continue;
            var entry = child.GetComponent<InputContextEntry>();
            if (entry == null) continue;
            string action = "", sprite = "";
            bool icon = false, keyText = false;
            try { action = Tmp(entry.ActionText)?.text ?? ""; }
            catch { action = ""; }
            try
            {
                var image = entry.KeybindImage;
                if (image != null)
                {
                    icon = image.gameObject.activeSelf;
                    sprite = image.sprite != null ? image.sprite.name ?? "" : "";
                }
            }
            catch { sprite = ""; }

            try
            {
                var key = Tmp(entry.KeybindText);
                keyText = key != null && key.gameObject.activeSelf && !string.IsNullOrWhiteSpace(key.text);
            }
            catch { keyText = false; }

            list.Add((entry, new MapHintEntry(child.gameObject.activeSelf, action, icon, sprite, keyText)));
        }

        return list;
    }

    /// <summary>
    /// A mouse tip written as text (<c>[Shift + Left Mouse] Share Location</c>), for a row with no such glyph; PAD1: a
    /// controller tip as its buttons' names (<c>[Pad Y + D-Pad Down]</c>) when the game's glyphs cannot be read.
    /// </summary>
    private static MapShareHintParts AsText(MapShareHintParts parts) =>
        parts.Glyph == MapShareGlyph.None ? parts
        : parts.Glyph == MapShareGlyph.Gamepad ? new MapShareHintParts("", MapShareGlyph.None, parts.KeyText, parts.Action)
        : new MapShareHintParts("", MapShareGlyph.None, (parts.Prefix.Length > 0 ? parts.Prefix + " " : "")
            + (parts.Glyph == MapShareGlyph.MouseLeft ? "Left Mouse" : "Right Mouse"), parts.Action);

    // ---- PAD1: the game's controller glyphs ---------------------------------------------------------

    /// <summary>PAD1: both glyphs a controller tip needs can be read from the game.</summary>
    private static bool GlyphsReadable(MapShareHintParts parts) =>
        GamepadGlyph(parts.GamepadKey) != null && (parts.GamepadHeld == UiKey.None || GamepadGlyph(parts.GamepadHeld) != null);

    /// <summary>
    /// PAD1: a controller tip's art, as the game draws a two-button binding (its <c>InputContextEntry</c> has an image, a
    /// combo text and a combo image): the held button's glyph, <c>+</c>, then the pressed button's; a single button is
    /// its glyph alone. Assumed (not seen live) that the entry lays the image before the combo image.
    /// </summary>
    private void ApplyGlyphs(MapShareHintParts parts)
    {
        bool two = parts.GamepadHeld != UiKey.None && _comboImage != null;
        var first = GamepadGlyph(two ? parts.GamepadHeld : parts.GamepadKey);
        if (_image != null && first != null)
        {
            _image.sprite = first;
            _image.gameObject.SetActive(true);
        }

        if (_comboImage != null)
        {
            var second = two ? GamepadGlyph(parts.GamepadKey) : null;
            if (second != null) _comboImage.sprite = second;
            _comboImage.gameObject.SetActive(second != null);
        }

        if (_comboText != null)
        {
            _comboText.text = "+";
            _comboText.gameObject.SetActive(two);
        }
    }

    private static int _controllerFrame = int.MinValue;
    private static bool _controllerActive;

    /// <summary>
    /// PAD1: whether the game is showing controller glyphs now: <c>InputActionSystem.UsingGamepad</c> (its
    /// <c>ControllerType</c> is <c>Gamepad</c>; the game switches it when the player uses the other device, and its hint
    /// rows follow). Read at most every ten frames. False when the system cannot be read. <b>Not live-verified.</b>
    /// </summary>
    internal static bool ControllerActive()
    {
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = 0; }
        if (frame - _controllerFrame < 10 && frame >= _controllerFrame) return _controllerActive;
        _controllerFrame = frame;
        try
        {
            // Without a client world the helper logs an error and returns null; the map (and its tip) needs one anyway.
            var world = Worlds.Client;
            var system = world != null && world.IsCreated ? ProjectM.InputHelper.GetInputActionSystem() : null;
            _controllerActive = system != null && system.UsingGamepad;
        }
        catch { _controllerActive = false; }
        return _controllerActive;
    }

    /// <summary>
    /// PAD1: the game's own glyph for a controller button, for the controller the player has (Xbox or PlayStation art):
    /// <c>ControlsVisualMapping.GetInputVisualsMapping(path, ControllerType.Gamepad, CurrentGamepadType, out text, out
    /// sprite)</c>, the table lookup the game's <c>GetBindingDisplayInfo</c> makes for its own hint glyphs, on the mapping
    /// the input system already loaded (<c>_ControlsVisualMapping</c>; null until the game first drew a glyph). The path is
    /// the button's <c>&lt;Gamepad&gt;/...</c> control path (<see cref="GamepadKeybindCatalog.PathOf"/>). Null when the
    /// mapping or the sprite is missing. <b>Not live-verified.</b>
    /// </summary>
    internal static Sprite? GamepadGlyph(UiKey key)
    {
        string path = GamepadKeybindCatalog.PathOf(key);
        if (path.Length == 0) return null;
        try
        {
            // Without a client world the helper logs an error and returns null; the map (and its tip) needs one anyway.
            var world = Worlds.Client;
            var system = world != null && world.IsCreated ? ProjectM.InputHelper.GetInputActionSystem() : null;
            var mapping = system?._ControlsVisualMapping;
            if (mapping == null) return null;
            mapping.GetInputVisualsMapping(path, ProjectM.ControllerType.Gamepad, system!.CurrentGamepadType, out string _, out Sprite sprite);
            return Alive(sprite) ? sprite : null;
        }
        catch { return null; }
    }

    private bool Build(RectTransform parent, MapShareHintParts requested)
    {
        var parts = requested;
        var entries = Entries(parent);
        var infos = new List<MapHintEntry>(entries.Count);
        foreach (var e in entries) infos.Add(e.Info);
        bool asText = false;
        if (parts.Glyph == MapShareGlyph.Gamepad && !GlyphsReadable(parts))
        {
            // PAD1: the game's controller art cannot be read yet: the buttons' names instead.
            parts = AsText(parts);
            asText = true;
        }

        int pick = MapShareLogic.PickDonor(infos, parts.Glyph);
        if (pick < 0 && parts.Glyph != MapShareGlyph.None)
        {
            // No entry with that mouse glyph: the key's name as text instead.
            parts = AsText(parts);
            asText = true;
            pick = MapShareLogic.PickDonor(infos, MapShareGlyph.None);
        }

        if (pick < 0)
        {
            if (entries.Count > 0) Warn("the map's key-hint row has no entry to copy; the key tip is off.");
            return false;
        }

        var donor = entries[pick].Entry;
        GameObject? holder = null;
        GameObject? go = null;
        bool done = false;
        try
        {
            holder = new GameObject(ObjectName + "-holder");
            holder.SetActive(false);
            go = UnityEngine.Object.Instantiate(donor.gameObject, holder.transform)?.TryCast<GameObject>();
            if (go == null) return false;
            go.name = ObjectName;

            var clone = go.GetComponent<InputContextEntry>();
            var interaction = Tmp(clone?.InteractionText);
            var action = Tmp(clone?.ActionText);
            var keyText = Tmp(clone?.KeybindText);
            GameObject? comboText = Tmp(clone?.ComboText)?.gameObject;
            GameObject? comboImage = clone?.KeybindImageCombo != null ? clone.KeybindImageCombo.gameObject : null;
            Image? image = clone?.KeybindImage;
            if (interaction == null || action == null || (parts.Glyph != MapShareGlyph.None ? image == null : keyText == null))
            {
                Warn("the copied key-hint entry is missing its texts or icon; the key tip is off.");
                return false;
            }

            foreach (var localized in go.GetComponentsInChildren<LocalizedText>(true))
            {
                try { UnityEngine.Object.DestroyImmediate(localized); } catch { /* */ }
            }

            if (clone != null)
            {
                try { UnityEngine.Object.DestroyImmediate(clone); } catch { /* */ }
            }

            Silence(go);

            // The prefix takes the caption's style (Dye all's tip, live 2026-10-08: the prefab's interaction word is heavier).
            interaction.font = action.font;
            if (action.fontSharedMaterial != null) interaction.fontSharedMaterial = action.fontSharedMaterial;
            interaction.fontSize = action.fontSize;
            interaction.fontStyle = action.fontStyle;
            interaction.color = action.color;
            action.gameObject.SetActive(true);
            action.text = parts.Action;
            if (comboText != null) comboText.SetActive(false);
            if (comboImage != null) comboImage.SetActive(false);
            if (parts.Glyph != MapShareGlyph.None)
            {
                image!.gameObject.SetActive(true);
                if (keyText != null) keyText.gameObject.SetActive(false);
            }
            else
            {
                // A keyboard key: the donor's key cap (when it draws its key as text) with the key's name on it; a
                // glyph-only donor's mouse art is turned off.
                keyText!.gameObject.SetActive(true);
                if (!infos[pick].KeyTextShown && image != null) image.gameObject.SetActive(false);
            }

            foreach (var g in go.GetComponentsInChildren<Graphic>(true))
            {
                try { g.raycastTarget = false; } catch { /* */ }
            }

            go.transform.SetParent(parent, false);
            go.SetActive(false);

            _go = go;
            _prefix = interaction;
            _keyText = keyText;
            _image = image;
            _comboImage = comboImage != null ? comboImage.GetComponent<Image>() : null;
            _comboText = comboText != null ? comboText.GetComponent<TextMeshProUGUI>() : null;
            _builtFor = parent.Pointer;
            _builtGlyph = requested.Glyph;
            _asText = asText;
            ApplyTexts(parts);
            _requestedText = requested.Text;
            done = true;
            Plugin.Logger?.LogInfo("Map share: key tip '" + parts.Text + "' in the map's key-hint row (copied from '"
                + infos[pick].Action + "', icon '" + infos[pick].Sprite + "').");
            return true;
        }
        finally
        {
            if (!done) DestroyObject(go);
            DestroyObject(holder);
        }
    }

    private void ApplyTexts(MapShareHintParts parts)
    {
        try
        {
            if (_prefix != null)
            {
                _prefix.text = parts.Prefix;
                _prefix.gameObject.SetActive(parts.Prefix.Length > 0);
            }

            if (_keyText != null && parts.Glyph == MapShareGlyph.None) _keyText.text = parts.KeyText;
            if (parts.Glyph == MapShareGlyph.Gamepad) ApplyGlyphs(parts);
            _builtText = parts.Text;
        }
        catch (Exception e) { Warn("the key tip's text could not be set: " + e.Message); }
    }

    /// <summary>Keep the tip right after the marker entry (pooled entries can move between refreshes).</summary>
    private void Place(RectTransform parent)
    {
        if (!Alive(_go) || _go!.transform.parent == null || _go.transform.parent.Pointer != parent.Pointer) return;
        var entries = Entries(parent);
        var infos = new List<MapHintEntry>(entries.Count);
        foreach (var e in entries) infos.Add(e.Info);
        int after = MapShareLogic.PlaceAfter(infos);
        int now = _go.transform.GetSiblingIndex();
        int target = after < 0 ? 0 : MapShareLogic.SiblingAfter(entries[after].Entry.transform.GetSiblingIndex(), now);
        if (target != now) _go.transform.SetSiblingIndex(target);
    }

    private static void Silence(GameObject go)
    {
        foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (b == null) continue;
            if (b.TryCast<Graphic>() != null || b.TryCast<LayoutGroup>() != null || b.TryCast<ContentSizeFitter>() != null
                || b.TryCast<LayoutElement>() != null)
                continue;
            try { b.enabled = false; } catch { /* */ }
        }
    }

    private static TextMeshProUGUI? Tmp(LocalizedText? localized)
    {
        if (localized == null) return null;
        try { return localized.Text ?? localized.GetComponent<TextMeshProUGUI>(); }
        catch { return null; }
    }

    private void Warn(string what)
    {
        if (_warned) return;
        _warned = true;
        Plugin.Logger?.LogWarning("Map share: " + what);
    }

    private static void DestroyObject(UnityEngine.Object? o)
    {
        if (!Alive(o)) return;
        try { UnityEngine.Object.Destroy(o); }
        catch { /* already gone */ }
    }

    private static bool Alive(UnityEngine.Object? o) => o != null && o.Pointer != IntPtr.Zero;
}
#endif
