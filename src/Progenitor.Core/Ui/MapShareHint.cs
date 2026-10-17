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

    /// <summary>A mouse tip written as text (<c>[Shift + Left Mouse] Share Location</c>), for a row with no such glyph.</summary>
    private static MapShareHintParts AsText(MapShareHintParts parts) =>
        parts.Glyph == MapShareGlyph.None ? parts
        : new MapShareHintParts("", MapShareGlyph.None, (parts.Prefix.Length > 0 ? parts.Prefix + " " : "")
            + (parts.Glyph == MapShareGlyph.MouseLeft ? "Left Mouse" : "Right Mouse"), parts.Action);

    private bool Build(RectTransform parent, MapShareHintParts requested)
    {
        var parts = requested;
        var entries = Entries(parent);
        var infos = new List<MapHintEntry>(entries.Count);
        foreach (var e in entries) infos.Add(e.Info);
        int pick = MapShareLogic.PickDonor(infos, parts.Glyph);
        bool asText = false;
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
