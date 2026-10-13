using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// An axis-aligned rectangle <b>in screen pixels</b>, or the explicit "could not be read"
/// answer. Game-free: <see cref="IUiRuntime.PanelRect"/> produces it, <see cref="HoverRouter"/>
/// consumes it, and the arithmetic in between has tests.
///
/// <para>
/// <b>Screen pixels, and why that is the right space (job FUI19; evidence 165/167).</b> The mod
/// overlay is a <c>ScreenSpaceOverlay</c> canvas that <i>copies the game's CanvasScaler</i>, so a
/// canvas unit is emphatically not a device pixel: the panel frame is 640 units wide and lands
/// anywhere from a few hundred to well over a thousand pixels wide depending on the player's
/// resolution and UI-scale setting. There are therefore two rulers in play and exactly one place
/// they agree — the screen. So the panel's rect is transformed <i>out</i> of canvas units into
/// screen pixels and compared against the mouse device's own screen-pixel position, which is the
/// same resolution <c>RectTransformUtility.RectangleContainsScreenPoint(rect, point, null)</c>
/// performs internally for an overlay canvas (its null camera means "the screen point is already
/// the world point", because an overlay canvas's world space <i>is</i> the pixel grid). Comparing
/// the mouse against <c>RectTransform.rect</c> directly, or converting the mouse into canvas
/// units by hand, are the two ways to get this wrong, and both are wrong only off the reference
/// resolution — that is, correct on the developer's machine and misaligned on the player's.
/// </para>
/// </summary>
internal readonly struct ScreenRect
{
    /// <summary>The unreadable rect: contains nothing, and never answers "not hovered".</summary>
    internal static readonly ScreenRect Unknown = default;

    private ScreenRect(float minX, float minY, float maxX, float maxY)
    {
        MinX = minX;
        MinY = minY;
        MaxX = maxX;
        MaxY = maxY;
        IsKnown = true;
    }

    internal float MinX { get; }

    internal float MinY { get; }

    internal float MaxX { get; }

    internal float MaxY { get; }

    /// <summary>False for <see cref="Unknown"/>: the panel's transform could not be read.</summary>
    internal bool IsKnown { get; }

    /// <summary>
    /// Build a rect from two opposite corners in screen pixels, in either order. A corner pair
    /// carrying NaN or an infinity — a transform read mid-teardown, a zero-scale canvas — is
    /// <see cref="Unknown"/> rather than a rect that silently contains everything or nothing.
    /// </summary>
    internal static ScreenRect FromCorners(float ax, float ay, float bx, float by)
    {
        if (!Finite(ax) || !Finite(ay) || !Finite(bx) || !Finite(by)) return Unknown;
        float minX = ax < bx ? ax : bx;
        float maxX = ax < bx ? bx : ax;
        float minY = ay < by ? ay : by;
        float maxY = ay < by ? by : ay;
        return new ScreenRect(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Exact containment for a rectangle, edges included. Half-open would be defensible too; the
    /// point is that it is the same test on both edges, so a pointer parked on the frame's border
    /// cannot oscillate between hovered and not from one frame to the next.
    /// </summary>
    internal bool Contains(float x, float y) =>
        IsKnown && x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
}

/// <summary>Which of the three things the pointer read produced.</summary>
internal enum PointerKind
{
    /// <summary>
    /// There is no mouse, or the read threw. Tri-state on purpose, exactly as
    /// <see cref="ChatFocus"/> is: "I cannot see the pointer" is not the same fact as "the
    /// pointer is not over the panel", and the difference is the whole of the fail-closed rule.
    /// </summary>
    Unreadable = 0,

    /// <summary>Read, and inside the game window.</summary>
    Inside = 1,

    /// <summary>
    /// Read, and outside the game window — a second monitor, an alt-tab, a pointer dragged off
    /// the edge. Not over any panel, so hover panels stop blocking.
    /// </summary>
    Outside = 2,
}

/// <summary>One frame's reading of the mouse, in screen pixels. See <see cref="ScreenRect"/>.</summary>
internal readonly struct PointerSample
{
    internal static readonly PointerSample Unreadable = default;

    internal static readonly PointerSample Outside = new(PointerKind.Outside, 0f, 0f);

    private PointerSample(PointerKind kind, float x, float y)
    {
        Kind = kind;
        X = x;
        Y = y;
    }

    internal PointerKind Kind { get; }

    internal float X { get; }

    internal float Y { get; }

    /// <summary>A readable in-window position. NaN or an infinity is <see cref="Unreadable"/>.</summary>
    internal static PointerSample At(float x, float y)
    {
        if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y))
            return Unreadable;
        return new PointerSample(PointerKind.Inside, x, y);
    }
}

/// <summary>One shown panel, as the router sees it: what it asked for, and where it is.</summary>
internal readonly struct HoverPanel
{
    internal HoverPanel(UiCaptureMode mode, ScreenRect rect)
    {
        Mode = mode;
        Rect = rect;
    }

    internal UiCaptureMode Mode { get; }

    /// <summary>Only consulted for <see cref="UiCaptureMode.WhenHovered"/>.</summary>
    internal ScreenRect Rect { get; }
}

/// <summary>
/// The whole of the routing decision, and the only thing that decides it: given the modes and
/// screen rects of the shown panels plus one pointer sample, should the gameplay input block be
/// on — and is that a change from the last answer.
///
/// <para>
/// <b>Why this exists (job FUI19).</b> "Capture" used to mean two things at once: unlock the
/// cursor, and block gameplay input, both for as long as a panel was shown. The first has to last
/// the whole time — a locked cursor cannot be moved to the panel at all — but the second does
/// not, and making it last turned any open mod panel into a screen-wide modal: the wheel would
/// not zoom the camera, a click would not swing, anywhere on the screen, because a 640-unit frame
/// in one corner happened to be open. Only the block follows the pointer, and this type is where
/// that is decided.
/// </para>
///
/// <para>
/// <b>Three rules, in order.</b> (1) A <see cref="UiCaptureMode.WhileShown"/> panel blocks
/// unconditionally and needs neither a rect nor a pointer, so a genuinely modal panel still works
/// when the mouse cannot be read at all. (2) A <see cref="UiCaptureMode.WhenHovered"/> panel
/// blocks when the pointer is inside its rect. (3) A <see cref="UiCaptureMode.Never"/> panel is
/// skipped entirely — <i>it does not shield the panels under it</i>. "Never" means "this panel
/// never asks for the block", not "this panel suppresses everyone else's": a HUD overlay stretched
/// across the screen must not make every other panel unclickable, and there is no cheap, honest
/// z-order to consult in a set of independent per-panel overlay canvases (R24 finding 3 gave each
/// panel its own). Overlapping panels are therefore an OR, which is also the only answer that
/// cannot leave a hovered panel unable to take the wheel.
/// </para>
///
/// <para>
/// <b>Fail closed, in both unreadable directions.</b> A pointer that cannot be read
/// (<see cref="PointerKind.Unreadable"/>) or a panel whose rect cannot be read
/// (<see cref="ScreenRect.Unknown"/>) makes a hover panel block, which degrades the mode to
/// exactly the pre-FUI19 behavior rather than to no capture at all. The starting value of
/// <see cref="Blocked"/> is <c>true</c> for the same reason: a process with no pointer source —
/// the server, a test, the frames before the mouse device exists — never calls
/// <see cref="Update"/> and keeps the old behavior by construction.
/// </para>
///
/// <para>
/// <b>Prior art.</b> Unity's own <c>EventSystem.IsPointerOverGameObject</c> answers "is the
/// pointer over mod UI" by raycasting the whole UI stack. This instead reads the cursor against
/// the bounds of the panels we opened, which is exact for a rectangular frame, costs no raycast,
/// and cannot be confused by a vanilla widget under the pointer.
/// </para>
/// </summary>
internal sealed class HoverRouter
{
    private bool _blocked = true;

    /// <summary>The last answer. Starts blocked; see the fail-closed paragraph above.</summary>
    internal bool Blocked => _blocked;

    /// <summary>
    /// How many times the answer actually changed. The number the debouncing is about: it must
    /// count pointer crossings of a panel edge, which are human-rate, and never frames.
    /// </summary>
    internal int Transitions { get; private set; }

    /// <summary>Back to the fail-closed default, for a teardown or an empty shown set.</summary>
    internal void Reset()
    {
        _blocked = true;
        Transitions = 0;
    }

    /// <summary>
    /// Take one pass. <paramref name="blocked"/> is always the current answer; the return value is
    /// <c>true</c> only when it differs from the previous one.
    ///
    /// <para>
    /// <b>That return value is the debounce.</b> Below this sits a refcount whose 0↔1 edges drive a
    /// real <c>AddInputContext</c> / <c>RemoveInputContext</c> against the game's own ordered list,
    /// with a deferral rule for <c>_DenyModificationsOfInputContexts</c> and a bounded retry budget
    /// behind it (<see cref="UiInputCapture"/>). Handing that two game calls per frame as the mouse
    /// drifts would be indefensible; crossing a panel edge happens at the speed a hand moves, and
    /// that is the only thing that reaches it.
    /// </para>
    /// </summary>
    internal bool Update(IReadOnlyList<HoverPanel>? shown, PointerSample pointer, out bool blocked)
    {
        blocked = ShouldBlock(shown, pointer);
        if (blocked == _blocked) return false;
        _blocked = blocked;
        Transitions++;
        return true;
    }

    /// <summary>The stateless decision. See the three rules on the type.</summary>
    internal static bool ShouldBlock(IReadOnlyList<HoverPanel>? shown, PointerSample pointer)
    {
        if (shown == null || shown.Count == 0) return false;

        bool hoverSeen = false;
        for (int i = 0; i < shown.Count; i++)
        {
            var panel = shown[i];
            switch (panel.Mode)
            {
                case UiCaptureMode.WhileShown:
                    return true;

                case UiCaptureMode.WhenHovered:
                    hoverSeen = true;
                    // An unreadable rect is not "not hovered": we cannot tell, so this panel
                    // blocks, which is the old behavior rather than a silently disabled panel.
                    if (!panel.Rect.IsKnown) return true;
                    if (pointer.Kind == PointerKind.Inside
                        && panel.Rect.Contains(pointer.X, pointer.Y))
                        return true;
                    break;

                default:
                    // Never: contributes nothing, and shields nothing.
                    break;
            }
        }

        // Only now is the unreadable pointer decisive, and only if some panel actually wanted to
        // be routed by it. A set of Never panels with no pointer must still not block.
        return hoverSeen && pointer.Kind == PointerKind.Unreadable;
    }
}

/// <summary>
/// The config-facing half of <see cref="UiCaptureMode"/>: the framework-wide default entry and
/// the parse that reads it. Game-free and constant, in the shape
/// <c>HarmonySlotPolicy.ConfigSection</c> / <c>ConfigKey</c> established for every other
/// Progenitor-owned entry, so the string handling is tested rather than living in the one file
/// that cannot be.
///
/// <para>
/// <b>Client-only, and deliberately not synced.</b> Progenitor's config sync makes a server the
/// owner of an entry's value; this one decides how a <i>player's</i> mouse behaves on a
/// <i>player's</i> screen, and no server has any business overriding it. It is bound in
/// <c>UiKeybindHost</c>, which only runs on a client, into Core's own client cfg
/// (<c>progenitor.ui.cfg</c>) rather than into Bootstrap's shared process-lifetime file.
/// </para>
/// </summary>
internal static class UiCaptureModes
{
    internal const string ConfigSection = "Ui";

    internal const string ConfigKey = "CaptureMode";

    /// <summary>
    /// The framework default (job FUI19). Not <c>WhileShown</c>, which is what every version
    /// before this shipped and what made an open panel take the whole screen hostage.
    /// </summary>
    internal const UiCaptureMode Default = UiCaptureMode.WhenHovered;

    internal const string ConfigDescription =
        "How an open Progenitor mod panel treats gameplay input. WhenHovered (default): input is "
        + "blocked only while the mouse is over one of the panels, so the wheel scrolls the panel "
        + "under the pointer and zooms the camera everywhere else. WhileShown: input is blocked "
        + "anywhere on the screen for as long as any panel is open (the behavior before this "
        + "setting existed; use it if a panel you rely on binds keys it cannot share). Never: "
        + "panels never block gameplay input, for a HUD-style overlay you read while playing — "
        + "note that with Never, Escape closes the panel AND opens the vanilla system menu, "
        + "because a panel that blocks nothing registers nothing to consume the key with. "
        + "This is a client setting and is never taken from a server: it decides how your own "
        + "mouse behaves. A plugin may override it for its own panels.";

    internal static string DefaultName => Default.ToString();

    /// <summary>
    /// KEYS5 (live 2026-09-30: Capture mode drew as a free-text box on Progenitor · Settings): the values the
    /// cfg accepts, the default first. Bound as an <c>AcceptableValueList&lt;string&gt;</c> (Core binds plain
    /// strings, never its own enum, so a <c>ConfigEntry</c> of a Core type cannot pin a generation), which the
    /// settings pages draw as a dropdown like every finite setting.
    /// </summary>
    internal static readonly string[] Names =
    {
        nameof(UiCaptureMode.WhenHovered), nameof(UiCaptureMode.WhileShown), nameof(UiCaptureMode.Never),
    };

    /// <summary>
    /// Parse a config value. Case-insensitive and whitespace-tolerant; empty or unrecognised is
    /// <see cref="Default"/> with <c>false</c> returned, so the caller can say <i>which</i> value
    /// it did not understand instead of silently pretending the file said what it wanted.
    /// </summary>
    internal static bool TryParse(string? value, out UiCaptureMode mode)
    {
        mode = Default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string trimmed = value!.Trim();

        if (Equal(trimmed, nameof(UiCaptureMode.WhenHovered)))
        {
            mode = UiCaptureMode.WhenHovered;
            return true;
        }

        if (Equal(trimmed, nameof(UiCaptureMode.WhileShown)))
        {
            mode = UiCaptureMode.WhileShown;
            return true;
        }

        if (Equal(trimmed, nameof(UiCaptureMode.Never)))
        {
            mode = UiCaptureMode.Never;
            return true;
        }

        return false;
    }

    internal static string BadValueLog(string? value) =>
        "Ui: " + ConfigSection + "." + ConfigKey + "='" + (value ?? "") + "' is not one of "
        + nameof(UiCaptureMode.WhenHovered) + " / " + nameof(UiCaptureMode.WhileShown) + " / "
        + nameof(UiCaptureMode.Never) + "; keeping the default " + DefaultName + ".";

    private static bool Equal(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The per-frame half: which panels are shown, what they asked for, where they are, where the
/// pointer is — and one call to <see cref="UiInputCapture.SetPointerBlock"/> when, and only when,
/// the answer changes.
///
/// <para>
/// <b>Why the gate is separate from the refcount.</b> <see cref="UiInputCapture"/> already owns a
/// refcount whose 0↔1 edges add and remove a context in the game's ordered input list, with a
/// deferral rule, a bounded retry budget and a chain budget behind it. This does not touch any of
/// that; it sets one extra boolean the refcount ANDs in. That is what makes the safety property
/// trivial rather than argued: the block is wanted only while <c>count &gt; 0</c>, so a gate stuck
/// at <c>true</c> — a throw at the wrong moment, a pump that stopped, a Core swap mid-frame —
/// still cannot leave a context registered once the last panel closes. The worst outcome
/// <see cref="UiInputCapture"/> documents (a list entry with no owner) is unreachable from here by
/// construction, not by care.
/// </para>
///
/// <para>
/// <b>Main thread only.</b> Every entry point either runs from <c>Plugin.Frame</c> or from
/// <c>IUiPanel.Show</c> / <c>Hide</c>, which already touch Unity; the runtime reads
/// (<see cref="IUiRuntime.PanelRect"/>, <see cref="IUiRuntime.ReadPointer"/>) are transform and
/// device reads, not context mutations, so unlike <see cref="UiInputCapture"/> they need no
/// posting — but they are still never made under <see cref="Gate"/>, for the same reason that
/// module gives: no game call runs under a lock.
/// </para>
///
/// <para>
/// <b>The pump is armed only when it can change something.</b> A shown set that is all
/// <see cref="UiCaptureMode.WhileShown"/> has a constant answer, so no frame handler is
/// subscribed; an empty set unsubscribes and resets. A client with no panel open pays nothing.
/// </para>
/// </summary>
internal static class UiHoverGate
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Shown = new();
    private static readonly HoverRouter Router = new();
    private static bool _pumpArmed;
    private static bool _rectFailureLogged;

    /// <summary>One shown panel. The runtime handle is the identity; the mode is frozen at Show.</summary>
    private readonly struct Entry
    {
        internal Entry(IRuntimePanel panel, UiCaptureMode mode)
        {
            Panel = panel;
            Mode = mode;
        }

        internal IRuntimePanel Panel { get; }

        internal UiCaptureMode Mode { get; }
    }

    /// <summary>How many panels are being routed. <c>Never</c> panels are never attached.</summary>
    internal static int Count
    {
        get { lock (Gate) return Shown.Count; }
    }

    /// <summary>One grep line for the pin diagnostic.</summary>
    internal static string Describe()
    {
        lock (Gate)
            return "hover routed=" + Shown.Count + " blocked=" + Router.Blocked
                + " transitions=" + Router.Transitions + " pump=" + _pumpArmed;
    }

    /// <summary>
    /// A panel became shown. Called <b>before</b> <see cref="UiInputCapture.Acquire"/> on purpose:
    /// the gate is settled first, so the 0→1 that follows registers the context with the answer
    /// already correct instead of registering it, discovering the pointer is elsewhere on the next
    /// frame, and removing it again — two game calls against the game's list per panel open, for
    /// nothing.
    /// </summary>
    internal static void Attach(IRuntimePanel? panel, UiCaptureMode mode)
    {
        if (panel == null || mode == UiCaptureMode.Never) return;
        lock (Gate)
        {
            if (IndexOfLocked(panel) < 0)
                Shown.Add(new Entry(panel, mode));
        }

        Refresh();
    }

    /// <summary>
    /// A panel stopped being shown — hidden, disposed while shown, or destroyed under us. Safe to
    /// call for a panel that was never attached, which is what makes the <c>Never</c> mode and the
    /// disabled-handle paths need no bookkeeping of their own.
    /// </summary>
    internal static void Detach(IRuntimePanel? panel)
    {
        if (panel == null) return;
        bool removed;
        lock (Gate)
        {
            int i = IndexOfLocked(panel);
            removed = i >= 0;
            if (removed) Shown.RemoveAt(i);
        }

        if (removed) Refresh();
    }

    /// <summary>
    /// The mode of an already-shown panel changed under the player. Rare — a plugin flipping
    /// <c>IUiPanel.CaptureMode</c> on an open panel — and it must re-decide immediately rather
    /// than wait for a pointer move, because a panel switched to <c>WhileShown</c> has to start
    /// blocking without one.
    /// </summary>
    internal static void Remode(IRuntimePanel? panel, UiCaptureMode mode)
    {
        if (panel == null) return;
        bool present;
        lock (Gate)
        {
            int i = IndexOfLocked(panel);
            present = i >= 0;
            if (!present) return;
            if (mode == UiCaptureMode.Never) Shown.RemoveAt(i);
            else Shown[i] = new Entry(panel, mode);
        }

        Refresh();
    }

    /// <summary>Core Uninstall / <c>Ui.Uninstall</c>: forget every panel and unsubscribe.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Shown.Clear();
            Router.Reset();
            DisarmPumpLocked();
            _rectFailureLogged = false;
        }

        // Outside the lock, and unconditional: the gate goes back to its fail-closed default so a
        // next generation starts from the same place a fresh process does.
        try { UiInputCapture.SetPointerBlock(true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: hover gate reset threw: " + e.Message); }
    }

    /// <summary>Subscribed to <c>Plugin.Frame</c> only while a hover-routed panel is shown.</summary>
    internal static void Pump() => Refresh();

    /// <summary>
    /// One decision pass. Never throws: a runtime that throws mid-read is answered the way an
    /// unreadable rect is — the panel blocks — because the alternative is a panel the player can
    /// see and cannot scroll.
    /// </summary>
    internal static void Refresh()
    {
        Entry[] snapshot;
        lock (Gate)
        {
            if (Shown.Count == 0)
            {
                Router.Reset();
                DisarmPumpLocked();
                snapshot = Array.Empty<Entry>();
            }
            else
            {
                snapshot = Shown.ToArray();
                if (NeedsPump(snapshot)) ArmPumpLocked();
                else DisarmPumpLocked();
            }
        }

        if (snapshot.Length == 0)
        {
            SetBlock(true);
            return;
        }

        // Game calls, with Gate released — the rule UiInputCapture documents, kept here too.
        IUiRuntime runtime;
        try { runtime = Ui.CurrentRuntime(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: hover gate runtime read threw: " + e.Message);
            SetBlock(true);
            return;
        }

        var panels = new HoverPanel[snapshot.Length];
        bool anyRectUnknown = false;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var entry = snapshot[i];
            ScreenRect rect = ScreenRect.Unknown;
            if (entry.Mode == UiCaptureMode.WhenHovered)
            {
                try { rect = runtime.PanelRect(entry.Panel); }
                catch (Exception e)
                {
                    rect = ScreenRect.Unknown;
                    if (!_rectFailureLogged)
                    {
                        _rectFailureLogged = true;
                        Plugin.Logger?.LogWarning(
                            "Ui: a panel's screen rect could not be read (" + e.Message
                            + "); that panel blocks gameplay input for as long as it is shown, "
                            + "which is the pre-FUI19 behavior. Logged once.");
                    }
                }

                if (!rect.IsKnown) anyRectUnknown = true;
            }

            panels[i] = new HoverPanel(entry.Mode, rect);
        }

        PointerSample pointer;
        try { pointer = runtime.ReadPointer(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: hover gate pointer read threw: " + e.Message);
            pointer = PointerSample.Unreadable;
        }

        bool changed;
        bool blocked;
        lock (Gate)
            changed = Router.Update(panels, pointer, out blocked);

        if (!changed) return;

        Plugin.Logger?.LogInfo(
            "Ui: pointer " + (blocked ? "entered" : "left") + " a mod panel; gameplay input is "
            + (blocked ? "blocked" : "released") + " (panels=" + panels.Length
            + " window=" + pointer.Kind
            + (anyRectUnknown ? " rect=unreadable" : "") + ").");

        SetBlock(blocked);
    }

    private static void SetBlock(bool blocked)
    {
        try { UiInputCapture.SetPointerBlock(blocked); }
        catch (Exception e)
        {
            // The refcount is the thing that keeps a context from being leaked and it is untouched
            // by a failure here; the worst case is a stale gate, which count > 0 still dominates.
            Plugin.Logger?.LogWarning("Ui: hover gate could not apply the input block: " + e.Message);
        }
    }

    /// <summary>
    /// True when some shown panel's answer can change without a Show or a Hide — i.e. when at
    /// least one is routed by the pointer. An all-<c>WhileShown</c> set is constant and needs no
    /// frame handler.
    /// </summary>
    private static bool NeedsPump(Entry[] snapshot)
    {
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].Mode == UiCaptureMode.WhenHovered) return true;
        }

        return false;
    }

    private static int IndexOfLocked(IRuntimePanel panel)
    {
        for (int i = 0; i < Shown.Count; i++)
        {
            if (ReferenceEquals(Shown[i].Panel, panel)) return i;
        }

        return -1;
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
            Plugin.Logger?.LogWarning("Ui: hover pump subscribe threw: " + e.Message);
        }
#endif
    }

    private static void DisarmPumpLocked()
    {
        if (!_pumpArmed) return;
        _pumpArmed = false;
#if !PROGENITOR_TESTS
        try { Plugin.Frame -= Pump; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: hover pump unsubscribe threw: " + e.Message); }
#endif
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// The <c>Plugin.Frame</c> subscription is a game event a test process has no frames for, so
    /// <see cref="ArmPumpLocked"/> only flips the flag there. Exposing it lets a test assert that
    /// the per-frame pass is armed exactly when a hovered panel is shown, and disarmed otherwise.
    /// </summary>
    internal static bool PumpArmedForTests
    {
        get { lock (Gate) return _pumpArmed; }
    }

    internal static bool BlockedForTests
    {
        get { lock (Gate) return Router.Blocked; }
    }

    internal static int TransitionsForTests
    {
        get { lock (Gate) return Router.Transitions; }
    }

    internal static void ResetForTests() => Reset();
#endif
}
