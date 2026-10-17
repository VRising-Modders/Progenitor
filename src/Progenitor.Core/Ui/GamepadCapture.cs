using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// PAD1: the Keybinds tab's controller capture, game-free: which gamepad chord a player means, from the held-buttons
/// mask read once a pump (<see cref="IKeyInputSource.TryReadGamepadHeld"/>).
///
/// <para><b>Why on release.</b> On a keyboard the modifiers are never main keys, so the capture can end on the first
/// key that goes down. On a gamepad any button can be the held one, so a capture ending on the first press would bind
/// the button the player meant to hold. The chord is decided when the first button comes up instead: one button pressed
/// is that button; two are the second pressed while the first was held (<c>D-Pad Down</c>, then <c>Pad Y</c>, gives
/// <c>Pad Y + D-Pad Down</c>, whichever is let go first); three or more are refused and the capture keeps waiting.</para>
///
/// <para><b>The press that started it.</b> Buttons already down on the first read (the Pad A that clicked the key
/// cap) are ignored until they come up.</para>
/// </summary>
internal sealed class GamepadCapture
{
    internal const string TooManyReason =
        "Hold one controller button and press another; three or more buttons together can't be one key.";

    /// <summary>PAD1 review: two buttons went down on the same frame, so which one is held cannot be told.</summary>
    internal const string SameTimeReason = "Press the button you hold first, then the other.";

    private bool _started;
    private uint _ignored;
    private uint _down;
    private readonly List<UiKey> _order = new(3);
    private bool _sameTime;

    /// <summary>A button is down in the gesture now (the capture's timeout waits while one is).</summary>
    internal bool Active => _down != 0;

    /// <summary>
    /// One read. True when the gesture ended: <paramref name="chord"/> is the chord, or <see cref="KeyChord.None"/> with
    /// <paramref name="refusal"/> set (the capture then keeps waiting for a new gesture).
    /// </summary>
    internal bool Observe(uint held, out KeyChord chord, out string? refusal)
    {
        chord = KeyChord.None;
        refusal = null;
        if (!_started)
        {
            _started = true;
            _ignored = held;
            return false;
        }

        _ignored &= held;
        uint effective = held & ~_ignored;
        uint pressed = effective & ~_down;
        int added = 0;
        foreach (var key in KeyChord.GamepadKeys)
        {
            if ((pressed & KeyChord.GamepadBit(key)) == 0 || _order.Contains(key)) continue;
            _order.Add(key);
            added++;
        }

        // PAD1 review: two new buttons on one read have no order; the held one cannot be told from the pressed one.
        if (added > 1) _sameTime = true;

        uint released = _down & ~effective;
        _down = effective;
        if (released == 0 || _order.Count == 0) return false;

        if (_order.Count > 2) refusal = TooManyReason;
        else if (_sameTime) refusal = SameTimeReason;
        else if (_order.Count == 1) chord = KeyChord.Gamepad(_order[0]);
        else chord = KeyChord.Gamepad(_order[1], _order[0]);

        // A new gesture starts once everything still held is let go.
        _order.Clear();
        _sameTime = false;
        _ignored |= effective;
        _down = 0;
        return true;
    }
}
