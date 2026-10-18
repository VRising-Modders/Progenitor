using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Progenitor.Ui;

/// <summary>
/// Client-side mod keybinds: a <see cref="KeyChord"/> per registration, backed by a plain
/// <c>string</c> entry in the owning plugin's <c>GUID.cfg</c> so the player can edit it with a
/// text editor and see it in <see cref="SettingsPanel"/>.
///
/// <para>
/// <b>Prior art.</b> The game has no seam for a mod bind in the vanilla controls screen:
/// <c>ProjectM.ButtonInputAction</c> is a compiled enum and the bindings come from the
/// <c>Settings/DefaultControls</c> asset (ui-input.md §4e), so mod binds live in our panel and are
/// polled by us. BepInEx's <c>KeyboardShortcut</c> supplies the value shape and the cfg format
/// (see <see cref="KeyChord"/>). No code is copied from it.
/// </para>
///
/// <para>
/// <b>Suppression.</b> A bind never fires while a mod panel is capturing gameplay input
/// (<see cref="UiInputCapture"/> refcount above zero with capture enabled) or while the game's
/// chat box is focused — otherwise typing "f" in chat would trigger every mod bound to F. A bind
/// registered with <c>allowWhileCaptured: true</c> is exempt from the first rule only; that is
/// what lets the settings panel's own key close the panel it opened. Chat <see cref="ChatFocus.Focused"/>
/// always wins, over every bind, both exemptions included.
/// </para>
///
/// <para>
/// <b>The <see cref="ChatFocus.Unknown"/> exemption (R35 finding 5).</b> <c>Unknown</c> does not
/// mean "the player might be typing": it means <i>no chat window exists in this scene</i> — the
/// main menu, the loading screen, the seconds before the HUD comes up. Failing closed there
/// suppressed the settings-panel opener too, so a player kicked back to the menu by a server whose
/// mods they declined could not press F10 to undo the install: the one place the panel is most
/// wanted was the one place it could not be opened. A registration may now opt in with
/// <c>allowWhenChatUnknown: true</c> and fire while no chat window can be read.
/// <b>The rule for opting in:</b> the handler must not type text anywhere — no chat send, no
/// console line, no text field it does not own — because <c>Unknown</c> is exactly the state in
/// which we cannot see a chat box. Opening a local panel qualifies; anything that could reach a
/// game text field does not. Core sets it on the settings opener; a plugin reaches it through
/// <c>ctx.Keybinds.RegisterOpener</c> (KEYOPEN1) and the map binds. Everything else still fails
/// closed, and <c>Focused</c> still suppresses the opt-in binds as well.
/// </para>
///
/// <para>
/// <b>What the exemption was still missing, and the two things that close it (R36 finding 5).</b>
/// The rule above is about what the <i>handler</i> types; the gate exists for what the <i>player</i>
/// is typing, and <see cref="ChatFocus"/> can only ever see <c>HUDChatWindow</c>. The main menu —
/// the very scene <c>Unknown</c> names — is full of TMP fields it is blind to: the character-name
/// box, the direct-connect address, the server-browser filter, a private server's password. So:
/// <list type="number">
/// <item><description>
/// <see cref="IKeyInputSource.TextFieldFocused"/> is a second, generic probe — the UI event
/// system's current selection carrying a focused input field — and it suppresses <b>every</b>
/// bind, the <c>allowWhenChatUnknown</c> ones included, and the rebind capture with them. The
/// <c>Unknown</c> exemption is an exemption from the <i>chat</i> probe and from nothing else.
/// </description></item>
/// <item><description>
/// An exempt bind may not sit on a chord a player could type. A chord whose main key is one a text
/// field would turn into a character and that does not carry <c>Ctrl</c> or <c>Alt</c> — or that
/// carries <c>Ctrl</c> and <c>Alt</c> together, which is AltGr on a non-US layout —
/// (<see cref="KeyChord.IsSafeWhileTyping"/>) is refused for such a bind, at
/// <see cref="Register"/> and in <see cref="Rebind"/> alike, with a line saying why and what to do
/// — because no probe covers a field we have not thought of, and a bind on <c>P</c> that fires at
/// the menu is one keystroke away from opening the panel over a half-typed name.
/// </description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>When Unknown is the real failure.</b> No chat window while there <i>is</i> a client world is
/// not the menu — it is a HUD lookup that has stopped working, and every non-exempt bind is dead
/// for the session. That case logs one warning
/// (<see cref="ChatUnknownWithClientWorldLog"/>) after
/// <see cref="ChatUnknownWorldPumps"/> consecutive unknown polls, so it is visible in a log
/// instead of looking like a quiet main menu.
/// </para>
///
/// <para>
/// <b>Lifetime.</b> Every registration is an <see cref="IDisposable"/> that
/// <c>PluginContext.Track</c> records, so plugin unload drops it; <see cref="Uninstall"/> drops
/// everything on a Core swap. Nothing here may outlive a collectible generation: the config entry
/// is <c>ConfigEntry&lt;string&gt;</c> (a BCL type, so it cannot pin Core through
/// <c>TomlTypeConverter</c>) and the frame subscription is removed with the last bind.
/// </para>
/// </summary>
public static class Keybinds
{
    /// <summary>Section every keybind is bound under in the owning plugin's cfg.</summary>
    public const string Section = "Keybinds";

    /// <summary>Appended to each keybind entry's cfg description so the format is self-documenting.</summary>
    public const string FormatHint =
        "Key chord, e.g. \"F10\", \"F10 + Ctrl\", \"K + Ctrl + Shift\". \"None\" unbinds it. "
        + "Key names are UnityEngine.InputSystem.Key names; modifiers are Ctrl / Shift / Alt.";

    private static readonly object Gate = new();
    private static readonly List<Registration> Live = new();
    private static IKeyInputSource _source = NullKeyInputSource.Instance;
    private static bool _pumpArmed;
    private static Registration? _rebinding;

    /// <summary>KEYS1: the Keybinds tab's key capture (<see cref="BeginCapture"/>), or null.</summary>
    private static Action<KeyCaptureResult>? _capture;

    /// <summary>PAD1: the capture waits for a controller chord (<see cref="BeginCapture(Action{KeyCaptureResult}, bool)"/>).</summary>
    private static bool _captureGamepad;

    /// <summary>PAD1: the controller capture's gesture state while <see cref="_captureGamepad"/>.</summary>
    private static GamepadCapture? _gamepadCapture;

    /// <summary>PAD1: true while the Keybinds tab waits for a controller button.</summary>
    internal static bool CapturingGamepad
    {
        get { lock (Gate) return _capture != null && _captureGamepad; }
    }

    internal const string NoGamepadReason = "No controller is connected. Connect one and press the button, or press Escape to cancel.";

    /// <summary>
    /// PAD1 review: a controller capture ends after this long with no button down (it takes B as a key, so B cannot
    /// cancel it). Each button held restarts it.
    /// </summary>
    internal const int GamepadCaptureTimeoutMs = 5000;

    internal const string GamepadCaptureTimedOutReason = "No controller button was pressed within 5 seconds, so the key was left as it was.";

    /// <summary>
    /// KEYS8: the left and right buttons a capture may take, each set once the capture has seen that button up.
    /// The click that started the capture (a row's button, a Keys row) is still going down or coming up when it
    /// begins, so it can never be read as the new key.
    /// </summary>
    private static bool _leftArmed, _rightArmed;

    /// <summary>
    /// HOLDMOD1: the capture's lone-modifier state (<see cref="TakeLoneModifier"/>): armed once every modifier has been
    /// seen up since the capture began, the modifiers held since then, and whether a key or button went down meanwhile.
    /// </summary>
    private static bool _loneArmed, _loneSpoiled;

    private static KeyModifiers _loneUnion;

    /// <summary>HOLDMOD1 review: whether the game window had focus at the last pump (<see cref="IKeyInputSource.WindowFocused"/>).</summary>
    private static bool _windowFocused = true;

    internal const string FocusLostReleaseLog = "Ui: the game window lost focus; held keybinds were released.";

    /// <summary>
    /// KEYS8: the left or right button a capture just took, until it has been released and two more pumps have
    /// run. While set, clicks on Progenitor's own widgets are dropped (<see cref="SwallowsUiClicks"/>), so the
    /// click that chose the key does not also press whatever button was under the pointer.
    /// </summary>
    private static UiKey _swallowButton;

    /// <summary>Pumps since <see cref="_swallowButton"/> was seen released; -1 while it is still held.</summary>
    private static int _swallowReleasedPumps = -1;

    /// <summary>When the swallow began (<see cref="Clock"/>); it never outlasts <see cref="SwallowMaxMs"/>.</summary>
    private static long _swallowSince;

    /// <summary>
    /// KEYS8: how long a swallow outlives the last pump that read its button held. While the button reads held the
    /// swallow lasts (a long hold must not restart the capture); this only ends one whose button stopped being read.
    /// </summary>
    internal const long SwallowMaxMs = 3000;

    /// <summary>When a capture last ended on Escape (<see cref="Clock"/>), for <see cref="EscapeBelongsToCapture"/>.</summary>
    private static long _escapeCancelledAt = long.MinValue;

    /// <summary>How long after a capture ended on Escape that press still belongs to it.</summary>
    internal const long EscapeGraceMs = 400;

    /// <summary>
    /// KEYS3 polish (user report 2026-09-30): Escape during a key capture closed the whole Mods workspace.
    /// True while any capture waits (the Keybinds tab's, a mod page Keys row's or the settings panel's
    /// rebind), and for <see cref="EscapeGraceMs"/> after one ended on Escape, because the capture's pump
    /// and the panel Escape hook see the same press in either order. The hide sweep and the search clear
    /// stand down while this is true: Escape cancels the capture and nothing else.
    /// </summary>
    internal static bool EscapeBelongsToCapture
    {
        get
        {
            lock (Gate)
            {
                if (_capture != null || _rebinding != null) return true;
                if (_escapeCancelledAt == long.MinValue) return false;
                long now;
                try { now = Clock(); }
                catch { return false; }
                long since = now - _escapeCancelledAt;
                return since >= 0 && since <= EscapeGraceMs;
            }
        }
    }

    /// <summary>When the current rebind or capture started (<see cref="Clock"/> ms).</summary>
    private static long _waitStarted;

    /// <summary>
    /// Review 2026-09-29: a rebind or capture nobody finishes is canceled after this long, so every bind
    /// (the Mods-menu opener included) comes back on its own.
    /// </summary>
    internal const int CaptureTimeoutMs = 10000;

    internal const string CaptureTimedOutReason = "No key was pressed within 10 seconds, so the key was left as it was.";

    /// <summary>
    /// KEYS2 review: a refused key restarts the 10 s wait (the player is still trying), but one capture never
    /// lasts longer than this in total, so refusals cannot keep every bind suppressed indefinitely.
    /// </summary>
    internal const int CaptureMaxMs = 30000;

    internal const string CaptureMaxReason = "No usable key was chosen within 30 seconds, so the key was left as it was.";

    /// <summary>When the current rebind or capture began (<see cref="Clock"/> ms); refusals do not move it.</summary>
    private static long _waitBegan;

    /// <summary>Milliseconds; a seam so tests can move time.</summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// The array <see cref="Pump"/> walks, rebuilt only when the registry changes. <c>Pump</c>
    /// runs on every client frame from install onward (the settings panel's own opener is always
    /// registered), and <c>Live.ToArray()</c> there allocated one array per frame for the whole
    /// session — 60 garbage arrays a second that nothing else ever reads (R34 finding 16). The
    /// array is never handed out and never mutated, so sharing it across frames is safe.
    /// </summary>
    private static Registration[]? _pumpSnapshot;

    /// <summary>
    /// True while the last chat-focus read answered <see cref="ChatFocus.Unknown"/>, so the
    /// fail-closed line is logged on the transition into that state rather than once a frame.
    /// </summary>
    private static bool _chatUnknown;

    /// <summary>Consecutive polls that answered <see cref="ChatFocus.Unknown"/>.</summary>
    private static int _chatUnknownPumps;

    /// <summary>
    /// True while the last poll saw a focused text field, so the line is logged on the transition
    /// rather than once a frame (a player types a server address for seconds at a time).
    /// </summary>
    private static bool _textFieldFocused;

    /// <summary>True once <see cref="ChatUnknownWithClientWorldLog"/> was logged for this episode.</summary>
    private static bool _chatUnknownWorldLogged;

    /// <summary>
    /// How the pump asks whether a client world exists, so <see cref="Keybinds"/> stays game-free
    /// and tests stay hermetic (they replace it rather than moving the real epoch). Reads
    /// <see cref="Progenitor.WorldState.ClientWorldEpoch.ObservedSequence"/>, which is 0 whenever
    /// there is no client world — i.e. exactly the menu and loading-screen case where an absent
    /// chat window is expected.
    /// </summary>
    private static Func<bool> _clientWorldPresent = DefaultClientWorldPresent;

    /// <summary>
    /// Consecutive unknown polls before the "this is not the menu" warning. ~5 s at 60 fps: long
    /// enough that a world change, a loading screen, or the one-second window-lookup interval in
    /// <c>UnityKeyInputSource</c> never trips it.
    /// </summary>
    internal const int ChatUnknownWorldPumps = 300;

    /// <summary>
    /// Raised after a press-a-key flow ends — bound, canceled, or refused — so an open panel can
    /// refresh its row and leave "press a key".
    ///
    /// <para>
    /// It carries a <see cref="RebindResult"/> rather than a bare id because "the flow ended and
    /// the bind is unchanged" is not a message a player can act on. A refusal has a reason and a
    /// suggested alternative, and the row is the only place they will see it (R38 finding 4).
    /// </para>
    /// </summary>
    public static event Action<RebindResult>? RebindFinished;

    /// <summary>
    /// KEYS2: a key pressed during a press-a-key flow was refused (reserved by Windows or Steam, a Windows-key
    /// shortcut, or a key something else has) and the flow is <b>still waiting</b>. The result is
    /// <see cref="RebindOutcome.Refused"/> with <see cref="RebindResult.StillCapturing"/> true and a row-sized
    /// <see cref="RebindResult.Reason"/>. Kept apart from <see cref="RebindFinished"/> so a subscriber written
    /// before KEYS2 still sees exactly one result per flow, at its end.
    /// </summary>
    public static event Action<RebindResult>? RebindRefused;

    private static void RaiseRefused(RebindResult result)
    {
        var handler = RebindRefused;
        if (handler == null) return;
        try { handler(result); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind RebindRefused threw: " + e.Message); }
    }

    /// <summary>
    /// KEYS1: bumped on every registration, rebind and removal, so a cached winner picture
    /// (<see cref="KeybindArbiter.Current"/>) is never older than the registry.
    /// </summary>
    internal static int Version => System.Threading.Volatile.Read(ref _version);

    private static int _version;

    private static void Bump() => System.Threading.Interlocked.Increment(ref _version);

    /// <summary>
    /// Something the Keybinds tab draws from changed without a bind changing (a <see cref="KeybindGroups"/>
    /// group defined or removed): bump <see cref="Version"/> so the shown tab rebuilds.
    /// </summary>
    internal static void NoteViewChanged() => Bump();

    /// <summary>How many binds are registered right now. Diagnostics and tests.</summary>
    public static int Count
    {
        get { lock (Gate) return Live.Count; }
    }

    /// <summary>The id currently waiting for a key press, or null.</summary>
    public static string? RebindingId
    {
        get { lock (Gate) return _rebinding?.Id; }
    }

    /// <summary>One registration line.</summary>
    public static string RegisteredLog(string pluginId, string id, KeyChord chord, string description) =>
        "Ui: keybind '" + pluginId + "/" + id + "' bound to " + chord
        + (string.IsNullOrWhiteSpace(description) ? "" : " (" + description.Trim() + ")");

    /// <summary>
    /// One line per conflict, naming both owners. Not an error: two plugins on one key is the
    /// player's to fix. KEYS1 (decided 2026-09-29): only the winner fires — the game's own key,
    /// else the mod installed first (<see cref="KeybindArbiter"/>) — and the other is not active until
    /// one of them is changed. <see cref="NotActiveLog"/> names the loser and why.
    /// </summary>
    public static string ConflictLog(KeyChord chord, string existing, string added) =>
        "Ui: keybind conflict on " + chord + ": '" + existing + "' and '" + added
        + "' are both bound to it. Only the one installed first fires; the other is not active until "
        + "one of them is changed in Mods > Keybinds, the mod's Keys section or the cfg.";

    /// <summary>KEYS1: a Progenitor bind that loses its chord, with the row's sentence.</summary>
    public static string NotActiveLog(string label, string reason) =>
        "Ui: keybind '" + label + "' is not active: " + reason;

    /// <summary>KEYS1: logged once per press of a bind that is not active, so a dead key is explained.</summary>
    public static string NotFiredLog(string label, string reason) =>
        "Ui: keybind '" + label + "' was pressed but not fired: " + reason;

    /// <summary>
    /// KEYS1: asked on a press edge — the reason this bind must not fire (it lost its chord to the game
    /// or to a mod installed first), or null. A seam so tests can pin it; production is
    /// <see cref="KeybindArbiter.BlockedReason"/>.
    /// </summary>
    internal static Func<string, string, string?> WinnerGate { get; set; } = KeybindArbiter.BlockedReason;

    /// <summary>Unparseable cfg value: keep the default and say so rather than silently unbinding.</summary>
    public static string BadValueLog(string pluginId, string id, string? raw, KeyChord fallback) =>
        "Ui: keybind '" + pluginId + "/" + id + "' has an unreadable value '" + (raw ?? "")
        + "' in the cfg; using " + fallback + " instead. " + FormatHint;

    /// <summary>
    /// The fail-closed line. Logged on the transition into <see cref="ChatFocus.Unknown"/> and
    /// again when a window becomes readable, never per frame.
    /// </summary>
    public static string ChatUnknownLog =>
        "Ui: no readable chat window (main menu, loading screen, or the HUD is not up yet); "
        + "keybinds are suppressed until one can be read. Failing closed is deliberate: an "
        + "unreadable chat window cannot prove the player is not typing, and firing a bind into a "
        + "chat line is worse than missing one. Binds registered with allowWhenChatUnknown (the "
        + "settings panel opener) still fire, so the panel is reachable from the main menu.";

    /// <summary>
    /// The text-field gate's line, logged on the transition into "something has focus" (R36
    /// finding 5). Unlike the chat gate this one admits no exemption, so it names no survivors.
    /// </summary>
    public static string TextFieldFocusedLog =>
        "Ui: a text input field has keyboard focus (a chat box, a name, address or password field, "
        + "or a mod panel's own field); every keybind is suppressed until it loses focus, the "
        + "settings panel opener included. The chat gate cannot see the main menu's fields, so "
        + "this is the probe that covers them.";

    /// <summary>
    /// The one that matters. No readable chat window while a client world exists is not the menu:
    /// the HUD lookup has failed, and every bind that did not opt in is dead until it recovers.
    /// Logged once per unknown episode, as a warning.
    /// </summary>
    public static string ChatUnknownWithClientWorldLog =>
        "Ui: no readable chat window for " + ChatUnknownWorldPumps + " consecutive frames while a "
        + "client world exists — this is not the main menu, so the HUDChatWindow lookup is failing. "
        + "Keybinds other than the settings panel opener stay suppressed until it recovers. Please "
        + "report this with the lines above.";

    /// <summary>
    /// The refusal line for a bare printable chord on a bind that fires while no chat window can
    /// be read (R36 finding 5). Says the rule, the chord, and the fix, because the player who sees
    /// it typed the key on purpose and has to know what to press instead.
    /// </summary>
    public static string PrintableChordRefusedLog(string label, KeyChord chord, string outcome) =>
        "Ui: keybind '" + label + "' cannot be bound to " + chord
        + ". This bind fires at the main menu and on loading screens, where Progenitor cannot see "
        + "the game's text fields, so a plain character key would type into a name, address or "
        + "password box and trigger the bind at the same time. Add Ctrl or Alt (e.g. \""
        + chord.Key + KeyChord.Separator + "Ctrl\"), or use a function key. Shift alone is not "
        + "enough — Shift is how capitals are typed — and Ctrl+Alt with a printable key is AltGr "
        + "on German, French, Polish and Nordic layouts, so prefer Ctrl+Shift or a function key. "
        + outcome;

    /// <summary>
    /// The limitation and the alternative for an AltGr-shaped chord — a printable key wanted with
    /// both <c>Ctrl</c> and <c>Alt</c> (R38 finding 4).
    ///
    /// <para>
    /// The chord is bindable and mostly works: it fires on <c>LeftCtrl+LeftAlt</c> and on either
    /// right-hand pairing. It is the <c>LeftCtrl+RightAlt</c> hand position that never fires it,
    /// because Windows delivers <c>AltGr</c> as exactly those two keys and there is no AltGr
    /// control to ask instead (<see cref="KeyChord.IsAltGrPair"/>, ui-input.md §16.2) — so on a
    /// German, French, Polish or Nordic layout that keystroke is the player typing <c>@</c>, and
    /// on a US or UK layout it is the most natural way to hold <c>Ctrl+Alt</c>. We cannot read the
    /// layout, so the refusal stands for both and this sentence is what makes it survivable.
    /// </para>
    /// </summary>
    public static string AltGrAlternative(KeyChord chord) =>
        "Windows delivers AltGr as LeftCtrl+RightAlt, so " + chord + " does not fire when Ctrl and "
        + "Alt are held that way — on German, French, Polish and Nordic layouts that keystroke "
        + "types a character. Hold LeftCtrl+LeftAlt instead (it fires normally), or rebind to \""
        + chord.Key + KeyChord.Separator + "Ctrl" + KeyChord.Separator + "Shift\" or a function key.";

    /// <summary>
    /// Logged once per press when a bound chord's key went down and only the AltGr pair stopped it
    /// firing. Silence here was half of R38 finding 4: the bind was not broken, it was broken for
    /// one hand position, and nothing said so.
    /// </summary>
    public static string AltGrNotFiredLog(string label, KeyChord chord) =>
        "Ui: keybind '" + label + "' was not fired: " + AltGrAlternative(chord)
        + " Logged once per press, not per frame.";

    /// <summary>
    /// The rebind capture's refusal, long form. The flow ends here — the panel is told, so it
    /// leaves "press a key" instead of looking broken (R38 finding 4).
    /// </summary>
    public static string AltGrRebindRefusedLog(string label, KeyChord chord) =>
        "Ui: keybind '" + label + "' was not rebound to " + chord
        + ": that keystroke was LeftCtrl+RightAlt, which cannot be told apart from AltGr. "
        + AltGrAlternative(chord) + " The bind is unchanged; press another combination.";

    /// <summary>The same refusal, row-sized, for <see cref="RebindResult.Reason"/>.</summary>
    public static string AltGrRebindRefusedReason(KeyChord chord) =>
        "Not bound: that was LeftCtrl+RightAlt, which cannot be told apart from AltGr. "
        + AltGrAlternative(chord);

    /// <summary>
    /// True when <paramref name="chord"/> may be given to a bind carrying
    /// <c>allowWhenChatUnknown</c>: a non-printable main key, or a printable one held with
    /// <c>Ctrl</c> or <c>Alt</c> but not both (Shift alone types capitals; Ctrl+Alt is AltGr).
    /// See <see cref="KeyChord.IsSafeWhileTyping"/>.
    /// </summary>
    public static bool MayBindWhenChatUnknown(KeyChord chord) => chord.IsSafeWhileTyping;

    // ---- PAD1: controller chords ----------------------------------------------------------------

    /// <summary>PAD1: the cfg key of a bind's controller chord, next to its keyboard entry: <c>&lt;id&gt;.Gamepad</c>.</summary>
    public const string GamepadEntrySuffix = ".Gamepad";

    /// <summary>PAD1: appended to a controller entry's cfg description.</summary>
    public const string GamepadFormatHint =
        "Controller button, e.g. \"GamepadNorth\", or \"GamepadNorth + GamepadDpadDown\" to press the first while holding "
        + "the second. Buttons: GamepadSouth/East/West/North, GamepadLeftShoulder/RightShoulder, GamepadLeftTrigger/RightTrigger, "
        + "GamepadDpadUp/Down/Left/Right, GamepadLeftStick/RightStick (pressed in), GamepadStart, GamepadSelect. \"None\" unbinds it.";

    internal const string NotGamepadReason = "That is not a controller button.";

    internal const string KeyNotGamepadReason = "A controller button goes in the bind's controller key, not its keyboard key.";

    /// <summary>Main thread only (the pump and the bridge's dispatch); saved and restored around each handler.</summary>
    private static bool _actingGamepad;

    /// <summary>
    /// PAD1: true while a bind's press, while-held or release handler runs because of its controller chord, false for its
    /// keyboard or mouse chord (and outside handlers). A handler that reads a pointer can read the game's controller
    /// cursor instead when this is true (the map share does).
    /// </summary>
    public static bool PressedWithGamepad => _actingGamepad;

    /// <summary>PAD1: the winner rule for a controller chord on a press. Production: <see cref="KeybindArbiter.GamepadBlockedReason"/>.</summary>
    internal static Func<string, string, string?> GamepadWinnerGate { get; set; } = KeybindArbiter.GamepadBlockedReason;

    /// <summary>PAD1: the cfg key of <paramref name="id"/>'s controller entry.</summary>
    public static string GamepadEntryKey(string id) => (id ?? "").Trim() + GamepadEntrySuffix;

    /// <summary>
    /// PAD1: bind <paramref name="key"/>'s controller entry when the bind has a controller default, when the cfg already
    /// holds a value for it (a saved choice survives a reload) or when <paramref name="force"/> (the player just chose one).
    /// Otherwise no entry is written, so a cfg gains a controller line only for a bind that uses one. Null when not bound.
    /// </summary>
    private static ConfigEntry<string>? BindGamepadEntry(ConfigFile? file, string plugin, string key, string description, KeyChord padDefault, bool force)
    {
        if (file == null) return null;
        var definition = new ConfigDefinition(Section, GamepadEntryKey(key));
        if (!force && !padDefault.IsBound)
        {
            bool saved = false;
            try
            {
                saved = file.ContainsKey(definition)
                    || (Progenitor.Config.ConfigHelper.GetOrphanedEntries(file)?.Contains(definition) ?? false);
            }
            catch { saved = false; }
            if (!saved) return null;
        }

        try
        {
            return file.Bind(Section, GamepadEntryKey(key), padDefault.ToString(),
                (string.IsNullOrWhiteSpace(description) ? "" : description.Trim() + " ") + GamepadFormatHint);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind '" + plugin + "/" + key + "' could not bind its controller entry: " + e.Message
                + "; its controller button stays " + padDefault + " and is not editable in the cfg.");
            return null;
        }
    }

    /// <summary>PAD1: a controller entry's chord: a gamepad chord or None; anything else keeps <paramref name="fallback"/> and says so.</summary>
    private static KeyChord ReadGamepadChord(ConfigEntry<string>? entry, string pluginId, string id, KeyChord fallback)
    {
        if (entry == null) return fallback;
        string? raw;
        try { raw = entry.Value; }
        catch { return fallback; }
        if (KeyChord.TryParse(raw, out var chord) && (!chord.IsBound || chord.IsGamepad)) return chord;
        Plugin.Logger?.LogWarning("Ui: keybind '" + pluginId + "/" + id + "' has an unreadable controller value '" + (raw ?? "")
            + "' in the cfg; using " + fallback + " instead. " + GamepadFormatHint);
        return fallback;
    }

    /// <summary>
    /// PAD1: give a registered bind a controller default (<see cref="KeyChord.Gamepad"/>; <see cref="KeyChord.None"/> for
    /// none). The player's saved choice in <c>[Keybinds] &lt;id&gt;.Gamepad</c> wins over it. The bind then fires on either
    /// its keyboard or mouse chord or its controller chord, through the same gates, scope and winner rule (the controller
    /// chord is checked against the game's controller bindings and the other binds' controller chords).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="padDefault"/> is bound and not a controller chord.</exception>
    public static IKeybindHandle SetGamepadDefault(IKeybindHandle handle, KeyChord padDefault)
    {
        if (handle is not Registration reg) throw new ArgumentException("Not a Progenitor keybind handle.", nameof(handle));
        if (padDefault.IsBound && !padDefault.IsGamepad)
            throw new ArgumentException(NotGamepadReason + " (" + padDefault + ")", nameof(padDefault));
        if (reg.Disposed) return reg;

        var entry = reg.GamepadEntry ?? BindGamepadEntry(reg.File, reg.PluginId, reg.Id, reg.Description, padDefault, force: false);
        var chord = ReadGamepadChord(entry, reg.PluginId, reg.Id, padDefault);
        lock (Gate)
        {
            reg.DefaultGamepadChord = padDefault;
            reg.GamepadEntry = entry;
            reg.GamepadChord = chord;
            _pumpSnapshot = null;
            Bump();
        }

        if (chord.IsBound)
            Plugin.Logger?.LogInfo("Ui: keybind '" + reg.Label + "' controller button " + chord + ".");
        return reg;
    }

    /// <summary>
    /// PAD1: store a new controller chord for a bind (the Keybinds tab's controller key). Refused with a row-sized
    /// <paramref name="reason"/> when it is not a controller chord, or when something it would meet already has it: one
    /// of the game's controller bindings that stops it in the bind's scope, or another Progenitor bind's controller chord
    /// (a remap never creates a conflict). <see cref="KeyChord.None"/> (unbind) and the chord it has are always accepted.
    /// The <c>&lt;id&gt;.Gamepad</c> entry is written (and bound first, when the bind had none).
    /// </summary>
    internal static bool TryRebindGamepad(string? pluginId, string id, KeyChord chord, bool reset, out string? reason)
    {
        reason = null;
        var target = FindLive(pluginId, id);
        if (target == null)
        {
            reason = NotRegisteredReason;
            return false;
        }

        if (chord.IsBound && !chord.IsGamepad) reason = NotGamepadReason;
        else if (chord.IsBound && chord != target.GamepadChord) reason = GamepadClashReason(target.PluginId, target.Id, target.Scope, chord, reset);

        if (reason == null)
        {
            lock (Gate)
            {
                if (target.Disposed) reason = NotRegisteredReason;
                else
                {
                    target.GamepadChord = chord;
                    _pumpSnapshot = null;
                    Bump();
                }
            }
        }

        if (reason != null)
        {
            Plugin.Logger?.LogWarning("Ui: keybind '" + target.Label + "' controller button was not changed to " + chord + ": " + reason);
            Raise(new RebindResult(target.Id, RebindOutcome.Refused, reason));
            return false;
        }

        try
        {
            target.GamepadEntry ??= BindGamepadEntry(target.File, target.PluginId, target.Id, target.Description, target.DefaultGamepadChord, force: true);
            target.GamepadEntry?.SetSerializedValue(chord.ToString());
            Persist(target.GamepadEntry);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind '" + target.Label + "' controller button set to " + chord + " in memory but the cfg write threw: " + e.Message);
        }

        Plugin.Logger?.LogInfo("Ui: keybind '" + target.Label + "' controller button set to " + chord + ".");
        Raise(new RebindResult(target.Id, RebindOutcome.Bound));
        return true;
    }

    /// <summary>
    /// PAD1: why a bind of <paramref name="scope"/> may not take controller chord <paramref name="chord"/>, or null. The
    /// holder is named as the keyboard guard names one (<see cref="KeybindRemapGuard.ClashReason"/>). Fails closed.
    /// </summary>
    internal static string? GamepadClashReason(string pluginId, string id, KeybindScope scope, KeyChord chord, bool reset = false)
    {
        if (!chord.IsBound) return null;
        if (!chord.IsGamepad) return NotGamepadReason;
        try
        {
            KeybindArbiter.RefreshIfStale();
            var holder = KeybindArbiter.GamepadHolderOf(pluginId, id, chord, scope);
            if (holder == null) return null;
            return reset ? KeybindRemapGuard.ResetClashReason(chord, holder) : KeybindRemapGuard.ClashReason(chord, holder);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: controller button check threw: " + e.Message + "; the button is refused.");
            return KeybindRemapGuard.CheckFailedReason;
        }
    }

    /// <summary>PAD1: whether a gamepad is connected now (the key source reads one). False on the server and in tests without one.</summary>
    internal static bool GamepadConnected
    {
        get
        {
            IKeyInputSource source;
            lock (Gate) source = _source;
            try { return source.GamepadPresent; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Register a keybind. Returns a handle whose <c>Dispose</c> unregisters it; the config entry
    /// itself is left in the cfg (the player's value survives a reload).
    /// </summary>
    /// <param name="owner">Owning plugin instance, so unload can sweep by owner.</param>
    /// <param name="pluginId">Owning plugin GUID; namespaces the id in logs and the panel.</param>
    /// <param name="id">Stable key inside <see cref="Section"/>. Also the cfg key.</param>
    /// <param name="file">The owning plugin's <c>GUID.cfg</c>.</param>
    /// <param name="defaultKey">Default chord, used when the cfg has no value or a bad one.</param>
    /// <param name="description">Shown in the cfg and in the settings panel.</param>
    /// <param name="onPressed">Invoked on the frame the chord goes down. Exceptions are logged, never rethrown.</param>
    /// <param name="allowWhileCaptured">True exempts this bind from panel-capture suppression (focused chat still wins).</param>
    /// <param name="allowWhenChatUnknown">
    /// True lets this bind fire while no chat window can be read at all — the main menu, the
    /// loading screen, the frames before the HUD is up (R35 finding 5). Only for a handler that
    /// types text nowhere: <c>Unknown</c> is precisely the state in which a chat box would be
    /// invisible to us. A focused chat window still suppresses it — and so does <b>any</b> focused
    /// text field (<see cref="IKeyInputSource.TextFieldFocused"/>), which is the probe the
    /// exemption does not reach past. A bare printable chord is refused here (R36 finding 5): see
    /// <see cref="PrintableChordRefusedLog"/> for what happens to the value and to the exemption.
    /// </param>
    public static IKeybindHandle Register(
        object? owner,
        string pluginId,
        string id,
        ConfigFile file,
        KeyChord defaultKey,
        string description,
        Action onPressed,
        bool allowWhileCaptured = false,
        bool allowWhenChatUnknown = false)
    {
        if (onPressed == null) throw new ArgumentNullException(nameof(onPressed));
        return Register(owner, pluginId, id, file, defaultKey, description, onPressed, null, null,
            allowWhileCaptured, allowWhenChatUnknown);
    }

    /// <summary>
    /// Register a keybind with press, hold and release handlers (KEYS1 API completeness). At least
    /// one handler is required. <paramref name="whileHeld"/> runs on every frame after the press
    /// frame while the main key stays down; <paramref name="onReleased"/> runs once when it comes up —
    /// or when the bind stops being allowed to fire mid-hold (a panel takes input, chat or a text
    /// field gets focus, a key capture starts), so a held action never sticks. A bind that is not
    /// active (it lost its chord, <see cref="KeybindArbiter"/>) starts no hold.
    /// </summary>
    public static IKeybindHandle Register(
        object? owner,
        string pluginId,
        string id,
        ConfigFile file,
        KeyChord defaultKey,
        string description,
        Action? onPressed,
        Action? whileHeld,
        Action? onReleased,
        bool allowWhileCaptured = false,
        bool allowWhenChatUnknown = false) =>
        RegisterScoped(owner, pluginId, id, file, defaultKey, description, onPressed, whileHeld, onReleased,
            allowWhileCaptured, allowWhenChatUnknown, KeybindScope.Gameplay);

    /// <summary>
    /// KEYS4: register a bind with a scope. <see cref="KeybindScope.Menu"/> fires only while the Mods
    /// workspace is open (<see cref="MenuScopeOpen"/>) and clashes only with other menu binds and the game's
    /// menu keys (<see cref="KeybindArbiter.ScopesMeet"/>). Core only: the navigation keys.
    /// </summary>
    internal static IKeybindHandle RegisterScoped(
        object? owner,
        string pluginId,
        string id,
        ConfigFile file,
        KeyChord defaultKey,
        string description,
        Action? onPressed,
        Action? whileHeld,
        Action? onReleased,
        bool allowWhileCaptured,
        bool allowWhenChatUnknown,
        KeybindScope scope)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Keybind id is required.", nameof(id));
        if (file == null) throw new ArgumentNullException(nameof(file));
        if (onPressed == null && whileHeld == null && onReleased == null)
            throw new ArgumentNullException(nameof(onPressed), "A keybind needs at least one of onPressed, whileHeld or onReleased.");

        string plugin = string.IsNullOrWhiteSpace(pluginId) ? "unowned" : pluginId.Trim();
        string key = id.Trim();
        string desc = description ?? "";

        // PAD1: a controller chord given as the default is the bind's controller default; its keyboard key is unbound.
        var padDefault = KeyChord.None;
        if (defaultKey.IsGamepad)
        {
            padDefault = defaultKey;
            defaultKey = KeyChord.None;
        }

        ConfigEntry<string> entry;
        try
        {
            entry = file.Bind(
                Section,
                key,
                defaultKey.ToString(),
                (desc.Length == 0 ? "" : desc.Trim() + " ") + FormatHint);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: keybind '" + plugin + "/" + key + "' could not bind its config entry: " + e.Message
                + "; it stays on its default " + defaultKey + " and is not editable in the cfg.");
            entry = null!;
        }

        var chord = ReadChord(entry, plugin, key, defaultKey);
        string label = plugin + "/" + key;
        if (chord.IsGamepad)
        {
            // PAD1: the keyboard entry holds keys and mouse buttons; a controller button goes in <id>.Gamepad.
            Plugin.Logger?.LogWarning("Ui: keybind '" + label + "': " + KeyNotGamepadReason + " Using " + defaultKey
                + " for the keyboard key; put " + chord + " in [" + Section + "] " + GamepadEntryKey(key) + ".");
            chord = defaultKey;
        }

        // KEYS2: a chord Windows or Steam takes first never becomes a bind. A cfg value on one falls back
        // to the default; a default on one (or a cfg value equal to it) is registered unbound. The
        // replacement is written back to the cfg once, so the next start reads a usable value and neither
        // the log line nor the problem (ReservedKeySource, dismissible) comes back.
        ReservedNote? reserved = null;
        if (ReservedChords.Find(chord, out var hit))
        {
            bool fromCfg = chord != defaultKey;
            var replacement = fromCfg && !ReservedChords.IsReserved(defaultKey) ? defaultKey : KeyChord.None;
            reserved = new ReservedNote(chord, hit.By, fromCfg, replacement);
            chord = replacement;
            string written = "";
            if (entry != null)
            {
                try
                {
                    entry.SetSerializedValue(replacement.ToString());
                    Persist(entry);
                    written = " The cfg now holds " + replacement + ".";
                }
                catch (Exception e) { written = " The cfg could not be rewritten (" + e.Message + ")."; }
            }

            Plugin.Logger?.LogWarning(
                "Ui: keybind '" + label + "': " + ReservedChords.RegistrationLog(KeybindArbiter.DisplayNameOf(plugin), reserved.Chord, hit.By)
                + " (" + reserved.Sentence + ")" + written);
        }

        // KEYS8: a bare left or right click (or a click in a scope where it presses something) never becomes a
        // bind. A cfg value on one falls back to the default; a default on one is registered unbound.
        if (MouseChords.Refusal(chord, scope) is { } clickRefusal)
        {
            var instead = chord != defaultKey && MouseChords.Allowed(defaultKey, scope) && !ReservedChords.IsReserved(defaultKey)
                ? defaultKey
                : KeyChord.None;
            Plugin.Logger?.LogWarning(ClickChordRefusedLog(label, chord, clickRefusal, instead));
            chord = instead;
        }

        // HOLDMOD1: Ctrl, Shift or Alt on its own is a hold key only. A cfg value on one falls back to the default; a
        // press bind's default on one is registered unbound.
        bool hold = whileHeld != null || onReleased != null;
        if (LoneModifierRefusal(chord, hold, scope) != null)
        {
            var instead = chord != defaultKey && LoneModifierRefusal(defaultKey, hold, scope) == null
                && MouseChords.Allowed(defaultKey, scope) && !ReservedChords.IsReserved(defaultKey)
                ? defaultKey
                : KeyChord.None;
            Plugin.Logger?.LogWarning(LoneModifierRefusedLog(label, chord, instead));
            chord = instead;
        }

        // R36 finding 5. An exempt bind fires in the states where no chat window can be read, so
        // its chord must not be one the player could be typing into a field we cannot see. The
        // player's cfg value is refused back to the default; a default that is itself a bare
        // printable is the plugin author's mistake, and the exemption — not the key — is what is
        // dropped, so the bind still works and only stops firing at the menu.
        if (allowWhenChatUnknown && !MayBindWhenChatUnknown(chord))
        {
            if (MayBindWhenChatUnknown(defaultKey) && !ReservedChords.IsReserved(defaultKey))
            {
                Plugin.Logger?.LogWarning(
                    PrintableChordRefusedLog(label, chord, "Using " + defaultKey + " instead."));
                chord = defaultKey;
            }
            else
            {
                Plugin.Logger?.LogWarning(PrintableChordRefusedLog(
                    label,
                    chord,
                    "Its default " + defaultKey + " is no better, so the bind keeps the key and "
                    + "loses its main-menu exemption instead: it now behaves like every other "
                    + "keybind and is suppressed whenever no chat window can be read."));
                allowWhenChatUnknown = false;
            }
        }

        var registration = new Registration(
            owner, plugin, key, desc, entry, defaultKey, chord, onPressed, allowWhileCaptured, allowWhenChatUnknown,
            whileHeld, onReleased) { Reserved = reserved, Scope = scope, File = file };

        // PAD1: the controller chord: a saved <id>.Gamepad value, else the controller default (none for most binds).
        registration.DefaultGamepadChord = padDefault;
        registration.GamepadEntry = BindGamepadEntry(file, plugin, key, desc, padDefault, force: false);
        registration.GamepadChord = ReadGamepadChord(registration.GamepadEntry, plugin, key, padDefault);

        string? conflict = null;
        lock (Gate)
        {
            if (chord.IsBound)
            {
                for (int i = 0; i < Live.Count; i++)
                {
                    if (Live[i].Chord == chord && Live[i].Scope == scope)
                    {
                        conflict = Live[i].Label;
                        break;
                    }
                }
            }

            Live.Add(registration);
            _pumpSnapshot = null;
            Bump();
            ArmPumpLocked();
        }

        Plugin.Logger?.LogInfo(RegisteredLog(plugin, key, chord, desc));
        if (registration.GamepadChord.IsBound)
            Plugin.Logger?.LogInfo("Ui: keybind '" + label + "' controller button " + registration.GamepadChord + ".");
        if (conflict != null)
            Plugin.Logger?.LogWarning(ConflictLog(chord, conflict, registration.Label));
        LogNotActive(chord);
        // KEYS4: a key this mod wants that something else has (or one it takes from another mod) is asked
        // about in a dialog on a later pump, once every bind of this registration batch is in.
        KeybindConflictPrompt.NoteRegistered(plugin);

        return registration;
    }

    /// <summary>
    /// KEYS1: after a registration or rebind lands on <paramref name="chord"/>, say which Progenitor
    /// binds on it are not active and why. Reads the game and the registry only (not other mods' cfg
    /// files, which may still be loading); the tab and the pump read the whole picture.
    /// </summary>
    private static void LogNotActive(KeyChord chord)
    {
        if (!chord.IsBound) return;
        try
        {
            var game = KeybindArbiter.GameSnapshot();
            var picture = KeybindArbiter.Compute(
                game.Available ? game.Claims : Array.Empty<KeybindClaim>(), KeybindArbiter.ReadProgenitor(),
                Array.Empty<KeybindClaim>());
            foreach (var v in picture.Verdicts)
            {
                if (v.Claim.Source != KeybindSource.Progenitor || v.Active || v.Claim.Chord != chord) continue;
                Plugin.Logger?.LogWarning(NotActiveLog(v.Claim.OwnerId + "/" + v.Claim.ActionId, v.Reason));
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind winner log threw: " + e.Message); }
    }

    /// <summary>Every live bind, newest last. The settings panel's keybind rows.</summary>
    public static IReadOnlyList<IKeybindInfo> Snapshot()
    {
        lock (Gate)
            return new List<IKeybindInfo>(Live);
    }

    /// <summary>
    /// Write a new chord for <paramref name="id"/> through the config entry, so the cfg file
    /// updates and a later Core generation reads the player's value. Returns false for an
    /// unknown id.
    ///
    /// <para>
    /// Also returns false — with the bind unchanged and the refusal logged and raised on
    /// <see cref="RebindFinished"/> — when:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the target carries <c>allowWhenChatUnknown</c> and <paramref name="chord"/> is a bare
    /// printable character key (R36 finding 5);</description></item>
    /// <item><description>KEYS2: <paramref name="chord"/> is one Windows or Steam takes first
    /// (<see cref="ReservedChords"/>: <c>Alt+F4 closes the window in Windows. Pick another key.</c>);</description></item>
    /// <item><description>KEYS2: something else already has <paramref name="chord"/>: a game action, another
    /// Progenitor bind (this mod's included) or an ordinary mod's key setting
    /// (<see cref="KeybindRemapGuard"/>: <c>F8 is used by Progenitor Sample (Net ping). Pick another key.</c>).
    /// A remap never creates a conflict.</description></item>
    /// </list>
    /// <para>
    /// Choosing the chord the bind already has is always accepted (a no-op, even when that chord is in an
    /// existing conflict). <see cref="KeyChord.None"/> (unbind) is always accepted.
    /// </para>
    /// </summary>
    public static bool Rebind(string? pluginId, string id, KeyChord chord) =>
        TryRebind(pluginId, id, chord, RebindCheck.Full, out _);

    /// <summary>
    /// KEYS2: <see cref="Rebind(string?, string, KeyChord)"/> without the conflict check, for tests that build
    /// a conflict on purpose. No production path uses it. Reserved chords and the printable rule still apply.
    /// </summary>
    internal static bool Rebind(string? pluginId, string id, KeyChord chord, bool checkConflicts) =>
        TryRebind(pluginId, id, chord, checkConflicts ? RebindCheck.Full : RebindCheck.ReservedOnly, out _);

    /// <summary>KEYS2: which refusals <see cref="TryRebind"/> applies.</summary>
    internal enum RebindCheck
    {
        /// <summary>Reserved chords, the printable rule and conflicts.</summary>
        Full = 0,

        /// <summary>As <see cref="Full"/>, with the refusal worded for a reset to the default.</summary>
        Reset = 1,

        /// <summary>Reserved chords and the printable rule only.</summary>
        ReservedOnly = 2,
    }

    internal const string NotRegisteredReason = "This key is no longer registered.";

    internal const string SwapNotSavableReason = "One of these keys cannot be saved to its config file, so they cannot be swapped.";

    /// <summary>
    /// The rebind, with the row-sized reason on refusal (<paramref name="reason"/>), so every remap flow —
    /// the Keybinds tab, the mod page's Keys section, the settings panel — shows the same sentence.
    /// </summary>
    internal static bool TryRebind(string? pluginId, string id, KeyChord chord, RebindCheck check, out string? reason)
    {
        reason = null;
        // PAD1: a controller chord is the bind's controller key.
        if (chord.IsGamepad) return TryRebindGamepad(pluginId, id, chord, check == RebindCheck.Reset, out reason);
        Registration? target = FindLive(pluginId, id);
        if (target == null)
        {
            reason = NotRegisteredReason;
            return false;
        }

        string? log = null;
        KeyChord current = target.Chord;
        if (chord != current)
            reason = RefusalFor(target, chord, check == RebindCheck.Reset, check != RebindCheck.ReservedOnly, null, out log);

        string? conflict = null;
        if (reason == null)
        {
            Registration? clashed = null;
            lock (Gate)
            {
                // KEYS2 review: the checks above ran outside the lock. Re-validate what can change under it:
                // the bind itself, and another Progenitor bind taking the chord meanwhile.
                if (target.Disposed)
                {
                    reason = NotRegisteredReason;
                }
                else if (target.Chord != current)
                {
                    reason = ChangedMeanwhileReason;
                }
                else
                {
                    if (chord.IsBound && chord != current)
                    {
                        for (int i = 0; i < Live.Count; i++)
                        {
                            if (ReferenceEquals(Live[i], target) || Live[i].Disposed) continue;
                            if (Live[i].Chord != chord || Live[i].Scope != target.Scope) continue;
                            if (check != RebindCheck.ReservedOnly) clashed = Live[i];
                            else conflict = Live[i].Label;
                            break;
                        }
                    }

                    if (clashed == null)
                    {
                        target.Chord = chord;
                        target.Reserved = null;
                        Bump();
                    }
                }
            }

            if (clashed != null) reason = ClashReasonOf(clashed, chord, check == RebindCheck.Reset);
        }

        // Refused: the bind is untouched and the cfg is not written, but a panel may be sitting in "press
        // a key", so it is told the flow ended (outside the lock - RebindFinished runs panel code that
        // calls back into this module).
        if (reason != null)
        {
            Plugin.Logger?.LogWarning(log ?? "Ui: keybind '" + target.Label + "' was not rebound to " + chord + ": " + reason);
            Raise(new RebindResult(target.Id, RebindOutcome.Refused, reason));
            return false;
        }

        try
        {
            target.Entry?.SetSerializedValue(chord.ToString());
            Persist(target.Entry);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: keybind '" + target.Label + "' rebound to " + chord
                + " in memory but the cfg write threw: " + e.Message);
        }

        Plugin.Logger?.LogInfo("Ui: keybind '" + target.Label + "' rebound to " + chord + ".");
        if (conflict != null)
            Plugin.Logger?.LogWarning(ConflictLog(chord, conflict, target.Label));
        LogNotActive(chord);

        Raise(new RebindResult(target.Id, RebindOutcome.Bound));
        return true;
    }

    internal const string ChangedMeanwhileReason = "This key changed while it was being saved; try again.";

    /// <summary>
    /// KEYS4 (found live 2026-09-30: a Give unbound the Sample's key in memory, and its cfg kept the old
    /// value): a key written through its entry is saved to disk even when the file does not save on every
    /// set. ConfigSync turns <c>SaveOnConfigSet</c> off on a plugin's file while the server locks one of its
    /// synced settings (connected to a server), and a startup-loaded plugin such as the Sample has synced
    /// settings. An explicit <see cref="ConfigFile.Save"/> is safe there: ConfigSync parks each locked key
    /// with the player's own value for exactly this (ConfigSync's class notes), so only the key changes on disk.
    /// </summary>
    internal static void Persist(ConfigEntryBase? entry)
    {
        var file = entry?.ConfigFile;
        if (file == null || file.SaveOnConfigSet) return;
        try { file.Save(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind '" + entry!.Definition + "' could not be saved to " + file.ConfigFilePath + ": " + e.Message);
        }
    }

    /// <summary>The refusal naming another live Progenitor bind as the holder of <paramref name="chord"/>.</summary>
    private static string ClashReasonOf(Registration holder, KeyChord chord, bool reset)
    {
        var claim = new KeybindClaim(KeybindSource.Progenitor, holder.PluginId, holder.Id,
            KeybindArbiter.KeybindDisplayName(holder.PluginId, holder.Id), holder.Chord);
        return reset ? KeybindRemapGuard.ResetClashReason(chord, claim) : KeybindRemapGuard.ClashReason(chord, claim);
    }

    /// <summary>
    /// KEYS2: the player dismissed a reserved-key problem (<see cref="ModProblems.Dismiss"/>): the note goes,
    /// so the Keybinds row stops repeating it. The key it got instead is unchanged.
    /// </summary>
    internal static void DismissReserved(string? pluginId, string id)
    {
        lock (Gate)
        {
            var r = FindLiveLocked(pluginId, id);
            if (r != null) r.Reserved = null;
        }
    }

    private static Registration? FindLiveLocked(string? pluginId, string id)
    {
        for (int i = 0; i < Live.Count; i++)
            if (Live[i].Matches(pluginId, id ?? "")) return Live[i];
        return null;
    }

    private static Registration? FindLive(string? pluginId, string id)
    {
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
                if (Live[i].Matches(pluginId, id ?? "")) return Live[i];
        }

        return null;
    }

    /// <summary>
    /// KEYS2: why <paramref name="target"/> may not take <paramref name="chord"/>, or null. Reserved first,
    /// then the printable rule for an exempt bind, then (with <paramref name="conflicts"/>) anything else
    /// that has the chord. <paramref name="log"/> is the long form for the log when it differs.
    /// </summary>
    private static string? RefusalFor(Registration target, KeyChord chord, bool reset, bool conflicts, string? alsoIgnore, out string? log)
    {
        log = null;
        if (!chord.IsBound) return null;
        string? reserved = reset ? ReservedChords.ResetReason(chord) : ReservedChords.Reason(chord);
        if (reserved != null) return reserved;
        string? click = MouseChords.Refusal(chord, target.Scope);
        if (click != null) return click;
        // HOLDMOD1: a lone modifier is a hold key only.
        string? lone = LoneModifierRefusal(chord, target.HasHold, target.Scope);
        if (lone != null) return lone;
        if (target.AllowWhenChatUnknown && !MayBindWhenChatUnknown(chord))
        {
            log = PrintableChordRefusedLog(target.Label, chord, "The bind stays on " + target.Chord + ".");
            return log;
        }

        if (!conflicts) return null;
        var clash = KeybindRemapGuard.Check(KeybindSource.Progenitor, target.PluginId, target.Id, target.Chord, chord, reset, alsoIgnore, target.Scope);
        return clash?.Reason;
    }

    /// <summary>
    /// KEYS2: the clash a capture for <paramref name="pluginId"/>/<paramref name="id"/> would hit on
    /// <paramref name="chord"/>, or null (the chord the bind already has never clashes).
    /// </summary>
    internal static RemapRefusal? ClashFor(string? pluginId, string id, KeyChord chord, bool reset = false)
    {
        var target = FindLive(pluginId, id);
        if (target == null) return null;
        return KeybindRemapGuard.Check(KeybindSource.Progenitor, target.PluginId, target.Id, target.Chord, chord, reset, scope: target.Scope);
    }

    /// <summary>
    /// KEYS2: swap two Progenitor binds' keys: A takes B's chord and B takes A's, both checked (reserved,
    /// the printable rule, and every other key but the pair), then both stored and both cfg entries
    /// written; if a write throws, both are put back. <paramref name="dryRun"/> only checks. False with
    /// <paramref name="reason"/> when either side may not move.
    /// </summary>
    internal static bool Swap(string? pluginA, string idA, string? pluginB, string idB, bool dryRun, out string? reason)
    {
        reason = null;
        var a = FindLive(pluginA, idA);
        var b = FindLive(pluginB, idB);
        if (a == null || b == null || ReferenceEquals(a, b))
        {
            reason = NotRegisteredReason;
            return false;
        }

        KeyChord aOld = a.Chord, bOld = b.Chord;
        if (aOld == bOld)
        {
            reason = "Both keys are already " + aOld + ".";
            return false;
        }

        // KEYS2 review: a side whose cfg entry could not be bound would swap in memory only and clash again
        // after a reload, so the swap is refused (and never offered).
        if (a.Entry == null || b.Entry == null)
        {
            reason = SwapNotSavableReason;
            return false;
        }

        string aKey = KeybindRemapGuard.KeyOf(KeybindSource.Progenitor, a.PluginId, a.Id);
        string bKey = KeybindRemapGuard.KeyOf(KeybindSource.Progenitor, b.PluginId, b.Id);
        reason = RefusalFor(a, bOld, false, true, bKey, out _) ?? RefusalFor(b, aOld, false, true, aKey, out _);
        if (reason != null)
        {
            if (!dryRun) Plugin.Logger?.LogWarning("Ui: keybind swap of '" + a.Label + "' and '" + b.Label + "' refused: " + reason);
            return false;
        }

        if (dryRun) return true;
        ReservedNote? aNote, bNote;
        lock (Gate)
        {
            if (a.Disposed || b.Disposed)
            {
                reason = NotRegisteredReason;
                return false;
            }

            // Re-validated under the lock: neither key moved, and no third bind took either key meanwhile.
            if (a.Chord != aOld || b.Chord != bOld)
            {
                reason = ChangedMeanwhileReason;
                return false;
            }

            for (int i = 0; i < Live.Count; i++)
            {
                var o = Live[i];
                if (ReferenceEquals(o, a) || ReferenceEquals(o, b) || o.Disposed || !o.Chord.IsBound) continue;
                if (o.Scope != a.Scope && o.Scope != b.Scope) continue;
                if (o.Chord == aOld || o.Chord == bOld)
                {
                    reason = ChangedMeanwhileReason;
                    return false;
                }
            }

            aNote = a.Reserved;
            bNote = b.Reserved;
            a.Chord = bOld;
            b.Chord = aOld;
            a.Reserved = null;
            b.Reserved = null;
            Bump();
        }

        try
        {
            a.Entry.SetSerializedValue(bOld.ToString());
            b.Entry.SetSerializedValue(aOld.ToString());
            Persist(a.Entry);
            if (!ReferenceEquals(a.Entry.ConfigFile, b.Entry.ConfigFile)) Persist(b.Entry);
        }
        catch (Exception e)
        {
            lock (Gate)
            {
                a.Chord = aOld;
                b.Chord = bOld;
                a.Reserved = aNote;
                b.Reserved = bNote;
                Bump();
            }

            try { a.Entry?.SetSerializedValue(aOld.ToString()); } catch { /* best effort */ }
            try { b.Entry?.SetSerializedValue(bOld.ToString()); } catch { /* best effort */ }
            reason = "The keys could not be saved (" + e.Message + "), so neither was changed.";
            Plugin.Logger?.LogWarning("Ui: keybind swap of '" + a.Label + "' and '" + b.Label + "' failed: " + e.Message);
            return false;
        }

        Plugin.Logger?.LogInfo("Ui: keybinds '" + a.Label + "' and '" + b.Label + "' swapped: " + a.Label + " = " + bOld + ", " + b.Label + " = " + aOld + ".");
        Raise(new RebindResult(a.Id, RebindOutcome.Bound));
        Raise(new RebindResult(b.Id, RebindOutcome.Bound));
        return true;
    }

    /// <summary>
    /// KEYS2: every live bind whose default or cfg value was refused at registration because Windows or
    /// Steam reserves it, and not changed since. <see cref="ReservedKeySource"/> turns these into problems.
    /// </summary>
    internal static IReadOnlyList<(string PluginId, string Id, ReservedNote Note)> ReservedRefusals()
    {
        var list = new List<(string, string, ReservedNote)>();
        lock (Gate)
        {
            foreach (var r in Live)
                if (!r.Disposed && r.Reserved != null) list.Add((r.PluginId, r.Id, r.Reserved));
        }

        return list;
    }

    /// <summary>KEYS2: the refused-at-registration note of one bind, or null.</summary>
    internal static ReservedNote? ReservedNoteOf(string? pluginId, string id) => FindLive(pluginId, id)?.Reserved;

    /// <summary>
    /// KEYS1: the key capture the Keybinds tab uses. Reads the next chord with the rebind flow's rules
    /// (the chat and text-field gates, Escape cancels, an AltGr keystroke is refused with its reason,
    /// modifiers alone never end it) but does <b>not</b> store it: <paramref name="onDone"/> gets the
    /// chord, so the tab can show a clash before anything is saved. While it waits no bind fires, as
    /// with <see cref="BeginRebind"/>; starting one cancels the other.
    /// </summary>
    internal static bool BeginCapture(Action<KeyCaptureResult> onDone) => BeginCapture(onDone, gamepad: false);

    /// <summary>
    /// PAD1: <paramref name="gamepad"/> true waits for a controller chord instead (<see cref="GamepadCapture"/>: decided
    /// when the first button comes up, so a held button is never taken as the key). Escape on the keyboard still cancels;
    /// other keys are ignored.
    /// </summary>
    internal static bool BeginCapture(Action<KeyCaptureResult> onDone, bool gamepad)
    {
        if (onDone == null) throw new ArgumentNullException(nameof(onDone));
        Registration? rebinding;
        Action<KeyCaptureResult>? previous;
        lock (Gate)
        {
            rebinding = _rebinding;
            _rebinding = null;
            previous = _capture;
            _capture = onDone;
            _captureGamepad = gamepad;
            _gamepadCapture = gamepad ? new GamepadCapture() : null;
            _waitStarted = Clock();
            _waitBegan = _waitStarted;
            _leftArmed = _rightArmed = _loneArmed = false;
            ArmPumpLocked();
        }

        if (rebinding != null) Raise(new RebindResult(rebinding.Id, RebindOutcome.Cancelled));
        if (previous != null) Deliver(previous, new KeyCaptureResult(RebindOutcome.Cancelled, KeyChord.None, null));
        return true;
    }

    /// <summary>Leave the capture with nothing chosen.</summary>
    internal static void CancelCapture()
    {
        Action<KeyCaptureResult>? was;
        lock (Gate)
        {
            was = _capture;
            _capture = null;
        }

        if (was != null) Deliver(was, new KeyCaptureResult(RebindOutcome.Cancelled, KeyChord.None, null));
    }

    /// <summary>True while a <see cref="BeginCapture"/> waits for a key.</summary>
    internal static bool Capturing
    {
        get { lock (Gate) return _capture != null; }
    }

    private static void Deliver(Action<KeyCaptureResult> to, KeyCaptureResult result)
    {
        try { to(result); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: key capture handler threw: " + e.Message); }
    }

    /// <summary>
    /// KEYS1 (bridge <c>keybind_press</c>): dispatch <paramref name="id"/> as a real press would — the
    /// capture, panel, chat and text-field gates, then the winner rule — without reading a device.
    /// Returns what happened to each bind it reached.
    /// </summary>
    public static IReadOnlyList<KeybindDispatch> SimulatePress(string? pluginId, string id)
    {
        Registration? target = null;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                if (!Live[i].Matches(pluginId, id ?? "")) continue;
                target = Live[i];
                break;
            }
        }

        if (target == null)
            return new[] { new KeybindDispatch(pluginId ?? "", id ?? "", "", false, "unknown bind") };
        return new[] { Dispatch(target) };
    }

    /// <summary>KEYS1: every bind on <paramref name="chord"/>, dispatched as one real press would reach them.</summary>
    public static IReadOnlyList<KeybindDispatch> SimulatePress(KeyChord chord)
    {
        Registration[] snapshot;
        lock (Gate) snapshot = PumpSnapshotLocked();
        var result = new List<KeybindDispatch>();
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Disposed || !chord.IsBound) continue;
            // PAD1: a controller chord reaches the binds it is the controller chord of.
            if (chord.IsGamepad)
            {
                if (snapshot[i].GamepadChord == chord) result.Add(Dispatch(snapshot[i], -1, gamepad: true));
                continue;
            }

            if (snapshot[i].Chord != chord) continue;
            result.Add(Dispatch(snapshot[i]));
        }

        return result;
    }

    /// <summary>
    /// KEYS1: press <paramref name="id"/>, hold it for <paramref name="frames"/> frames and release it, as
    /// a real hold would reach it: the same gates and winner rule as <see cref="SimulatePress(string?, string)"/>,
    /// then the press handler, the while-held handler <paramref name="frames"/> times and the release handler.
    /// <c>Fired</c> is true when any handler ran.
    /// </summary>
    public static IReadOnlyList<KeybindDispatch> SimulateHold(string? pluginId, string id, int frames)
    {
        Registration? target = null;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                if (!Live[i].Matches(pluginId, id ?? "")) continue;
                target = Live[i];
                break;
            }
        }

        if (target == null)
            return new[] { new KeybindDispatch(pluginId ?? "", id ?? "", "", false, "unknown bind") };
        return new[] { Dispatch(target, Math.Max(0, Math.Min(frames, 600)), gamepad: false) };
    }

    private static KeybindDispatch Dispatch(Registration reg) => Dispatch(reg, -1, gamepad: false);

    /// <summary>
    /// PAD1 (bridge <c>keybind_press</c> with <c>gamepad: true</c>): dispatch <paramref name="id"/>'s controller chord as a
    /// real controller press would reach it: the capture, panel, chat and scope gates and the controller winner rule, with
    /// <see cref="PressedWithGamepad"/> true in the handler.
    /// </summary>
    public static IReadOnlyList<KeybindDispatch> SimulateGamepadPress(string? pluginId, string id)
    {
        var target = FindLive(pluginId, id);
        if (target == null)
            return new[] { new KeybindDispatch(pluginId ?? "", id ?? "", "", false, "unknown bind") };
        return new[] { Dispatch(target, -1, gamepad: true) };
    }

    /// <summary>TEXTUNDO1: the refusal while a framework text field with owned focus takes the bind's key.</summary>
    internal const string FieldTakesKeyReason = "suppressed: a framework text field has focus and takes this key";

    /// <summary><paramref name="holdFrames"/> &lt; 0: a press only. Otherwise press, hold that many frames, release.</summary>
    private static KeybindDispatch Dispatch(Registration reg, int holdFrames, bool gamepad)
    {
        KeybindArbiter.RefreshIfStale();
        var chord = gamepad ? reg.GamepadChord : reg.Chord;
        string chordText = chord.ToString();
        KeybindDispatch Not(string why) => new(reg.PluginId, reg.Id, chordText, false, why);
        if (reg.Disposed) return Not("unknown bind");
        if (!chord.IsBound) return Not(gamepad ? "no controller button" : "unbound");
        IKeyInputSource source;
        bool waiting;
        lock (Gate)
        {
            source = _source;
            waiting = _rebinding != null || _capture != null;
        }

        if (waiting) return Not("suppressed: a key capture is waiting for a key");
        if (!ScopeOpen(reg.Scope)) return Not(ScopeClosedReason(reg.Scope));
        if (UiInputCapture.IsFullyBlocking && !reg.AllowWhileCaptured) return Not("suppressed: a mod panel is taking input");
        if (ClickChordSuppressedReason(reg.Scope, chord, raycast: true) is { } click) return Not(click);
        ChatFocus focus;
        try { focus = source.ChatFocus; }
        catch { focus = ChatFocus.Unknown; }
        if (reg.Scope == KeybindScope.Chat)
        {
            // CHAT2: the chat box is the field a chat bind serves, so its focus is the gate (ScopeOpen above),
            // not a suppression. Press only. A Progenitor-owned text field still takes its own keys.
            if (MenuHotkeyGuard.HotkeysBlocked) return Not(FieldTakesKeyReason);
            string? lost = gamepad ? SafeGamepadWinnerGate(reg) : SafeWinnerGate(reg);
            if (lost != null) return Not("not-active: " + lost);
            if (!reg.HasPress) return Not("no press handler: this bind acts while held; use a hold");
            try { reg.OnPressed(gamepad); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Ui: keybind '" + reg.Label + "' handler threw: " + e);
                return Not("handler threw: " + e.Message);
            }

            return new KeybindDispatch(reg.PluginId, reg.Id, chordText, true, "");
        }

        if (focus == ChatFocus.Focused) return Not("suppressed: chat has focus");
        bool typing;
        try { typing = source.TextFieldFocused; }
        catch { typing = false; }
        // A mouse button cannot type: a bind on one (Mouse Back = Navigate back) still works while a field has focus.
        if (typing && !TypingSafeChord(chord)) return Not("suppressed: a text field has focus");
        if (MenuHotkeyGuard.HotkeysBlocked && UiTypedKeys.FieldTakes(chord))
            return Not(FieldTakesKeyReason);
        if (focus == ChatFocus.Unknown && !reg.AllowWhenChatUnknown && source.IsAvailable)
            return Not("suppressed: no chat window can be read");
        string? blocked = gamepad ? SafeGamepadWinnerGate(reg) : GateNow(reg);
        if (blocked != null) return Not("not-active: " + blocked);
        if (holdFrames < 0)
        {
            // Review 2026-09-29: a hold-only bind has no press handler, so a press runs nothing.
            if (!reg.HasPress) return Not("no press handler: this bind acts while held; use a hold");
            try { reg.OnPressed(gamepad); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Ui: keybind '" + reg.Label + "' handler threw: " + e);
                return Not("handler threw: " + e.Message);
            }

            return new KeybindDispatch(reg.PluginId, reg.Id, chordText, true, "");
        }

        if (!reg.HasPress && !reg.HasHold) return Not("no handler");
        try { reg.OnPressed(gamepad); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ui: keybind '" + reg.Label + "' handler threw: " + e);
            return Not("handler threw: " + e.Message);
        }

        reg.Holding = true;
        reg.HoldingGamepad = gamepad;
        for (int i = 0; i < holdFrames; i++) reg.RunWhileHeld();
        reg.Release();
        return new KeybindDispatch(reg.PluginId, reg.Id, chordText, true, "");
    }

    /// <summary>KEYS8: why a left or right click chord did not run with the pointer on a Progenitor window.</summary>
    internal const string ClickOverModUiReason = "suppressed: the pointer is over a Progenitor window, so the click is the window's";

    /// <summary>KEYS8: why a gameplay left or right click chord did not run with the game's full-screen menu open.</summary>
    internal const string ClickInGameMenuReason = "suppressed: the game's menu is open, so the click is the menu's";

    /// <summary>
    /// KEYS8: whether a shown Progenitor panel holds the pointer (a hovered panel, or one that takes input
    /// wherever the pointer is). Production: <see cref="UiInputCapture.Count"/> and its pointer gate.
    /// </summary>
    internal static Func<bool> PointerOverModUi { get; set; } = DefaultPointerOverModUi;

    private static bool DefaultPointerOverModUi()
    {
        // The gate reads "blocked" with no panel shown (its fail-closed default), so the count decides first.
        try { return UiInputCapture.Count > 0 && UiInputCapture.PointerBlock; }
        catch { return true; }
    }

    /// <summary>
    /// KEYS8 review: whether the top UI raycast hit under the pointer is a Progenitor-owned object, which also
    /// covers a passive overlay or HUD panel that never registered for input capture. A raycast, so it is asked
    /// only on a click chord's press edge, in <c>keybind_press</c> and for a capture's bare click, never per frame.
    /// Production: <see cref="IUiRuntime.PointerOverOwnedUi"/>.
    /// </summary>
    internal static Func<bool> PointerHitsModUi { get; set; } = DefaultPointerHitsModUi;

    private static bool DefaultPointerHitsModUi()
    {
        try { return Ui.CurrentRuntime().PointerOverOwnedUi(); }
        catch { return false; }
    }

    /// <summary>KEYS8: whether the game's full-screen menu (the map included) is open. Production: <see cref="GameMenuHistory.SafeMenuOpen"/>.</summary>
    internal static Func<bool> GameMenuOpen { get; set; } = DefaultGameMenuOpen;

    private static bool DefaultGameMenuOpen() => GameMenuHistory.SafeMenuOpen();

    /// <summary>
    /// KEYS8: why a bind on <paramref name="chord"/> in <paramref name="scope"/> may not fire on this frame's
    /// click, or null. Only the left and right buttons are ever stopped here, and whatever
    /// <c>allowWhileCaptured</c> says: a click on a Progenitor window is that window's (its button, its scroll
    /// bar), and a gameplay click with the game's full-screen menu open is the menu's.
    /// </summary>
    internal static string? ClickChordSuppressedReason(KeybindScope scope, KeyChord chord) =>
        ClickChordSuppressedReason(scope, chord, raycast: false);

    /// <summary>
    /// As above; <paramref name="raycast"/> also asks <see cref="PointerHitsModUi"/> (the press edge and the bridge),
    /// so a click on any Progenitor-owned object, a passive overlay's button included, is the object's.
    /// </summary>
    internal static string? ClickChordSuppressedReason(KeybindScope scope, KeyChord chord, bool raycast)
    {
        if (!chord.IsBound || !KeyChord.IsClickButton(chord.Key)) return null;
        if (PointerOnModUi(raycast)) return ClickOverModUiReason;
        if (scope != KeybindScope.Gameplay) return null;
        bool menu;
        try { menu = GameMenuOpen(); }
        catch { menu = true; }
        return menu ? ClickInGameMenuReason : null;
    }

    /// <summary>KEYS8: the pointer is on a Progenitor window: a capturing panel holds it, or (with <paramref name="raycast"/>) the top UI hit is ours.</summary>
    private static bool PointerOnModUi(bool raycast)
    {
        bool over;
        try { over = PointerOverModUi(); }
        catch { over = true; }
        if (over || !raycast) return over;
        try { return PointerHitsModUi(); }
        catch { return true; }
    }

    /// <summary>
    /// KEYS8: true while a click a capture just took is still down, and for two pumps after it comes up.
    /// Progenitor's widgets drop clicks then (<see cref="UiSlotTable.BootstrapDispatch"/>), so the click
    /// that chose a key does not also press the button under the pointer.
    /// </summary>
    internal static bool SwallowsUiClicks
    {
        get
        {
            if (_swallowButton == UiKey.None) return false;
            if (!SwallowExpired()) return true;
            _swallowButton = UiKey.None;
            return false;
        }
    }

    private static void BeginSwallow(UiKey button)
    {
        _swallowButton = button;
        _swallowReleasedPumps = -1;
        try { _swallowSince = Clock(); }
        catch { _swallowSince = 0; }
    }

    private static bool SwallowExpired()
    {
        long now;
        try { now = Clock(); }
        catch { return true; }
        long since = now - _swallowSince;
        return since < 0 || since > SwallowMaxMs;
    }

    /// <summary>One pump of the swallow: it ends two pumps after the button is seen up (the widget's click lands on the release).</summary>
    private static void TickSwallow(IKeyInputSource source)
    {
        if (_swallowButton == UiKey.None) return;
        if (SwallowExpired())
        {
            _swallowButton = UiKey.None;
            return;
        }

        bool held;
        try { held = source.IsHeld(new KeyChord(_swallowButton)); }
        catch { return; } // unreadable: neither held nor released; only the cap can end it
        if (held)
        {
            // Review: a long hold keeps the swallow; the cap counts from the last pump that read it held, so it
            // only ends a swallow whose button can no longer be read.
            _swallowReleasedPumps = -1;
            try { _swallowSince = Clock(); }
            catch { /* the cap ends it */ }
            return;
        }

        if (++_swallowReleasedPumps >= 2) _swallowButton = UiKey.None;
    }

    /// <summary>KEYS8: a button's held state for the capture's arming; a read that throws counts as held (never armed).</summary>
    private static bool HeldOrUnknown(IKeyInputSource source, UiKey button)
    {
        try { return source.IsHeld(new KeyChord(button)); }
        catch { return true; }
    }

    /// <summary>
    /// HOLDMOD1: why a bind may not use lone-modifier chord <paramref name="chord"/>, or null. Ctrl, Shift or Alt on its
    /// own fires the moment it goes down, so it would also fire on every Ctrl+C, every capital letter and every Alt+X:
    /// only a bind that acts while the key is held (a <c>whileHeld</c> or <c>onReleased</c> handler) may use one, and
    /// never in the chat scope, which is press only. Null for any other chord.
    /// </summary>
    internal static string? LoneModifierRefusal(KeyChord chord, bool hold, KeybindScope scope)
    {
        if (!chord.IsLoneModifier) return null;
        if (hold && scope != KeybindScope.Chat) return null;
        return LoneModifierPressReason(chord);
    }

    /// <summary>HOLDMOD1: the row-sized refusal for a lone modifier on a press-only bind.</summary>
    internal static string LoneModifierPressReason(KeyChord chord) =>
        chord.DisplayText + " on its own only works for a key you hold down, and this key acts on a press. "
        + "Press another key with it (for example X + " + chord.DisplayText + ") or pick another key.";

    /// <summary>
    /// HOLDMOD1: the same refusal for the bind <paramref name="pluginId"/>/<paramref name="id"/> (the Keybinds tab's
    /// rows), or null when it may take <paramref name="chord"/> or is not a live bind.
    /// </summary>
    internal static string? LoneModifierRefusalFor(string? pluginId, string id, KeyChord chord)
    {
        if (!chord.IsLoneModifier) return null;
        var target = FindLive(pluginId, id);
        return target == null ? null : LoneModifierRefusal(chord, target.HasHold, target.Scope);
    }

    /// <summary>HOLDMOD1: a registration's default or cfg value on a lone modifier its handlers cannot use.</summary>
    public static string LoneModifierRefusedLog(string label, KeyChord chord, KeyChord instead) =>
        "Ui: keybind '" + label + "' cannot be bound to " + chord + ": " + LoneModifierPressReason(chord) + " "
        + (instead.IsBound ? "Using " + instead + " instead." : "It is registered unbound; pick a key in Mods > Keybinds.");

    /// <summary>KEYS8: a registration's default or cfg value on a click chord its scope refuses.</summary>
    public static string ClickChordRefusedLog(string label, KeyChord chord, string reason, KeyChord instead) =>
        "Ui: keybind '" + label + "' cannot be bound to " + chord + ": " + reason + " "
        + (instead.IsBound ? "Using " + instead + " instead." : "It is registered unbound; pick a key in Mods > Keybinds.");

    /// <summary>KEYS4: why a menu bind did not run with the Mods workspace closed.</summary>
    internal const string MenuClosedReason = "suppressed: works only while the Mods menu is open";

    /// <summary>HUB2: why a window key (<see cref="KeybindScope.Window"/>) did not fire.</summary>
    internal const string WindowClosedReason = "suppressed: works only in menus, or while a window with tabs holds the input";

    /// <summary>HUB2: why a window key did not fire: another mod's key on its chord acts on this press.</summary>
    internal static string WindowYieldReason(KeybindClaim holder) =>
        "suppressed: " + KeybindArbiter.DisplayNameOf(holder.OwnerId) + "'s " + holder.DisplayName + " has this key and acts";

    internal const string PhotoInactiveReason = "suppressed: works only in photo mode";

    /// <summary>MAPTP1: why a map bind did not run with the world map closed.</summary>
    internal const string MapClosedReason = "suppressed: works only while the map is open";

    /// <summary>CHAT2: why a chat bind did not run while the chat box does not have focus.</summary>
    internal const string ChatClosedReason = "suppressed: works only while typing in chat";

    private static string ScopeClosedReason(KeybindScope scope) => scope switch
    {
        KeybindScope.Photo => PhotoInactiveReason,
        KeybindScope.Map => MapClosedReason,
        KeybindScope.Chat => ChatClosedReason,
        KeybindScope.Window => WindowClosedReason,
        _ => MenuClosedReason,
    };

    /// <summary>
    /// KEYS4: whether the Mods workspace is open, for <see cref="KeybindScope.Menu"/> binds. Production:
    /// open and not suspended, in either host. A seam for tests.
    /// </summary>
    internal static Func<bool> MenuScopeOpen { get; set; } = DefaultMenuScopeOpen;

    /// <summary>KEYS7: the Mods workspace, or the game's full-screen menu on any tab (never plain gameplay).</summary>
    private static bool DefaultMenuScopeOpen() => ModsMenu.MenuContextOpen;

    /// <summary>
    /// HUB2: whether a window key (<see cref="KeybindScope.Window"/>) fires: a menu the workspace history covers is open
    /// (<see cref="MenuScopeOpen"/>), or a window with a tab history holds the input (<see cref="WindowHoldsInput"/>: the
    /// game's input is blocked, so no gameplay action fires on the same press). A seam for tests.
    /// </summary>
    internal static Func<bool> WindowScopeOpen { get; set; } = DefaultWindowScopeOpen;

    private static bool DefaultWindowScopeOpen()
    {
        try { if (MenuScopeOpen()) return true; }
        catch { /* not in a menu */ }
        return WindowHoldsInput();
    }

    /// <summary>
    /// Whether photo mode is active, for <see cref="KeybindScope.Photo"/> binds. Set by photo mode's
    /// install (client) and put back to "never" on its uninstall and in test resets.
    /// </summary>
    internal static Func<bool> PhotoScopeOpen { get; set; } = NeverOpen;

    /// <summary>
    /// MAPTP1: whether the game's world map is open, for <see cref="KeybindScope.Map"/> binds. Production:
    /// the full-screen menu is open on its Map tab (<see cref="GameMenuHistory.SafeMapOpen"/>, the probe the
    /// back/forward history already reads every frame). A seam for tests.
    /// </summary>
    internal static Func<bool> MapScopeOpen { get; set; } = DefaultMapScopeOpen;

    private static bool DefaultMapScopeOpen() => GameMenuHistory.SafeMapOpen();

    /// <summary>
    /// CHAT2: whether the player is typing in the game's chat box, for <see cref="KeybindScope.Chat"/> binds.
    /// Production: the key source's chat focus reads <see cref="ChatFocus.Focused"/>. A seam for tests.
    /// </summary>
    internal static Func<bool> ChatScopeOpen { get; set; } = DefaultChatScopeOpen;

    private static bool DefaultChatScopeOpen() => ChatFocusNow == ChatFocus.Focused;

    private static bool NeverOpen() => false;

    private static bool ScopeOpen(KeybindScope scope)
    {
        try
        {
            return scope switch
            {
                KeybindScope.Menu => MenuScopeOpen(),
                KeybindScope.Window => WindowScopeOpen(),
                KeybindScope.Photo => PhotoScopeOpen(),
                KeybindScope.Map => MapScopeOpen(),
                KeybindScope.Chat => ChatScopeOpen(),
                _ => true,
            };
        }
        catch { return false; }
    }

    /// <summary>KEYS4: a live bind's scope (<see cref="KeybindScope.Gameplay"/> for anything else).</summary>
    internal static KeybindScope ScopeOf(IKeybindInfo? bind) => bind is Registration r ? r.Scope : KeybindScope.Gameplay;

    /// <summary>
    /// The winner rule for a press, plus HUB2's rule for a window key (<see cref="KeybindScope.Window"/>): it gives way to
    /// another mod's key on its chord whenever that key can act now (<see cref="SharerActingNow"/>), in menus and in play
    /// alike, so one press never fires both, and never neither. Outside menus it acts only while its window holds the input
    /// (<see cref="WindowScopeOpen"/>); the game's own actions are blocked then (<c>GameInputBlocker</c> applies
    /// <c>BlockAllKeys</c> for a full mask), so they never meet it.
    /// </summary>
    private static string? GateNow(Registration reg)
    {
        string? blocked = SafeWinnerGate(reg);
        if (blocked != null || reg.Scope != KeybindScope.Window) return blocked;
        try
        {
            if (SharerActingNow(reg) is { } holder) return WindowYieldReason(holder);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: window key check threw: " + e.Message); }
        return null;
    }

    /// <summary>
    /// HUB2: the other mod's key on window key <paramref name="reg"/>'s chord that acts on this press, or null. An ordinary
    /// mod's key setting always counts (that mod reads the key itself, Progenitor cannot tell when). A Progenitor bind counts
    /// when it is live, active (not beaten by the game or a mod installed first), its scope is open now, and it may run while
    /// a panel holds the input when one does: the same gates the pump applies to it.
    /// </summary>
    private static KeybindClaim? SharerActingNow(Registration reg)
    {
        var sharers = KeybindArbiter.SharersOf(reg.PluginId, reg.Id);
        if (sharers.Count == 0) return null;
        bool captured = UiInputCapture.IsFullyBlocking;
        foreach (var s in sharers)
        {
            if (s.Source == KeybindSource.Cfg) return s;
            if (s.Source != KeybindSource.Progenitor) continue;
            var other = FindLive(s.OwnerId, s.ActionId);
            if (other == null || other.Disposed || other.Chord != reg.Chord) continue;
            if (captured && !other.AllowWhileCaptured) continue;
            if (!ScopeOpen(other.Scope)) continue;
            if (SafeWinnerGate(other) != null) continue;
            return s;
        }

        return null;
    }

    /// <summary>
    /// HUB1: a window with a tab history is the target and the game's input is blocked now: the applied native mask is
    /// <see cref="UiMaskScope.Full"/> or <see cref="UiMaskScope.Exclusive"/> (a <see cref="UiCaptureMode.WhileShown"/>
    /// window, or the pointer over a hovered one). A mask that could not be measured counts as not blocked. A seam for tests.
    /// </summary>
    internal static Func<bool> WindowHoldsInput { get; set; } = DefaultWindowHoldsInput;

    private static bool DefaultWindowHoldsInput()
    {
        if (UiTabHistoryRoute.Target() == null) return false;
        return UiInputCapture.IsBlocking && UiInputCapture.TryGetAppliedScope(out var scope) && scope != UiMaskScope.CloseKeyOnly;
    }

    /// <summary>PAD1: <see cref="SafeWinnerGate"/> for the controller chord (the gamepad picture's verdict).</summary>
    private static string? SafeGamepadWinnerGate(Registration reg)
    {
        try { return GamepadWinnerGate(reg.PluginId, reg.Id); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind controller winner check threw: " + e.Message + "; the bind fires.");
            return null;
        }
    }

    private static string? SafeWinnerGate(Registration reg)
    {
        try { return WinnerGate(reg.PluginId, reg.Id); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind winner check threw: " + e.Message + "; the bind fires.");
            return null;
        }
    }

    /// <summary>
    /// Start the panel's press-a-key flow. Until the next non-modifier key press (or
    /// <see cref="CancelRebind"/>) <b>no</b> bind fires — the point is that pressing the key you
    /// want does not also run whatever is already on it. Returns false for an unknown id.
    /// </summary>
    public static bool BeginRebind(string? pluginId, string id)
    {
        Action<KeyCaptureResult>? capture = null;
        bool started = false;
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                if (!Live[i].Matches(pluginId, id)) continue;
                _rebinding = Live[i];
                _waitStarted = Clock();
                _waitBegan = _waitStarted;
                _leftArmed = _rightArmed = _loneArmed = false;
                capture = _capture;
                _capture = null;
                ArmPumpLocked();
                started = true;
                break;
            }
        }

        if (capture != null) Deliver(capture, new KeyCaptureResult(RebindOutcome.Cancelled, KeyChord.None, null));
        return started;
    }

    /// <summary>Leave the press-a-key flow with the bind unchanged.</summary>
    public static void CancelRebind()
    {
        Registration? was;
        lock (Gate)
        {
            was = _rebinding;
            _rebinding = null;
        }

        if (was != null) Raise(new RebindResult(was.Id, RebindOutcome.Cancelled));
    }

    /// <summary>HUB1: the polls so far (one a frame on the client).</summary>
    internal static int PumpFrame { get; private set; }

    /// <summary>
    /// One poll. Called from <c>Plugin.Frame</c> on the client; tests call it directly.
    /// Order matters: chat first (it wins over everything, including an exempt bind), then the
    /// rebind capture (so the key you are binding does not fire its old owner), then the binds.
    /// </summary>
    public static void Pump()
    {
        // HUB1: one count per poll, so a per-frame answer (the window a tab-history key acts on) is taken once a frame.
        unchecked { PumpFrame++; }
        // KEYS4: the key-conflict dialog's frame (it catches and logs its own failures), and the Keybinds
        // tab following a bind that came, went or changed while it is shown.
        KeybindConflictPrompt.Tick();
        try { ModsMenu.TickKeybindsTab(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybinds tab refresh threw: " + e.Message); }
        // KEYS5: the Mods workspace's back/forward history takes each settled view once a frame.
        try { ModsMenu.TickNavigation(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Mods workspace history tick threw: " + e.Message); }
        // Chat stage 3 (mod links): a link waiting for the workspace to open, a share waiting for it to close.
        ModLinks.Tick();

        IKeyInputSource source;
        Registration[] snapshot;
        Registration? rebinding;
        Action<KeyCaptureResult>? capture;
        lock (Gate)
        {
            source = _source;
            if (Live.Count == 0 && _capture == null && _swallowButton == UiKey.None)
            {
                DisarmPumpLocked();
                return;
            }

            snapshot = PumpSnapshotLocked();
            rebinding = _rebinding;
            capture = _capture;
        }

        bool waitingForKey = rebinding != null || capture != null;
        long nowMs = waitingForKey ? Clock() : 0;
        bool padWait;
        lock (Gate) padWait = capture != null && _captureGamepad;
        int timeout = padWait ? GamepadCaptureTimeoutMs : CaptureTimeoutMs;
        if (waitingForKey && (nowMs - _waitStarted > timeout || nowMs - _waitBegan > CaptureMaxMs))
        {
            TimeOutWait(rebinding, capture, nowMs - _waitBegan > CaptureMaxMs ? CaptureMaxReason
                : padWait ? GamepadCaptureTimedOutReason : CaptureTimedOutReason);
            rebinding = null;
            capture = null;
            waitingForKey = false;
        }

        // KEYS8: a click a capture took holds Progenitor's widget clicks until that button is up again.
        TickSwallow(source);

        // The *effective* block, not merely "a panel is open" (job FUI19): it carries the pointer term,
        // so a panel open in one corner while the player looks at the world does not suppress their
        // binds, and a hovered or WhileShown panel does. Job FUI23 (R44 finding 2): IsFullyBlocking, not
        // IsBlocking — a panel the pointer is not over still consumes Escape, but not the keyboard.
        bool captured = UiInputCapture.IsFullyBlocking;

        // Nothing on this frame could fire and nothing is held or waiting for a key: return before
        // touching the source at all, so the frame costs one refcount read and a walk of a cached
        // array (R34 finding 16).
        if (!waitingForKey && !AnyLive(snapshot, captured) && !AnyHolding(snapshot)) return;

        bool available;
        try { available = source.IsAvailable; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind input source read threw: " + e.Message);
            ReleaseHolding(snapshot);
            return;
        }

        if (!available)
        {
            ReleaseHolding(snapshot);
            return;
        }

        // HOLDMOD1 review: the window losing focus (Alt+Tab) ends every hold, since the key's release happens in another
        // window and may never be seen. Only the edge acts: an unfocused window still runs binds (the bridge's key op
        // with background input), and a capture forgets a modifier it was watching.
        bool? focused;
        try { focused = source.WindowFocused; }
        catch { focused = null; }
        if (focused == false && _windowFocused)
        {
            _windowFocused = false;
            _loneSpoiled = true;
            if (AnyHolding(snapshot)) Plugin.Logger?.LogDiag(FocusLostReleaseLog);
            ReleaseHolding(snapshot);
        }
        else if (focused == true)
        {
            _windowFocused = true;
        }

        ChatFocus focus;
        try { focus = source.ChatFocus; }
        catch (Exception e)
        {
            // A source is contracted never to throw; one that does is exactly the "cannot prove
            // the player is not typing" case, so it lands in the same fail-closed branch.
            Plugin.Logger?.LogWarning("Ui: keybind chat-focus read threw: " + e.Message);
            focus = ChatFocus.Unknown;
        }

        // Fail closed (R35 finding 5): an absent or unreadable chat window cannot prove the player is
        // not typing, so Unknown suppresses binds as Focused does, except those that opted in with
        // allowWhenChatUnknown. Logged on the transition, not per frame.
        bool chatUnknown = focus == ChatFocus.Unknown;
        if (chatUnknown)
        {
            if (!_chatUnknown)
            {
                _chatUnknown = true;
                _chatUnknownPumps = 0;
                _chatUnknownWorldLogged = false;
                Plugin.Logger?.LogInfo(ChatUnknownLog);
            }

            if (_chatUnknownPumps < ChatUnknownWorldPumps) _chatUnknownPumps++;
            NoteUnknownWithClientWorld();
        }
        else if (_chatUnknown)
        {
            _chatUnknown = false;
            _chatUnknownPumps = 0;
            _chatUnknownWorldLogged = false;
            Plugin.Logger?.LogInfo("Ui: a chat window is readable again; keybinds are live.");
        }

        if (focus == ChatFocus.Focused)
        {
            ReleaseHolding(snapshot);
            // CHAT2: while the player types in chat only chat binds run (press only; they never hold).
            if (!waitingForKey) PumpChatBinds(source, snapshot);
            return;
        }

        // R36 finding 5: any focused text input the UI event system has selected gates every bind,
        // the allowWhenChatUnknown ones and the key capture included.
        bool typing;
        try { typing = source.TextFieldFocused; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind text-field read threw: " + e.Message);
            typing = false;
        }

        if (typing)
        {
            if (!_textFieldFocused)
            {
                _textFieldFocused = true;
                Plugin.Logger?.LogDiag(TextFieldFocusedLog);
            }

            // The key capture never reads a key while the player types; mouse-button binds still run below
            // (TypingSafeChord: a mouse button types nothing, so Mouse Back still navigates back).
            if (waitingForKey)
            {
                ReleaseHolding(snapshot);
                return;
            }
        }
        else if (_textFieldFocused)
        {
            _textFieldFocused = false;
            Plugin.Logger?.LogDiag("Ui: no text field has focus; keybinds are live.");
        }

        // A key capture reads *any* key, so it never runs while chat is unreadable and nothing of ours has
        // the input: the keys the player might be typing must not become a binding. Review 2026-09-29: when
        // our own panel holds the input (the Keybinds tab or a Keys row, at the title screen too) the player
        // is in our UI, so it runs; a focused text field still stops it above. While one waits no bind fires,
        // and the wait times out (CaptureTimeoutMs).
        if (waitingForKey)
        {
            ReleaseHolding(snapshot);
            if (chatUnknown && !captured) return;
            PumpCapture(source, rebinding, capture);
            return;
        }

        // KEYS1: the winner picture is rebuilt here when something changed, never on the press below.
        KeybindArbiter.RefreshIfStale();

        // TEXTUNDO1: a framework text field with owned focus takes its own keys (letters, Ctrl+Z, the
        // editing keys). Armed fields are read-only to TMP, so the TextFieldFocused probe above cannot be
        // relied on for them; a bind on such a chord waits until the field loses focus.
        bool ownedField = MenuHotkeyGuard.HotkeysBlocked;

        for (int i = 0; i < snapshot.Length; i++)
        {
            var reg = snapshot[i];
            if (reg.Disposed) continue;
            bool gated = !(captured && !reg.AllowWhileCaptured) && !(chatUnknown && !reg.AllowWhenChatUnknown)
                && ScopeOpen(reg.Scope);
            bool allowed = gated && !(typing && !TypingSafeChord(reg.Chord))
                && !(ownedField && UiTypedKeys.FieldTakes(reg.Chord))
                && ClickChordSuppressedReason(reg.Scope, reg.Chord) == null;
            // PAD1: a controller button types nothing and clicks nothing, so only the panel, chat and scope gates apply.
            bool padAllowed = gated && reg.GamepadChord.IsBound;

            if (reg.Holding)
            {
                bool still = false;
                var heldChord = reg.HoldingGamepad ? reg.GamepadChord : reg.Chord;
                if ((reg.HoldingGamepad ? padAllowed : allowed) && heldChord.IsBound)
                {
                    try { still = source.IsHeld(heldChord); }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning("Ui: keybind '" + reg.Label + "' held read threw: " + e.Message);
                    }
                }

                if (still) reg.RunWhileHeld();
                else reg.Release();
                continue;
            }

            if (allowed && reg.Chord.IsBound && PumpPress(source, reg, gamepad: false)) continue;
            if (padAllowed) PumpPress(source, reg, gamepad: true);
        }
    }

    /// <summary>
    /// One bind's press edge on one of its chords (the keyboard or mouse one, or PAD1 the controller one): the read, the
    /// click-over-our-UI and winner gates, the handler, and the hold. True when the chord went down (fired or not).
    /// </summary>
    private static bool PumpPress(IKeyInputSource source, Registration reg, bool gamepad)
    {
        var chord = gamepad ? reg.GamepadChord : reg.Chord;
        bool down;
        bool refusedAsAltGr = false;
        try { down = source.WasPressedThisFrame(chord, out refusedAsAltGr); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind '" + reg.Label + "' key read threw: " + e.Message);
            return false;
        }

        if (!down)
        {
            // Edge-triggered, so once per attempt: the only thing that tells a US-layout player why
            // the chord they are holding does nothing (R38 finding 4).
            if (refusedAsAltGr) Plugin.Logger?.LogWarning(AltGrNotFiredLog(reg.Label, chord));
            return false;
        }

        // KEYS8 review: on a click chord's press edge, a click whose top UI hit is a Progenitor object (a
        // passive overlay's button too) is that object's. The per-frame gate above covers capturing panels.
        if (KeyChord.IsClickButton(chord.Key) && PointerOnModUi(raycast: true))
        {
            Plugin.Logger?.LogDiag("Ui: keybind '" + reg.Label + "' was not fired: " + ClickOverModUiReason + ".");
            return true;
        }

        // KEYS1: the winner rule. A bind that lost its chord to the game or to a mod installed
        // first does not run — asked on the press edge only, so a quiet frame costs nothing.
        string? blocked = gamepad ? SafeGamepadWinnerGate(reg) : GateNow(reg);
        if (blocked != null)
        {
            Plugin.Logger?.LogInfo(NotFiredLog(reg.Label + (gamepad ? " (controller)" : ""), blocked));
            return true;
        }

        try { reg.OnPressed(gamepad); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ui: keybind '" + reg.Label + "' handler threw: " + e);
        }

        if (reg.HasHold)
        {
            reg.Holding = true;
            reg.HoldingGamepad = gamepad;
        }

        return true;
    }

    /// <summary>
    /// CHAT2: the pass that runs while the chat box has focus: only <see cref="KeybindScope.Chat"/> binds, on
    /// the press edge, through the same winner rule. Nothing else of the pump applies: the chat box is the text
    /// field these binds serve.
    /// </summary>
    private static void PumpChatBinds(IKeyInputSource source, Registration[] snapshot)
    {
        bool any = false;
        for (int i = 0; i < snapshot.Length; i++)
            if (!snapshot[i].Disposed && snapshot[i].Scope == KeybindScope.Chat) { any = true; break; }
        if (!any) return;

        // The same gates the press path (Dispatch) keeps: a mod panel taking input, and a Progenitor-owned text
        // field (which takes its own keys) both stop a chat bind.
        bool captured = UiInputCapture.IsFullyBlocking;
        if (MenuHotkeyGuard.HotkeysBlocked) return;

        KeybindArbiter.RefreshIfStale();
        for (int i = 0; i < snapshot.Length; i++)
        {
            var reg = snapshot[i];
            if (reg.Disposed || reg.Scope != KeybindScope.Chat || !reg.HasPress || !reg.Chord.IsBound) continue;
            if (captured && !reg.AllowWhileCaptured) continue;
            bool down;
            try { down = source.WasPressedThisFrame(reg.Chord, out _); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: keybind '" + reg.Label + "' key read threw: " + e.Message);
                continue;
            }

            if (!down) continue;
            string? blocked = GateNow(reg);
            if (blocked != null)
            {
                Plugin.Logger?.LogInfo(NotFiredLog(reg.Label, blocked));
                continue;
            }

            try { reg.OnPressed(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Ui: keybind '" + reg.Label + "' handler threw: " + e);
            }
        }
    }

    /// <summary>The rebind flow or the tab's key capture: read one chord and hand it on.</summary>
    private static void PumpCapture(IKeyInputSource source, Registration? rebinding, Action<KeyCaptureResult>? capture)
    {
        GamepadCapture? pad;
        lock (Gate) pad = rebinding == null && _captureGamepad ? _gamepadCapture : null;
        if (capture != null && pad != null)
        {
            PumpGamepadCapture(source, capture, pad);
            return;
        }

        // KEYS8: a left or right button the capture has not yet seen up belongs to the click that started it.
        if (!_leftArmed && !HeldOrUnknown(source, UiKey.MouseLeft)) _leftArmed = true;
        if (!_rightArmed && !HeldOrUnknown(source, UiKey.MouseRight)) _rightArmed = true;

        KeyChord pressed;
        ModifierSides pressedSides;
        bool got;
        try { got = source.TryReadPressedChord(out pressed, out pressedSides); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind rebind read threw: " + e.Message);
            return;
        }

        // HOLDMOD1: a key or button going down while a modifier is held makes it a chord, never a lone modifier.
        if (got) _loneSpoiled = true;
        else if (!TakeLoneModifier(source, out pressed, out pressedSides)) return;

        if (KeyChord.IsClickButton(pressed.Key))
        {
            if (!(pressed.Key == UiKey.MouseLeft ? _leftArmed : _rightArmed)) return;
            // Review: a bare click on Progenitor's own UI (Cancel, a scroll bar, empty panel space, the capturing
            // row) is ordinary navigation: no refusal flashes and the capture keeps waiting. Off our UI it is
            // refused below with its reason. The capture's help line says what a mouse key needs.
            if (pressed.Modifiers == KeyModifiers.None && PointerOnModUi(raycast: true)) return;
            // A click held with a modifier is the player choosing it (or a refusal they will read): its release
            // must not also press the Progenitor button under the pointer. A bare click is refused below and
            // still reaches the widget, so Cancel and Unbind keep working with the mouse during a capture.
            if (pressed.Modifiers != KeyModifiers.None) BeginSwallow(pressed.Key);
        }

        // KEYS2: a Windows-key shortcut, a reserved chord, or (for a rebind) a chord something else already
        // has is refused with its reason, and the capture keeps waiting so the player can try another key.
        bool altGr = pressed.IsAltGrShaped && KeyChord.IsAltGrPair(pressedSides);
        string? keepOpen = altGr ? null : CaptureRefusal(rebinding, pressed, pressedSides);
        if (keepOpen != null)
        {
            lock (Gate)
            {
                if (rebinding != null ? !ReferenceEquals(_rebinding, rebinding) : !ReferenceEquals(_capture, capture)) return;
                _waitStarted = Clock();
            }

            Plugin.Logger?.LogInfo("Ui: keybind '" + (rebinding?.Label ?? "keybinds tab") + "' key capture refused "
                + (KeyChord.IsWindowsKeyHeld(pressedSides) ? "a Windows-key shortcut" : pressed.ToString()) + ": " + keepOpen
                + " Still waiting for a key.");
            if (rebinding != null)
                RaiseRefused(new RebindResult(rebinding.Id, RebindOutcome.Refused, keepOpen, stillCapturing: true));
            else
                Deliver(capture!, new KeyCaptureResult(RebindOutcome.Refused, pressed, keepOpen, stillCapturing: true));
            return;
        }

        lock (Gate)
        {
            if (rebinding != null)
            {
                if (!ReferenceEquals(_rebinding, rebinding)) return;
                _rebinding = null;
            }
            else
            {
                if (!ReferenceEquals(_capture, capture)) return;
                _capture = null;
            }
        }

        string label = rebinding?.Label ?? "keybinds tab";

        // An AltGr-shaped keystroke is refused, but never silently and never by leaving the flow
        // waiting (R38 finding 4): the reason and the alternative go to the log and to the row.
        if (altGr)
        {
            Plugin.Logger?.LogWarning(AltGrRebindRefusedLog(label, pressed));
            if (rebinding != null)
                Raise(new RebindResult(rebinding.Id, RebindOutcome.Refused, AltGrRebindRefusedReason(pressed)));
            else
                Deliver(capture!, new KeyCaptureResult(RebindOutcome.Refused, pressed, AltGrRebindRefusedReason(pressed)));
            return;
        }

        if (pressed.Key == UiKey.Escape)
        {
            long at;
            try { at = Clock(); }
            catch { at = long.MinValue; }
            lock (Gate) _escapeCancelledAt = at;
            Plugin.Logger?.LogInfo("Ui: keybind '" + label + "' key capture canceled (Escape).");
            if (rebinding != null) Raise(new RebindResult(rebinding.Id, RebindOutcome.Cancelled));
            else Deliver(capture!, new KeyCaptureResult(RebindOutcome.Cancelled, KeyChord.None, null));
            return;
        }

        if (rebinding != null) Rebind(rebinding.PluginId, rebinding.Id, pressed);
        else Deliver(capture!, new KeyCaptureResult(RebindOutcome.Bound, pressed, null));
    }

    /// <summary>
    /// HOLDMOD1: the capture's lone-modifier rule. Ctrl, Shift or Alt pressed and released with no other key, button
    /// or modifier in between is that modifier on its own (<see cref="KeyChord.LoneModifier"/>), returned on the pump
    /// it comes up. A modifier already down when the capture began counts only after every modifier has been seen up.
    /// Two modifiers together (Ctrl+Alt released with no key) record nothing and the capture keeps waiting.
    /// </summary>
    private static bool TakeLoneModifier(IKeyInputSource source, out KeyChord chord, out ModifierSides sides)
    {
        chord = KeyChord.None;
        sides = ModifierSides.None;
        ModifierSides held, pressed;
        try
        {
            if (!source.TryReadModifiers(out held, out pressed)) return false;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind capture modifier read threw: " + e.Message);
            return false;
        }

        bool meta = KeyChord.IsWindowsKeyHeld(held);
        var mods = KeyChord.ModifiersOf(held);
        if (!_loneArmed)
        {
            if (mods != KeyModifiers.None || meta) return false;
            _loneArmed = true;
            _loneUnion = KeyModifiers.None;
            _loneSpoiled = false;
            return false;
        }

        // Review: a press edge counts even when the key is already up again (a tap shorter than one frame).
        if (meta || KeyChord.IsWindowsKeyHeld(pressed)) _loneSpoiled = true;
        _loneUnion |= mods | KeyChord.ModifiersOf(pressed);
        if (mods != KeyModifiers.None || meta) return false;

        var union = _loneUnion;
        bool spoiled = _loneSpoiled;
        _loneUnion = KeyModifiers.None;
        _loneSpoiled = false;
        if (spoiled || !KeyChord.IsSingleModifier(union)) return false;
        chord = KeyChord.LoneModifier(union);
        return true;
    }

    /// <summary>
    /// PAD1: the controller capture's pump. Escape on the keyboard cancels; any other key is ignored. The held buttons go
    /// to <see cref="GamepadCapture"/>, which decides the chord when the first button comes up. A refusal (three buttons)
    /// keeps the capture waiting, as a reserved key does.
    /// </summary>
    private static void PumpGamepadCapture(IKeyInputSource source, Action<KeyCaptureResult> capture, GamepadCapture pad)
    {
        bool escape = false;
        try
        {
            if (source.TryReadPressedChord(out var key, out _) && key.Key == UiKey.Escape) escape = true;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind controller capture key read threw: " + e.Message); }

        if (escape)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_capture, capture)) return;
                _capture = null;
                _gamepadCapture = null;
                try { _escapeCancelledAt = Clock(); }
                catch { _escapeCancelledAt = long.MinValue; }
            }

            Plugin.Logger?.LogInfo("Ui: keybind controller capture canceled (Escape).");
            Deliver(capture, new KeyCaptureResult(RebindOutcome.Cancelled, KeyChord.None, null));
            return;
        }

        uint held;
        bool connected;
        try { connected = source.TryReadGamepadHeld(out held); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: keybind controller capture read threw: " + e.Message);
            return;
        }

        if (!connected) return;
        bool ended = pad.Observe(held, out var chord, out var refusal);
        // A button held is the player choosing: the 5 s wait counts from the last read with none down.
        if (pad.Active)
        {
            lock (Gate)
                if (ReferenceEquals(_capture, capture)) _waitStarted = Clock();
        }

        if (!ended) return;
        if (refusal != null)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_capture, capture)) return;
                _waitStarted = Clock();
            }

            Plugin.Logger?.LogInfo("Ui: keybind controller capture refused: " + refusal + " Still waiting for a button.");
            Deliver(capture, new KeyCaptureResult(RebindOutcome.Refused, KeyChord.None, refusal, stillCapturing: true));
            return;
        }

        lock (Gate)
        {
            if (!ReferenceEquals(_capture, capture)) return;
            _capture = null;
            _gamepadCapture = null;
        }

        Deliver(capture, new KeyCaptureResult(RebindOutcome.Bound, chord, null));
    }

    /// <summary>
    /// KEYS2: why a captured keystroke cannot be used and the capture should keep waiting, or null. A key
    /// pressed with a Windows key held is Windows' shortcut (<c>Win+G</c>, <c>Win+Print Screen</c>: no
    /// <see cref="KeyChord"/> can express it); a reserved chord is refused with its own reason; a rebind's
    /// chord that something else already has is refused with the holder (the tab's capture checks that
    /// itself, against the row it edits). Escape alone is not refused: it cancels.
    /// </summary>
    internal static string? CaptureRefusal(IKeybindInfo? rebinding, KeyChord pressed, ModifierSides sides)
    {
        // Escape cancels whatever is held with it (Ctrl+Esc, Alt+Esc, Win+Esc): never refused here.
        if (pressed.Key == UiKey.Escape) return null;
        if (KeyChord.IsWindowsKeyHeld(sides)) return ReservedChords.WindowsKeyHeldReason;
        string? reserved = ReservedChords.Reason(pressed);
        if (reserved != null) return reserved;
        // KEYS8: the tab's capture does not know the row's scope yet (its own check does), so only the bare-click
        // rule, which holds in every scope, applies to it here.
        string? click = MouseChords.Refusal(pressed, rebinding != null ? ScopeOf(rebinding) : KeybindScope.Gameplay);
        if (click != null) return click;
        // HOLDMOD1: a press-only bind's rebind refuses a lone modifier (the tab's capture checks the row it edits).
        if (rebinding is Registration target && LoneModifierRefusal(pressed, target.HasHold, target.Scope) is { } lone)
            return lone;
        if (rebinding == null || pressed == rebinding.Chord) return null;
        // Fail closed: a check that cannot run refuses the key rather than allowing a possible clash.
        try { return ClashFor(rebinding.PluginId, rebinding.Id, pressed)?.Reason; }
        catch { return KeybindRemapGuard.CheckFailedReason; }
    }

    private static void TimeOutWait(Registration? rebinding, Action<KeyCaptureResult>? capture, string why)
    {
        lock (Gate)
        {
            if (rebinding != null && ReferenceEquals(_rebinding, rebinding)) _rebinding = null;
            if (capture != null && ReferenceEquals(_capture, capture)) _capture = null;
        }

        Plugin.Logger?.LogInfo("Ui: keybind key capture timed out (" + why + "); binds are live again.");
        if (rebinding != null) Raise(new RebindResult(rebinding.Id, RebindOutcome.Cancelled, why));
        if (capture != null) Deliver(capture, new KeyCaptureResult(RebindOutcome.Cancelled, KeyChord.None, why));
    }

    /// <summary>KEYS1: the owner of <paramref name="pluginId"/>'s first live bind (its plugin instance), or null.</summary>
    internal static object? OwnerOf(string? pluginId)
    {
        lock (Gate)
        {
            for (int i = 0; i < Live.Count; i++)
                if (string.Equals(Live[i].PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) return Live[i].Owner;
        }

        return null;
    }

    private static bool AnyHolding(Registration[] snapshot)
    {
        for (int i = 0; i < snapshot.Length; i++)
            if (snapshot[i].Holding) return true;
        return false;
    }

    /// <summary>A hold ends when its bind stops being allowed to fire, so a held action never sticks.</summary>
    private static void ReleaseHolding(Registration[] snapshot)
    {
        for (int i = 0; i < snapshot.Length; i++)
            if (snapshot[i].Holding) snapshot[i].Release();
    }

    /// <summary>Drop every bind this plugin registered. Returns how many. Plugin unload.</summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        Registration[] doomed;
        lock (Gate)
        {
            var list = new List<Registration>();
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(Live[i].Owner, owner)) continue;
                list.Add(Live[i]);
            }

            doomed = list.ToArray();
        }

        for (int i = 0; i < doomed.Length; i++)
            doomed[i].Dispose();

        if (doomed.Length > 0)
            Plugin.Logger?.LogInfo("Ui: keybinds removed=" + doomed.Length + " for the unloading plugin.");
        return doomed.Length;
    }

    /// <summary>
    /// Whether <paramref name="chord"/> may fire while a text field has focus: a mouse-button chord, which can never be a
    /// character the player is typing (its modifiers type nothing either). Keys stay suppressed. User report 2026-10-10:
    /// with the search box focused, Mouse Back (Navigate back) was dead.
    /// </summary>
    internal static bool TypingSafeChord(KeyChord chord) => chord.IsBound && (KeyChord.IsMouseKey(chord.Key) || chord.IsGamepad);

    /// <summary>
    /// <c>mods_ui nav_state</c> <c>textFocus</c>: whether the key source sees a focused text field now (every key bind but
    /// the mouse-button ones held back), whether the pump last saw one, and which Progenitor field holds owned focus.
    /// </summary>
    internal static Dictionary<string, object?> TextFocusReadback()
    {
        IKeyInputSource source;
        lock (Gate)
            source = _source;
        bool probe;
        try { probe = source.TextFieldFocused; }
        catch { probe = false; }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["textFieldFocused"] = probe,
            ["pumpSawTextField"] = _textFieldFocused,
            ["ownedField"] = MenuHotkeyGuard.FocusedField,
            ["keysSuppressed"] = probe,
            ["mouseBindsLive"] = true,
        };
    }

    /// <summary>
    /// True while the player is typing in the chat box or another text field, so their keystrokes
    /// are text, not play. Read by <c>ctx.Input</c>'s user-activity sampling. An unreadable chat
    /// window counts as not typing.
    /// </summary>
    internal static bool PlayerIsTyping
    {
        get
        {
            IKeyInputSource source;
            lock (Gate)
                source = _source;
            try { return source.ChatFocus == ChatFocus.Focused || source.TextFieldFocused; }
            catch { return false; }
        }
    }

    /// <summary>KEYS4: what the key source reads of the chat window now (Unknown on a loading screen or at the title).</summary>
    internal static ChatFocus ChatFocusNow
    {
        get
        {
            IKeyInputSource source;
            lock (Gate)
                source = _source;
            try { return source.ChatFocus; }
            catch { return ChatFocus.Unknown; }
        }
    }

    /// <summary>DIALOGS1: true while the game's chat box has focus (Enter is the chat's then, not a dialog's).</summary>
    internal static bool ChatIsFocused
    {
        get
        {
            IKeyInputSource source;
            lock (Gate)
                source = _source;
            try { return source.ChatFocus == ChatFocus.Focused; }
            catch { return false; }
        }
    }

    /// <summary>Point the module at the process input source. <c>Ui.Install</c> passes the real one.</summary>
    internal static void Use(IKeyInputSource? source)
    {
        lock (Gate)
            _source = source ?? NullKeyInputSource.Instance;
    }

    /// <summary>Core Uninstall: every bind goes, the frame subscription goes, no handler survives.</summary>
    internal static void Uninstall()
    {
        Registration[] doomed;
        lock (Gate)
        {
            doomed = Live.ToArray();
            Live.Clear();
            _pumpSnapshot = null;
            Bump();
            _rebinding = null;
            _capture = null;
            _source = NullKeyInputSource.Instance;
            _captureGamepad = false;
            _gamepadCapture = null;
            DisarmPumpLocked();
        }

        for (int i = 0; i < doomed.Length; i++)
            doomed[i].MarkDisposed();

        RebindFinished = null;
        RebindRefused = null;
        KeybindConflictPrompt.Reset();
        if (doomed.Length > 0)
            Plugin.Logger?.LogInfo("Ui: keybinds uninstalled=" + doomed.Length);
    }

    /// <summary>One grep line for <c>Ui.DescribePins</c>.</summary>
    internal static string Describe() => "keybinds=" + Count + (RebindingId != null ? " rebinding" : "");

    /// <summary>
    /// The pump's view of the registry, rebuilt only when it changed. Callers must not hand the
    /// array out or write to it: it is shared across frames on purpose (R34 finding 16).
    /// </summary>
    private static Registration[] PumpSnapshotLocked() => _pumpSnapshot ??= Live.ToArray();

    /// <summary>
    /// True when at least one registration could fire on this frame. No allocation and no read of
    /// the input source, so a client whose only bind is suppressed pays a walk of a cached array
    /// and nothing else.
    /// </summary>
    private static bool AnyLive(Registration[] snapshot, bool captured)
    {
        for (int i = 0; i < snapshot.Length; i++)
        {
            var reg = snapshot[i];
            if (reg.Disposed) continue;
            if (captured && !reg.AllowWhileCaptured) continue;
            if (!reg.Chord.IsBound && !reg.GamepadChord.IsBound) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The real failure case behind <see cref="ChatFocus.Unknown"/>: a client world exists, so the
    /// player is in a world with a HUD, and we still cannot read a chat window. One warning per
    /// unknown episode, after <see cref="ChatUnknownWorldPumps"/> consecutive polls.
    /// </summary>
    private static void NoteUnknownWithClientWorld()
    {
        if (_chatUnknownWorldLogged) return;
        if (_chatUnknownPumps < ChatUnknownWorldPumps) return;

        bool inWorld;
        try { inWorld = _clientWorldPresent(); }
        catch { return; }

        if (!inWorld) return;
        _chatUnknownWorldLogged = true;
        Plugin.Logger?.LogWarning(ChatUnknownWithClientWorldLog);
    }

    private static bool DefaultClientWorldPresent() =>
        Progenitor.WorldState.ClientWorldEpoch.ObservedSequence != 0;

    private static KeyChord ReadChord(ConfigEntry<string>? entry, string pluginId, string id, KeyChord fallback)
    {
        if (entry == null) return fallback;
        string? raw;
        try { raw = entry.Value; }
        catch { return fallback; }

        if (KeyChord.TryParse(raw, out var chord)) return chord;
        Plugin.Logger?.LogWarning(BadValueLog(pluginId, id, raw, fallback));
        return fallback;
    }

    private static void Raise(RebindResult result)
    {
        var handler = RebindFinished;
        if (handler == null) return;
        try { handler(result); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind RebindFinished threw: " + e.Message); }
    }

    private static void Forget(Registration registration)
    {
        bool empty;
        lock (Gate)
        {
            Live.Remove(registration);
            _pumpSnapshot = null;
            Bump();
            if (ReferenceEquals(_rebinding, registration)) _rebinding = null;
            empty = Live.Count == 0;
            if (empty && _capture == null) DisarmPumpLocked();
        }
    }

    private static void ArmPumpLocked()
    {
        if (_pumpArmed) return;
        _pumpArmed = true;
#if !PROGENITOR_TESTS
        try { Plugin.Frame += Pump; }
        catch (Exception e)
        {
            _pumpArmed = false;
            Plugin.Logger?.LogWarning("Ui: keybind pump subscribe threw: " + e.Message);
        }
#endif
    }

    private static void DisarmPumpLocked()
    {
        if (!_pumpArmed) return;
        _pumpArmed = false;
#if !PROGENITOR_TESTS
        try { Plugin.Frame -= Pump; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind pump unsubscribe threw: " + e.Message); }
#endif
    }

#if PROGENITOR_TESTS
    internal static void SetSourceForTests(IKeyInputSource? source) => Use(source);

    /// <summary>
    /// The pump's cached registry array. Exposed so a test can assert the identity across frames:
    /// the point of the cache is that a quiet frame allocates nothing (R34 finding 16), and only
    /// reference identity can show that.
    /// </summary>
    internal static Array PumpSnapshotForTests()
    {
        lock (Gate) return PumpSnapshotLocked();
    }

    /// <summary>True while the frame subscription is held. Test processes have no frames.</summary>
    internal static bool PumpArmedForTests
    {
        get { lock (Gate) return _pumpArmed; }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Live.Clear();
            _pumpSnapshot = null;
            Bump();
            _rebinding = null;
            _capture = null;
            _escapeCancelledAt = long.MinValue;
            _waitStarted = 0;
            _waitBegan = 0;
            _source = NullKeyInputSource.Instance;
            _pumpArmed = false;
            Clock = () => Environment.TickCount64;
            WinnerGate = KeybindArbiter.BlockedReason;
            GamepadWinnerGate = KeybindArbiter.GamepadBlockedReason;
            _captureGamepad = false;
            _gamepadCapture = null;
            _actingGamepad = false;
            _chatUnknown = false;
            _chatUnknownPumps = 0;
            _chatUnknownWorldLogged = false;
            _textFieldFocused = false;
            _clientWorldPresent = DefaultClientWorldPresent;
        }

        RebindFinished = null;
        RebindRefused = null;
        MenuScopeOpen = DefaultMenuScopeOpen;
        WindowScopeOpen = DefaultWindowScopeOpen;
        WindowHoldsInput = DefaultWindowHoldsInput;
        PhotoScopeOpen = NeverOpen;
        MapScopeOpen = DefaultMapScopeOpen;
        ChatScopeOpen = DefaultChatScopeOpen;
        PointerOverModUi = DefaultPointerOverModUi;
        GameMenuOpen = DefaultGameMenuOpen;
        PointerHitsModUi = DefaultPointerHitsModUi;
        _leftArmed = _rightArmed = _loneArmed = false;
        _swallowButton = UiKey.None;
        _windowFocused = true;
        _swallowReleasedPumps = -1;
        _swallowSince = 0;
        KeybindConflictPrompt.ResetForTests();
        KeybindGroups.ResetForTests();
    }

    /// <summary>
    /// Replace "is there a client world?" for a test, so the R35 finding 5 warning can be exercised
    /// without moving the process-wide client epoch other suites read.
    /// </summary>
    internal static void SetClientWorldProbeForTests(Func<bool>? probe)
    {
        lock (Gate)
            _clientWorldPresent = probe ?? DefaultClientWorldPresent;
    }
#endif

    private sealed class Registration : IKeybindHandle, IKeybindInfo
    {
        private readonly Action? _onPressed;
        private readonly Action? _whileHeld;
        private readonly Action? _onReleased;
        private int _disposed;

        public Registration(
            object? owner,
            string pluginId,
            string id,
            string description,
            ConfigEntry<string>? entry,
            KeyChord defaultChord,
            KeyChord chord,
            Action? onPressed,
            bool allowWhileCaptured,
            bool allowWhenChatUnknown,
            Action? whileHeld = null,
            Action? onReleased = null)
        {
            _whileHeld = whileHeld;
            _onReleased = onReleased;
            Owner = owner;
            PluginId = pluginId;
            Id = id;
            Description = description;
            Entry = entry;
            DefaultChord = defaultChord;
            Chord = chord;
            _onPressed = onPressed;
            AllowWhileCaptured = allowWhileCaptured;
            AllowWhenChatUnknown = allowWhenChatUnknown;
        }

        public object? Owner { get; }

        public string PluginId { get; }

        public string Id { get; }

        public string Description { get; }

        public ConfigEntry<string>? Entry { get; }

        public KeyChord DefaultChord { get; }

        public KeyChord Chord { get; set; }

        /// <summary>PAD1: the controller chord (<see cref="KeyChord.None"/> when it has none).</summary>
        public KeyChord GamepadChord { get; set; }

        /// <summary>PAD1: the plugin's controller default (<see cref="Keybinds.SetGamepadDefault"/>).</summary>
        public KeyChord DefaultGamepadChord { get; set; }

        /// <summary>PAD1: the <c>&lt;id&gt;.Gamepad</c> cfg entry, bound once the bind has a controller default or value.</summary>
        public ConfigEntry<string>? GamepadEntry { get; set; }

        /// <summary>PAD1: the owning plugin's cfg, for binding <see cref="GamepadEntry"/> when it is first needed.</summary>
        public ConfigFile? File { get; init; }

        /// <summary>PAD1: the hold now running was started by the controller chord (its button is the one read for held).</summary>
        public bool HoldingGamepad { get; set; }

        /// <summary>KEYS2: the reserved chord refused at registration, until the player picks a key.</summary>
        public ReservedNote? Reserved { get; set; }

        /// <summary>KEYS4: where the bind works (<see cref="KeybindScope.Menu"/>: only while the Mods workspace is open).</summary>
        public KeybindScope Scope { get; init; }

        public bool AllowWhileCaptured { get; }

        /// <summary>See <see cref="Keybinds.Register"/>: fires while no chat window can be read.</summary>
        public bool AllowWhenChatUnknown { get; }

        public bool Disposed => System.Threading.Volatile.Read(ref _disposed) != 0;

        public bool IsDisposed => Disposed;

        public string Label => PluginId + "/" + Id;

        public void OnPressed() => _onPressed?.Invoke();

        /// <summary>PAD1: the press handler, with <see cref="Keybinds.PressedWithGamepad"/> saying which chord fired.</summary>
        public void OnPressed(bool gamepad)
        {
            bool was = _actingGamepad;
            _actingGamepad = gamepad;
            try { _onPressed?.Invoke(); }
            finally { _actingGamepad = was; }
        }

        public bool HasPress => _onPressed != null;

        /// <summary>KEYS1: true when the bind has a while-held or release handler, so a press starts a hold.</summary>
        public bool HasHold => _whileHeld != null || _onReleased != null;

        /// <summary>Pump-thread state: the press that started a hold has not been released yet.</summary>
        public bool Holding { get; set; }

        public void RunWhileHeld()
        {
            if (_whileHeld == null) return;
            bool was = _actingGamepad;
            _actingGamepad = HoldingGamepad;
            try { _whileHeld(); }
            catch (Exception e) { Plugin.Logger?.LogError("Ui: keybind '" + Label + "' while-held handler threw: " + e); }
            finally { _actingGamepad = was; }
        }

        public void Release()
        {
            if (!Holding) return;
            Holding = false;
            bool pad = HoldingGamepad;
            HoldingGamepad = false;
            if (_onReleased == null) return;
            bool was = _actingGamepad;
            _actingGamepad = pad;
            try { _onReleased(); }
            catch (Exception e) { Plugin.Logger?.LogError("Ui: keybind '" + Label + "' release handler threw: " + e); }
            finally { _actingGamepad = was; }
        }

        public bool Matches(string? pluginId, string id) =>
            string.Equals(Id, id, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(pluginId)
                || string.Equals(PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

        public void MarkDisposed()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Review 2026-09-29: a bind that goes while held is released, so a held action never sticks.
            Release();
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release();
            Forget(this);
        }
    }
}

/// <summary>How a press-a-key flow ended.</summary>
public enum RebindOutcome
{
    /// <summary>The chord was stored and written through to the cfg.</summary>
    Bound = 0,

    /// <summary>Escape, <see cref="Keybinds.CancelRebind"/>, or the bind went away. Nothing is wrong.</summary>
    Cancelled = 1,

    /// <summary>
    /// The chord was refused and the bind is unchanged. <see cref="RebindResult.Reason"/> says why
    /// and what to press instead; the flow is over either way, so the panel must leave "press a
    /// key" (R38 finding 4).
    /// </summary>
    Refused = 2,
}

/// <summary>What <see cref="Keybinds.RebindFinished"/> carries.</summary>
public readonly struct RebindResult
{
    public RebindResult(string id, RebindOutcome outcome, string? reason = null)
    {
        Id = id;
        Outcome = outcome;
        Reason = reason;
        StillCapturing = false;
    }

    /// <summary>KEYS2: a refusal after which the press-a-key flow keeps waiting (<see cref="StillCapturing"/>).</summary>
    public RebindResult(string id, RebindOutcome outcome, string? reason, bool stillCapturing)
        : this(id, outcome, reason)
    {
        StillCapturing = stillCapturing;
    }

    /// <summary>The keybind id whose flow ended.</summary>
    public string Id { get; }

    /// <summary>Bound, canceled, or refused.</summary>
    public RebindOutcome Outcome { get; }

    /// <summary>
    /// One row-sized sentence for <see cref="RebindOutcome.Refused"/>, null otherwise. The long
    /// form, with the rule and the citation, goes to the log.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// KEYS2: true when the chord was refused (reserved by Windows or Steam, a Windows-key shortcut, or
    /// already used by something else) and the flow is <b>still</b> waiting for a key, so a panel keeps
    /// "press a key" and shows <see cref="Reason"/> next to it. False for every other result.
    /// </summary>
    public bool StillCapturing { get; }
}

/// <summary>A registered keybind. <c>Dispose</c> unregisters it; the cfg value stays.</summary>
public interface IKeybindHandle : IDisposable
{
    /// <summary>Stable id inside <see cref="Keybinds.Section"/>.</summary>
    string Id { get; }

    /// <summary>Owning plugin GUID.</summary>
    string PluginId { get; }

    /// <summary>Chord this bind currently answers to.</summary>
    KeyChord Chord { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/> or a Core uninstall.</summary>
    bool IsDisposed { get; }

    /// <summary>
    /// PAD1: the controller chord this bind also answers to (<see cref="KeyChord.Gamepad"/>), or <see cref="KeyChord.None"/>.
    /// Set a default with <c>ctx.Keybinds.SetGamepad</c>; the player can change it in the Keybinds tab.
    /// </summary>
    KeyChord GamepadChord => KeyChord.None;
}

/// <summary>Read-only view of one keybind, for the settings panel and diagnostics.</summary>
public interface IKeybindInfo
{
    string PluginId { get; }

    string Id { get; }

    string Description { get; }

    KeyChord Chord { get; }

    /// <summary>What the plugin asked for, so the panel can offer "reset".</summary>
    KeyChord DefaultChord { get; }

    /// <summary>PAD1: the controller chord (<see cref="KeyChord.None"/> when it has none).</summary>
    KeyChord GamepadChord => KeyChord.None;

    /// <summary>PAD1: the plugin's controller default (<see cref="KeyChord.None"/> when it set none).</summary>
    KeyChord DefaultGamepadChord => KeyChord.None;
}

/// <summary>KEYS1: how the Keybinds tab's key capture ended (<see cref="Keybinds.BeginCapture"/>).</summary>
internal readonly struct KeyCaptureResult
{
    internal KeyCaptureResult(RebindOutcome outcome, KeyChord chord, string? reason, bool stillCapturing = false)
    {
        Outcome = outcome;
        Chord = chord;
        Reason = reason;
        StillCapturing = stillCapturing;
    }

    /// <summary>KEYS2: refused, and the capture is still waiting for a key (<see cref="RebindResult.StillCapturing"/>).</summary>
    internal bool StillCapturing { get; }

    /// <summary><see cref="RebindOutcome.Bound"/> means a chord was read (nothing is stored yet).</summary>
    internal RebindOutcome Outcome { get; }

    internal KeyChord Chord { get; }

    internal string? Reason { get; }
}

/// <summary>What <see cref="Keybinds.SimulatePress(string?, string)"/> did with one bind.</summary>
public readonly struct KeybindDispatch
{
    public KeybindDispatch(string pluginId, string id, string chord, bool fired, string reason)
    {
        PluginId = pluginId ?? "";
        Id = id ?? "";
        Chord = chord ?? "";
        Fired = fired;
        Reason = reason ?? "";
    }

    public string PluginId { get; }

    public string Id { get; }

    /// <summary>The chord the bind answers to, in the cfg form.</summary>
    public string Chord { get; }

    /// <summary>True when the press handler ran.</summary>
    public bool Fired { get; }

    /// <summary>Empty when fired; otherwise <c>unknown bind</c>, <c>unbound</c>, <c>suppressed: …</c>, <c>not-active: …</c> or <c>handler threw: …</c>.</summary>
    public string Reason { get; }
}
