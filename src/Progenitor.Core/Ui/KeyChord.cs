using System;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// Keyboard key a mod keybind can be bound to. The member <b>names</b> mirror
/// <c>UnityEngine.InputSystem.Key</c> (client) exactly, and the production
/// input source maps ours to the game's by name (<c>Enum.TryParse</c>), never by number — so a
/// Unity Input System upgrade that renumbers the enum cannot silently rebind every key. Numbers
/// here are ours and are not a wire contract.
///
/// <para>KEYS4: the last five are mouse buttons, not keyboard keys: <see cref="MouseMiddle"/>,
/// <see cref="MouseBack"/> and <see cref="MouseForward"/> (the Input System's <c>Mouse.middleButton</c>,
/// <c>backButton</c> and <c>forwardButton</c>; the game names the forward button "Mouse Button 4" and the
/// back button "Mouse Button 5"), and since KEYS8 <see cref="MouseLeft"/> and <see cref="MouseRight"/>
/// (<c>leftButton</c>, <c>rightButton</c>). They bind and clash like keys. The left and right buttons
/// attack, move and click menus, so a chord on one needs a modifier and they are never Mods-menu keys
/// (<see cref="MouseChords"/>).</para>
/// </summary>
public enum UiKey
{
    None = 0,
    Space, Enter, Tab, Backquote, Quote, Semicolon, Comma, Period, Slash, Backslash,
    LeftBracket, RightBracket, Minus, Equals,
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0,
    LeftShift, RightShift, LeftAlt, RightAlt, LeftCtrl, RightCtrl, LeftMeta, RightMeta,
    ContextMenu, Escape,
    LeftArrow, RightArrow, UpArrow, DownArrow,
    Backspace, PageDown, PageUp, Home, End, Insert, Delete,
    CapsLock, NumLock, PrintScreen, ScrollLock, Pause,
    NumpadEnter, NumpadDivide, NumpadMultiply, NumpadPlus, NumpadMinus, NumpadPeriod, NumpadEquals,
    Numpad0, Numpad1, Numpad2, Numpad3, Numpad4, Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    /// <summary>KEYS4: the middle mouse button (the wheel click; the game calls it "Scroll Wheel").</summary>
    MouseMiddle,

    /// <summary>KEYS4: the mouse's back button (the game's "Mouse Button 5").</summary>
    MouseBack,

    /// <summary>KEYS4: the mouse's forward button (the game's "Mouse Button 4").</summary>
    MouseForward,

    /// <summary>KEYS8: the left mouse button. A chord on it needs Ctrl, Shift or Alt (<see cref="MouseChords"/>).</summary>
    MouseLeft,

    /// <summary>KEYS8: the right mouse button. A chord on it needs Ctrl, Shift or Alt (<see cref="MouseChords"/>).</summary>
    MouseRight,
}

/// <summary>Modifier keys held with a <see cref="KeyChord"/>'s main key. Side-agnostic: either Ctrl counts.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// Which physical modifier keys are held, side by side. A chord is still stored side-agnostically
/// (<see cref="KeyModifiers"/>) — a player who binds <c>Ctrl+K</c> means either Ctrl — but the
/// <i>read</i> has to know the sides, because Windows delivers <c>AltGr</c> as
/// <c>LeftCtrl</c> + <c>RightAlt</c> and there is no AltGr control to ask instead
/// (<see cref="KeyChord.IsAltGrPair"/>). Source: <c>Keyboard.leftShiftKey</c> /
/// <c>rightShiftKey</c> / <c>leftAltKey</c> / <c>rightAltKey</c> / <c>leftCtrlKey</c> /
/// <c>rightCtrlKey</c> and their getters (client). R37 finding 1.
/// </summary>
[Flags]
public enum ModifierSides
{
    None = 0,
    LeftCtrl = 1,
    RightCtrl = 2,
    LeftShift = 4,
    RightShift = 8,
    LeftAlt = 16,
    RightAlt = 32,

    /// <summary>
    /// KEYS2: the left Windows key (<c>Keyboard.leftMetaKey</c>, client). Never part of a chord: a key pressed while it is held is Windows' shortcut, so the
    /// capture refuses it and the pump never fires on it (<see cref="ReservedChords"/>).
    /// </summary>
    LeftMeta = 64,

    /// <summary>KEYS2: the right Windows key (<c>Keyboard.rightMetaKey</c>).</summary>
    RightMeta = 128,
}

/// <summary>
/// One key plus its modifiers, stored in a cfg as a string the player can edit.
///
/// <para>
/// The value type and its serialized shape are borrowed from <b>BepInEx's
/// <c>KeyboardShortcut</c></b> (main key first, then modifiers, joined with <c>" + "</c>, e.g.
/// <c>F10 + Ctrl</c>) so a V Rising player who has edited any other BepInEx mod's cfg already
/// knows the format. Mechanism only, no code copied: BepInEx's type is built on the legacy
/// <c>UnityEngine.KeyCode</c> and its own <c>TomlTypeConverter</c> registration, neither of which
/// is usable here (this build reads input through the new Input System, and Core binds keybinds as
/// plain <c>string</c> so a <c>ConfigEntry&lt;T&gt;</c> of a Core type can never pin a collectible
/// generation — the same rule <c>ConfigHelper.BindEnum</c> follows).
/// </para>
///
/// <para>
/// Mod binds cannot be added to the vanilla controls screen: <c>ProjectM.ButtonInputAction</c> is
/// a compiled enum with no free slot and the binding list comes from the
/// <c>Settings/DefaultControls</c> asset, so mod binds are
/// listed in <b>our</b> settings panel instead.
/// </para>
/// </summary>
public readonly struct KeyChord : IEquatable<KeyChord>
{
    /// <summary>The separator BepInEx's <c>KeyboardShortcut</c> serializes with.</summary>
    public const string Separator = " + ";

    /// <summary>Unbound. <see cref="IsBound"/> is false; the poll never fires it.</summary>
    public static readonly KeyChord None = default;

    public KeyChord(UiKey key, KeyModifiers modifiers = KeyModifiers.None)
    {
        Key = key;
        Modifiers = key == UiKey.None ? KeyModifiers.None : modifiers;
    }

    /// <summary>Main key. <see cref="UiKey.None"/> means unbound.</summary>
    public UiKey Key { get; }

    /// <summary>Modifiers that must be held. Side-agnostic.</summary>
    public KeyModifiers Modifiers { get; }

    /// <summary>False for <see cref="None"/>: nothing to poll, nothing to conflict with.</summary>
    public bool IsBound => Key != UiKey.None;

    /// <summary>
    /// True when this key, pressed on its own, is one a text field would turn into a character —
    /// letters, digits, the punctuation block, Space, Tab and Enter, and the numpad's printing
    /// keys. Function keys, arrows, Escape, Home/End/Delete and the lock keys are not printable:
    /// a text field ignores them or moves the caret, so a bind on one cannot eat a keystroke the
    /// player meant for a field.
    ///
    /// <para>
    /// This is the predicate behind <see cref="IsSafeWhileTyping"/> and, through it, the
    /// registration and rebind refusal on <c>allowWhenChatUnknown</c> binds (R36 finding 5).
    /// </para>
    /// </summary>
    public static bool IsPrintableKey(UiKey key) =>
        key is UiKey.Space or UiKey.Enter or UiKey.Tab
            or UiKey.Backquote or UiKey.Quote or UiKey.Semicolon or UiKey.Comma or UiKey.Period
            or UiKey.Slash or UiKey.Backslash or UiKey.LeftBracket or UiKey.RightBracket
            or UiKey.Minus or UiKey.Equals
            or (>= UiKey.A and <= UiKey.Z)
            or (>= UiKey.Digit1 and <= UiKey.Digit0)
            or UiKey.NumpadEnter or UiKey.NumpadDivide or UiKey.NumpadMultiply
            or UiKey.NumpadPlus or UiKey.NumpadMinus or UiKey.NumpadPeriod or UiKey.NumpadEquals
            or (>= UiKey.Numpad0 and <= UiKey.Numpad9);

    /// <summary>
    /// True when this chord cannot be produced by a player simply typing: it is unbound, its main
    /// key is not printable, or it carries a modifier combination a text field cannot deliver.
    ///
    /// <para>
    /// <b>Not "any modifier" (R37 finding 1).</b> Two of the eight combinations are exactly how
    /// characters are typed:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>Shift</c> alone is how every capital letter and every shifted symbol is produced, so
    /// <c>Shift + N</c> fires on precisely the keystroke that types a capital <c>N</c>.
    /// </description></item>
    /// <item><description>
    /// <c>Ctrl + Alt</c> together is <b>AltGr</b> on Windows: the OS delivers <c>AltGr</c> as
    /// <c>LeftCtrl</c> + <c>RightAlt</c>, which is how a German, French, Polish or Nordic layout
    /// types <c>@</c>, <c>\</c>, <c>{</c> and the rest of the third level. So a <c>Ctrl+Alt+Q</c>
    /// bind fires while such a player types <c>@</c> into the direct-connect address box.
    /// (<see cref="MatchesHeld"/> refuses the AltGr pair at the read as well, for ordinary binds
    /// and for a chord that was already in a cfg.)
    /// </description></item>
    /// </list>
    /// <para>
    /// So a printable key is safe only with <c>Ctrl</c> or <c>Alt</c> — never <c>Shift</c> alone,
    /// never <c>Ctrl</c> and <c>Alt</c> together. <c>Ctrl+Shift</c> and <c>Alt+Shift</c> are fine.
    /// A bare <c>P</c> is not safe; <c>P + Ctrl</c> is. A focused field suppresses the bind anyway
    /// through the text-field gate; this predicate is the second line, for the states no probe can
    /// see. <see cref="Keybinds.Register"/> and <see cref="Keybinds.Rebind"/> refuse an unsafe
    /// chord for a bind that carries the <c>allowWhenChatUnknown</c> exemption, because that bind
    /// fires in exactly the states where we cannot read a chat window (R36 finding 5).
    /// </para>
    /// </summary>
    public bool IsSafeWhileTyping =>
        !IsBound || !IsPrintableKey(Key) || IsTypingProof(Modifiers);

    /// <summary>
    /// The modifier half of <see cref="IsSafeWhileTyping"/>: <c>Ctrl</c> or <c>Alt</c> is held and
    /// not both. <c>Shift</c> is neither necessary nor sufficient — it types capitals — so it only
    /// ever rides along with one of the other two.
    /// </summary>
    public static bool IsTypingProof(KeyModifiers modifiers)
    {
        var ctrl = (modifiers & KeyModifiers.Ctrl) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;
        return (ctrl || alt) && !(ctrl && alt);
    }

    /// <summary>
    /// True when <paramref name="sides"/> is Windows' <b>AltGr</b>: <c>LeftCtrl</c> and
    /// <c>RightAlt</c> held with neither <c>RightCtrl</c> nor <c>LeftAlt</c>. The Input System
    /// has no separate AltGr control to ask — <c>Key.AltGr</c> (client) is
    /// value <c>54</c>, the same value as <c>Key.RightAlt</c>, declared as its alias
    /// (<c>alias = "AltGr"</c>) — so the pair is the only way to see it,
    /// and it is read from the six side-specific controls.
    /// <c>Shift</c> is ignored here on purpose: <c>Shift+AltGr</c> is the fourth level on the same
    /// layouts and types characters too.
    /// </summary>
    public static bool IsAltGrPair(ModifierSides sides) =>
        (sides & (ModifierSides.LeftCtrl | ModifierSides.RightAlt))
            == (ModifierSides.LeftCtrl | ModifierSides.RightAlt)
        && (sides & (ModifierSides.RightCtrl | ModifierSides.LeftAlt)) == ModifierSides.None;

    /// <summary>
    /// True when this chord is one the AltGr refusal in <see cref="MatchesHeld"/> can hit at all:
    /// a <b>printable</b> main key wanted with both <c>Ctrl</c> and <c>Alt</c>. Such a chord is
    /// perfectly bindable and fires on <c>LeftCtrl+LeftAlt</c> (and on either right-hand pairing);
    /// it is only the <c>LeftCtrl+RightAlt</c> hand position that never fires it, because that is
    /// what Windows delivers for <c>AltGr</c> and we cannot tell the two apart.
    ///
    /// <para>
    /// It exists as a cheap chord-only predicate so the pump can decide, without reading the
    /// keyboard twice, whether a bind that did not fire deserves the explanation line — and so the
    /// settings row can carry the limitation and the alternative next to the bind it applies to
    /// (R38 finding 4). See <see cref="Keybinds.AltGrAlternative"/>.
    /// </para>
    /// </summary>
    public bool IsAltGrShaped =>
        IsBound && IsPrintableKey(Key)
        && (Modifiers & KeyModifiers.Ctrl) != 0 && (Modifiers & KeyModifiers.Alt) != 0;

    /// <summary>KEYS2: true while either Windows key is held (<see cref="ModifierSides.LeftMeta"/> / <see cref="ModifierSides.RightMeta"/>).</summary>
    public static bool IsWindowsKeyHeld(ModifierSides sides) =>
        (sides & (ModifierSides.LeftMeta | ModifierSides.RightMeta)) != ModifierSides.None;

    /// <summary>The side-agnostic modifiers <paramref name="sides"/> amounts to.</summary>
    public static KeyModifiers ModifiersOf(ModifierSides sides)
    {
        var held = KeyModifiers.None;
        if ((sides & (ModifierSides.LeftCtrl | ModifierSides.RightCtrl)) != 0) held |= KeyModifiers.Ctrl;
        if ((sides & (ModifierSides.LeftShift | ModifierSides.RightShift)) != 0) held |= KeyModifiers.Shift;
        if ((sides & (ModifierSides.LeftAlt | ModifierSides.RightAlt)) != 0) held |= KeyModifiers.Alt;
        return held;
    }

    /// <summary>
    /// True when this chord's main key going down with <paramref name="sides"/> held should fire
    /// it. Exact matching, as before (<c>Ctrl+K</c> does not fire on <c>Ctrl+Shift+K</c>), plus one
    /// refusal: a <b>printable</b> key on a chord wanting both <c>Ctrl</c> and <c>Alt</c> does not
    /// fire when the held pair is AltGr (<see cref="IsAltGrPair"/>), because that keystroke is the
    /// player typing a third-level character on a non-US layout and we cannot tell it apart from a
    /// deliberate <c>LeftCtrl+RightAlt</c>. <c>LeftCtrl+LeftAlt</c> (or either right modifier)
    /// still fires, and a non-printable main key — <c>Ctrl+Alt+F5</c> — is never refused, since no
    /// layout types a function key. R37 finding 1.
    ///
    /// <para>
    /// The refusal is <b>not</b> silent any more (R38 finding 4): the callers of this predicate
    /// hand the same <paramref name="sides"/> back to <see cref="Keybinds"/>, which logs the
    /// reason and the alternative once per press, and the rebind flow ends with a refusal rather
    /// than leaving the panel in "press a key". This method stays a pure predicate.
    /// </para>
    /// </summary>
    public bool MatchesHeld(ModifierSides sides)
    {
        // KEYS2: a key pressed with a Windows key held is Windows' shortcut (Win+G, Win+Alt+R), never ours.
        if (IsWindowsKeyHeld(sides)) return false;
        if (ModifiersOf(sides) != Modifiers) return false;
        return !(IsAltGrShaped && IsAltGrPair(sides));
    }

    /// <summary>KEYS4: true for the mouse buttons a bind can use (<see cref="UiKey.MouseMiddle"/>, back, forward; KEYS8: left, right).</summary>
    public static bool IsMouseKey(UiKey key) =>
        key is UiKey.MouseMiddle or UiKey.MouseBack or UiKey.MouseForward or UiKey.MouseLeft or UiKey.MouseRight;

    /// <summary>KEYS8: true for the left and right buttons, the ones that attack, move and click menus.</summary>
    public static bool IsClickButton(UiKey key) => key is UiKey.MouseLeft or UiKey.MouseRight;

    /// <summary>True when <paramref name="key"/> is itself a modifier and cannot be a main key.</summary>
    public static bool IsModifierKey(UiKey key) =>
        key is UiKey.LeftShift or UiKey.RightShift
            or UiKey.LeftCtrl or UiKey.RightCtrl
            or UiKey.LeftAlt or UiKey.RightAlt
            or UiKey.LeftMeta or UiKey.RightMeta;

    /// <summary>
    /// Parse the cfg form. Tokens are split on <c>+</c> and trimmed; modifier tokens are
    /// recognized in any position and in any order (<c>Ctrl</c>, <c>Control</c>, <c>LeftCtrl</c>,
    /// <c>LeftControl</c>, <c>Shift</c>, <c>Alt</c>); the first remaining token is the main key,
    /// matched case-insensitively against <see cref="UiKey"/> with a few player-friendly aliases
    /// (<c>Ctrl</c>-less digits <c>1</c>-<c>0</c> mean <c>Digit1</c>-<c>Digit0</c>, <c>Return</c>
    /// means <c>Enter</c>, <c>Esc</c> means <c>Escape</c>). Empty, whitespace, <c>None</c> and
    /// modifiers-only all parse to <see cref="None"/> and return true — an unbound key is a
    /// legitimate value a player types to switch a bind off, not an error.
    /// </summary>
    /// <summary>
    /// KEYS4 (found live 2026-09-30: capturing <c>Shift</c> through the bridge offered "Unbind this key"):
    /// true when <paramref name="text"/> names modifiers and nothing else (<c>Shift</c>, <c>Ctrl + Alt</c>).
    /// <see cref="TryParse"/> reads that as <see cref="None"/>, which is right for a cfg value and wrong for
    /// a captured key: a capture refuses it.
    /// </summary>
    public static bool IsModifiersOnly(string? text, out KeyModifiers modifiers)
    {
        modifiers = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var part in text!.Split('+'))
        {
            string token = part.Trim();
            if (token.Length == 0) continue;
            var mod = ModifierFor(token);
            if (mod == KeyModifiers.None) return false;
            modifiers |= mod;
        }

        return modifiers != KeyModifiers.None;
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = None;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var parts = text.Split('+');
        var modifiers = KeyModifiers.None;
        UiKey key = UiKey.None;
        bool sawKey = false;

        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (token.Length == 0) continue;

            var mod = ModifierFor(token);
            if (mod != KeyModifiers.None)
            {
                modifiers |= mod;
                continue;
            }

            if (sawKey) return false;
            if (!TryParseKey(token, out key)) return false;
            sawKey = true;
        }

        chord = key == UiKey.None ? None : new KeyChord(key, modifiers);
        return true;
    }

    /// <summary>Parse, or <paramref name="fallback"/> when the text is not a chord.</summary>
    public static KeyChord Parse(string? text, KeyChord fallback) =>
        TryParse(text, out var chord) ? chord : fallback;

    /// <summary>
    /// The cfg form. <c>None</c> for an unbound chord (never the empty string: an empty cfg value
    /// would read back as "the player has not chosen", and here they have).
    /// </summary>
    public override string ToString()
    {
        if (!IsBound) return "None";
        var sb = new StringBuilder(Key.ToString());
        if ((Modifiers & KeyModifiers.Ctrl) != 0) sb.Append(Separator).Append("Ctrl");
        if ((Modifiers & KeyModifiers.Shift) != 0) sb.Append(Separator).Append("Shift");
        if ((Modifiers & KeyModifiers.Alt) != 0) sb.Append(Separator).Append("Alt");
        return sb.ToString();
    }

    /// <summary>
    /// KEYS5: the chord as the Keybinds tab, the mod page and every sentence show it: the cfg form with the
    /// mouse buttons in words (<c>Mouse Back</c>, <c>Mouse Forward</c>, <c>Middle Mouse</c>, <c>Left Mouse</c>,
    /// <c>Right Mouse</c>; the cfg keeps <c>MouseBack</c>). <c>None</c> for an unbound chord. It parses back (<see cref="TryParse"/>).
    /// </summary>
    public string DisplayText
    {
        get
        {
            if (!IsBound) return "None";
            var sb = new StringBuilder(KeyDisplayName(Key));
            if ((Modifiers & KeyModifiers.Ctrl) != 0) sb.Append(Separator).Append("Ctrl");
            if ((Modifiers & KeyModifiers.Shift) != 0) sb.Append(Separator).Append("Shift");
            if ((Modifiers & KeyModifiers.Alt) != 0) sb.Append(Separator).Append("Alt");
            return sb.ToString();
        }
    }

    /// <summary>KEYS5: a key's shown name: the mouse buttons in words, every other key by its name.</summary>
    public static string KeyDisplayName(UiKey key) => key switch
    {
        UiKey.MouseBack => "Mouse Back",
        UiKey.MouseForward => "Mouse Forward",
        UiKey.MouseMiddle => "Middle Mouse",
        UiKey.MouseLeft => "Left Mouse",
        UiKey.MouseRight => "Right Mouse",
        _ => key.ToString(),
    };

    public bool Equals(KeyChord other) => Key == other.Key && Modifiers == other.Modifiers;

    public override bool Equals(object? obj) => obj is KeyChord other && Equals(other);

    public override int GetHashCode() => ((int)Key * 397) ^ (int)Modifiers;

    public static bool operator ==(KeyChord a, KeyChord b) => a.Equals(b);

    public static bool operator !=(KeyChord a, KeyChord b) => !a.Equals(b);

    private static KeyModifiers ModifierFor(string token)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
            case "leftctrl":
            case "rightctrl":
            case "leftcontrol":
            case "rightcontrol":
                return KeyModifiers.Ctrl;
            case "shift":
            case "leftshift":
            case "rightshift":
                return KeyModifiers.Shift;
            case "alt":
            case "leftalt":
            case "rightalt":
                return KeyModifiers.Alt;
            default:
                return KeyModifiers.None;
        }
    }

    private static bool TryParseKey(string token, out UiKey key)
    {
        key = UiKey.None;
        switch (token.ToLowerInvariant())
        {
            case "none": return true;
            case "return": key = UiKey.Enter; return true;
            case "esc": key = UiKey.Escape; return true;
            case "0": key = UiKey.Digit0; return true;
            case "1": key = UiKey.Digit1; return true;
            case "2": key = UiKey.Digit2; return true;
            case "3": key = UiKey.Digit3; return true;
            case "4": key = UiKey.Digit4; return true;
            case "5": key = UiKey.Digit5; return true;
            case "6": key = UiKey.Digit6; return true;
            case "7": key = UiKey.Digit7; return true;
            case "8": key = UiKey.Digit8; return true;
            case "9": key = UiKey.Digit9; return true;
        }

        // The punctuation keys by the character they type on a US layout, and their common names. The console key is
        // Backquote: "`" and "~" (its shifted character) both name it. "+" cannot be a key name: it joins a chord.
        if (TryPunctuationKey(token, out key)) return true;

        // KEYS4: the mouse buttons by the game's words too ("Mouse Button 4" is the forward button).
        switch (token.Replace(" ", "").ToLowerInvariant())
        {
            case "mousebutton4": key = UiKey.MouseForward; return true;
            case "mousebutton5": key = UiKey.MouseBack; return true;
            // KEYS5: the shown names parse back ("Mouse Back", "Mouse Forward", "Middle Mouse").
            case "mouseback": key = UiKey.MouseBack; return true;
            case "mouseforward": key = UiKey.MouseForward; return true;
            case "mousemiddle": key = UiKey.MouseMiddle; return true;
            case "scrollwheel":
            case "middlemouse":
            case "middlebutton":
            case "mouse2":
                key = UiKey.MouseMiddle;
                return true;
            // KEYS8: the left and right buttons. Mouse0 to Mouse4 are Unity's KeyCode names (what BepInEx's
            // KeyboardShortcut writes), counted from 0: Mouse3 is the back button and Mouse4 the forward one.
            case "mouseleft":
            case "leftmouse":
            case "leftbutton":
            case "leftclick":
            case "mouse0":
                key = UiKey.MouseLeft;
                return true;
            case "mouseright":
            case "rightmouse":
            case "rightbutton":
            case "rightclick":
            case "mouse1":
                key = UiKey.MouseRight;
                return true;
            case "mouse3":
            case "backbutton":
                key = UiKey.MouseBack;
                return true;
            case "mouse4":
            case "forwardbutton":
                key = UiKey.MouseForward;
                return true;
        }

        if (!Enum.TryParse(token, ignoreCase: true, out UiKey parsed)) return false;
        // "3" would otherwise parse as the numeric value of UiKey — reject anything numeric.
        if (!Enum.IsDefined(typeof(UiKey), parsed)) return false;
        if (char.IsDigit(token[0])) return false;
        key = parsed;
        return true;
    }

    /// <summary>
    /// A punctuation key by its US-layout character (<c>`</c>, <c>~</c>, <c>'</c>, <c>;</c>, <c>,</c>, <c>.</c>,
    /// <c>/</c>, <c>\</c>, <c>[</c>, <c>]</c>, <c>-</c>, <c>=</c>) or a common name (<c>Tilde</c>, <c>Grave</c>,
    /// <c>Apostrophe</c>, <c>Hyphen</c>, <c>Dash</c>). The enum names (<c>Backquote</c>, <c>Quote</c>, <c>Minus</c>...)
    /// parse through the enum. A shifted character names its key (<c>~</c> is Backquote), never a Shift chord.
    /// </summary>
    public static bool TryPunctuationKey(string token, out UiKey key)
    {
        key = token switch
        {
            "`" or "~" => UiKey.Backquote,
            "'" => UiKey.Quote,
            ";" => UiKey.Semicolon,
            "," => UiKey.Comma,
            "." => UiKey.Period,
            "/" => UiKey.Slash,
            "\\" => UiKey.Backslash,
            "[" => UiKey.LeftBracket,
            "]" => UiKey.RightBracket,
            "-" => UiKey.Minus,
            "=" => UiKey.Equals,
            _ => UiKey.None,
        };
        if (key != UiKey.None) return true;
        key = token.ToLowerInvariant() switch
        {
            "tilde" or "grave" or "backtick" => UiKey.Backquote,
            "apostrophe" => UiKey.Quote,
            "hyphen" or "dash" => UiKey.Minus,
            _ => UiKey.None,
        };
        return key != UiKey.None;
    }
}
