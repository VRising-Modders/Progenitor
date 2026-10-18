#if !PROGENITOR_TESTS
using System;
using ProjectM;
using ProjectM.UI;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// A display-only entry in the active build menu's native key-hint row. The caller owns
/// visibility and must call Release on unload/world exit. No game-owned entry is modified.
/// </summary>
internal static class BuildMenuCopyHint
{
    private const string ObjectName = "progenitor-build-copy-hint";
    private static GameObject? _root;
    private static TextMeshProUGUI? _key;
    private static Image? _icon;
    private static RectTransform? _parent;
    private static IntPtr _menu;
    private static string? _chord;
    private static bool _warned;
    private static ControlsVisualMapping? _mapping;

    internal static string KeyIconName => Alive(_icon) && _icon!.gameObject.activeSelf && Alive(_icon.sprite) ? _icon.sprite.name : "";

    internal static string DescribeMouseMapping(World? world)
    {
        var rows = Mapping(world)?.KBMData;
        if (rows == null) return "unavailable";
        var result = new System.Collections.Generic.List<string>();
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if ((row.ControlPath ?? "").IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0)
                result.Add(row.ControlPath + "=" + (Alive(row.InputSprite) ? row.InputSprite.name : "no sprite"));
        }
        return string.Join("; ", result);
    }

    internal static void Show(BuildMenu menu, KeyChord chord, World world)
    {
        try
        {
            if (!Alive(menu) || !menu.gameObject.activeInHierarchy)
            {
                Hide();
                return;
            }

            var row = FindRow(menu);
            var parent = row?.ContextsParent;
            var donor = Alive(parent) ? FindDonor(parent!) : null;
            if (!Alive(parent) || !Alive(donor))
            {
                Hide();
                return;
            }

            if (!Alive(_root) || !Alive(_key) || !Alive(_parent)
                || _parent!.Pointer != parent!.Pointer || _menu != menu.Pointer)
            {
                Release();
                if (!Build(parent!, donor!)) return;
                _menu = menu.Pointer;
            }

            string label = chord.DisplayText;
            if (_chord != label || (KeyChord.IsMouseKey(chord.Key) && KeyIconName.Length == 0 && Time.frameCount % 60 == 0))
            {
                var sprite = MouseIcon(world, chord.Key);
                bool useIcon = Alive(_icon) && Alive(sprite);
                if (useIcon)
                {
                    _icon!.sprite = sprite;
                    _icon.preserveAspect = true;
                    SetActive(_icon, true);
                    string modifiers = "";
                    if ((chord.Modifiers & KeyModifiers.Ctrl) != 0) modifiers += "Ctrl + ";
                    if ((chord.Modifiers & KeyModifiers.Shift) != 0) modifiers += "Shift + ";
                    if ((chord.Modifiers & KeyModifiers.Alt) != 0) modifiers += "Alt + ";
                    SetText(_key!, modifiers);
                    SetActive(_key, modifiers.Length > 0);
                }
                else
                {
                    SetActive(_icon, false);
                    SetText(_key!, label);
                }
                _chord = label;
            }

            // Game entries are pooled and can change order between mapper updates.
            if (_root!.transform.GetSiblingIndex() != parent!.childCount - 1)
                _root.transform.SetAsLastSibling();
            if (!_root.activeSelf) _root.SetActive(true);
        }
        catch (Exception e)
        {
            Hide();
            if (_warned) return;
            _warned = true;
            Plugin.Logger?.LogWarning("Build copy: could not show the key hint: " + e.Message);
        }
    }

    internal static void Hide()
    {
        try { if (Alive(_root) && _root!.activeSelf) _root.SetActive(false); }
        catch { /* the game's row may already have been destroyed */ }
    }

    internal static void Release()
    {
        Destroy(_root);
        _root = null;
        _key = null;
        _icon = null;
        _mapping = null;
        _parent = null;
        _menu = IntPtr.Zero;
        _chord = null;
        _warned = false;
    }

    private static InputContextsCollection? FindRow(BuildMenu menu)
    {
        // This serialized field is present in the client interop. Never activate it ourselves.
        var direct = menu.ControllerInputContexts;
        if (Usable(direct)) return direct;

        var local = FindRowUnder(menu.transform, menu);
        if (local != null) return local;

        // The keyboard footer can belong to the CharacterMenu hosting the build tab.
        // Stop there rather than searching the HUD/canvas and borrowing unrelated hints.
        for (var t = menu.transform.parent; Alive(t); t = t.parent)
        {
            var owner = t.GetComponent<CharacterMenu>();
            if (!Alive(owner)) continue;
            if (!Alive(owner!.BuildMenu) || owner.BuildMenu.Pointer != menu.Pointer) return null;
            return FindRowUnder(t, menu);
        }
        return null;
    }

    private static InputContextsCollection? FindRowUnder(Transform scope, BuildMenu menu)
    {
        InputContextsCollection? best = null;
        int bestCount = 0;
        foreach (var row in scope.GetComponentsInChildren<InputContextsCollection>(false))
        {
            if (!Usable(row)) continue;
            bool excluded = false;
            for (var t = row.transform; Alive(t) && t.Pointer != scope.Pointer; t = t.parent)
            {
                if (t.GetComponent<FakeTooltip>() != null || t.GetComponent<ContainerTooltip>() != null)
                {
                    excluded = true;
                    break;
                }
                var owner = t.GetComponent<HUDMenu>();
                if (Alive(owner) && owner!.Pointer != menu.Pointer && owner.TryCast<CharacterMenu>() == null)
                {
                    excluded = true;
                    break;
                }
            }
            if (excluded) continue;
            int count = 0;
            var parent = row.ContextsParent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.gameObject.activeInHierarchy && child.GetComponent<InputContextEntry>() != null) count++;
            }
            if (count <= bestCount) continue;
            best = row;
            bestCount = count;
        }
        return best;
    }

    private static bool Usable(InputContextsCollection? row) => Alive(row)
        && row!.gameObject.activeInHierarchy && Alive(row.ContextsParent)
        && row.ContextsParent.gameObject.activeInHierarchy && FindDonor(row.ContextsParent) != null;

    private static InputContextEntry? FindDonor(RectTransform parent)
    {
        InputContextEntry? fallback = null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (!child.gameObject.activeInHierarchy || child.name == ObjectName) continue;
            var entry = child.GetComponent<InputContextEntry>();
            if (!Alive(entry)) continue;
            var key = Tmp(entry!.KeybindText);
            var action = Tmp(entry.ActionText);
            if (!Alive(key) || !Alive(action)) continue;
            // Prefer the native text-key layout over a mouse-only donor.
            if (key!.gameObject.activeInHierarchy) return entry;
            fallback ??= entry;
        }
        return fallback;
    }

    private static bool Build(RectTransform parent, InputContextEntry donor)
    {
        GameObject? holder = null;
        GameObject? go = null;
        bool retained = false;
        try
        {
            // Same inactive-clone pattern as Dye/HintLabel: strip drivers before activation.
            holder = new GameObject(ObjectName + "-holder");
            holder.SetActive(false);
            go = UnityEngine.Object.Instantiate(donor.gameObject, holder.transform)?.TryCast<GameObject>();
            if (!Alive(go)) return false;
            go!.name = ObjectName;
            go.SetActive(false);
            var clone = go.GetComponent<InputContextEntry>();
            if (!Alive(clone)) return false;
            var key = Tmp(clone.KeybindText);
            var action = Tmp(clone.ActionText);
            var icon = clone.KeybindImage;
            if (!Alive(key) || !Alive(action)) return false;

            SetActive(Tmp(clone.InteractionText), false);
            SetActive(Tmp(clone.ComboText), false);
            SetActive(clone.KeybindImage, false);
            SetActive(clone.KeybindImageCombo, false);

            // Do not catch removal failures and then show a clone still driven by the game.
            foreach (var localized in go.GetComponentsInChildren<LocalizedText>(true))
                UnityEngine.Object.DestroyImmediate(localized);
            UnityEngine.Object.DestroyImmediate(clone);
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour.TryCast<Graphic>() != null || behaviour.TryCast<LayoutGroup>() != null
                    || behaviour.TryCast<ContentSizeFitter>() != null || behaviour.TryCast<LayoutElement>() != null)
                    continue;
                behaviour.enabled = false;
            }
            // Animators are Behaviours, not MonoBehaviours; a copied animation must not drive the row.
            foreach (var animator in go.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
            foreach (var animation in go.GetComponentsInChildren<UnityEngine.Animation>(true)) animation.enabled = false;
            foreach (var graphic in go.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;

            SetText(action!, "Copy building");
            SetActive(key, true);
            go.transform.SetParent(parent, false);
            _root = go;
            _key = key;
            _icon = icon;
            _parent = parent;
            retained = true;
            return true;
        }
        finally
        {
            if (!retained) Destroy(go);
            Destroy(holder);
        }
    }

    private static Sprite? MouseIcon(World world, UiKey key)
    {
        string? path = key switch
        {
            UiKey.MouseMiddle => "<Mouse>/middleButton",
            UiKey.MouseLeft => "<Mouse>/leftButton",
            UiKey.MouseRight => "<Mouse>/rightButton",
            UiKey.MouseBack => "<Mouse>/backButton",
            UiKey.MouseForward => "<Mouse>/forwardButton",
            _ => null,
        };
        if (path == null) return null;
        var mapping = Mapping(world, path);
        var rows = mapping?.KBMData;
        if (rows == null) return null;
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (string.Equals(row.ControlPath, path, StringComparison.OrdinalIgnoreCase)) return row.InputSprite;
        }
        return null;
    }

    private static bool HasIcon(ControlsVisualMapping? mapping, string? path)
    {
        if (!Alive(mapping) || mapping!.KBMData == null) return false;
        if (path == null) return true;
        var rows = mapping.KBMData;
        for (int i = 0; i < rows.Length; i++)
            if (string.Equals(rows[i].ControlPath, path, StringComparison.OrdinalIgnoreCase) && Alive(rows[i].InputSprite)) return true;
        return false;
    }

    private static ControlsVisualMapping? Mapping(World? world, string? path = null)
    {
        if (HasIcon(_mapping, path)) return _mapping;
        _mapping = world?.GetExistingSystemManaged<InputActionSystem>()?._ControlsVisualMapping;
        if (HasIcon(_mapping, path)) return _mapping;
        var assets = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<ControlsVisualMapping>());
        for (int i = 0; i < assets.Length; i++)
        {
            var candidate = assets[i].TryCast<ControlsVisualMapping>();
            if (HasIcon(candidate, path)) return _mapping = candidate;
        }
        return null;
    }

    private static void SetText(TextMeshProUGUI text, string value)
    {
        text.richText = false;
        text.text = value;
        text.gameObject.SetActive(true);
        // LocalizedText normally resizes these rects. It has been removed from our clone,
        // so update the width for the full chord instead of retaining a one-key donor width.
        float width = text.preferredWidth;
        text.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
        var layout = text.GetComponent<LayoutElement>();
        if (layout != null) layout.preferredWidth = width;
    }

    private static TextMeshProUGUI? Tmp(LocalizedText? text) => Alive(text)
        ? text!.Text ?? text.GetComponent<TextMeshProUGUI>() : null;

    private static void SetActive(Component? component, bool active)
    {
        if (Alive(component)) component!.gameObject.SetActive(active);
    }

    private static void Destroy(GameObject? go)
    {
        try
        {
            if (!Alive(go)) return;
            go!.SetActive(false);
            UnityEngine.Object.Destroy(go);
        }
        catch { /* already destroyed with its native row */ }
    }

    private static bool Alive(UnityEngine.Object? value) => value != null && value.Pointer != IntPtr.Zero;
}
#endif
