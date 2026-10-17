using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// PAD1: the game-free half of reading V Rising's own gamepad bindings, for the gamepad picture
/// (<see cref="KeybindArbiter.CurrentGamepad"/>). Which <c>ProjectM.ButtonInputAction</c>s have a gamepad binding, the
/// screen each works in (the same modes as <see cref="GameKeybindCatalog"/>, so the scope rules apply to controller
/// buttons as they do to keys), how a binding's control paths (<c>&lt;Gamepad&gt;/buttonNorth</c>, a composite's
/// <c>&lt;Gamepad&gt;/leftTrigger</c> + <c>&lt;Gamepad&gt;/dpad/left</c>) become a gamepad <see cref="KeyChord"/>, and the
/// bindings the game ships (<see cref="Defaults"/>), used when no gamepad is connected.
///
/// <para><b>Where the defaults come from.</b> The game's input asset (<c>Settings/DefaultControls</c>), every button
/// action registered by <c>ButtonInputAction</c> name with a binding in a <c>Gamepad</c> control scheme. Both camera
/// schemes (<c>GamepadStickCamera</c>, <c>GamepadShoulderCamera</c>) are listed, since the player's choice cannot be
/// known offline: a button either scheme uses counts as used. Bindings in the <c>Disabled</c> scheme, the PlayStation
/// touchpad and the analog values (sticks, zoom) are not buttons a chord can use and are left out. A
/// <c>ButtonWithTwoModifiers</c> composite (the admin auth toggle) is not a chord this API can express and is left
/// out too.</para>
/// </summary>
internal static class GamepadKeybindCatalog
{
    /// <summary>One shipped gamepad binding: the action, a composite's held button (or empty) and the pressed button.</summary>
    internal readonly struct Binding
    {
        internal Binding(string action, string modifierControl, string control, string scheme)
        {
            Action = action;
            ModifierControl = modifierControl ?? "";
            Control = control ?? "";
            Scheme = scheme ?? "";
        }

        internal string Action { get; }

        /// <summary>The composite's <c>modifier</c> part (the button held), as a Gamepad control name; empty for a plain binding.</summary>
        internal string ModifierControl { get; }

        /// <summary>The button pressed (a plain binding, or the composite's <c>binding</c> part).</summary>
        internal string Control { get; }

        /// <summary>The binding's control-scheme groups as the asset writes them.</summary>
        internal string Scheme { get; }
    }

    /// <summary>
    /// The game's shipped gamepad bindings, in the asset's order. Generated from the asset's own binding list; see the
    /// class notes for what is left out.
    /// </summary>
    internal static readonly IReadOnlyList<Binding> Defaults = new Binding[]
    {
        new("Interact", "", "buttonSouth", "Gamepad"),
        new("CliffJump", "", "buttonSouth", "Gamepad"),
        new("Interrupt", "", "buttonSouth", "Gamepad"),
        new("Respawn", "", "buttonSouth", "Gamepad"),
        new("AutoMove", "leftTrigger", "dpad/left", "GamepadStickCamera"),
        new("AutoMove", "buttonNorth", "dpad/left", "Gamepad;GamepadShoulderCamera"),
        new("CancelRevive", "", "buttonWest", "Gamepad"),
        new("TrackItem", "", "buttonSouth", "Gamepad"),
        new("MapMarker", "", "buttonSouth", "Gamepad"),
        new("MapMarker", "", "rightTrigger", "Gamepad"),
        new("CharacterMenu", "", "start", "Gamepad"),
        new("BuildMenu", "", "buttonNorth", "GamepadStickCamera"),
        new("BuildMenu", "buttonWest", "dpad/left", "GamepadShoulderCamera"),
        new("MapMenu", "", "select", "Gamepad"),
        new("ToggleActionWheel", "", "buttonWest", "Gamepad"),
        new("JournalClaim", "", "rightStickPress", "Gamepad"),
        new("AbilityDetails", "buttonWest", "dpad/right", "Gamepad"),
        new("MapCenterPlayer", "", "leftTrigger", "Gamepad"),
        new("MapToggleLegend", "", "buttonWest", "Gamepad"),
        new("ArenaZonePainting", "", "rightStickPress", "Gamepad"),
        new("RotateCamera", "", "buttonNorth", "GamepadShoulderCamera"),
        new("RotateCamera", "", "leftTrigger", "GamepadStickCamera"),
        new("ResetCamera", "", "buttonNorth", "GamepadShoulderCamera"),
        new("CameraQuickTurnLeft", "", "leftShoulder", "GamepadShoulderCamera"),
        new("CameraStepPitchUp", "leftTrigger", "dpad/up", "GamepadStickCamera"),
        new("CameraStepPitchUp", "buttonNorth", "dpad/up", "GamepadShoulderCamera"),
        new("CameraStepPitchDown", "leftTrigger", "dpad/down", "GamepadStickCamera"),
        new("CameraStepPitchDown", "buttonNorth", "dpad/down", "GamepadShoulderCamera"),
        new("CameraCharacterFocusMode", "leftTrigger", "dpad/down", "GamepadStickCamera"),
        new("CameraCharacterFocusMode", "buttonNorth", "dpad/down", "GamepadShoulderCamera"),
        new("CameraOverviewFocusMode", "leftTrigger", "dpad/up", "GamepadStickCamera"),
        new("CameraOverviewFocusMode", "buttonNorth", "dpad/up", "GamepadShoulderCamera"),
        new("CameraReleaseFocusMode", "", "leftShoulder", "Gamepad"),
        new("CameraReleaseFocusMode", "", "rightShoulder", "Gamepad"),
        new("CameraReleaseFocusMode", "", "leftTrigger", "GamepadShoulderCamera"),
        new("CameraReleaseFocusMode", "", "rightTrigger", "Gamepad"),
        new("CameraQuickTurnRight", "", "rightShoulder", "GamepadShoulderCamera"),
        new("Primary", "", "rightTrigger", "Gamepad"),
        new("TravelSkill", "", "buttonEast", "Gamepad"),
        new("BlockCombatState", "", "buttonNorth", "Gamepad"),
        new("WeaponSkill1", "", "leftTrigger", "GamepadShoulderCamera"),
        new("WeaponSkill1", "leftTrigger", "rightShoulder", "GamepadStickCamera"),
        new("WeaponSkill2", "rightTrigger", "leftShoulder", "GamepadStickCamera"),
        new("WeaponSkill2", "leftTrigger", "rightTrigger", "GamepadShoulderCamera"),
        new("OffensiveSpell", "", "leftShoulder", "Gamepad"),
        new("DefensiveSpell", "", "rightShoulder", "Gamepad"),
        new("UltimateSpell", "leftShoulder", "rightShoulder", "GamepadStickCamera"),
        new("UltimateSpell", "leftShoulder", "rightShoulder", "GamepadShoulderCamera"),
        new("TransferItem", "", "buttonSouth", "Gamepad"),
        new("RepairItem", "", "buttonWest", "Gamepad"),
        new("ActionBar1", "", "dpad/up", "Gamepad"),
        new("ActionBar2", "", "dpad/right", "Gamepad"),
        new("ActionBar3", "", "dpad/down", "Gamepad"),
        new("ActionBar4", "", "dpad/left", "Gamepad"),
        new("ActionBar5", "", "dpad/up", "Gamepad"),
        new("ActionBar6", "", "dpad/right", "Gamepad"),
        new("ActionBar7", "", "dpad/down", "Gamepad"),
        new("ActionBar8", "", "dpad/left", "Gamepad"),
        new("CancelCraft", "", "buttonWest", "Gamepad"),
        new("SplitItem", "", "buttonWest", "Gamepad"),
        new("DropItem", "", "buttonWest", "Gamepad"),
        new("SmartTakeItems", "", "leftStickPress", "Gamepad"),
        new("AutoTransferItems", "", "rightStickPress", "Gamepad"),
        new("MoveItem", "", "buttonNorth", "Gamepad"),
        new("UseItem", "", "buttonSouth", "Gamepad"),
        new("EquipItem", "", "buttonSouth", "Gamepad"),
        new("EditItem", "", "rightStickPress", "Gamepad"),
        new("StartCraft", "", "buttonSouth", "Gamepad"),
        new("TrackRecipe", "", "buttonNorth", "Gamepad"),
        new("RemoveBuff", "", "buttonWest", "Gamepad"),
        new("CustomSplitItem", "", "buttonNorth", "Gamepad"),
        new("QuickRouteItems", "", "rightStickPress", "Gamepad"),
        new("ToggleAutoRouteItems", "", "leftStickPress", "Gamepad"),
        new("Building_Cancel", "", "buttonEast", "Gamepad"),
        new("Building_RotateRight", "", "rightShoulder", "Gamepad"),
        new("Building_Place", "", "rightTrigger", "Gamepad"),
        new("Building_Deselect", "", "buttonEast", "Gamepad"),
        new("Building_Dismantle", "", "leftTrigger", "GamepadShoulderCamera"),
        new("Building_Dismantle", "", "buttonNorth", "GamepadStickCamera"),
        new("Building_Repair", "", "buttonWest", "Gamepad"),
        new("Building_ToggleColorWheel", "", "buttonWest", "Gamepad"),
        new("Building_RotateLeft", "", "leftShoulder", "Gamepad"),
        new("Building_Exit", "", "buttonEast", "Gamepad"),
        new("Building_Pickup", "", "rightTrigger", "Gamepad"),
        new("Building_Fill", "", "rightShoulder", "Gamepad"),
        new("Building_ToggleCursorMode", "", "rightStickPress", "Gamepad"),
        new("MapTeleport", "leftStickPress", "rightStickPress", "Gamepad"),
        new("Menu_Up", "", "dpad/up", "Gamepad"),
        new("Menu_Down", "", "dpad/down", "Gamepad"),
        new("Menu_Left", "", "dpad/left", "Gamepad"),
        new("Menu_Right", "", "dpad/right", "Gamepad"),
        new("Menu_Select", "", "buttonSouth", "Gamepad"),
        new("Menu_Select_Hold", "", "buttonSouth", "Gamepad"),
        new("Menu_Close", "", "buttonEast", "Gamepad"),
        new("Menu_North", "", "buttonNorth", "Gamepad"),
        new("Menu_North_Hold", "", "buttonNorth", "Gamepad"),
        new("Menu_West", "", "buttonWest", "Gamepad"),
        new("Menu_West_Hold", "", "buttonWest", "Gamepad"),
        new("Menu_TabLeft", "", "leftShoulder", "Gamepad"),
        new("Menu_TabRight", "", "rightShoulder", "Gamepad"),
        new("Menu_TabLeftMinor", "", "leftTrigger", "Gamepad"),
        new("Menu_TabRightMinor", "", "rightTrigger", "Gamepad"),
        new("Menu_LStick_Button", "", "leftStickPress", "Gamepad"),
        new("Menu_RStick_Button", "", "rightStickPress", "Gamepad"),
        new("Menu_Close_Hold", "", "buttonEast", "Gamepad"),
        new("Menu_SelectStarted", "", "buttonSouth", "Gamepad"),
        new("DebugMenu", "rightStickPress", "dpad/up", "Gamepad"),
        new("DebugModifier", "", "rightStickPress", "Gamepad"),
    };

    /// <summary>The actions with a shipped gamepad binding, each once, in the asset's order (what the live reader reads).</summary>
    internal static IReadOnlyList<string> Actions => _actions ??= BuildActions();

    private static IReadOnlyList<string>? _actions;

    private static IReadOnlyList<string> BuildActions()
    {
        var list = new List<string>();
        foreach (var b in Defaults)
            if (!list.Contains(b.Action)) list.Add(b.Action);
        return list;
    }

    /// <summary>The game's inventory and crafting screen actions (<see cref="GameKeybindCatalog.InInventory"/>).</summary>
    private static readonly HashSet<string> InventoryActions = new(StringComparer.Ordinal)
    {
        "TrackItem", "MoveItem", "EditItem", "TransferItem", "UseItem", "EquipItem", "RemoveBuff", "QuickRouteItems",
        "StartCraft", "CancelCraft", "TrackRecipe", "ToggleAutoRouteItems",
    };

    /// <summary>
    /// The screen an action works in, as <see cref="GameKeybindCatalog"/> words it: the keyboard catalog's own mode where
    /// it lists the action, <c>in the map</c> for the map actions and the admin map teleport, <c>in menus</c> for the
    /// menu-navigation actions (the full-screen menu, the map included), the inventory screens, building, and empty (an
    /// always-active gameplay button) otherwise.
    /// </summary>
    internal static string ModeOf(string action)
    {
        string a = action ?? "";
        foreach (var e in GameKeybindCatalog.Listed)
            if (string.Equals(e.Action, a, StringComparison.Ordinal) && e.Mode.Length > 0) return e.Mode;
        if (a is "MapMarker" or "MapCenterPlayer" or "MapToggleLegend" or "MapTeleport" or "MapPan") return GameKeybindCatalog.InTheMap;
        if (a.StartsWith("Menu_", StringComparison.Ordinal)) return GameKeybindCatalog.InMenus;
        if (InventoryActions.Contains(a)) return GameKeybindCatalog.InInventory;
        if (a.StartsWith("Building_", StringComparison.Ordinal)) return GameKeybindCatalog.WhileBuilding;
        return "";
    }

    /// <summary>The debug actions: listed, never compared (the game's debug menu is off for players).</summary>
    internal static bool NeverConflicts(string action) => (action ?? "").StartsWith("Debug", StringComparison.Ordinal);

    /// <summary>
    /// A gamepad control path (<c>&lt;Gamepad&gt;/buttonNorth</c>, <c>&lt;XInputController&gt;/select</c>,
    /// <c>dpad/up</c>) or bare control name as a gamepad <see cref="UiKey"/>. False for a keyboard or mouse path, a stick
    /// or trigger axis, the touchpad, or anything unknown.
    /// </summary>
    internal static bool TryButton(string? path, out UiKey key)
    {
        key = UiKey.None;
        string p = (path ?? "").Trim();
        if (p.Length == 0) return false;
        if (p.StartsWith("<", StringComparison.Ordinal))
        {
            int close = p.IndexOf(">/", StringComparison.Ordinal);
            if (close < 0) return false;
            string layout = p.Substring(1, close - 1);
            if (layout.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) || layout.Equals("Mouse", StringComparison.OrdinalIgnoreCase)
                || layout.Equals("Pointer", StringComparison.OrdinalIgnoreCase) || layout.Equals("Touchscreen", StringComparison.OrdinalIgnoreCase))
                return false;
            p = p.Substring(close + 2);
        }

        key = p.ToLowerInvariant() switch
        {
            "buttonsouth" => UiKey.GamepadSouth,
            "buttoneast" => UiKey.GamepadEast,
            "buttonwest" => UiKey.GamepadWest,
            "buttonnorth" => UiKey.GamepadNorth,
            "leftshoulder" => UiKey.GamepadLeftShoulder,
            "rightshoulder" => UiKey.GamepadRightShoulder,
            "lefttrigger" or "lefttriggerbutton" => UiKey.GamepadLeftTrigger,
            "righttrigger" or "righttriggerbutton" => UiKey.GamepadRightTrigger,
            "dpad/up" => UiKey.GamepadDpadUp,
            "dpad/down" => UiKey.GamepadDpadDown,
            "dpad/left" => UiKey.GamepadDpadLeft,
            "dpad/right" => UiKey.GamepadDpadRight,
            "leftstickpress" or "leftstickbutton" => UiKey.GamepadLeftStick,
            "rightstickpress" or "rightstickbutton" => UiKey.GamepadRightStick,
            "start" => UiKey.GamepadStart,
            "select" => UiKey.GamepadSelect,
            _ => UiKey.None,
        };
        return key != UiKey.None;
    }

    /// <summary>The control path of a gamepad button (<c>&lt;Gamepad&gt;/buttonNorth</c>), what the game's glyph table is keyed by; empty for any other key.</summary>
    internal static string PathOf(UiKey key) => key switch
    {
        UiKey.GamepadSouth => "<Gamepad>/buttonSouth",
        UiKey.GamepadEast => "<Gamepad>/buttonEast",
        UiKey.GamepadWest => "<Gamepad>/buttonWest",
        UiKey.GamepadNorth => "<Gamepad>/buttonNorth",
        UiKey.GamepadLeftShoulder => "<Gamepad>/leftShoulder",
        UiKey.GamepadRightShoulder => "<Gamepad>/rightShoulder",
        UiKey.GamepadLeftTrigger => "<Gamepad>/leftTrigger",
        UiKey.GamepadRightTrigger => "<Gamepad>/rightTrigger",
        UiKey.GamepadDpadUp => "<Gamepad>/dpad/up",
        UiKey.GamepadDpadDown => "<Gamepad>/dpad/down",
        UiKey.GamepadDpadLeft => "<Gamepad>/dpad/left",
        UiKey.GamepadDpadRight => "<Gamepad>/dpad/right",
        UiKey.GamepadLeftStick => "<Gamepad>/leftStickPress",
        UiKey.GamepadRightStick => "<Gamepad>/rightStickPress",
        UiKey.GamepadStart => "<Gamepad>/start",
        UiKey.GamepadSelect => "<Gamepad>/select",
        _ => "",
    };

    /// <summary>A binding's paths as a gamepad chord: the pressed button and, for a composite, the held one. False when either is not a button.</summary>
    internal static bool TryChord(string? modifierPath, string? keyPath, out KeyChord chord)
    {
        chord = KeyChord.None;
        if (!TryButton(keyPath, out var key)) return false;
        var held = UiKey.None;
        if (!string.IsNullOrWhiteSpace(modifierPath) && !TryButton(modifierPath, out held)) return false;
        chord = KeyChord.Gamepad(key, held);
        return chord.IsBound;
    }

    /// <summary>
    /// A game claim for one gamepad binding of one action (slot 0, 1, ... in the order read). Not comparable when the
    /// paths are not buttons or the action is a debug one.
    /// </summary>
    internal static KeybindClaim Claim(string action, string displayName, int slot, string? modifierPath, string? keyPath, string help = "")
    {
        bool chorded = TryChord(modifierPath, keyPath, out var chord);
        bool comparable = chorded && !NeverConflicts(action);
        string name = string.IsNullOrWhiteSpace(displayName) ? GameKeybindCatalog.FallbackName(action) : displayName.Trim();
        return new KeybindClaim(
            KeybindSource.Game, KeybindArbiter.GameOwnerId, action, name, comparable ? chord : KeyChord.None,
            chordText: chorded ? chord.DisplayText : "None", help: help ?? "", comparable: comparable, slot: slot,
            category: GameKeybindCatalog.Other, mode: ModeOf(action));
    }

    /// <summary>The game's shipped gamepad bindings as a snapshot (no gamepad connected, or a game-free host).</summary>
    internal static GameKeybindSnapshot DefaultSnapshot()
    {
        var claims = new List<KeybindClaim>(Defaults.Count);
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var b in Defaults)
        {
            slots.TryGetValue(b.Action, out int slot);
            slots[b.Action] = slot + 1;
            claims.Add(Claim(b.Action, "", slot,
                b.ModifierControl.Length > 0 ? "<Gamepad>/" + b.ModifierControl : null, "<Gamepad>/" + b.Control));
        }

        return new GameKeybindSnapshot(true, claims, "");
    }
}
