namespace Progenitor.Ui;

/// <summary>
/// What one physical key means to the owned text-input path. Game-free so the
/// Mods search sequence, modifier suppression and the Digit0-index trap have
/// tests that do not need TMP or the Input System.
/// </summary>
internal enum UiTypedCommandKind
{
    None = 0,

    /// <summary>Shift / Ctrl / Alt / Meta on their own. Consumed; no character.</summary>
    Modifier = 1,

    Insert = 2,
    Backspace = 3,
    Delete = 4,
    Left = 5,
    Right = 6,
    Home = 7,
    End = 8,

    /// <summary>Ctrl (without Alt/Meta) plus a letter: A/C/X/V or a consumed no-op.</summary>
    Shortcut = 9,
}

/// <summary>One decided keystroke. <see cref="Char"/> is the insert or shortcut letter.</summary>
internal readonly struct UiTypedCommand
{
    public UiTypedCommand(UiTypedCommandKind kind, char c = '\0')
    {
        Kind = kind;
        Char = c;
    }

    public UiTypedCommandKind Kind { get; }

    public char Char { get; }

    public static UiTypedCommand None => default;

    public static UiTypedCommand Modifier { get; } = new(UiTypedCommandKind.Modifier);

    public static UiTypedCommand Insert(char c) => new(UiTypedCommandKind.Insert, c);

    public static UiTypedCommand Backspace { get; } = new(UiTypedCommandKind.Backspace);

    public static UiTypedCommand Delete { get; } = new(UiTypedCommandKind.Delete);

    public static UiTypedCommand Left { get; } = new(UiTypedCommandKind.Left);

    public static UiTypedCommand Right { get; } = new(UiTypedCommandKind.Right);

    public static UiTypedCommand Home { get; } = new(UiTypedCommandKind.Home);

    public static UiTypedCommand End { get; } = new(UiTypedCommandKind.End);

    public static UiTypedCommand Shortcut(char letter) => new(UiTypedCommandKind.Shortcut, letter);
}

/// <summary>
/// Deliberate physical-key mapping for the shown-input pump. This build cannot
/// subscribe to <c>Keyboard.onTextInput</c> from collectible Core, so the pump
/// samples keys and maps them here — never by <c>Key</c> enum index. Editing keys
/// always come from here; printable characters come from Bootstrap's text queue
/// when it is present (<see cref="UiTextInputMerge"/>), and from the US map here
/// otherwise.
///
/// <para>
/// Unity <c>Key.Digit0 = 50</c> and <c>Key.LeftCtrl = 55</c> on this client
/// (the IL viewer, <c>Unity.InputSystem.dll</c>). A loop of
/// <c>(Key)((int)Key.Digit0 + i)</c> with <c>(char)('0' + i)</c> therefore
/// treats Ctrl as digit 5 and Shift as digit 1. Digits are named one by one.
/// </para>
///
/// <para>
/// The pump is the only writer. Armed TMP fields set <c>readOnly = true</c>
/// (and <c>onFocusSelectAll = false</c>), the same sole-writer rule as
/// <c>scrollSensitivity = 0</c> for the wheel. The caret lives in
/// <see cref="UiFieldCaret"/> because a <c>field.text</c> assign resets TMP's
/// <c>stringPosition</c> to 0.
/// </para>
/// </summary>
internal static class UiTypedKeys
{
    /// <summary>
    /// TMP's own key processing is switched off on every armed field.
    /// Two writers (native + pump) is the live "bb" / "ll" duplication.
    /// </summary>
    internal const bool NativeTmpProcessingDisabled = true;

    /// <summary>
    /// Clipboard paste is <c>GUIUtility.systemCopyBuffer</c>, a supported Unity
    /// seam. Ctrl+V goes through <see cref="UiTextEdit.Paste"/> rather than typing <c>v</c>.
    /// </summary>
    internal const bool ClipboardPasteWired = true;

    internal static bool IsModifierKey(UiKey key) =>
        key is UiKey.LeftShift or UiKey.RightShift
            or UiKey.LeftAlt or UiKey.RightAlt
            or UiKey.LeftCtrl or UiKey.RightCtrl
            or UiKey.LeftMeta or UiKey.RightMeta;

    /// <summary>
    /// Named digit keys only. Do not compute <c>'0' + (key - Digit0)</c>:
    /// <see cref="UiKey.Digit0"/> sits after <see cref="UiKey.Digit9"/>.
    /// </summary>
    internal static bool TryDigitChar(UiKey key, out char c)
    {
        switch (key)
        {
            case UiKey.Digit0: c = '0'; return true;
            case UiKey.Digit1: c = '1'; return true;
            case UiKey.Digit2: c = '2'; return true;
            case UiKey.Digit3: c = '3'; return true;
            case UiKey.Digit4: c = '4'; return true;
            case UiKey.Digit5: c = '5'; return true;
            case UiKey.Digit6: c = '6'; return true;
            case UiKey.Digit7: c = '7'; return true;
            case UiKey.Digit8: c = '8'; return true;
            case UiKey.Digit9: c = '9'; return true;
            default:
                c = '\0';
                return false;
        }
    }

    internal static bool TryLetter(UiKey key, out char lower)
    {
        if (key < UiKey.A || key > UiKey.Z)
        {
            lower = '\0';
            return false;
        }

        lower = (char)('a' + (key - UiKey.A));
        return true;
    }

    /// <summary>
    /// TEXTUNDO1: true when a framework text field with focus acts on <paramref name="chord"/> itself:
    /// a printable key (typed, or with Ctrl a shortcut such as Ctrl+Z; Ctrl+Alt is AltGr and types), or an
    /// editing key (Backspace, Delete, the arrows, Home, End). A bind on such a chord must not also fire
    /// while the field has focus. Alt without Ctrl, function keys, Escape and mouse buttons are not the
    /// field's: the field ignores them, so binds on them (F10, Back and Forward) keep working.
    /// </summary>
    internal static bool FieldTakes(KeyChord chord)
    {
        if (!chord.IsBound) return false;
        var key = chord.Key;
        if (key is UiKey.Backspace or UiKey.Delete or UiKey.LeftArrow or UiKey.RightArrow or UiKey.Home or UiKey.End)
            return true;
        if (!KeyChord.IsPrintableKey(key)) return false;
        bool ctrl = (chord.Modifiers & KeyModifiers.Ctrl) != 0;
        bool alt = (chord.Modifiers & KeyModifiers.Alt) != 0;
        return !alt || ctrl;
    }

    internal static UiTypedCommand Decide(UiKey key, bool ctrl, bool alt, bool meta, bool shift)
    {
        if (key == UiKey.None) return UiTypedCommand.None;
        if (IsModifierKey(key)) return UiTypedCommand.Modifier;

        bool suppress = UiTextEdit.SuppressPrintable(ctrl, alt, meta);
        bool ctrlOnly = ctrl && !alt && !meta;

        switch (key)
        {
            case UiKey.Backspace:
                return suppress ? UiTypedCommand.None : UiTypedCommand.Backspace;
            case UiKey.Delete:
                return suppress ? UiTypedCommand.None : UiTypedCommand.Delete;
            case UiKey.LeftArrow:
                return UiTypedCommand.Left;
            case UiKey.RightArrow:
                return UiTypedCommand.Right;
            case UiKey.Home:
                return UiTypedCommand.Home;
            case UiKey.End:
                return UiTypedCommand.End;
        }

        if (TryLetter(key, out char lower))
        {
            if (ctrlOnly) return UiTypedCommand.Shortcut(lower);
            if (suppress) return UiTypedCommand.None;
            return UiTypedCommand.Insert(shift ? char.ToUpperInvariant(lower) : lower);
        }

        if (suppress) return UiTypedCommand.None;
        if (TryDigitChar(key, out char digit))
            return UiTypedCommand.Insert(shift ? ShiftedDigit(digit) : digit);
        if (TryUsPunctuation(key, shift, out char punct))
            return UiTypedCommand.Insert(punct);
        if (TryNumpadChar(key, out char pad))
            return UiTypedCommand.Insert(pad);
        return UiTypedCommand.None;
    }

    /// <summary>
    /// TEXTINPUT1 fallback: the US layout's Shift row over the digits (<c>1</c>→<c>!</c> …
    /// <c>0</c>→<c>)</c>). Used only when Bootstrap's typed-character queue is unavailable.
    /// </summary>
    internal static char ShiftedDigit(char digit) => digit switch
    {
        '1' => '!',
        '2' => '@',
        '3' => '#',
        '4' => '$',
        '5' => '%',
        '6' => '^',
        '7' => '&',
        '8' => '*',
        '9' => '(',
        '0' => ')',
        _ => digit,
    };

    /// <summary>
    /// TEXTINPUT1 fallback: the US layout's punctuation keys, unshifted and shifted, plus Space.
    /// A non-US player gets the US character for the physical key here; the queue path
    /// (<see cref="UiTextInputMerge"/>) gives the real one.
    /// </summary>
    internal static bool TryUsPunctuation(UiKey key, bool shift, out char c)
    {
        switch (key)
        {
            case UiKey.Space: c = ' '; return true;
            case UiKey.Backquote: c = shift ? '~' : '`'; return true;
            case UiKey.Minus: c = shift ? '_' : '-'; return true;
            case UiKey.Equals: c = shift ? '+' : '='; return true;
            case UiKey.LeftBracket: c = shift ? '{' : '['; return true;
            case UiKey.RightBracket: c = shift ? '}' : ']'; return true;
            case UiKey.Backslash: c = shift ? '|' : '\\'; return true;
            case UiKey.Semicolon: c = shift ? ':' : ';'; return true;
            case UiKey.Quote: c = shift ? '"' : '\''; return true;
            case UiKey.Comma: c = shift ? '<' : ','; return true;
            case UiKey.Period: c = shift ? '>' : '.'; return true;
            case UiKey.Slash: c = shift ? '?' : '/'; return true;
            default: c = '\0'; return false;
        }
    }

    /// <summary>
    /// TEXTINPUT1 fallback: the numpad's printing keys (digits assume Num Lock on; this read cannot
    /// see the lock state). Numpad Enter is a line break or submit, not a character.
    /// </summary>
    internal static bool TryNumpadChar(UiKey key, out char c)
    {
        switch (key)
        {
            case UiKey.Numpad0: c = '0'; return true;
            case UiKey.Numpad1: c = '1'; return true;
            case UiKey.Numpad2: c = '2'; return true;
            case UiKey.Numpad3: c = '3'; return true;
            case UiKey.Numpad4: c = '4'; return true;
            case UiKey.Numpad5: c = '5'; return true;
            case UiKey.Numpad6: c = '6'; return true;
            case UiKey.Numpad7: c = '7'; return true;
            case UiKey.Numpad8: c = '8'; return true;
            case UiKey.Numpad9: c = '9'; return true;
            case UiKey.NumpadDivide: c = '/'; return true;
            case UiKey.NumpadMultiply: c = '*'; return true;
            case UiKey.NumpadPlus: c = '+'; return true;
            case UiKey.NumpadMinus: c = '-'; return true;
            case UiKey.NumpadPeriod: c = '.'; return true;
            case UiKey.NumpadEquals: c = '='; return true;
            default: c = '\0'; return false;
        }
    }

    internal static UiTextEditResult Execute(
        UiTextEditState state,
        UiTypedCommand command,
        bool shift,
        string? clipboard,
        int maxLength = int.MaxValue)
    {
        switch (command.Kind)
        {
            case UiTypedCommandKind.None:
                return new UiTextEditResult(UiTextEdit.Normalize(state), false, false);
            case UiTypedCommandKind.Modifier:
                return new UiTextEditResult(UiTextEdit.Normalize(state), false, true);
            case UiTypedCommandKind.Insert:
                return UiTextEdit.TypeChar(state, command.Char, maxLength);
            case UiTypedCommandKind.Backspace:
                return UiTextEdit.TypeChar(state, '\b', maxLength);
            case UiTypedCommandKind.Delete:
            {
                var next = UiTextEdit.Delete(state);
                state = UiTextEdit.Normalize(state);
                return new UiTextEditResult(next, next.Text != state.Text, true);
            }
            case UiTypedCommandKind.Left:
                return new UiTextEditResult(UiTextEdit.Left(state, shift), false, true);
            case UiTypedCommandKind.Right:
                return new UiTextEditResult(UiTextEdit.Right(state, shift), false, true);
            case UiTypedCommandKind.Home:
                return new UiTextEditResult(UiTextEdit.Home(state, shift), false, true);
            case UiTypedCommandKind.End:
                return new UiTextEditResult(UiTextEdit.End(state, shift), false, true);
            case UiTypedCommandKind.Shortcut:
                return UiTextEdit.ApplyShortcut(state, command.Char, clipboard, maxLength);
            default:
                return new UiTextEditResult(UiTextEdit.Normalize(state), false, false);
        }
    }

    internal static UiTextEditResult Apply(
        UiTextEditState state,
        UiKey key,
        bool ctrl,
        bool alt,
        bool meta,
        bool shift,
        string? clipboard,
        int maxLength = int.MaxValue)
    {
        var command = Decide(key, ctrl, alt, meta, shift);
        return Execute(state, command, shift, clipboard, maxLength);
    }
}

/// <summary>
/// TEXTINPUT1. Which source types the characters this frame: the OS's typed text from
/// Bootstrap's <c>TextInputQueue</c> (layout-aware: AltGr, dead keys, accents, non-US
/// punctuation) or <see cref="UiTypedKeys"/>' physical US map. Editing keys (Backspace, arrows,
/// Home/End, Delete, Enter, Ctrl shortcuts) always come from the physical keys.
///
/// <para>
/// The rule: with no queue, the physical map types. With a queue, a frame in which the queue
/// delivered text types from the queue and the physical inserts of that frame are dropped; this
/// also <i>proves</i> the queue. Once proven, physical inserts never type again (the OS's own
/// key repeat arrives through the queue). Before the first delivery the physical map still types,
/// so a Bootstrap whose subscription silently delivers nothing cannot leave fields dead.
/// </para>
/// </summary>
internal sealed class UiTextInputMerge
{
    /// <summary>True once the queue has delivered printable text in this Core generation.</summary>
    internal bool Proven { get; private set; }

    /// <summary>
    /// The frame floor for the next drain: <paramref name="frame"/> − 1 when the previous drain ran
    /// on the previous frame (an entry stamped then arrived after it), otherwise
    /// <paramref name="frame"/>, so text typed while nothing was draining is discarded.
    /// </summary>
    internal static int MinFrame(int frame, int lastDrainFrame) =>
        lastDrainFrame != int.MinValue && lastDrainFrame == frame - 1 ? frame - 1 : frame;

    /// <summary>
    /// A queued character a text field may insert: not a control character (Enter, Tab, Backspace
    /// and Ctrl+letter arrive as controls and are handled by the physical keys) and not DEL.
    /// </summary>
    internal static bool IsTypedText(char c) => !char.IsControl(c) && c != '\u007f';

    /// <summary>
    /// Decide this frame's source. <paramref name="queueAvailable"/>: the capability exists and is
    /// subscribed. <paramref name="queuedText"/>: printable characters drained this frame.
    /// Returns true when the physical map's inserts must be ignored this frame.
    /// </summary>
    internal bool SuppressPhysicalInserts(bool queueAvailable, int queuedText)
    {
        if (!queueAvailable) return false;
        if (queuedText > 0) Proven = true;
        return Proven;
    }
}
