using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>
/// KEYS1: the game-free half of reading V Rising's own keys. Which <c>ProjectM.ButtonInputAction</c>s
/// the game's Controls page lists, in its groups (native <c>RebindingMenu.Start</c>), and how a binding's Input System control paths
/// (<c>&lt;Keyboard&gt;/f</c>, a composite's <c>&lt;Keyboard&gt;/leftCtrl</c> + <c>&lt;Keyboard&gt;/f</c>)
/// become a <see cref="KeyChord"/>. KEYS4 (live sweep 2026-09-30 of the game's input asset):
/// besides the page's rows, every other
/// action the game registers (<c>ButtonInputAction</c> / <c>AnalogInputAction</c>) with a keyboard-and-mouse
/// binding is listed under <see cref="Other"/> with the screen it works in, and the debug actions under
/// <see cref="Debug"/>, which never conflict. Gamepad-only and menu-navigation actions (no keyboard binding)
/// are not listed.
/// </summary>
internal static class GameKeybindCatalog
{
    /// <summary>
    /// One listed action: its id, the Controls page group and, for a movement direction, the
    /// <c>ProjectM.AnalogInputAction</c> axis and which half of it (KEYS4). A button action's id is its
    /// <c>ButtonInputAction</c> name; a direction's id is its own (<c>MoveUp</c>), because the page lists
    /// one row per half of the <c>MoveX</c> / <c>MoveY</c> axes.
    /// </summary>
    internal readonly struct Entry
    {
        internal Entry(
            string action, string category, string analog = "", bool positive = false, string fallbackName = "",
            string mode = "", bool neverConflicts = false, bool wholeAxis = false)
        {
            Action = action;
            Category = category;
            Analog = analog ?? "";
            Positive = positive;
            FallbackName = fallbackName ?? "";
            Mode = mode ?? "";
            NeverConflicts = neverConflicts;
            WholeAxis = wholeAxis;
        }

        /// <summary>
        /// KEYS4: the screen the action works in (<c>in the map</c>), for an action the Controls page does
        /// not list; empty: from <see cref="ModeOf"/>. A mode-only key warns and never stops a mod key.
        /// </summary>
        internal string Mode { get; }

        /// <summary>KEYS4: listed but never compared (the debug keys; the game's debug menu is off for players).</summary>
        internal bool NeverConflicts { get; }

        /// <summary>KEYS4: an analog action read as one row (a mouse axis such as the scroll wheel), not two halves.</summary>
        internal bool WholeAxis { get; }

        internal string Action { get; }
        internal string Category { get; }

        /// <summary>The <c>AnalogInputAction</c> name (<c>MoveX</c>, <c>MoveY</c>), or empty for a button action.</summary>
        internal string Analog { get; }

        /// <summary>A direction's half of its axis: the composite's <c>positive</c> part, else <c>negative</c>.</summary>
        internal bool Positive { get; }

        /// <summary>The English name when the game's localized one cannot be read; empty: from the id.</summary>
        internal string FallbackName { get; }

        internal bool IsAnalog => Analog.Length > 0;
    }

    /// <summary>
    /// KEYS4 (bug found live 2026-09-30: W was offered as a free key). The Movement group's four
    /// directions, in the page's order. Native <c>RebindingMenu.Start</c>, keyboard
    /// branch: <c>AddEntry(MoveY, positive: true)</c>, <c>(MoveX, false)</c>, <c>(MoveY, false)</c>,
    /// <c>(MoveX, true)</c>, then AutoMove and Walk. The names are the game's English words
    /// (<c>English.json</c>: Move Up, Move Left, Move Down, Move Right); the reader shows the localized
    /// ones from <c>TryGetAnalogInputActionLocalization(axis, positive, ...)</c>.
    /// </summary>
    internal static readonly IReadOnlyList<Entry> MovementDirections = new[]
    {
        new Entry("MoveUp", Movement, "MoveY", positive: true, fallbackName: "Move Up"),
        new Entry("MoveLeft", Movement, "MoveX", positive: false, fallbackName: "Move Left"),
        new Entry("MoveDown", Movement, "MoveY", positive: false, fallbackName: "Move Down"),
        new Entry("MoveRight", Movement, "MoveX", positive: true, fallbackName: "Move Right"),
    };

    internal const string Movement = "Movement";
    internal const string Skills = "Skills & Attacks";
    internal const string General = "General";
    internal const string Camera = "Camera";
    internal const string Panels = "Panels";
    internal const string ActionBar = "Action Bar";
    internal const string Building = "Building";

    /// <summary>
    /// KEYS4: the game's keys its Controls page does not list (the map, the inventory and crafting screens,
    /// menus, chat, camera modes, mouse axes). Our label; the game has none.
    /// </summary>
    internal const string Other = "Other game keys";

    /// <summary>KEYS4: the game's debug actions. Listed for completeness, never compared: the debug menu is off for players.</summary>
    internal const string Debug = "Debug keys";

    /// <summary>
    /// KEYS3: the Bindings Menu's categories in the page's order (native <c>RebindingMenu.Start</c>).
    /// These English words are the game's own labels (<c>StreamingAssets/Localization/English.json</c>);
    /// the localized titles come from the menu's <c>LK_Category_*</c> keys when a
    /// <c>RebindingMenu</c> can be reached (<see cref="GameKeybindCategories"/>). KEYS4 adds
    /// <see cref="Other"/> and <see cref="Debug"/> after them (our labels).
    /// </summary>
    internal static readonly IReadOnlyList<string> CategoryOrder = new[] { Movement, Skills, General, Camera, Panels, ActionBar, Building, Other, Debug };

    /// <summary>
    /// KEYS3: the <c>RebindingMenu</c> field that holds each category's localization key (client
    /// metadata: <c>LK_Category_Movement</c> 0xB0 … <c>LK_Category_Building</c> 0x110).
    /// </summary>
    internal static string CategoryKeyField(string category) => category switch
    {
        Movement => "LK_Category_Movement",
        Skills => "LK_Category_SkillsAndAttacks",
        General => "LK_Category_General",
        Camera => "LK_Category_Camera",
        Panels => "LK_Category_Panels",
        ActionBar => "LK_Category_ActionBar",
        Building => "LK_Category_Building",
        _ => "",
    };

    /// <summary>KEYS3: a category's place on the page; an unknown one sorts after every known one.</summary>
    internal static int CategoryIndex(string? category)
    {
        for (int i = 0; i < CategoryOrder.Count; i++)
            if (string.Equals(CategoryOrder[i], category, StringComparison.OrdinalIgnoreCase)) return i;
        return CategoryOrder.Count;
    }

    /// <summary>
    /// The Controls page's order: every row the keyboard page lists, the four movement directions
    /// (halves of the analog <c>MoveX</c> / <c>MoveY</c> axes, KEYS4) included.
    /// </summary>
    internal static IReadOnlyList<Entry> Listed => _listed ??= Build();

    // Built on first use: the lists it takes (MovementDirections, OtherKeys) are static fields declared
    // below this one, which a field initializer here would read before they are set.
    private static IReadOnlyList<Entry>? _listed;

    private static IReadOnlyList<Entry> Build()
    {
        var list = new List<Entry>();
        void Add(string category, params string[] actions)
        {
            foreach (var a in actions) list.Add(new Entry(a, category));
        }

        list.AddRange(MovementDirections);
        Add(Movement, "AutoMove", "Walk");
        Add(Skills, "Primary", "WeaponSkill1", "WeaponSkill2", "TravelSkill", "OffensiveSpell", "DefensiveSpell",
            "UltimateSpell", "CliffJump", "ToggleActionWheel", "Shapeshift_Modifier");
        for (int i = 1; i <= 12; i++) Add(Skills, "Shapeshift_" + i.ToString(CultureInfo.InvariantCulture));
        Add(Skills, "Emotes_Modifier");
        for (int i = 1; i <= 12; i++) Add(Skills, "Emotes_" + i.ToString(CultureInfo.InvariantCulture));
        Add(General, "Interact", "Interrupt", "DropItem", "SplitItem", "CustomSplitItem", "RepairItem", "SmartTakeItems",
            "AutoTransferItems", "SortItems", "Respawn", "CancelRevive", "Chat", "ToggleEmoteWheel", "PushToTalk",
            "AdvancedTooltip");
        Add(Camera, "RotateCamera");
        Add(Panels, "CharacterMenu", "BuildMenu", "MapMenu", "ClanMenu", "SpellBookMenu", "VampirePowersMenu",
            "EmotesMenu", "VBloodMenu");
        for (int i = 1; i <= 8; i++) Add(ActionBar, "ActionBar" + i.ToString(CultureInfo.InvariantCulture));
        Add(ActionBar, "AbilityDetails");
        Add(Building, "Building_Place", "Building_Dismantle", "Building_Repair", "Building_RotateLeft",
            "Building_RotateRight", "Building_Cancel", "Building_Deselect", "Building_ToggleColorWheel", "Building_Fill");
        list.AddRange(OtherKeys);
        return list;
    }

    internal const string InTheMap = "in the map";
    internal const string InMenus = "in menus";
    internal const string InChat = "in chat";
    internal const string InInventory = "in inventory and crafting screens";
    internal const string InCameraModes = "in camera modes";

    /// <summary>
    /// KEYS4 (live sweep 2026-09-30: 35 of the game's keyboard-and-mouse actions were missing). Every action
    /// the game registers with a keyboard-and-mouse binding that the Controls page does not list, with the
    /// screen it works in (from the action's name and input map; not observed per screen). Mouse buttons and
    /// mouse axes are listed but never clash with a key. Not listed: <c>ActionBar9</c> (key 9 in the asset,
    /// but no <c>ButtonInputAction</c> value, so the game never registers it) and the actions with no
    /// keyboard-and-mouse binding (menu navigation, the camera buttons, admin, the gamepad composites).
    /// </summary>
    internal static readonly IReadOnlyList<Entry> OtherKeys = new[]
    {
        new Entry("Back", Other, mode: InMenus),
        new Entry("Enter", Other, mode: InMenus),
        new Entry("SelectProgression", Other, mode: InMenus),
        new Entry("CycleChat", Other, mode: InChat),
        new Entry("MapPan", Other, mode: InTheMap),
        new Entry("MapMarker", Other, mode: InTheMap),
        new Entry("MapCenterPlayer", Other, mode: InTheMap),
        new Entry("MapToggleLegend", Other, mode: InTheMap),
        new Entry("MapZoom", Other, "MapZoom", fallbackName: "Map Zoom", mode: InTheMap, wholeAxis: true),
        new Entry("TrackItem", Other, mode: InInventory),
        new Entry("MoveItem", Other, mode: InInventory),
        new Entry("EditItem", Other, mode: InInventory),
        new Entry("TransferItem", Other, mode: InInventory),
        new Entry("UseItem", Other, mode: InInventory),
        new Entry("EquipItem", Other, mode: InInventory),
        new Entry("RemoveBuff", Other, mode: InInventory),
        new Entry("QuickRouteItems", Other, mode: InInventory),
        new Entry("StartCraft", Other, mode: InInventory),
        new Entry("CancelCraft", Other, mode: InInventory),
        new Entry("TrackRecipe", Other, mode: InInventory),
        new Entry("Ability4", Other),
        new Entry("Building_Exit", Other),
        new Entry("Building_Pickup", Other),
        new Entry("ZoomCamera", Other, "ZoomCamera", fallbackName: "Zoom Camera", wholeAxis: true),
        new Entry("RotateCameraX", Other, "RotateCameraX", fallbackName: "Rotate Camera X", wholeAxis: true),
        new Entry("RotateCameraY", Other, "RotateCameraY", fallbackName: "Rotate Camera Y", wholeAxis: true),
        new Entry("MoveCameraLeft", Other, "MoveCameraX", positive: false, fallbackName: "Move Camera X -", mode: InCameraModes),
        new Entry("MoveCameraRight", Other, "MoveCameraX", positive: true, fallbackName: "Move Camera X +", mode: InCameraModes),
        new Entry("MoveCameraBack", Other, "MoveCameraY", positive: false, fallbackName: "Move Camera Y -", mode: InCameraModes),
        new Entry("MoveCameraForward", Other, "MoveCameraY", positive: true, fallbackName: "Move Camera Y +", mode: InCameraModes),
        new Entry("MoveCameraDown", Other, "MoveCameraZ", positive: false, fallbackName: "Move Camera Z -", mode: InCameraModes),
        new Entry("MoveCameraUp", Other, "MoveCameraZ", positive: true, fallbackName: "Move Camera Z +", mode: InCameraModes),
        new Entry("DebugMenu", Debug, neverConflicts: true),
        new Entry("DisableAutoSnapPoints", Debug, neverConflicts: true),
        new Entry("DisableAutoSnapGrid", Debug, neverConflicts: true),
    };

    /// <summary>
    /// KEYS4: the game's words for a mouse control, where the game's own key text is not read (the analog
    /// rows): <c>Mouse Button 4</c> is <c>forwardButton</c> and <c>Mouse Button 5</c> <c>backButton</c>, as
    /// the in-game read of AutoMove and Interrupt showed live 2026-09-30.
    /// </summary>
    internal static string MouseText(string? path)
    {
        string p = (path ?? "").Trim();
        const string prefix = "<Mouse>/";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
        return p.Substring(prefix.Length).ToLowerInvariant() switch
        {
            "leftbutton" => "Left Mouse",
            "rightbutton" => "Right Mouse",
            "middlebutton" => "Scroll Wheel",
            "forwardbutton" => "Mouse Button 4",
            "backbutton" => "Mouse Button 5",
            "scroll/y" or "scroll" => "Scroll Wheel",
            "delta/x" => "Mouse X",
            "delta/y" => "Mouse Y",
            "delta" => "Mouse",
            _ => "",
        };
    }

    internal const string WhileBuilding = "while building";
    internal const string InEmoteMenu = "in the emote menu";
    internal const string WhileChoosingForm = "while choosing a form";
    internal const string AsModifier = "as a modifier key";

    /// <summary>
    /// Review 2026-09-29: the mode an action works in, or empty for an always-active gameplay key. Until
    /// checked live, a mode-only key never stops a mod key (<see cref="KeybindArbiter.GameWins"/>).
    /// Building keys, the emote and shapeshift slots and their modifiers are mode-only; so is any binding
    /// whose key is itself a modifier.
    /// </summary>
    internal static string ModeOf(string action, KeyChord chord)
    {
        string a = action ?? "";
        if (a.StartsWith("Building_", StringComparison.Ordinal)) return WhileBuilding;
        if (a.StartsWith("Emotes_", StringComparison.Ordinal)) return InEmoteMenu;
        if (a.StartsWith("Shapeshift_", StringComparison.Ordinal)) return WhileChoosingForm;
        if (a.EndsWith("_Modifier", StringComparison.Ordinal)) return AsModifier;
        if (chord.IsBound && KeyChord.IsModifierKey(chord.Key)) return AsModifier;
        return "";
    }

    /// <summary>A readable name from the enum when the game's own localized name cannot be read.</summary>
    internal static string FallbackName(string action)
    {
        string a = (action ?? "").Trim();
        foreach (var d in MovementDirections)
            if (string.Equals(d.Action, a, StringComparison.Ordinal)) return d.FallbackName;
        foreach (var d in OtherKeys)
            if (d.FallbackName.Length > 0 && string.Equals(d.Action, a, StringComparison.Ordinal)) return d.FallbackName;
        foreach (var prefix in new[] { "Building_", "Shapeshift_", "Emotes_" })
        {
            if (!a.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string rest = a.Substring(prefix.Length);
            return prefix.TrimEnd('_') + ": " + ModSettingsLayout.DisplayName(rest).ToLowerInvariant();
        }

        return ModSettingsLayout.DisplayName(a);
    }

    /// <summary>
    /// A binding as a chord: the main key's path and, for a modifier composite, the modifier's path.
    /// False (not comparable) for a mouse or gamepad control, an unknown name, or no key at all.
    /// </summary>
    internal static bool TryChord(string? modifierPath, string? keyPath, out KeyChord chord)
    {
        chord = KeyChord.None;
        // KEYS4: the mouse buttons a bind can use are keys too (AutoMove's Mouse Button 4 is the forward button).
        if (string.IsNullOrWhiteSpace(modifierPath) && TryMouseButton(keyPath, out var mouse))
        {
            chord = new KeyChord(mouse);
            return true;
        }

        if (!TryKeyboardControl(keyPath, out string keyName) || !TryUiKey(keyName, out var key)) return false;
        var mods = KeyModifiers.None;
        if (!string.IsNullOrWhiteSpace(modifierPath))
        {
            if (!TryKeyboardControl(modifierPath, out string modName)) return false;
            mods = ModifierOf(modName);
            if (mods == KeyModifiers.None) return false;
        }

        chord = new KeyChord(key, mods);
        return true;
    }

    /// <summary>
    /// KEYS4: <c>&lt;Mouse&gt;/middleButton</c>, <c>backButton</c>, <c>forwardButton</c> as a <see cref="UiKey"/>;
    /// false for the left and right buttons, the axes and any other device.
    /// </summary>
    internal static bool TryMouseButton(string? path, out UiKey key)
    {
        key = UiKey.None;
        string p = (path ?? "").Trim();
        const string prefix = "<Mouse>/";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        switch (p.Substring(prefix.Length).ToLowerInvariant())
        {
            case "middlebutton": key = UiKey.MouseMiddle; return true;
            case "backbutton": key = UiKey.MouseBack; return true;
            case "forwardbutton": key = UiKey.MouseForward; return true;
            default: return false;
        }
    }

    /// <summary><c>&lt;Keyboard&gt;/leftShift</c> → <c>leftShift</c>; false for any other device.</summary>
    internal static bool TryKeyboardControl(string? path, out string control)
    {
        control = "";
        string p = (path ?? "").Trim();
        if (p.Length == 0) return false;
        const string prefix = "<Keyboard>/";
        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        control = p.Substring(prefix.Length).Trim();
        return control.Length > 0;
    }

    /// <summary>An Input System keyboard control name (<c>f</c>, <c>1</c>, <c>leftBracket</c>) as a <see cref="UiKey"/>.</summary>
    internal static bool TryUiKey(string control, out UiKey key)
    {
        key = UiKey.None;
        string c = (control ?? "").Trim();
        if (c.Length == 1 && c[0] >= '0' && c[0] <= '9')
            return Enum.TryParse("Digit" + c, out key);
        if (c.Length == 0 || char.IsDigit(c[0])) return false;
        switch (c.ToLowerInvariant())
        {
            case "shift": key = UiKey.LeftShift; return true;
            case "ctrl": key = UiKey.LeftCtrl; return true;
            case "alt": key = UiKey.LeftAlt; return true;
            case "altgr": key = UiKey.RightAlt; return true;
        }

        return Enum.TryParse(c, ignoreCase: true, out key) && Enum.IsDefined(typeof(UiKey), key) && key != UiKey.None;
    }

    private static KeyModifiers ModifierOf(string control) => (control ?? "").ToLowerInvariant() switch
    {
        "shift" or "leftshift" or "rightshift" => KeyModifiers.Shift,
        "ctrl" or "leftctrl" or "rightctrl" => KeyModifiers.Ctrl,
        "alt" or "leftalt" or "rightalt" or "altgr" => KeyModifiers.Alt,
        _ => KeyModifiers.None,
    };

    /// <summary>
    /// A game claim for one slot of one action. <paramref name="text"/> is the game's own (localized)
    /// key text; when empty the chord's text is shown, and <c>None</c> for an unbound slot.
    /// <paramref name="description"/> (KEYS3) is the game's own localized description of the action
    /// (the second key of <c>TryGetButtonInputActionLocalization</c>), carried as the claim's help;
    /// empty when the game has none or it could not be read.
    /// </summary>
    internal static KeybindClaim Claim(
        Entry entry, string displayName, int slot, string? modifierPath, string? keyPath, string text, string description = "",
        bool defaultKnown = false, string? defaultModifierPath = null, string? defaultKeyPath = null, bool slotExists = true)
    {
        bool bound = !string.IsNullOrWhiteSpace(keyPath) || !string.IsNullOrWhiteSpace(modifierPath);
        bool keyboard = TryChord(modifierPath, keyPath, out var chord);
        string mouse = MouseText(keyPath);
        string shown = !string.IsNullOrWhiteSpace(text) ? text.Trim()
            : keyboard ? chord.ToString()
            : mouse.Length > 0 ? mouse
            : bound ? (keyPath ?? "").Trim() : "None";
        if (!bound) shown = "None";
        // KEYS4: a debug key is shown with its key but never compared.
        bool comparable = keyboard && !entry.NeverConflicts;
        string mode = entry.Mode.Length > 0 ? entry.Mode : ModeOf(entry.Action, chord);
        // KEYS9: the game's own default for this slot (the binding's path without the player's overrides).
        bool changed = defaultKnown
            && (!SamePath(keyPath, defaultKeyPath) || !SamePath(modifierPath, defaultModifierPath));
        // Unchanged: the game's own key text (Shift, 1); changed: the text from the default's path.
        string defaultText = !defaultKnown ? "" : changed ? PathText(defaultModifierPath, defaultKeyPath) : shown;
        return new KeybindClaim(
            KeybindSource.Game, KeybindArbiter.GameOwnerId, entry.Action,
            string.IsNullOrWhiteSpace(displayName) ? FallbackName(entry.Action) : displayName,
            comparable ? chord : KeyChord.None, chordText: shown, help: (description ?? "").Trim(), comparable: comparable, slot: slot,
            category: entry.Category, mode: mode, defaultText: defaultText, gameChanged: changed, gameSlotExists: slotExists);
    }

    /// <summary>KEYS9: two control paths name the same control (Input System paths are case-insensitive; empty is unbound).</summary>
    internal static bool SamePath(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>KEYS9: a binding's text from its paths alone (a default the game has no key text for): <c>W</c>, <c>Mouse Button 4</c>, <c>None</c>.</summary>
    internal static string PathText(string? modifierPath, string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath) && string.IsNullOrWhiteSpace(modifierPath)) return "None";
        if (TryChord(modifierPath, keyPath, out var chord)) return chord.DisplayText;
        string mouse = MouseText(keyPath);
        return mouse.Length > 0 ? mouse : (keyPath ?? "").Trim();
    }
}

/// <summary>
/// KEYS3: the Bindings Menu category titles the tab shows. The game's own localized titles when a
/// <c>RebindingMenu</c> was reached this Core generation (its <c>LK_Category_*</c> keys, resolved
/// through the game's string table), else the English labels of <see cref="GameKeybindCatalog.CategoryOrder"/>.
/// Game-free: the Unity side (<c>KeybindArtDonors</c>) calls <see cref="Use"/>; the tab reads
/// <see cref="TitleOf"/> and <see cref="Source"/>.
/// </summary>
internal static class GameKeybindCategories
{
    internal const string SourceGame = "game";
    internal const string SourceCatalog = "catalog";

    private static readonly object Gate = new();
    private static Dictionary<string, string> _titles = new(StringComparer.OrdinalIgnoreCase);
    private static string _from = "";

    /// <summary><c>game</c> once any localized title was read, else <c>catalog</c>.</summary>
    internal static string Source
    {
        get { lock (Gate) return _titles.Count > 0 ? SourceGame : SourceCatalog; }
    }

    /// <summary>Where the localized titles were read (<c>live</c>, <c>resident</c>), or empty.</summary>
    internal static string From
    {
        get { lock (Gate) return _from; }
    }

    /// <summary>
    /// Keep the localized titles read from the game. Blank titles are ignored; an empty set leaves
    /// what an earlier read found (a later miss never forgets a good read in the same generation).
    /// </summary>
    internal static void Use(IReadOnlyDictionary<string, string>? titles, string from)
    {
        if (titles == null) return;
        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in titles)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            next[pair.Key.Trim()] = pair.Value.Trim();
        }

        if (next.Count == 0) return;
        lock (Gate)
        {
            _titles = next;
            _from = from ?? "";
        }
    }

    /// <summary>The title of <paramref name="category"/>: the game's localized word, else the catalog's.</summary>
    internal static string TitleOf(string? category)
    {
        string id = (category ?? "").Trim();
        lock (Gate)
            if (_titles.TryGetValue(id, out var title)) return title;
        return id;
    }

    /// <summary>Core unload (and tests): forget the localized titles.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _from = "";
        }
    }
}
