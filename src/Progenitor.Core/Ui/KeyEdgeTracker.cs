using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Turns a per-frame keyboard sample into a "this key went down" edge, without trusting the
/// Input System's own <c>wasPressedThisFrame</c> to line up with the frame we sample on.
///
/// <para>
/// <b>Why this exists (evidence 161).</b> Every mod panel took two presses of its keybind to
/// open. The client log showed one panel state change per <i>pair</i> of presses, so the handler
/// was not firing twice and canceling itself out — half the presses never reached a handler at
/// all. The only detector in that path was <c>ButtonControl.wasPressedThisFrame</c>, which does
/// not mean "the key went down since you last looked": it is
/// <c>pressedStepCount == updateStepCount</c>, true only during the input update step that
/// processed the press. Our pump samples once per rendered frame from
/// <c>FrameTicker.Update</c>. When the game advances the input update step more than once
/// between two of our samples, the step that saw the press is one we never observed and the
/// press is gone — <c>isPressed</c> reads true, <c>wasPressedThisFrame</c> already reads false.
/// Nothing about that is intermittent from the player's side: it reads as "I have to press F10
/// twice".
/// </para>
///
/// <para>
/// <b>The rule.</b> Fire when the Input System says so, <i>or</i> when we saw this key up on the
/// immediately preceding frame we sampled and it is down now. The second clause is what recovers
/// a press whose update step we missed. It is deliberately restricted to a contiguous pair of
/// samples: the pump skips the source entirely on frames where every bind is suppressed by a
/// captured panel (R34 finding 16), and after a gap like that a key that is merely still held
/// must not read as a fresh press the moment suppression lifts.
/// </para>
///
/// <para>
/// <b>One answer per key per frame.</b> Two chords can share a main key: the Sample binds both
/// <c>F9</c> and <c>Ctrl+F9</c>. The pump asks about each in the same frame, so the second ask
/// must get the same answer as the first rather than re-deriving an edge against state the first
/// ask already advanced. That cached verdict is what keeps the modifier test above this a pure
/// filter.
/// </para>
///
/// <para>
/// <b>Prior art.</b> BepInEx's own <c>KeyboardShortcut.IsDown()</c> also edge-detects a held
/// state itself rather than leaning on a frame-scoped flag; no code is copied from it. Game-free on purpose: the arithmetic is the part that was wrong, so it
/// is the part with tests.
/// </para>
/// </summary>
internal sealed class KeyEdgeTracker
{
    private readonly Dictionary<UiKey, Sample> _state = new();
    private int _deviceId = int.MinValue;

    /// <summary>
    /// Presses recovered by the held-state edge because <c>wasPressedThisFrame</c> was already
    /// false by the time we looked. Zero on a build where the pump and the input update step
    /// agree; anything else is the misalignment this type exists for, which
    /// <c>UnityKeyInputSource</c> reports in the log once.
    /// </summary>
    internal int EdgeOnlyPresses { get; private set; }

    /// <summary>How many distinct keys have been sampled. For the describe/pin line only.</summary>
    internal int TrackedKeys => _state.Count;

    /// <summary>
    /// Drop all state when the keyboard is replaced — an unplug, or the Steam overlay
    /// re-initializing input. Held state read from a dead device says nothing about the live one,
    /// and the contiguity rule would otherwise compare across the swap. A no-op when the id is
    /// unchanged, so it is safe to call on every sample.
    /// </summary>
    internal void NoteDevice(int deviceId)
    {
        if (deviceId == _deviceId) return;
        _deviceId = deviceId;
        _state.Clear();
    }

    /// <summary>
    /// Record this frame's reading of <paramref name="key"/> and answer whether it went down.
    /// </summary>
    /// <param name="key">The chord's main key.</param>
    /// <param name="frame">The frame this sample was taken on; <c>Time.frameCount</c> in the client.</param>
    /// <param name="isPressed">The control's live held state.</param>
    /// <param name="wasPressedThisFrame">The Input System's own edge flag, trusted whenever set.</param>
    internal bool Observe(UiKey key, int frame, bool isPressed, bool wasPressedThisFrame)
    {
        if (_state.TryGetValue(key, out var previous) && previous.Frame == frame)
            return previous.Edge;

        bool contiguous = previous.Seen && previous.Frame == frame - 1;
        bool edge = wasPressedThisFrame || (isPressed && contiguous && !previous.Pressed);

        if (edge && !wasPressedThisFrame) EdgeOnlyPresses++;

        // A press that went down and back up between two samples reads isPressed=false with the
        // flag set. Remembering it as held is what stops the *next* sample calling the release an
        // edge of its own; the sample after that is free to see a genuine new press.
        _state[key] = new Sample(frame, isPressed || wasPressedThisFrame, edge);
        return edge;
    }

    private readonly struct Sample
    {
        internal Sample(int frame, bool pressed, bool edge)
        {
            Frame = frame;
            Pressed = pressed;
            Edge = edge;
            Seen = true;
        }

        internal int Frame { get; }

        internal bool Pressed { get; }

        internal bool Edge { get; }

        /// <summary>False only for the <c>default</c> a failed lookup leaves behind.</summary>
        internal bool Seen { get; }
    }
}
