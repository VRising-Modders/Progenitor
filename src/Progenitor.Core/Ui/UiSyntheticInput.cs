using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>The synthetic pointer's buttons (<see cref="UiSyntheticInput"/>).</summary>
[Flags]
internal enum UiSyntheticButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4,
}

/// <summary>Navigation buttons held through the synthetic input (<see cref="UiSyntheticInput.HoldNav"/>).</summary>
[Flags]
internal enum UiSyntheticNav
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 4,
    Right = 8,
    Submit = 16,
    Cancel = 32,
    Reset = 64,
    TabPrev = 128,
    TabNext = 256,
}

/// <summary>
/// The synthetic pointer's press edges for a reader that samples buttons itself instead of receiving the driver's
/// pointer events (the chat log's link and line clicks): the buttons that went down since the last sample, derived from
/// the held state. Sample once a frame; while the synthetic input is inactive nothing is pressed and the state is
/// forgotten, so a lease that starts with a button held does not replay an old press. Game-free.
/// </summary>
internal sealed class UiSyntheticPressEdges
{
    private UiSyntheticButtons _seen;

    internal UiSyntheticButtons Sample(bool active, UiSyntheticButtons held)
    {
        if (!active)
        {
            _seen = UiSyntheticButtons.None;
            return UiSyntheticButtons.None;
        }

        var went = held & ~_seen;
        _seen = held;
        return went;
    }
}

/// <summary>
/// BRIDGEUI1: synthetic input at the UI kit's own input layer, for the dev bridge's <c>ui_input</c> op. While a lease is
/// held, the kit's samplers (<c>UnityUiRuntime.SamplePointer</c>, <c>PrimaryPointerHeld</c>, <c>HeldClickModifiers</c>,
/// the shown-input pump's wheel, click and typed-key reads, and NAV1's <c>SampleNav</c>) read this state instead of the
/// Input System devices, and the runtime's synthetic driver dispatches pointer enter, exit, down, up, click and drag to
/// the UI objects under the synthetic point through the game's <c>EventSystem</c> raycast. No OS input and no device
/// event is involved, so nothing here depends on the game window having focus.
///
/// <para>Game-free: the state, the lease clock, the wheel's notch edges, timed key taps and navigation holds. The
/// Unity half is <c>UnityUiRuntime.SyntheticInput.cs</c>.</para>
///
/// <para><b>Lifetime.</b> A lease ends on <see cref="End"/> (the bridge's <c>release</c>), when its time runs out
/// (<see cref="Tick"/>), when the player uses the real mouse or keyboard (the runtime ends it with
/// <c>user-activity</c>), and on the Core unload (the <c>synthetic-input</c> widget teardown). Ending lets go of every
/// button and key, and the runtime sends the pointer's exits first.</para>
/// </summary>
internal static class UiSyntheticInput
{
    internal const int DefaultLeaseMs = 30_000;
    internal const int MinLeaseMs = 500;
    internal const int MaxLeaseMs = 300_000;

    /// <summary>One wheel notch in the mouse's scroll units (Windows' WHEEL_DELTA), as the kit's wheel handler reads it.</summary>
    internal const float WheelDeltaPerNotch = 120f;

    /// <summary>Frames a tapped key stays held: long enough for the held-state edge trackers to see the press.</summary>
    internal const int DefaultTapFrames = 2;

    /// <summary>The clock (milliseconds); tests replace it.</summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private static bool _leased;
    private static long _untilMs;
    private static long _startedMs;
    private static string _owner = "";
    private static string _lastEnd = "";

    private static readonly Queue<int> Wheel = new();
    private static bool _wheelGap;

    private static readonly Dictionary<UiKey, List<(int From, int Until)>> KeyTaps = new();
    private static readonly List<UiKey> KeyScratch = new();

    private static UiSyntheticNav _nav;
    private static long _navUntilMs;
    private static float _stick;
    private static long _stickUntilMs;

    /// <summary>Called once, before the state is cleared, when a lease ends (the runtime sends the pointer's exits).</summary>
    internal static Action<string>? BeforeEnd { get; set; }

    /// <summary>A lease is held and has not run out.</summary>
    internal static bool Active => _leased && Clock() < _untilMs;

    internal static string Owner => _owner;

    /// <summary>Why the last lease ended (<c>release</c>, <c>timeout</c>, <c>user-activity</c>, <c>unload</c>), or empty.</summary>
    internal static string LastEnd => _lastEnd;

    internal static bool HasPointer { get; private set; }

    /// <summary>Screen pixels, origin bottom-left (the Input System's <c>Mouse.position</c> space).</summary>
    internal static float X { get; private set; }

    internal static float Y { get; private set; }

    internal static UiSyntheticButtons Buttons { get; private set; }

    internal static UiClickModifiers Modifiers { get; private set; }

    /// <summary>Bumped on every pointer move or button change, so NAV1 can tell the pointer was used.</summary>
    internal static int PointerSerial { get; private set; }

    internal static int PendingNotches
    {
        get
        {
            int n = 0;
            foreach (int v in Wheel) n += Math.Abs(v);
            return n;
        }
    }

    internal static int ClampLease(int? ms) => Math.Max(MinLeaseMs, Math.Min(MaxLeaseMs, ms ?? DefaultLeaseMs));

    /// <summary>Take or renew the lease for <paramref name="leaseMs"/> from now. A second owner cannot take a held lease.</summary>
    internal static string? Begin(string owner, int? leaseMs)
    {
        owner = string.IsNullOrWhiteSpace(owner) ? "bridge" : owner.Trim();
        long now = Clock();
        if (_leased && now >= _untilMs) End("timeout");
        if (_leased && !string.Equals(_owner, owner, StringComparison.Ordinal))
            return "the synthetic input is held by '" + _owner + "'";
        if (!_leased)
        {
            _leased = true;
            _startedMs = now;
            _owner = owner;
            _lastEnd = "";
        }

        _untilMs = now + ClampLease(leaseMs);
        return null;
    }

    /// <summary>End the lease when its time ran out. Returns true when this call ended it.</summary>
    internal static bool Tick()
    {
        if (!_leased || Clock() < _untilMs) return false;
        End("timeout");
        return true;
    }

    /// <summary>End the lease: every button and key up, the pointer gone, the wheel and taps dropped.</summary>
    internal static void End(string reason)
    {
        bool was = _leased;
        if (was)
        {
            try { BeforeEnd?.Invoke(reason); }
            catch { /* the state is cleared below whatever the dispatch did */ }
        }

        _leased = false;
        _untilMs = 0;
        _owner = "";
        HasPointer = false;
        X = Y = 0f;
        Buttons = UiSyntheticButtons.None;
        Modifiers = UiClickModifiers.None;
        Wheel.Clear();
        _wheelGap = false;
        KeyTaps.Clear();
        TextOwner = 0;
        ClearNav();
        if (was) _lastEnd = reason ?? "";
    }

    /// <summary>Navigation holds outlive no lease of their own: dropped on the Core unload too.</summary>
    internal static void ClearNav()
    {
        _nav = UiSyntheticNav.None;
        _navUntilMs = 0;
        _stick = 0f;
        _stickUntilMs = 0;
    }

    // ---- Pointer ---------------------------------------------------------------------------------

    internal static void MoveTo(float x, float y)
    {
        if (!Active) return;
        if (!HasPointer || Math.Abs(x - X) > 0.01f || Math.Abs(y - Y) > 0.01f) PointerSerial++;
        HasPointer = true;
        X = x;
        Y = y;
    }

    internal static void SetButtons(UiSyntheticButtons buttons)
    {
        if (!Active) return;
        if (buttons != Buttons) PointerSerial++;
        Buttons = buttons;
    }

    internal static void SetModifiers(UiClickModifiers modifiers)
    {
        if (!Active) return;
        Modifiers = modifiers;
    }

    /// <summary>Queue <paramref name="notches"/> wheel notches (positive up, away from the player).</summary>
    internal static void QueueWheel(int notches)
    {
        if (!Active || notches == 0) return;
        // One notch a sample (live 2026-10-14: a -2 queued as one -240 reading was counted as one notch by the pump,
        // which counts a 0 to non-zero crossing as one step).
        int step = notches > 0 ? 1 : -1;
        for (int i = 0; i < Math.Abs(notches); i++) Wheel.Enqueue(step);
    }

    /// <summary>
    /// The wheel reading for one sample of the shown-input pump: a queued notch as its delta (notches × 120), and a zero
    /// between two queued notches, so the pump's held-state edge rule (a 0 to non-zero crossing is a notch) counts each.
    /// </summary>
    internal static float SampleWheel()
    {
        if (_wheelGap || Wheel.Count == 0)
        {
            _wheelGap = false;
            return 0f;
        }

        _wheelGap = true;
        return Wheel.Dequeue() * WheelDeltaPerNotch;
    }

    // ---- Keys (UI layer: the typed-key pump and the Escape hook, never a keybind) -------------------

    /// <summary>
    /// Hold <paramref name="key"/> from <paramref name="frame"/> for <paramref name="frames"/> frames, as the typed-key
    /// pump reads a physical key. The Escape hook's half of an Escape is the caller's, at tap time
    /// (<see cref="ForwardsEscape"/>), so nothing waits for a pump that may not run.
    /// </summary>
    internal static bool Tap(UiKey key, int frame, int frames = DefaultTapFrames)
    {
        if (!Active || key == UiKey.None) return false;
        if (!KeyTaps.TryGetValue(key, out var spans)) KeyTaps[key] = spans = new List<(int From, int Until)>();
        // A tap of a key still held (or released this very frame) is queued after one released frame, so the held-state
        // edge tracker sees each press, never one merged press (live 2026-10-14: a Backspace tap was lost). Earlier
        // taps stay: none is overwritten before it was sampled.
        if (spans.Count > 0 && frame <= spans[spans.Count - 1].Until) frame = spans[spans.Count - 1].Until + 1;
        spans.Add((frame, frame + Math.Max(1, frames)));
        return true;
    }

    /// <summary>The frame at which <paramref name="key"/>'s last queued tap is released, or -1 when none is queued.</summary>
    internal static int TapUntil(UiKey key) => KeyTaps.TryGetValue(key, out var spans) && spans.Count > 0 ? spans[spans.Count - 1].Until : -1;

    /// <summary>
    /// Whether a tap of <paramref name="key"/> goes to the framework's Escape hook now: an Escape while a panel is shown,
    /// which is when Bootstrap's forwarder delivers a physical one. With no panel shown it reaches nothing, as a real one.
    /// </summary>
    internal static bool ForwardsEscape(UiKey key, int shownPanels) => key == UiKey.Escape && shownPanels > 0;

    /// <summary>Whether <paramref name="key"/> is held at <paramref name="frame"/>.</summary>
    internal static bool KeyHeld(UiKey key, int frame)
    {
        if (!Active || !KeyTaps.TryGetValue(key, out var spans)) return false;
        for (int i = 0; i < spans.Count; i++)
            if (frame >= spans[i].From && frame < spans[i].Until) return true;
        return false;
    }

    /// <summary>Drop taps that ended before <paramref name="frame"/> (kept one frame past their end).</summary>
    internal static void PruneKeys(int frame)
    {
        if (KeyTaps.Count == 0) return;
        KeyScratch.Clear();
        foreach (var pair in KeyTaps)
        {
            pair.Value.RemoveAll(s => frame > s.Until);
            if (pair.Value.Count == 0) KeyScratch.Add(pair.Key);
        }

        foreach (var k in KeyScratch) KeyTaps.Remove(k);
    }

    /// <summary>Keys with a tap held or queued.</summary>
    internal static int HeldKeyCount => KeyTaps.Count;

    /// <summary>Drop every queued wheel notch (an action that failed or was abandoned).</summary>
    internal static int DrainWheel()
    {
        int n = PendingNotches;
        Wheel.Clear();
        _wheelGap = false;
        return n;
    }

    // ---- Injected text the bridge queued for one field ---------------------------------------------

    /// <summary>The field (its instance id) the bridge queued injected text or an Enter for; 0 when none.</summary>
    internal static int TextOwner { get; private set; }

    internal static void NoteTextFor(int fieldId) => TextOwner = fieldId;

    internal static void ClearTextOwner() => TextOwner = 0;

    /// <summary>
    /// Whether queued injected text must be dropped: the bridge queued it for one field, some is still pending, and that
    /// field no longer has the keyboard (it closed, a dialog took Enter, another field took focus). Text must never
    /// land in a field it was not typed for.
    /// </summary>
    internal static bool TextOrphaned(int focusedFieldId, int pending) => TextOwner != 0 && pending > 0 && focusedFieldId != TextOwner;

    /// <summary>Once the queue is empty the bridge owns nothing in it (a later <c>mods_ui type</c> is not ours to drop).</summary>
    internal static void NoteQueue(int pending)
    {
        if (pending == 0) TextOwner = 0;
    }

    // ---- Navigation (NAV1's sample, no device; bounded by the lease) ------------------------------

    /// <summary>Hold navigation buttons for <paramref name="ms"/> through NAV1's own per-frame sample (repeat included).</summary>
    internal static void HoldNav(UiSyntheticNav buttons, int ms)
    {
        _nav = buttons;
        _navUntilMs = Clock() + Math.Max(1, ms);
    }

    /// <summary>The right stick's vertical axis (-1..1, positive up) for <paramref name="ms"/>.</summary>
    internal static void HoldStick(float y, int ms)
    {
        _stick = Math.Max(-1f, Math.Min(1f, y));
        _stickUntilMs = Clock() + Math.Max(1, ms);
    }

    /// <summary>Held navigation, only while the lease is held: the player's activity or a release ends it with the lease.</summary>
    internal static UiSyntheticNav NavHeld => Active && Clock() < _navUntilMs ? _nav : UiSyntheticNav.None;

    internal static float StickHeld => Active && Clock() < _stickUntilMs ? _stick : 0f;

    // ---- Readback ----------------------------------------------------------------------------------

    internal static Dictionary<string, object?> Describe()
    {
        long now = Clock();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["active"] = Active,
            ["owner"] = _owner,
            ["msLeft"] = _leased ? Math.Max(0, _untilMs - now) : 0,
            ["heldMs"] = _leased ? now - _startedMs : 0,
            ["lastEnd"] = _lastEnd,
            ["pointer"] = HasPointer ? new Dictionary<string, object?> { ["x"] = X, ["y"] = Y } : null,
            ["buttons"] = ButtonsText(Buttons),
            ["modifiers"] = ModifiersText(Modifiers),
            ["pendingNotches"] = PendingNotches,
            ["keysHeld"] = KeyTaps.Count,
            ["nav"] = NavHeld == UiSyntheticNav.None ? "" : NavHeld.ToString(),
            ["stick"] = StickHeld,
        };
    }

    internal static string ButtonsText(UiSyntheticButtons b) =>
        b == UiSyntheticButtons.None ? "" : b.ToString().ToLowerInvariant().Replace(" ", "");

    internal static string ModifiersText(UiClickModifiers m) =>
        m == UiClickModifiers.None ? "" : m.ToString().ToLowerInvariant().Replace(" ", "");
}
