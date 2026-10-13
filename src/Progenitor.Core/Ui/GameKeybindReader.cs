#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Il2CppSystem;
using ProjectM;
using Stunlock.Localization;
using UnityEngine.InputSystem;
using Exception = System.Exception;
using Environment = System.Environment;
using StringComparison = System.StringComparison;

namespace Progenitor.Ui;

/// <summary>
/// KEYS1: reads the game's current keyboard bindings, read-only, for the Keybinds tab and the winner
/// rule. Client, at the title or in a world: <c>ProjectM.InputActionSystem</c> has its actions loaded at the title too
/// (live 2026-10-12); before it has loaded them the snapshot is unavailable and says so.
///
/// <para><b>What it calls</b> (source-checked against the client's metadata and native code,
/// <b>not live-verified</b>): <c>InputHelper.GetInputActionSystem</c>
/// (already used by <see cref="GameInputBlocker"/>) after checking the client world;
/// <c>_LoadedInputActions.FindAction(name)</c> per listed action; the game's own primary/secondary
/// selection <c>GetBindingIndexInAction</c> and <c>IsBindingComposite</c> with
/// <c>ControllerType.KeyboardAndMouse</c> (never a gamepad type: asking for one with no pad connected
/// makes the game add a fake XInput device); Unity's
/// <c>InputActionRebindingExtensions.GetBindingDisplayString(action, index, out layout, out path)</c>
/// for the effective control path; <c>GetBindingDisplayInfo</c> for the game's
/// own key text; <c>TryGetButtonInputActionLocalization</c> for the action's name. No
/// generic collection over a game value type is touched (<c>_ButtonInputActions</c>,
/// <c>InputAction.bindings</c> are deliberately not read), nothing is written, and every action is read
/// in its own try/catch so one failure costs one row.</para>
///
/// <para>Read only when <see cref="KeybindArbiter"/> asks: at world entry and when the Keybinds tab
/// opens (review 2026-09-29: never on a press frame, never on a timer).</para>
/// </summary>
internal static class GameKeybindReader
{
    private static bool _loggedFailure;

    internal static GameKeybindSnapshot Read() => ReadNow();

    internal static void Forget() => _loggedFailure = false;

    private static GameKeybindSnapshot ReadNow()
    {
        try
        {
            // User report 2026-10-12: the input system is loaded at the title too (live: HasLoadedInputActions true
            // with only Default World), so the keys are read there, not only in a client world.
            InputActionSystem? system = InputHelper.GetInputActionSystem();
            if (system == null || !system.HasLoadedInputActions)
                return GameKeybindSnapshot.Unavailable(KeybindArbiter.GameUnavailableNote);
            var asset = system._LoadedInputActions;
            if (asset == null) return GameKeybindSnapshot.Unavailable(KeybindArbiter.GameUnavailableNote);

            var kbm = new Nullable_Unboxed<ControllerType>(ControllerType.KeyboardAndMouse);
            var claims = new List<KeybindClaim>();
            int failed = 0;
            string firstError = "";
            foreach (var entry in GameKeybindCatalog.Listed)
            {
                try
                {
                    if (entry.IsAnalog)
                    {
                        ReadDirection(system, asset, entry, kbm, claims);
                        continue;
                    }

                    if (!System.Enum.TryParse(entry.Action, out ButtonInputAction button)) continue;
                    var action = asset.FindAction(entry.Action, false);
                    if (action == null) continue;
                    string name = ActionName(system, button, entry.Action, out string description);
                    for (int slot = 0; slot < 2; slot++)
                    {
                        bool primary = slot == 0;
                        bool exists = ReadSlot(system, action, button, primary, kbm, out string? modifierPath, out string? keyPath, out string text,
                            out bool defaultKnown, out string? defaultModifier, out string? defaultKey);
                        claims.Add(GameKeybindCatalog.Claim(entry, name, slot, modifierPath, keyPath, text, description,
                            defaultKnown, defaultModifier, defaultKey, exists));
                    }
                }
                catch (Exception e)
                {
                    failed++;
                    if (firstError.Length == 0) firstError = entry.Action + ": " + e.Message;
                }
            }

            if (failed > 0 && !_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Logger?.LogWarning(
                    "Ui: " + failed + " game key binding(s) could not be read and are not listed (" + firstError + "). Logged once.");
            }

            if (claims.Count == 0)
                return GameKeybindSnapshot.Unavailable("The game's keys could not be read" + (firstError.Length > 0 ? " (" + firstError + ")." : "."));
            return new GameKeybindSnapshot(true, claims, "");
        }
        catch (Exception e)
        {
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                Plugin.Logger?.LogWarning("Ui: the game's key bindings could not be read: " + e.Message + ". Logged once.");
            }

            return GameKeybindSnapshot.Unavailable("The game's keys could not be read (" + e.Message + ").");
        }
    }

    /// <summary>One slot of a button action; false when the game has no keyboard binding for it.</summary>
    private static bool ReadSlot(
        InputActionSystem system, InputAction action, ButtonInputAction button, bool primary, Nullable_Unboxed<ControllerType> kbm,
        out string? modifierPath, out string? keyPath, out string text,
        out bool defaultKnown, out string? defaultModifier, out string? defaultKey)
    {
        modifierPath = null;
        keyPath = null;
        text = "";
        defaultKnown = false;
        defaultModifier = null;
        defaultKey = null;
        int first = system.GetBindingIndexInAction(action, primary, false, false, kbm);
        if (first < 0) return false;
        bool composite = system.IsBindingComposite(action, primary, kbm);
        if (composite)
        {
            int second = system.GetBindingIndexInAction(action, primary, false, true, kbm);
            modifierPath = PathOf(action, first);
            keyPath = second >= 0 ? PathOf(action, second) : null;
            defaultKnown = TryDefaultPath(action, first, out defaultModifier) & (second < 0 | TryDefaultPath(action, second, out defaultKey));
        }
        else
        {
            keyPath = PathOf(action, first);
            defaultKnown = TryDefaultPath(action, first, out defaultKey);
        }

        if (keyPath == null && modifierPath == null) return true;
        try
        {
            var info = system.GetBindingDisplayInfo(button, primary, false, kbm);
            if (info != null)
            {
                string a = (info.InputText ?? "").Trim();
                string b = info.IsComposite ? (info.SecondInputText ?? "").Trim() : "";
                // The game's own order: InputText is the modifier of a composite, SecondInputText the key.
                text = b.Length > 0 ? b + KeyChord.Separator + a : a;
            }
        }
        catch { text = ""; /* the chord's own text is shown instead */ }
        return true;
    }

    /// <summary>
    /// KEYS4 (bug found live 2026-09-30: the Movement rows were missing, so W was offered as free): one
    /// movement direction, both slots. The Controls page lists each half of the analog <c>MoveX</c> /
    /// <c>MoveY</c> axes as a row (<c>RebindingMenu.AddEntry(AnalogInputAction, bool positive)</c>).
    /// The action is found by the axis's enum name, as <c>RegisterInputActions</c>
    /// registers it; the slot is the game's own selection, <c>GetBindingIndexInAction</c> with
    /// <c>isAnalog: true</c>, which counts the composite part named <c>negative</c>, or <c>positive</c>
    /// when <c>useSecondCompositePart</c> is true (the native method, its string literals
    /// resolved from the game's metadata). A part is a plain key binding, so its path reads as a button's does.
    /// The name is the page's own, from <c>TryGetAnalogInputActionLocalization(axis, positive, …)</c>
    /// (the same out-key shape as the button version). Source-checked only. KEYS4 also reads the
    /// other analog actions with a keyboard-and-mouse binding this way (the camera's MoveCameraX/Y/Z halves, and
    /// the mouse axes ZoomCamera, RotateCameraX/Y and MapZoom as one row each).
    /// </summary>
    private static void ReadDirection(
        InputActionSystem system, InputActionAsset asset, GameKeybindCatalog.Entry entry, Nullable_Unboxed<ControllerType> kbm,
        List<KeybindClaim> claims)
    {
        if (!System.Enum.TryParse(entry.Analog, out AnalogInputAction axis)) return;
        var action = asset.FindAction(entry.Analog, false);
        if (action == null) return;
        string name = DirectionName(system, axis, entry, out string description);
        for (int slot = 0; slot < 2; slot++)
        {
            // A whole axis (a mouse axis on a plain binding, KEYS4) is its first plain binding: a plain binding
            // counts whatever the part name, so the negative-part query finds it.
            int index = system.GetBindingIndexInAction(action, slot == 0, true, !entry.WholeAxis && entry.Positive, kbm);
            string? keyPath = index >= 0 ? PathOf(action, index) : null;
            string? defaultKey = null;
            bool defaultKnown = index >= 0 && TryDefaultPath(action, index, out defaultKey);
            claims.Add(GameKeybindCatalog.Claim(entry, name, slot, null, keyPath, "", description, defaultKnown, null, defaultKey, index >= 0));
        }
    }

    private static string DirectionName(InputActionSystem system, AnalogInputAction axis, GameKeybindCatalog.Entry entry, out string description)
    {
        description = "";
        try
        {
            if (system.TryGetAnalogInputActionLocalization(axis, entry.Positive, out LocalizationKey name, out LocalizationKey desc))
            {
                try
                {
                    if (Localization.Localization.TryGetGameString(desc, out string d) && !string.IsNullOrWhiteSpace(d))
                        description = d.Trim();
                }
                catch { description = ""; }
                if (Localization.Localization.TryGetGameString(name, out string text) && !string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }
        catch { /* the English name below */ }
        return GameKeybindCatalog.FallbackName(entry.Action);
    }

    internal static string? PathOf(InputAction action, int index)
    {
        if (index < 0) return null;
        InputActionRebindingExtensions.GetBindingDisplayString(action, index, out string layout, out string control);
        if (string.IsNullOrEmpty(layout) || string.IsNullOrEmpty(control)) return null;
        return "<" + layout + ">/" + control;
    }

    /// <summary>
    /// KEYS9: the slot's path as the game ships it, ignoring the player's overrides (the same call with
    /// <c>DisplayStringOptions.IgnoreBindingOverrides</c> = 8, which reads the binding's own path instead of its
    /// effective path). False when the read throws; the row then shows no default and never reads as changed.
    /// </summary>
    internal static bool TryDefaultPath(InputAction action, int index, out string? path)
    {
        path = null;
        try
        {
            InputActionRebindingExtensions.GetBindingDisplayString(action, index, out string layout, out string control,
                InputBinding.DisplayStringOptions.IgnoreBindingOverrides);
            if (!string.IsNullOrEmpty(layout) && !string.IsNullOrEmpty(control)) path = "<" + layout + ">/" + control;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The action's localized name and (KEYS3) its localized description, both from the one
    /// <c>TryGetButtonInputActionLocalization</c> call the Bindings Menu's details panel uses
    /// (<c>SettingsEntry_Binding.SetInputInfo(headerKey, desc)</c>). The description is empty when the
    /// game has none or its key does not resolve.
    /// </summary>
    private static string ActionName(InputActionSystem system, ButtonInputAction button, string fallback, out string description)
    {
        description = "";
        try
        {
            if (system.TryGetButtonInputActionLocalization(button, out LocalizationKey name, out LocalizationKey desc))
            {
                try
                {
                    if (Localization.Localization.TryGetGameString(desc, out string d) && !string.IsNullOrWhiteSpace(d))
                        description = d.Trim();
                }
                catch { description = ""; }
                if (Localization.Localization.TryGetGameString(name, out string text) && !string.IsNullOrWhiteSpace(text))
                    return text.Trim();
            }
        }
        catch { /* the enum's readable name below */ }
        return GameKeybindCatalog.FallbackName(fallback);
    }
}
#endif
