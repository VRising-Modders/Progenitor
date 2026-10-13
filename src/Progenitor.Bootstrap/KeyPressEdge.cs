namespace Progenitor.Bootstrap;

/// <summary>
/// Job FUI16 (R43 finding 1, research evidence 161). The press edge for a <b>single</b> key,
/// derived from that key's held state rather than borrowed from the Input System's frame-scoped
/// flag. Bootstrap's Escape forwarding in <c>Progenitor.Ui.UiInputContext.Update</c> is the one
/// caller.
///
/// <para>
/// <b>Why this exists.</b> Evidence 161 established that
/// <c>ButtonControl.wasPressedThisFrame</c> is <c>pressedStepCount == updateStepCount</c> — true
/// only during the input update step that processed the press, not "the key went down since you
/// last looked". Anything sampling once per rendered frame misses every press whose update step
/// it never observed. Core's keybinds were fixed for exactly this
/// (<c>Progenitor.Core/Ui/KeyEdgeTracker.cs</c>), but <c>UiInputContext.Update</c> — a
/// MonoBehaviour <c>Update</c>, the indicted cadence — kept reading
/// <c>kb.escapeKey.wasPressedThisFrame</c> bare. The panel therefore opened on one press of F10
/// and needed two presses of Escape to close, which is the same bug on the other half of the same
/// interaction.
/// </para>
///
/// <para>
/// <b>The rule, unchanged from <c>KeyEdgeTracker</c>.</b> Fire when the Input System says so,
/// <i>or</i> when the key was up on the immediately preceding sample and is down now. The second
/// clause recovers the press whose update step we missed; restricting it to a <i>contiguous</i>
/// pair of samples is what stops a key that is merely still held from reading as a fresh press
/// after a gap in sampling. Bootstrap's reader has real gaps: <c>Update</c> returns before
/// sampling whenever capture is off or the Core slot is null, so a panel closing and reopening
/// while Escape is held leaves stale state behind exactly the way the suppressed pump does in
/// Core.
/// </para>
///
/// <para>
/// <b>Why a second type instead of sharing <c>KeyEdgeTracker</c>.</b> Bootstrap cannot reference
/// Core — Core is loaded into a collectible ALC by Bootstrap and the dependency only goes one way
/// — so sharing would mean linking <c>KeyEdgeTracker.cs</c> into Bootstrap as a compile item, the
/// <c>PatchSlots</c>/<c>PatchSlotLink</c> shape. That was rejected here for three reasons.
/// <c>KeyEdgeTracker</c> is keyed on <c>UiKey</c>, a Core enum Bootstrap has no use for (its
/// reader holds a raw <c>escapeKey</c> control, not a <c>UiKey</c>), so the enum would have to be
/// linked too. Linking would then put <c>Progenitor.Ui.KeyEdgeTracker</c> and
/// <c>Progenitor.Ui.UiKey</c> into two assemblies at once in the same process, one collectible
/// and one not, in a codebase whose Core→Bootstrap hand-offs resolve types <i>by name</i>
/// (<c>PatchSlotLink</c>, <c>UnsafeHarmonySlotLink</c>, <c>BootstrapLink</c>) — a duplicate name
/// across the ALC boundary is a trap to set deliberately, not by accident. And there is nothing
/// to share: for one key the dictionary is a field, and the per-frame verdict cache is not merely
/// unnecessary but wrong here (see below). The mechanism worth sharing is the contiguity rule,
/// which is prose and arithmetic, and both are reproduced with the same tests.
/// </para>
///
/// <para>
/// <b>No same-frame verdict cache, on purpose.</b> <c>KeyEdgeTracker</c> caches its verdict per
/// key per frame because two chords can share a main key and the pump asks about each in the same
/// frame. Nothing asks twice here: there is one key and one reader. If a second
/// <c>UiInputContext</c> component ever did sample in the same frame, a cache would hand it the
/// same <c>true</c> and Escape would fire twice; without one the second sample sees the key
/// already recorded as held and answers false, which is the behavior we want.
/// </para>
///
/// <para>
/// <b>An unreadable frame number is not frame zero.</b> <c>Time.frameCount</c> can throw during
/// teardown; the caller passes <see cref="NoFrame"/> when it does. That value can neither match
/// nor be adjacent to any real frame, so the tracker degrades to trusting the raw flag alone —
/// the pre-fix behavior, never worse than it — instead of poisoning the contiguity test with a
/// fabricated number.
/// </para>
///
/// <para>
/// Game-free on purpose: the arithmetic is the part that was wrong, so it is the part with tests
/// (<c>tests/Progenitor.Tests/KeyPressEdgeTests.cs</c>). No code is copied from BepInEx's
/// <c>KeyboardShortcut.IsDown()</c>, which reaches the same conclusion independently.
/// </para>
/// </summary>
internal sealed class KeyPressEdge
{
    /// <summary>
    /// Pass as the frame number when it could not be read. Chosen so that neither
    /// <c>frame</c> nor <c>frame - 1</c> can collide with a stored real frame; both comparisons
    /// are guarded against it rather than relying on the value alone.
    /// </summary>
    internal const int NoFrame = int.MinValue;

    private int _frame = NoFrame;
    private bool _pressed;
    private bool _seen;
    private int _deviceId = int.MinValue;

    /// <summary>
    /// Presses recovered from held state because <c>wasPressedThisFrame</c> was already false by
    /// the time <c>Update</c> looked. Zero on a build where the rendered frame and the input
    /// update step agree; anything else is the misalignment this type exists for, which
    /// <c>UiInputContext</c> reports in the log once.
    /// </summary>
    internal int EdgeOnlyPresses { get; private set; }

    /// <summary>
    /// Drop held state when the keyboard is replaced — an unplug, or the Steam overlay
    /// re-initializing input. State read from a dead device says nothing about the live one, and
    /// the contiguity rule would otherwise compare across the swap. A no-op when the id is
    /// unchanged, so it is safe to call on every sample.
    /// </summary>
    internal void NoteDevice(int deviceId)
    {
        if (deviceId == _deviceId) return;
        _deviceId = deviceId;
        _frame = NoFrame;
        _pressed = false;
        _seen = false;
    }

    /// <summary>
    /// Record this sample of the key and answer whether it went down.
    /// </summary>
    /// <param name="frame">
    /// The frame this sample was taken on — <c>Time.frameCount</c> in the client, or
    /// <see cref="NoFrame"/> if it could not be read.
    /// </param>
    /// <param name="isPressed">The control's live held state.</param>
    /// <param name="wasPressedThisFrame">The Input System's own edge flag, trusted whenever set.</param>
    internal bool Observe(int frame, bool isPressed, bool wasPressedThisFrame)
    {
        bool contiguous = _seen && frame != NoFrame && _frame != NoFrame && _frame == frame - 1;
        bool edge = wasPressedThisFrame || (isPressed && contiguous && !_pressed);

        if (edge && !wasPressedThisFrame) EdgeOnlyPresses++;

        _seen = true;
        _frame = frame;
        // A press that went down and back up between two samples reads isPressed=false with the
        // flag set. Remembering it as held is what stops the *next* sample calling the release an
        // edge of its own; the sample after that is free to see a genuine new press.
        _pressed = isPressed || wasPressedThisFrame;
        return edge;
    }
}
