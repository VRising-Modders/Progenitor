using System;
using System.Collections.Generic;
using BepInEx.Logging;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Dye;

/// <summary>
/// Dye all's key tip: one more entry in the game's own key-hint row at the bottom of the inventory menu, next to
/// the game's <i>[left mouse] Set Color</i>, reading <i>Hold [left mouse] Set All</i> (the game's own form for a
/// hold: its console entries read <i>Hold [X] + [Q] Space</i>).
///
/// <para><b>The row.</b> The inventory sub-menu (<c>CharacterInventorySubMenu</c>, which holds the equipment tab and
/// its <c>TransmogContainer</c>) has an <c>InputContextsCollection</c> under <c>MotionRoot/InputContexts</c>; its
/// <c>ContextsParent</c> is a <c>HorizontalLayoutGroup</c> of pooled <c>InputContextEntry</c> clones the game fills
/// for the current context (<c>InteractionText</c>, <c>KeybindImage</c>, <c>KeybindText</c>, <c>ActionText</c>).
/// The collection's <c>EntryGroup</c> drives only the entries it instantiated, and the clone has no
/// <c>InputContextEntry</c>, so nothing in the game addresses it.</para>
///
/// <para><b>The clone.</b> The donor is the game's own <i>Set Color</i> entry (<see cref="DyeHint.PickDonor"/>), so
/// the font, size, color, spacing and the mouse glyph are the game's. It is instantiated under an inactive holder (so
/// none of its components wakes up), then loses everything that would let the game drive it: the
/// <c>InputContextEntry</c> and every <c>LocalizedText</c> are destroyed (a <c>LocalizedText</c> re-applies its key's
/// string), and the remaining behaviours other than the text, image and layout ones are disabled. Its texts are set
/// once: <c>InteractionText</c> <see cref="DyeHint.Interaction"/>, <c>ActionText</c> <see cref="DyeHint.Action"/>;
/// the key text and the combo parts stay off, the mouse glyph stays on. It is parented into <c>ContextsParent</c> right
/// after the donor, so the row's layout places it; nothing the game owns is written.</para>
///
/// <para><b>Lifetime.</b> Shown while the palette shows a color (<see cref="DyeHint.ShouldShow"/>), hidden otherwise.
/// A row destroyed under it takes it along; the next <see cref="Show"/> rebuilds. <see cref="Dispose"/> destroys it when
/// Core uninstalls, so it cannot outlive the Core generation that built it.</para>
/// </summary>
internal sealed class HintLabel : IDisposable
{
    internal const string ObjectName = "progenitor-dyeall-hint";

    private readonly ManualLogSource _log;
    private GameObject? _go;
    private TextMeshProUGUI? _interaction;
    private IntPtr _builtFor;
    private InputContextsCollection? _row;
    private IntPtr _rowFor;
    private bool _warned;
    private bool _disposed;

    internal HintLabel(ManualLogSource log) => _log = log;

    /// <summary>Whether the entry is built and showing (for the dev bridge's <c>dye</c> readback).</summary>
    internal bool Shown
    {
        get
        {
            try { return !_disposed && Alive(_go) && _go!.activeInHierarchy; }
            catch { return false; }
        }
    }

    /// <summary>The row the entry sits in (the <c>ContextsParent</c>'s name path tail), for the readback.</summary>
    internal string? Where { get; private set; }

    /// <summary>Show the entry in the key-hint row of <paramref name="container"/>'s inventory menu.</summary>
    internal void Show(TransmogContainer container)
    {
        if (_disposed || !Alive(container)) return;
        try
        {
            if (!Alive(_row) || _rowFor != container.Pointer)
            {
                _row = FindRow(container);
                _rowFor = container.Pointer;
            }

            var row = _row;
            if (row == null) return;
            var parent = row.ContextsParent;
            if (!Alive(parent)) return;
            if (!Alive(_go) || _builtFor != parent.Pointer)
            {
                DestroyObject(_go);
                _go = null;
                _interaction = null;
                if (!Build(parent)) return;
            }

            PlaceAfterDonor(parent);
            if (!_go!.activeSelf) _go.SetActive(true);
        }
        catch (Exception e)
        {
            if (_warned) return;
            _warned = true;
            _log.LogWarning("Dye all: the key tip could not be shown: " + e.Message);
        }
    }

    /// <summary>Hide the entry (it stays built for the next time the palette shows).</summary>
    internal void Hide()
    {
        if (!Alive(_go)) return;
        try
        {
            if (_go!.activeSelf) _go.SetActive(false);
        }
        catch { /* the row took it along */ }
    }

    /// <summary>Destroy the entry.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DestroyObject(_go);
        _go = null;
        _interaction = null;
        _builtFor = IntPtr.Zero;
        _row = null;
        _rowFor = IntPtr.Zero;
    }

    /// <summary>
    /// The inventory sub-menu's key-hint row: of the <c>InputContextsCollection</c>s under the nearest ancestor of
    /// <paramref name="container"/> that has any, the one with the most entries. <c>MotionRoot</c> holds two: the
    /// bottom row (<c>MotionRoot/InputContexts</c>) and an empty one inside the item tooltip
    /// (<c>MotionRoot/FakeTooltip/InformationEntries/InputContexts</c>), which comes first in the hierarchy.
    /// </summary>
    private static InputContextsCollection? FindRow(TransmogContainer container)
    {
        for (var t = container.transform.parent; t != null; t = t.parent)
        {
            var found = t.GetComponentsInChildren<InputContextsCollection>(true);
            if (found == null || found.Length == 0) continue;
            InputContextsCollection? best = null;
            int bestCount = -1;
            foreach (var row in found)
            {
                if (row == null) continue;
                int count = 0;
                try
                {
                    var parent = row.ContextsParent;
                    if (Alive(parent))
                        for (int i = 0; i < parent.childCount; i++)
                            if (parent.GetChild(i).GetComponent<InputContextEntry>() != null) count++;
                }
                catch { count = 0; }

                if (count > bestCount)
                {
                    best = row;
                    bestCount = count;
                }
            }

            return best;
        }

        return null;
    }

    /// <summary>The row's game entries, in sibling order, as <see cref="DyeHint.PickDonor"/> candidates.</summary>
    private static List<(InputContextEntry Entry, DyeHintDonor Info)> Entries(RectTransform parent)
    {
        var list = new List<(InputContextEntry, DyeHintDonor)>();
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == null || child.name == ObjectName) continue;
            var entry = child.GetComponent<InputContextEntry>();
            if (entry == null) continue;
            string action = "";
            string sprite = "";
            bool icon = false;
            try { action = entry.ActionText != null && entry.ActionText.Text != null ? entry.ActionText.Text.text ?? "" : ""; }
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

            list.Add((entry, new DyeHintDonor(child.gameObject.activeSelf, action, icon, sprite)));
        }

        return list;
    }

    private bool Build(RectTransform parent)
    {
        var entries = Entries(parent);
        var infos = new List<DyeHintDonor>(entries.Count);
        foreach (var e in entries) infos.Add(e.Info);
        int pick = DyeHint.PickDonor(infos);
        if (pick < 0)
        {
            if (!_warned)
            {
                _warned = true;
                _log.LogWarning("Dye all: the inventory key-hint row has no mouse entry to copy; the key tip is off.");
            }

            return false;
        }

        var donor = entries[pick].Entry;
        GameObject? holder = null;
        GameObject? go = null;
        bool done = false;
        try
        {
            // Under an inactive holder the clone's components never wake up before they are stripped.
            holder = new GameObject(ObjectName + "-holder");
            holder.SetActive(false);
            go = UnityEngine.Object.Instantiate(donor.gameObject, holder.transform)?.TryCast<GameObject>();
            if (go == null) return false;
            go.name = ObjectName;

            var clone = go.GetComponent<InputContextEntry>();
            TextMeshProUGUI? interaction = Tmp(clone?.InteractionText);
            TextMeshProUGUI? action = Tmp(clone?.ActionText);
            GameObject? keyText = Tmp(clone?.KeybindText)?.gameObject;
            GameObject? comboText = Tmp(clone?.ComboText)?.gameObject;
            GameObject? comboImage = clone?.KeybindImageCombo != null ? clone.KeybindImageCombo.gameObject : null;
            Image? image = clone?.KeybindImage;
            if (interaction == null || action == null || image == null)
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

            interaction.gameObject.SetActive(true);
            // The prefab draws the interaction word heavier than the caption (its console "Hold"); live on
            // 2026-10-08 "Hold" stood out next to "Edit", so it takes the caption's own style.
            interaction.font = action.font;
            if (action.fontSharedMaterial != null) interaction.fontSharedMaterial = action.fontSharedMaterial;
            interaction.fontSize = action.fontSize;
            interaction.fontStyle = action.fontStyle;
            interaction.color = action.color;
            interaction.text = DyeHint.Interaction;
            action.gameObject.SetActive(true);
            action.text = DyeHint.Action;
            image.gameObject.SetActive(true);
            if (keyText != null) keyText.SetActive(false);
            if (comboText != null) comboText.SetActive(false);
            if (comboImage != null) comboImage.SetActive(false);
            foreach (var g in go.GetComponentsInChildren<Graphic>(true))
            {
                try { g.raycastTarget = false; } catch { /* */ }
            }

            go.transform.SetParent(parent, false);
            go.transform.SetSiblingIndex(donor.transform.GetSiblingIndex() + 1);
            go.SetActive(false);

            _go = go;
            _interaction = interaction;
            _builtFor = parent.Pointer;
            Where = parent.parent != null ? parent.parent.name + "/" + parent.name : parent.name;
            done = true;
            _log.LogInfo("Dye all: key tip next to '" + infos[pick].Action + "' in '" + Where + "' (copied from the game's entry: font '"
                + (action.font != null ? action.font.name : "none") + "' " + action.fontSize.ToString("0.#") + ", icon '"
                + infos[pick].Sprite + "').");
            return true;
        }
        finally
        {
            if (!done) DestroyObject(go);
            DestroyObject(holder);
        }
    }

    /// <summary>Keep the entry right after the game's donor entry (pooled entries can move between refreshes).</summary>
    private void PlaceAfterDonor(RectTransform parent)
    {
        if (!Alive(_go) || _go!.transform.parent == null || _go.transform.parent.Pointer != parent.Pointer) return;
        var entries = Entries(parent);
        var infos = new List<DyeHintDonor>(entries.Count);
        foreach (var e in entries) infos.Add(e.Info);
        int pick = DyeHint.PickDonor(infos);
        if (pick < 0) return;
        int donor = entries[pick].Entry.transform.GetSiblingIndex();
        int now = _go.transform.GetSiblingIndex();
        int target = DyeHint.SiblingAfter(donor, now);
        if (target != now) _go.transform.SetSiblingIndex(target);
    }

    /// <summary>Disable every behaviour the donor brought that is not a text, an image or a layout.</summary>
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
        _log.LogWarning("Dye all: " + what);
    }

    private static void DestroyObject(UnityEngine.Object? o)
    {
        if (!Alive(o)) return;
        try { UnityEngine.Object.Destroy(o); }
        catch { /* already gone */ }
    }

    private static bool Alive(UnityEngine.Object? o) => o != null && o.Pointer != IntPtr.Zero;
}
