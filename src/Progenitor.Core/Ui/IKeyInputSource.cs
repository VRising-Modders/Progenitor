namespace Progenitor.Ui;

/// <summary>
/// The seam between <see cref="Keybinds"/> (registry, config round trip, suppression, conflict
/// logging — game-free) and the one place Core reads the keyboard. Tests supply a fake; the
/// client uses <c>UnityKeyInputSource</c>; the server and the main menu use
/// <see cref="NullKeyInputSource"/>.
///
/// <para>
/// <b>Which read, and why.</b> A mod cannot add a
/// <c>ProjectM.ButtonInputAction</c> — it is a compiled enum — and the binding list comes from the
/// <c>Settings/DefaultControls</c> <c>InputActionAsset</c>, so there is no vanilla seam that would
/// hand us "a key the player bound". The reachable read is the Input System's device layer:
/// <c>UnityEngine.InputSystem.Keyboard.current</c> (<c>get_current</c>), indexed by <c>Key</c>
/// (<c>KeyControl get_Item(Key)</c>), read through <c>ButtonControl.wasPressedThisFrame</c>
/// and <c>isPressed</c> for modifiers. The interop assembly is shipped (<c>BepInEx/interop/Unity.InputSystem.dll</c>) and
/// Bootstrap's own <c>UiInputContext.Update</c> already reads <c>Keyboard.current.escapeKey</c>
/// live, so this path is proven on this build.
/// </para>
///
/// <para>
/// Legacy <c>UnityEngine.Input.GetKeyDown(KeyCode)</c> is present in the game's metadata
/// and is <b>not</b> used: V Rising ships with the
/// Input System package as the active input handler, where the legacy module throws
/// <c>InvalidOperationException</c> on first read. It stays documented here as the fallback if a
/// future build flips <c>activeInputHandling</c> to Both.
/// </para>
/// </summary>
/// <summary>
/// The three answers <see cref="IKeyInputSource.ChatFocus"/> can give. Deliberately tri-state:
/// "there is no readable chat window" is not the same fact as "the player is not typing", and
/// the difference is the whole of the fail-closed rule.
/// </summary>
internal enum ChatFocus
{
    /// <summary>A chat window was read and the player is not typing in it. Binds may fire.</summary>
    NotFocused = 0,

    /// <summary>A chat window was read and the player is typing. Binds are suppressed.</summary>
    Focused = 1,

    /// <summary>
    /// No chat window could be found, or the read threw. Concretely: <i>no chat window exists in
    /// this scene</i> — the main menu, a loading screen, the frames before the HUD is up.
    /// Treated as focused by <see cref="Keybinds.Pump"/> for every ordinary bind (firing a bind
    /// into a chat line is worse than missing one), with one opt-in exemption for a handler that
    /// types text nowhere and has to work at the menu: see <c>allowWhenChatUnknown</c> on
    /// <see cref="Keybinds.Register"/> (R35 finding 5).
    /// </summary>
    Unknown = 2,
}

internal interface IKeyInputSource
{
    /// <summary>False when there is no keyboard (server process, headless, pre-device-init).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Whether the player is typing in the game's chat box. Read from
    /// <c>ProjectM.UI.HUDChatWindow.IsChatFocused</c> (client <c>get_IsChatFocused</c>,
    /// backing field <c>_ChatIsFocused</c> at <c>0x234</c>).
    ///
    /// <para>
    /// <b>Never throws, and never guesses.</b> A window that cannot be found or cannot be read
    /// answers <see cref="ChatFocus.Unknown"/>, not <see cref="ChatFocus.NotFocused"/>: the
    /// caller's fail-closed rule (<see cref="Keybinds.Pump"/>, docs/modules.md)
    /// is only reachable if this seam can say "I do not know". Answering false there is what
    /// R34 finding 7 was: three documents promised fail-closed and the shipped source could not
    /// enter that branch.
    /// </para>
    /// </summary>
    ChatFocus ChatFocus { get; }

    /// <summary>
    /// Whether <b>any</b> text input field currently has keyboard focus — the game's, another
    /// mod's, or one of ours. Read from the UI event system's current selection
    /// (<c>UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject</c>, client)
    /// plus an <c>isFocused</c> read on the <c>TMPro.TMP_InputField</c> (<c>get_isFocused</c>)
    /// or legacy <c>UnityEngine.UI.InputField</c> component on it.
    ///
    /// <para>
    /// <b>Why this exists (R36 finding 5).</b> <see cref="ChatFocus"/> only ever looks for
    /// <c>ProjectM.UI.HUDChatWindow</c>, so it is blind by construction to every other text field
    /// in the game — and the main menu, which is precisely where <see cref="ChatFocus.Unknown"/>
    /// lives, is full of them: the character-name box, the direct-connect address, the server
    /// browser's filter, a private server's password. The <c>Unknown</c> exemption was argued from
    /// what the <i>handler</i> types; the gate exists for what the <i>player</i> is typing. This
    /// probe answers that question generically and gates <b>every</b> bind, exemptions included.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws.</b> Unlike <see cref="ChatFocus"/> this is two-state on purpose: "there is
    /// no EventSystem" and "nothing is selected" are both the same readable fact — no field has
    /// focus — and a read that does throw answers false rather than killing every bind for the
    /// session (the settings opener included, which is the failure FUI8 fixed). The in-world
    /// fail-closed guarantee stays with <see cref="ChatFocus"/>.
    /// </para>
    /// </summary>
    bool TextFieldFocused { get; }

    /// <summary>
    /// True on the frame <paramref name="chord"/>'s main key went down with its modifiers held.
    /// Must never throw.
    ///
    /// <para>
    /// <b>Modifiers are read per side</b> (<see cref="ModifierSides"/>) and handed to
    /// <see cref="KeyChord.MatchesHeld"/>, which is the whole of the firing rule. That is not
    /// decoration: Windows delivers <c>AltGr</c> as <c>LeftCtrl</c> + <c>RightAlt</c>, so a
    /// <c>Ctrl+Alt</c> chord on a printable key would otherwise fire while a German- or
    /// Nordic-layout player types <c>@</c> into the direct-connect address box (R37 finding 1).
    /// The Input System has no AltGr control to ask instead — <c>Key.AltGr</c> is an alias of
    /// <c>rightAlt</c> on the same bit (client metadata) — so the
    /// pair is the only evidence there is.
    /// </para>
    /// </summary>
    /// <param name="refusedAsAltGr">
    /// True when the main key did go down with exactly this chord's modifiers and the <i>only</i>
    /// reason it did not fire is the AltGr pair — <c>LeftCtrl</c> + <c>RightAlt</c> on a printable
    /// <c>Ctrl+Alt</c> chord. That refusal used to be invisible in all three places a player could
    /// notice it (R38 finding 4); reporting it here costs no extra device read and lets
    /// <see cref="Keybinds.Pump"/> log the reason and the alternative once per press. False
    /// whenever the method returns true.
    /// </param>
    bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr);

    /// <summary>
    /// Rebind flow: the first non-modifier key pressed this frame, with whatever modifiers are
    /// held. False when nothing was pressed.
    ///
    /// <para>
    /// <b>The physical sides come out with it</b>, and the source does <i>not</i> silently drop a
    /// keystroke it would refuse. Skipping an AltGr-shaped press here is what left the panel
    /// sitting in "press a key" with no log and no reason (R38 finding 4) — indistinguishable from
    /// a broken panel for the US- or UK-layout player for whom <c>LeftCtrl+RightAlt</c> is simply
    /// how they hold <c>Ctrl+Alt</c>. The decision belongs to <see cref="Keybinds"/>, which can
    /// end the flow, log, and put the reason on the row.
    /// </para>
    /// </summary>
    bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides);

    /// <summary>
    /// KEYS1: true while <paramref name="chord"/>'s main key is down (modifiers are not re-checked after
    /// the press, so letting go of Ctrl first does not end a hold). Drives while-held and release
    /// handlers. Never throws; false when it cannot tell. Defaulted so existing sources need no change.
    /// </summary>
    bool IsHeld(KeyChord chord) => false;

    /// <summary>
    /// PAD1: which gamepad buttons are down now, as a mask of <see cref="KeyChord.GamepadBit"/> bits, read from
    /// <c>Gamepad.current</c> (each button's <c>ButtonControl.isPressed</c>; the triggers by their press point). False
    /// when there is no gamepad. The gamepad key capture reads this once a pump (<see cref="GamepadCapture"/>). A gamepad
    /// chord's press and hold go through <see cref="WasPressedThisFrame"/> and <see cref="IsHeld"/> like any other chord.
    /// Never throws. Defaulted so existing sources need no change.
    /// </summary>
    bool TryReadGamepadHeld(out uint held)
    {
        held = 0;
        return false;
    }
}

/// <summary>Server process, Ui skipped, or no keyboard: nothing is ever pressed.</summary>
internal sealed class NullKeyInputSource : IKeyInputSource
{
    public static readonly NullKeyInputSource Instance = new();

    public bool IsAvailable => false;

    /// <summary>
    /// There is no chat window and no keyboard either, so nothing can fire: answering
    /// <see cref="ChatFocus.NotFocused"/> here is not a guess, and <see cref="IsAvailable"/>
    /// stops the pump before the gate is ever consulted.
    /// </summary>
    public ChatFocus ChatFocus => ChatFocus.NotFocused;

    /// <summary>No UI, no event system, no text field: nothing is focused.</summary>
    public bool TextFieldFocused => false;

    public bool WasPressedThisFrame(KeyChord chord, out bool refusedAsAltGr)
    {
        refusedAsAltGr = false;
        return false;
    }

    public bool TryReadPressedChord(out KeyChord chord, out ModifierSides sides)
    {
        chord = KeyChord.None;
        sides = ModifierSides.None;
        return false;
    }
}
