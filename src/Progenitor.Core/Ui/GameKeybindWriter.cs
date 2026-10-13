#if !PROGENITOR_TESTS
using System;
using Il2CppSystem;
using ProjectM;
using UnityEngine.InputSystem;
using Exception = System.Exception;
using StringComparison = System.StringComparison;

namespace Progenitor.Ui;

/// <summary>
/// KEYS9: changes one of the game's own key slots, client, at the title or in a world. The Unity half of
/// <see cref="GameKeyRemap"/>.
///
/// <para><b>The route is the game's own.</b> The game's Bindings Menu clears a key through
/// <c>InputActionSystem.ClearInputActionBinding</c>, which finds the slot with
/// <c>GetBindingIndexInAction</c>, disables the action, calls Unity's
/// <c>InputActionRebindingExtensions.ApplyBindingOverride(action, index, path)</c> (an empty path for a
/// clear), enables the action again and calls <c>InputActionSystem.SaveSettings</c>. <c>SaveSettings</c>
/// stores <c>SaveBindingOverridesAsJson</c> of the loaded actions in the game's input settings
/// (<c>InputSettings.InputActionOverridesAsJson</c>), saves them to the game's own settings file, bumps the
/// system's binding version and clears its key-text cache, so the HUD's key hints and the game's own
/// Controls page show the new key. This writer does exactly that, with the new key's path instead of the
/// empty one, and <c>RemoveBindingOverride(action, index)</c> to go back to the default. Every argument is a
/// reference or a primitive; no generic over a game value type is touched.</para>
///
/// <para><b>Movement.</b> Move Up / Left / Down / Right are the <c>positive</c> and <c>negative</c> parts of
/// the 1D-axis composites <c>MoveY</c> / <c>MoveX</c>. <c>GetBindingIndexInAction(action, primary,
/// isAnalog: true, positive)</c> returns the part's own binding index (the game's interactive rebinding
/// uses the same index), and an override on a part binding changes only that part.</para>
///
/// <para>Verified live 2026-10-10 on the Steam client (docs/mods-menu.md#keys9-live-results-2026-10-10): the override
/// moved the character on the new key and not the old one, and the game's settings file held it. Persistence across a
/// game restart was not checked.</para>
/// </summary>
internal static class GameKeybindWriter
{
    internal static GameKeyWriteResult Write(GameKeyWrite w)
    {
        // User report 2026-10-12: no client world is needed. At the title the game's input system is already loaded
        // (live: HasLoadedInputActions true in Default World), and the game's own Controls page rebinds there.
        InputActionSystem? system = InputHelper.GetInputActionSystem();
        if (system == null || !system.HasLoadedInputActions) return new GameKeyWriteResult(false, GameKeyRemap.OutsideWorldReason);
        var asset = system._LoadedInputActions;
        if (asset == null) return new GameKeyWriteResult(false, GameKeyRemap.OutsideWorldReason);

        var entry = w.Entry;
        bool analog = entry.IsAnalog;
        var action = asset.FindAction(analog ? entry.Analog : entry.Action, false);
        if (action == null) return new GameKeyWriteResult(false, "The game has no action " + (analog ? entry.Analog : entry.Action) + ".");

        var kbm = new Nullable_Unboxed<ControllerType>(ControllerType.KeyboardAndMouse);
        bool primary = w.Slot == 0;
        if (!analog && system.IsBindingComposite(action, primary, kbm))
            return new GameKeyWriteResult(false, GameKeyRemap.OnlyInGameMenuReason);
        int index = system.GetBindingIndexInAction(action, primary, analog, analog && entry.Positive, kbm);
        if (index < 0) return new GameKeyWriteResult(false, GameKeyRemap.NoSlotReason);

        string before = GameKeybindReader.PathOf(action, index) ?? "";
        // The game's own default chosen again: drop the override instead of storing one equal to the default (found
        // live 2026-10-10: Move Up set back to W left an override, so the row read unchanged and Reset had nothing to do).
        bool toDefault = !w.Reset && w.Path!.Length > 0
            && GameKeybindReader.TryDefaultPath(action, index, out string? shipped)
            && GameKeybindCatalog.SamePath(shipped, w.Path);
        bool wasEnabled = action.enabled;
        try
        {
            if (wasEnabled) action.Disable();
            if (w.Reset || toDefault) InputActionRebindingExtensions.RemoveBindingOverride(action, index);
            else InputActionRebindingExtensions.ApplyBindingOverride(action, index, w.Path!);
        }
        finally
        {
            if (wasEnabled && !action.enabled) action.Enable();
        }

        // The game's own save: overrides JSON into its input settings, the settings file, the version bump and
        // the key-text cache. The override is already applied; a failed save is reported as such, never as "not changed".
        string? saveError = null;
        try { system.SaveSettings(); }
        catch (Exception e) { saveError = e.Message; }

        string? after = GameKeybindReader.PathOf(action, index);
        // What the slot should read now: the game's default after a reset (or the default chosen again), else the new path.
        string? expected;
        if (w.Reset || toDefault)
            expected = GameKeybindReader.TryDefaultPath(action, index, out string? shippedPath) ? shippedPath : after;
        else expected = w.Path!.Length == 0 ? null : w.Path;
        bool ok = GameKeybindCatalog.SamePath(after, expected);
        Plugin.Logger?.LogInfo("Ui: game key " + w + " index=" + index + " before=" + (before.Length == 0 ? "none" : before)
            + " after=" + (after ?? "none") + (ok ? saveError == null ? " saved" : " APPLIED-NOT-SAVED (" + saveError + ")" : " MISMATCH"));
        if (!ok) return new GameKeyWriteResult(false, "The game did not take the key (it reads " + (after ?? "no key") + ").", after);
        if (saveError != null) return new GameKeyWriteResult(true, GameKeyRemap.NotSavedStatus(saveError), after, saved: false);
        return new GameKeyWriteResult(true, w.Reset ? GameKeyRemap.ResetStatus : GameKeyRemap.SavedStatus, after);
    }
}
#endif
