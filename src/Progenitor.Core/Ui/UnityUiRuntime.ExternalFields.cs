#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH1: a Core-owned text field that lives inside a <b>game</b> screen (the build menu)
/// instead of inside a Progenitor panel. It is an ordinary armed field: the same composed search
/// look, owned caret, typed-key pump, click-to-focus and menu-hotkey guard as the Mods search.
///
/// <para>What differs from a panel field is only who keeps the pump running. The shown-input pump is
/// armed while any panel is shown (<see cref="UiShownInput.ShouldArm"/>); an external field counts as
/// one more reason (<see cref="ExternalFieldCount"/>), so typing works while no Progenitor panel is
/// open. <see cref="ReleaseInputPumps"/> (the workspace closing) keeps external fields armed: they
/// belong to their own owner, which releases them with <see cref="ReleaseExternalField"/>.</para>
///
/// <para>No key is read here. Escape and Enter come from the pump, which already samples them for
/// every armed field, and reach the owner through the callbacks given at creation.</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    private sealed class ExternalField
    {
        public TMP_InputField Field = null!;
        public string GuardId = MenuHotkeyGuard.NoField;
        public Action? OnEscape;
        public Action? OnSubmit;
    }

    private static readonly List<ExternalField> _externalFields = new();

    /// <summary>Live external fields; each one keeps the shown-input pump armed.</summary>
    internal static int ExternalFieldCount
    {
        get
        {
            PruneExternalFields();
            return _externalFields.Count;
        }
    }

    /// <summary>
    /// Build a composed search field as a child of <paramref name="host"/>, stretched over it, and arm
    /// it. <paramref name="guardId"/> is the <see cref="MenuHotkeyGuard"/> id reported while it has
    /// focus. Null when the field could not be built (logged).
    /// </summary>
    internal static TMP_InputField? CreateExternalSearchField(
        GameObject host, string placeholder, string guardId, Action? onEscape, Action? onSubmit)
    {
        if (host == null) return null;
        float width = 320f;
        float height = InputHeight;
        try
        {
            var rt = host.GetComponent<RectTransform>();
            if (rt != null)
            {
                if (rt.sizeDelta.x > 1f) width = rt.sizeDelta.x;
                if (rt.sizeDelta.y > 1f) height = rt.sizeDelta.y;
            }
        }
        catch { /* the defaults stand */ }

        var columns = new RowColumns(0f, 0f, width, 0f);
        var go = BuildInput(host, columns, "", placeholder: null, searchField: true);
        if (go == null)
        {
            Plugin.Logger?.LogWarning("Ui: external search field '" + guardId + "' could not be built.");
            return null;
        }

        var field = FindIn<TMP_InputField>(go);
        if (field == null)
        {
            TryDestroyImmediate(go);
            return null;
        }

        try
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
        catch { /* keeps the build-time placement */ }

        try { field.lineType = TMP_InputField.LineType.SingleLine; } catch { /* */ }
        try { field.characterLimit = BuildSearchMatch.MaxQueryLength; } catch { /* */ }
        AttachSearchHints(go, field, "", placeholder);
        PaintSearchField(go, field);
        Activate(go);
        ArmTextField(go, field);
        BringSearchHintToFront(go);

        _externalFields.Add(new ExternalField
        {
            Field = field,
            GuardId = guardId,
            OnEscape = onEscape,
            OnSubmit = onSubmit,
        });
        SyncShownInputPump();
        return field;
    }

    /// <summary>Disarm and forget an external field. Destroying its GameObject is the caller's.</summary>
    internal static void ReleaseExternalField(TMP_InputField? field)
    {
        if (field == null) return;
        if (ReferenceEquals(_ownedFocusField, field)) BlurExternalField(field);
        for (int i = _externalFields.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_externalFields[i].Field, field)) _externalFields.RemoveAt(i);
        }

        for (int i = _armedFields.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_armedFields[i].Field, field)) continue;
            try { if (_armedFields[i].Caret != null) UnityEngine.Object.Destroy(_armedFields[i].Caret); } catch { /* */ }
            _armedFields.RemoveAt(i);
        }

        SyncShownInputPump();
    }

    /// <summary>True while <paramref name="field"/> has the pump's owned focus.</summary>
    internal static bool IsExternalFieldFocused(TMP_InputField? field)
    {
        if (field == null) return false;
        try { return ReferenceEquals(FocusedArmedField(), field); }
        catch { return false; }
    }

    /// <summary>
    /// The game's menu navigation selected the field in the event system without Progenitor giving it
    /// focus: take the selection back, so TMP does not report it focused (which would suppress every
    /// keybind as "typing"). True when a stray selection was dropped.
    /// </summary>
    internal static bool DropStraySelection(TMP_InputField? field)
    {
        if (field == null || ReferenceEquals(_ownedFocusField, field)) return false;
        try
        {
            var system = EventSystem.current;
            if (system == null || !ReferenceEquals(system.currentSelectedGameObject, field.gameObject)) return false;
            try { field.DeactivateInputField(); } catch { /* */ }
            system.SetSelectedGameObject(null);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Give the field the keyboard, exactly as a click on it does.</summary>
    internal static bool FocusExternalField(TMP_InputField? field) =>
        field != null && IsExternal(field) && FocusOwnedField(field);

    /// <summary>Take the keyboard away from the field (Escape, Enter, the menu closing).</summary>
    internal static void BlurExternalField(TMP_InputField? field)
    {
        if (field == null) return;
        try { field.DeactivateInputField(); } catch { /* */ }
        try
        {
            var system = EventSystem.current;
            if (system != null && ReferenceEquals(system.currentSelectedGameObject, field.gameObject))
                system.SetSelectedGameObject(null);
        }
        catch { /* */ }
        if (ReferenceEquals(_ownedFocusField, field)) NoteOwnedFocus(null);
        HideAllOwnedCarets();
    }

    /// <summary>Replace the field's text through the owned edit path (caret, hint and undo stay coherent).</summary>
    internal static void SetExternalFieldText(TMP_InputField? field, string text)
    {
        if (field == null) return;
        text ??= "";
        var before = ReadFieldState(field);
        if (string.Equals(before.Text, text, StringComparison.Ordinal)) return;
        WriteFieldState(field, before, new UiTextEditState(text, text.Length, text.Length));
    }

    /// <summary>Re-arm an external field that a workspace release dropped from the armed list.</summary>
    internal static void EnsureExternalArmed(TMP_InputField? field)
    {
        if (field == null || !IsExternal(field) || FindArmed(field) != null) return;
        GameObject? go = null;
        try { go = field.gameObject; } catch { go = null; }
        if (go == null) return;
        ArmTextField(go, field);
        SyncShownInputPump();
    }

    /// <summary>The framework's text font on a label an external host builds itself.</summary>
    internal static void ApplyFrameworkFont(TextMeshProUGUI? tmp)
    {
        if (tmp != null) ApplyCachedFont(tmp);
    }

    private static bool IsExternal(TMP_InputField? field) => FindExternal(field) != null;

    private static ExternalField? FindExternal(TMP_InputField? field)
    {
        if (field == null) return null;
        for (int i = 0; i < _externalFields.Count; i++)
        {
            if (ReferenceEquals(_externalFields[i].Field, field)) return _externalFields[i];
        }

        return null;
    }

    private static void PruneExternalFields()
    {
        for (int i = _externalFields.Count - 1; i >= 0; i--)
        {
            bool dead;
            try { dead = _externalFields[i].Field == null || _externalFields[i].Field.gameObject == null; }
            catch { dead = true; }
            if (dead) _externalFields.RemoveAt(i);
        }
    }

    /// <summary>The pump saw Escape: an external field with focus hands it to its owner.</summary>
    private static void NotifyExternalEscape(TMP_InputField? focused)
    {
        var external = FindExternal(focused);
        if (external?.OnEscape == null) return;
        try { external.OnEscape(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: external field escape threw: " + e.Message); }
    }

    /// <summary>The pump saw Enter: an external field with focus hands it to its owner.</summary>
    private static void NotifyExternalSubmit(TMP_InputField? focused)
    {
        var external = FindExternal(focused);
        if (external?.OnSubmit == null) return;
        try { external.OnSubmit(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: external field submit threw: " + e.Message); }
    }

    /// <summary>The guard id for an external field, or null when <paramref name="field"/> is not one.</summary>
    private static string? ExternalGuardId(TMP_InputField? field) => FindExternal(field)?.GuardId;
}
#endif
