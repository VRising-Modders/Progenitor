#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// BRIDGEUI1: the Unity half of <see cref="UiSyntheticInput"/>. While the bridge holds the synthetic input, once a frame:
/// the game's <c>EventSystem</c> raycasts at the synthetic point, and the objects under it get the pointer events an
/// input module sends for a real mouse: enter and exit along the hovered chain, down and up on the pressed object, a
/// click when the release lands on the object that took the press, and initialize-drag, begin-drag, drag and end-drag
/// on the drag handler. Events go to the handlers this UI uses (<c>EventTrigger</c>, which carries every kit watch and
/// slot trampoline; <c>Selectable</c> and its subclasses: buttons, toggles, scroll bars, sliders, input fields;
/// <c>ScrollRect</c>; the game's <c>SimpleStunButton</c>), called directly on the component with a synthetic
/// <c>PointerEventData</c>, as RADIOCLICK1's <c>click_at</c> already does. No OS input, no Input System event, no window
/// focus: the raycast and the handlers do not read a device.
///
/// <para>Limits. Only those handler types are reached (a game script's own pointer interface on another component is
/// not). The wheel is not sent to uGUI's scroll handlers: the kit's wheel mover is the shown-input pump, which reads
/// the synthetic notches itself. The real mouse's own hover, handled by the game's input module, is not changed; do
/// not move the real mouse during a run (doing so ends the lease: <c>user-activity</c>).</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    private static bool _synthOn;
    private static PointerEventData? _synthEvent;
    private static readonly List<GameObject> SynthHovered = new();
    private static GameObject? _synthTop;
    private static int _synthTopId;
    private static readonly List<int> SynthHoveredIds = new();
    private static string _synthTopPath = "";
    private static RaycastResult _synthTopResult;
    private static UiSyntheticButtons _synthButtonsSeen;
    private static GameObject? _synthPress;
    private static GameObject? _synthClick;
    private static string? _synthClickKind;
    private static GameObject? _synthDrag;
    private static bool _synthDragging;
    private static PointerEventData.InputButton _synthPressButton;
    private static Vector2 _synthPressAt;
    private static Vector2 _synthLast = new(float.NaN, float.NaN);
    private static float _physX, _physY;
    private static bool _physHave;
    private static readonly List<string> SynthLog = new();

    /// <summary>The last events the driver sent, newest last (readback; at most 40).</summary>
    internal static IReadOnlyList<string> SyntheticEventLog => SynthLog;

    /// <summary>Start the per-frame driver (idempotent). Called when the bridge takes the lease.</summary>
    internal static void EnsureSyntheticDriver()
    {
        UiSyntheticInput.BeforeEnd = SyntheticBeforeEnd;
        if (_synthOn) return;
        _synthOn = true;
        _physHave = false;
        try { Plugin.Frame += SyntheticFrame; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: synthetic input driver could not start: " + e.Message); _synthOn = false; }
        UiWidgetLifetime.Register("synthetic-input", () =>
        {
            // Core unload: the kit's objects are going, so no exits are sent into them; but a game Selectable still held
            // down by the synthetic press gets its pointer-up first, or it stays pressed into the next generation.
            try
            {
                var ev = _synthEvent;
                if (ev != null && _synthPress != null && Alive(_synthPress))
                {
                    try { ev.button = _synthPressButton; } catch { /* */ }
                    try { _synthPress.GetComponent<Selectable>()?.OnPointerUp(ev); } catch { /* */ }
                }
            }
            catch { /* */ }

            DropOwnedText();
            UiSyntheticInput.BeforeEnd = null;
            UiSyntheticInput.End("unload");
            UiSyntheticInput.ClearNav();
            StopSyntheticDriver();
            ResetSyntheticState();
        });
    }

    private static void StopSyntheticDriver()
    {
        ReleaseInputModule();
        if (!_synthOn) return;
        _synthOn = false;
        try { Plugin.Frame -= SyntheticFrame; }
        catch { /* */ }
    }

    private static void ResetSyntheticState()
    {
        SynthHovered.Clear();
        SynthHoveredIds.Clear();
        _synthTop = null;
        _synthTopId = 0;
        _synthTopPath = "";
        _synthButtonsSeen = UiSyntheticButtons.None;
        _synthPress = _synthClick = _synthDrag = null;
        _synthClickKind = null;
        _synthDragging = false;
        _synthEvent = null;
        _synthLast = new Vector2(float.NaN, float.NaN);
    }

    /// <summary>The lease ends: release the buttons (no click) and exit every hovered object, then forget them.</summary>
    private static void SyntheticBeforeEnd(string reason)
    {
        try
        {
            var ev = SynthEvent();
            if (ev != null)
            {
                if (_synthPress != null || _synthDragging) ReleasePress(ev, click: false);
                for (int i = SynthHovered.Count - 1; i >= 0; i--) SendExit(SynthHovered[i], ev);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: synthetic input release threw: " + e.Message); }

        DropOwnedText();
        ReleaseInputModule();
        ResetSyntheticState();
        Plugin.Logger?.LogInfo("Ui: synthetic input ended (" + reason + ").");
    }

    private static void SyntheticFrame()
    {
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = 0; }
        if (UiSyntheticInput.Tick() || !UiSyntheticInput.Active)
        {
            StopSyntheticDriver();
            return;
        }

        UiSyntheticInput.PruneKeys(frame);
        // Text the bridge queued for a field that lost the keyboard (a dialog's Submit, a close, another field) is
        // dropped, never typed into whatever is focused next.
        if (UiSyntheticInput.TextOrphaned(FieldId(FocusedArmedField()), UiTextInjection.Pending))
        {
            Note("dropped " + UiTextInjection.Pending + " queued text item(s): their field lost the keyboard");
            DropOwnedText();
        }

        UiSyntheticInput.NoteQueue(UiTextInjection.Pending);
        if (PhysicalInputUsed(ref _physX, ref _physY, ref _physHave))
        {
            UiSyntheticInput.End("user-activity");
            StopSyntheticDriver();
            return;
        }

        // While leased, the pointer is only the synthetic one: the event system's input module, which turns the real
        // cursor's position into hover, enter and exit (tooltips, hover washes, the help markers' hover count), is held off.
        HoldInputModule();
        PlaceCaretClick();
        if (!UiSyntheticInput.HasPointer) return;
        try { SyntheticPointerFrame(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: synthetic pointer frame threw: " + e.Message); }
    }

    private static BaseInputModule? _heldModule;

    /// <summary>Disable the event system's current input module for the lease (kept off if something re-enables it).</summary>
    private static void HoldInputModule()
    {
        try
        {
            var es = EventSystem.current;
            var module = es == null ? null : es.currentInputModule;
            if (module == null)
            {
                // The event system dropped its current module once ours was disabled: keep the held one off.
                if (_heldModule != null && Alive(_heldModule) && _heldModule.enabled) _heldModule.enabled = false;
                return;
            }

            if (!module.enabled) return;
            module.enabled = false;
            if (_heldModule == null || !Alive(_heldModule) || _heldModule.GetInstanceID() != module.GetInstanceID())
            {
                ReleaseInputModule();
                _heldModule = module;
                Note("input module held off: " + SafePath(module.gameObject));
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: synthetic input could not hold the input module: " + e.Message); }
    }

    /// <summary>Give the input module back (the lease ended, the Core unloads).</summary>
    private static void ReleaseInputModule()
    {
        var module = _heldModule;
        _heldModule = null;
        if (module == null) return;
        try { if (Alive(module) && !module.enabled) module.enabled = true; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: synthetic input could not re-enable the input module: " + e.Message); }
    }

    private static int FieldId(TMP_InputField? field)
    {
        try { return field == null ? 0 : field.GetInstanceID(); }
        catch { return 0; }
    }

    private static int GoId(GameObject? go)
    {
        try { return go == null ? 0 : go.GetInstanceID(); }
        catch { return 0; }
    }

    /// <summary>Drop injected text and keys the bridge queued for one field (its field lost focus, or the lease ended).</summary>
    private static void DropOwnedText()
    {
        if (UiSyntheticInput.TextOwner == 0) return;
        UiTextInjection.Text.Clear();
        UiTextInjection.Keys.Clear();
        UiSyntheticInput.ClearTextOwner();
    }

    /// <summary>The bridge queued injected text for the focused field (its <c>type</c> path): track the owner.</summary>
    internal static void NoteBridgeTextForFocused() => UiSyntheticInput.NoteTextFor(FieldId(FocusedArmedField()));

    private static PointerEventData? SynthEvent()
    {
        if (_synthEvent != null) return _synthEvent;
        EventSystem? es = null;
        try { es = EventSystem.current; } catch { /* */ }
        if (es == null) return null;
        _synthEvent = new PointerEventData(es) { pointerId = -1 };
        return _synthEvent;
    }

    private static void SyntheticPointerFrame()
    {
        var ev = SynthEvent();
        if (ev == null) return;
        var pos = new Vector2(UiSyntheticInput.X, UiSyntheticInput.Y);
        Vector2 delta = float.IsNaN(_synthLast.x) ? Vector2.zero : pos - _synthLast;
        _synthLast = pos;
        ev.position = pos;
        ev.delta = delta;

        // The topmost UI object under the point.
        GameObject? top = null;
        _synthTopResult = default;
        try
        {
            var es = EventSystem.current;
            if (es != null)
            {
                var probe = new PointerEventData(es) { position = pos };
                var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
                es.RaycastAll(probe, results);
                if (results != null && results.Count > 0)
                {
                    _synthTopResult = results[0];
                    top = results[0].gameObject;
                }
            }
        }
        catch { top = null; }

        try { ev.pointerCurrentRaycast = _synthTopResult; } catch { /* */ }
        int topId = GoId(top);
        if (topId != _synthTopId)
        {
            _synthTopId = topId;
            _synthTop = top;
            _synthTopPath = top == null ? "" : SafePath(top);
            UpdateHover(top, ev);
        }

        // Buttons: one press at a time, as a mouse's left / right / middle.
        var buttons = UiSyntheticInput.Buttons;
        if (buttons != _synthButtonsSeen)
        {
            var went = buttons & ~_synthButtonsSeen;
            var gone = _synthButtonsSeen & ~buttons;
            _synthButtonsSeen = buttons;
            if (gone != UiSyntheticButtons.None && (_synthPress != null || _synthDragging) && (gone & ToFlag(_synthPressButton)) != 0)
                ReleasePress(ev, click: true);
            if (went != UiSyntheticButtons.None && _synthPress == null && !_synthDragging)
                Press(top, ev, (went & UiSyntheticButtons.Left) != 0 ? PointerEventData.InputButton.Left
                    : (went & UiSyntheticButtons.Right) != 0 ? PointerEventData.InputButton.Right
                    : PointerEventData.InputButton.Middle);
        }

        if (_synthDrag != null && (_synthPress != null || _synthDragging))
        {
            float threshold = 10f;
            try { threshold = EventSystem.current != null ? EventSystem.current.pixelDragThreshold : 10f; } catch { /* */ }
            if (!_synthDragging && (pos - _synthPressAt).sqrMagnitude >= threshold * threshold)
            {
                _synthDragging = true;
                try { ev.dragging = true; } catch { /* */ }
                SendDrag(_synthDrag, ev, "begin-drag");
                // As the input module does when a drag starts on another object than the press: the press target gets
                // its pointer-up now and the press is no longer eligible for a click. A drag never ends in a click.
                if (_synthPress != null && GoId(_synthPress) != GoId(_synthDrag) && Alive(_synthPress))
                {
                    try { _synthPress.GetComponent<EventTrigger>()?.OnPointerUp(ev); } catch { /* */ }
                    try { _synthPress.GetComponent<Selectable>()?.OnPointerUp(ev); } catch { /* */ }
                    Note("up " + SafePath(_synthPress) + " (drag started)");
                    _synthPress = null;
                    try { ev.pointerPress = null; ev.rawPointerPress = null; } catch { /* */ }
                }

                _synthClick = null;
                _synthClickKind = null;
                try { ev.eligibleForClick = false; } catch { /* */ }
            }

            if (_synthDragging && delta.sqrMagnitude > 0f) SendDrag(_synthDrag, ev, "drag");
        }
    }

    private static UiSyntheticButtons ToFlag(PointerEventData.InputButton b) => b switch
    {
        PointerEventData.InputButton.Right => UiSyntheticButtons.Right,
        PointerEventData.InputButton.Middle => UiSyntheticButtons.Middle,
        _ => UiSyntheticButtons.Left,
    };

    // ---- Hover --------------------------------------------------------------------------------------

    /// <summary>Exit what is no longer under the point (leaf first), enter what newly is (leaf first), as the input module does.</summary>
    private static void UpdateHover(GameObject? top, PointerEventData ev)
    {
        // Compared by instance id: two interop wrappers of one object are different managed references.
        var chain = new List<GameObject>();
        var chainIds = new List<int>();
        var t = top == null ? null : top.transform;
        int guard = 0;
        while (t != null && guard++ < 64)
        {
            var go = t.gameObject;
            chain.Add(go);
            chainIds.Add(GoId(go));
            t = t.parent;
        }

        for (int i = 0; i < SynthHovered.Count; i++)
            if (!chainIds.Contains(SynthHoveredIds[i])) SendExit(SynthHovered[i], ev);

        try { ev.pointerEnter = top; } catch { /* */ }
        for (int i = 0; i < chain.Count; i++)
            if (!SynthHoveredIds.Contains(chainIds[i])) SendEnter(chain[i], ev);
        SynthHovered.Clear();
        SynthHovered.AddRange(chain);
        SynthHoveredIds.Clear();
        SynthHoveredIds.AddRange(chainIds);
    }

    private static void SendEnter(GameObject go, PointerEventData ev)
    {
        if (!Alive(go)) return;
        bool sent = false;
        try { var trigger = go.GetComponent<EventTrigger>(); if (trigger != null) { trigger.OnPointerEnter(ev); sent = true; } } catch { /* */ }
        try { var sel = go.GetComponent<Selectable>(); if (sel != null) { sel.OnPointerEnter(ev); sent = true; } } catch { /* */ }
        if (sent) Note("enter " + SafePath(go));
    }

    private static void SendExit(GameObject go, PointerEventData ev)
    {
        if (!Alive(go)) return;
        bool sent = false;
        try { var trigger = go.GetComponent<EventTrigger>(); if (trigger != null) { trigger.OnPointerExit(ev); sent = true; } } catch { /* */ }
        try { var sel = go.GetComponent<Selectable>(); if (sel != null) { sel.OnPointerExit(ev); sent = true; } } catch { /* */ }
        if (sent) Note("exit " + SafePath(go));
    }

    // ---- Press, release, click, drag ---------------------------------------------------------------

    private static bool HasDownHandler(GameObject go)
    {
        try { if (go.GetComponent<EventTrigger>() != null) return true; } catch { /* */ }
        try { if (go.GetComponent<Selectable>() != null) return true; } catch { /* */ }
        return false;
    }

    private static bool HasDragHandler(GameObject go)
    {
        try { if (go.GetComponent<EventTrigger>() != null) return true; } catch { /* */ }
        try { if (go.GetComponent<Scrollbar>() != null) return true; } catch { /* */ }
        try { if (go.GetComponent<ScrollRect>() != null) return true; } catch { /* */ }
        try { if (go.GetComponent<Slider>() != null) return true; } catch { /* */ }
        try { if (go.GetComponent<TMP_InputField>() != null) return true; } catch { /* */ }
        return false;
    }

    private static GameObject? FirstUp(GameObject? from, Func<GameObject, bool> has)
    {
        var t = from == null ? null : from.transform;
        int guard = 0;
        while (t != null && guard++ < 64)
        {
            if (has(t.gameObject)) return t.gameObject;
            t = t.parent;
        }

        return null;
    }

    private static string? ClickKindUp(GameObject? from, out GameObject? at)
    {
        at = null;
        var t = from == null ? null : from.transform;
        int guard = 0;
        while (t != null && guard++ < 64)
        {
            string? kind = ClickHandlerKind(t.gameObject);
            if (kind != null)
            {
                at = t.gameObject;
                return kind;
            }

            t = t.parent;
        }

        return null;
    }

    private static void Press(GameObject? top, PointerEventData ev, PointerEventData.InputButton button)
    {
        _synthPressButton = button;
        _synthPressAt = ev.position;
        try
        {
            ev.button = button;
            ev.pointerId = button == PointerEventData.InputButton.Left ? -1 : button == PointerEventData.InputButton.Right ? -2 : -3;
            ev.pressPosition = ev.position;
            ev.pointerPressRaycast = _synthTopResult;
            ev.eligibleForClick = true;
            ev.delta = Vector2.zero;
            ev.dragging = false;
            ev.useDragThreshold = true;
        }
        catch { /* */ }

        // The input module's DeselectIfSelectionChanged: a press on another selectable drops the selection.
        try
        {
            var es = EventSystem.current;
            var selectable = FirstUp(top, go => { try { return go.GetComponent<Selectable>() != null; } catch { return false; } });
            if (es != null && es.currentSelectedGameObject != null
                && (selectable == null || es.currentSelectedGameObject.GetInstanceID() != selectable.GetInstanceID()))
                es.SetSelectedGameObject(null, ev);
        }
        catch { /* */ }

        var press = FirstUp(top, HasDownHandler);
        _synthPress = press;
        try { ev.pointerPress = press; ev.rawPointerPress = top; } catch { /* */ }
        if (press != null)
        {
            try { press.GetComponent<EventTrigger>()?.OnPointerDown(ev); } catch { /* */ }
            try { press.GetComponent<Selectable>()?.OnPointerDown(ev); } catch { /* */ }
            Note("down " + button.ToString().ToLowerInvariant() + " " + SafePath(press));
        }

        if (button == PointerEventData.InputButton.Left) NoteCaretClick(top, ev.position);
        _synthClickKind = ClickKindUp(top, out _synthClick);
        _synthDrag = FirstUp(top, HasDragHandler);
        _synthDragging = false;
        try { ev.pointerDrag = _synthDrag; } catch { /* */ }
        if (_synthDrag != null) SendDrag(_synthDrag, ev, "init-drag");
        if (press == null && _synthClick == null) Note("down " + button.ToString().ToLowerInvariant() + " on nothing that takes a press");
    }

    private static void ReleasePress(PointerEventData ev, bool click)
    {
        var press = _synthPress;
        try { ev.button = _synthPressButton; } catch { /* */ }
        if (press != null && Alive(press))
        {
            try { press.GetComponent<EventTrigger>()?.OnPointerUp(ev); } catch { /* */ }
            try { press.GetComponent<Selectable>()?.OnPointerUp(ev); } catch { /* */ }
            Note("up " + SafePath(press));
        }

        if (click && _synthClick != null && _synthClickKind != null && Alive(_synthClick))
        {
            // A click only when the release is over the object that took the press (the input module's rule).
            ClickKindUp(_synthTop, out var nowClick);
            if (nowClick != null && nowClick.GetInstanceID() == _synthClick.GetInstanceID())
            {
                bool invoked;
                if (_synthPressButton != PointerEventData.InputButton.Left && _synthClickKind != "event-trigger")
                {
                    // Buttons, toggles and the game's buttons answer the left button only; a trigger on the same object takes any.
                    var trigger = _synthClick.GetComponent<EventTrigger>();
                    invoked = false;
                    if (trigger != null)
                    {
                        try { trigger.OnPointerClick(ev); invoked = true; } catch { /* */ }
                    }
                }
                else if (_synthClickKind == "input")
                {
                    invoked = false;
                    try { _synthClick.GetComponent<TMP_InputField>()?.OnPointerClick(ev); invoked = true; } catch { /* */ }
                }
                else invoked = InvokePointerClick(_synthClick, _synthClickKind, ev);

                Note("click " + _synthPressButton.ToString().ToLowerInvariant() + " " + SafePath(_synthClick) + (invoked ? "" : " (not taken)"));
            }
            else Note("click dropped: released off " + SafePath(_synthClick));
        }

        if (_synthDragging && _synthDrag != null && Alive(_synthDrag)) SendDrag(_synthDrag, ev, "end-drag");
        _synthPress = null;
        _synthClick = null;
        _synthClickKind = null;
        _synthDrag = null;
        _synthDragging = false;
        try
        {
            ev.pointerPress = null;
            ev.rawPointerPress = null;
            ev.pointerDrag = null;
            ev.dragging = false;
            ev.eligibleForClick = false;
        }
        catch { /* */ }
    }

    private static void SendDrag(GameObject go, PointerEventData ev, string phase)
    {
        if (!Alive(go)) return;
        try
        {
            var trigger = go.GetComponent<EventTrigger>();
            if (trigger != null)
            {
                if (phase == "init-drag") trigger.OnInitializePotentialDrag(ev);
                else if (phase == "begin-drag") trigger.OnBeginDrag(ev);
                else if (phase == "drag") trigger.OnDrag(ev);
                else trigger.OnEndDrag(ev);
            }
        }
        catch { /* */ }

        try
        {
            var bar = go.GetComponent<Scrollbar>();
            if (bar != null)
            {
                if (phase == "init-drag") bar.OnInitializePotentialDrag(ev);
                else if (phase == "begin-drag") bar.OnBeginDrag(ev);
                else if (phase == "drag") bar.OnDrag(ev);
            }
        }
        catch { /* */ }

        try
        {
            var scroll = go.GetComponent<ScrollRect>();
            if (scroll != null)
            {
                if (phase == "init-drag") scroll.OnInitializePotentialDrag(ev);
                else if (phase == "begin-drag") scroll.OnBeginDrag(ev);
                else if (phase == "drag") scroll.OnDrag(ev);
                else scroll.OnEndDrag(ev);
            }
        }
        catch { /* */ }

        try
        {
            var slider = go.GetComponent<Slider>();
            if (slider != null)
            {
                if (phase == "init-drag") slider.OnInitializePotentialDrag(ev);
                else if (phase == "drag") slider.OnDrag(ev);
            }
        }
        catch { /* */ }

        if (phase != "drag") Note(phase + " " + SafePath(go));
    }

    private static void Note(string line)
    {
        SynthLog.Add(line);
        if (SynthLog.Count > 40) SynthLog.RemoveAt(0);
    }

    // ---- Bridge helpers ------------------------------------------------------------------------------

    /// <summary>The driver's readback: the topmost hit, the hovered chain (leaf first), the press, the drag, the log.</summary>
    internal static Dictionary<string, object?> DescribeSynthetic()
    {
        var hovered = new List<string>();
        for (int i = 0; i < SynthHovered.Count && i < 12; i++)
            hovered.Add(Alive(SynthHovered[i]) ? SafePath(SynthHovered[i]) : "(destroyed)");
        string selected = "";
        try
        {
            var es = EventSystem.current;
            if (es != null && es.currentSelectedGameObject != null) selected = SafePath(es.currentSelectedGameObject);
        }
        catch { /* */ }

        var d = UiSyntheticInput.Describe();
        d["driver"] = _synthOn;
        d["top"] = _synthTopPath;
        d["topOurs"] = _synthTop != null && IsOurs(_synthTop);
        d["hovered"] = hovered;
        d["pressed"] = _synthPress != null && Alive(_synthPress) ? SafePath(_synthPress) : "";
        d["dragging"] = _synthDragging;
        d["dragTarget"] = _synthDrag != null && Alive(_synthDrag) ? SafePath(_synthDrag) : "";
        d["selected"] = selected;
        d["events"] = new List<string>(SynthLog);
        return d;
    }

    internal static void ClearSyntheticLog() => SynthLog.Clear();

    /// <summary>
    /// Give the kit text field at the screen point the keyboard through the shown-input pump's own click-to-focus
    /// (<see cref="FocusFieldAt"/>: visible, inside its clipping viewport, not under a dialog). When that path refuses,
    /// nothing is focused and the reply names the field the raycast found, so the bridge never succeeds where a click
    /// would not.
    /// </summary>
    internal static Dictionary<string, object?> BridgeFocusFieldAt(float x, float y)
    {
        var d = new Dictionary<string, object?> { ["ok"] = false };
        // The click path first: the shown-input pump's own click-to-focus over the armed fields' rects.
        bool clickPath = false;
        try { clickPath = FocusFieldAt(x, y); } catch { clickPath = false; }
        if (clickPath && FocusedArmedField() is { } byClick)
        {
            d["ok"] = true;
            d["field"] = SafePath(byClick.gameObject);
            d["focused"] = true;
            d["path"] = "click-to-focus";
            return d;
        }

        EventSystem? es = null;
        try { es = EventSystem.current; } catch { /* */ }
        if (es == null)
        {
            d["error"] = "EventSystem.current is null";
            return d;
        }

        var ev = new PointerEventData(es) { position = new Vector2(x, y) };
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        es.RaycastAll(ev, results);
        if (results == null || results.Count == 0)
        {
            d["error"] = "nothing under (" + x.ToString("F0") + ", " + y.ToString("F0") + ")";
            return d;
        }

        var hit = results[0].gameObject;
        d["hit"] = hit == null ? null : SafePath(hit);
        TMP_InputField? field = null;
        var t = hit == null ? null : hit.transform;
        int guard = 0;
        while (t != null && field == null && guard++ < 8)
        {
            try { field = t.GetComponent<TMP_InputField>(); } catch { field = null; }
            if (field == null)
            {
                try { field = t.GetComponentInChildren<TMP_InputField>(); } catch { field = null; }
            }

            t = t.parent;
        }

        if (field == null)
        {
            d["error"] = "no text field at or above the hit object";
            return d;
        }

        // Not focused here on purpose: the bridge succeeds only where a player's click would (the click path refused it:
        // the field is hidden, clipped out of its viewport, under a dialog, or not a kit field).
        d["field"] = SafePath(field.gameObject);
        d["error"] = "the click-to-focus path did not take this point (the field is hidden, clipped by its viewport, under a dialog, or not a kit field)";
        return d;
    }

    /// <summary>Whether a kit text field has the keyboard now, and its path.</summary>
    internal static Dictionary<string, object?> DescribeFocusedField()
    {
        var field = FocusedArmedField();
        if (field == null) return new Dictionary<string, object?> { ["focused"] = false };
        string text = "";
        try { text = field.text ?? ""; } catch { /* */ }
        return new Dictionary<string, object?>
        {
            ["focused"] = true,
            ["path"] = SafePath(field.gameObject),
            ["text"] = text.Length > 200 ? text.Substring(0, 200) + "…" : text,
            ["multiline"] = IsMultiline(field),
        };
    }

    /// <summary>The kit tooltip box: shown, title, body and its rect in screen pixels (origin bottom-left).</summary>
    internal static Dictionary<string, object?> DescribeTooltip()
    {
        var d = new Dictionary<string, object?> { ["shown"] = false };
        try
        {
            if (!Alive(_tipRoot) || !_tipRoot!.activeSelf) return d;
            d["shown"] = true;
            d["title"] = _tipTitle != null && _tipTitle.gameObject.activeSelf ? _tipTitle.text : "";
            d["body"] = _tipBody != null && _tipBody.gameObject.activeSelf ? _tipBody.text : "";
            if (_tipBox != null) d["rect"] = RectDict(ScreenRectOf(_tipBox));
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }

    internal static Dictionary<string, object?>? RectDict(ScreenRect r) => !r.IsKnown ? null : new Dictionary<string, object?>
    {
        ["x"] = Math.Round(r.MinX, 1),
        ["y"] = Math.Round(r.MinY, 1),
        ["w"] = Math.Round(r.MaxX - r.MinX, 1),
        ["h"] = Math.Round(r.MaxY - r.MinY, 1),
    };

    /// <summary>The submit half of a synthetic Enter on a focused single-line field (TMP's own Enter reads the OS queue).</summary>
    internal static bool SyntheticSubmitFocused()
    {
        var field = FocusedArmedField();
        if (field == null || IsMultiline(field)) return false;
        // A widget's value box and an external field take Enter through the pump's own Enter branch (the synthetic key's
        // edge: ReleaseWidgetField / NotifyExternalSubmit); only a plain kit field needs TMP's submit, as `type` gives it.
        if (EnterLeavesField(field) || ExternalGuardId(field) != null) return false;
        // Owned: dropped by the driver if the field loses the keyboard first (a dialog's Submit), and on the lease's end.
        UiSyntheticInput.NoteTextFor(FieldId(field));
        UiTextInjection.Keys.Enqueue(new UiTestChord(UiKey.Enter, ctrl: false, alt: false, shift: false, meta: false));
        return true;
    }

    /// <summary>
    /// A synthetic point as the pointer sample: inside the window whatever the focus (it is the framework's own reading,
    /// so the focus test that guards the device does not apply), outside past the window's edges. The window's size here
    /// is the same device-pixel ruler as the pointer-bounds test.
    /// </summary>
    private static PointerSample SyntheticPointerSample(float x, float y)
    {
        var (w, h) = ScreenSize();
        if (w > 0 && h > 0 && (x < 0f || y < 0f || x > w || y > h)) return PointerSample.Outside;
        return PointerSample.At(x, y);
    }

    // ---- Caret: line moves and a click at a text position ------------------------------------------

    /// <summary>
    /// The focused multi-line field's line starts: TMP's laid-out lines (wrapping included) when its layout matches the
    /// text (no rich text, every character laid out), else the text's line breaks.
    /// </summary>
    private static int[] LineStarts(TMP_InputField field, string text)
    {
        try
        {
            var tmp = field.textComponent;
            var info = tmp == null ? null : tmp.textInfo;
            if (info != null && UiCaretLayout.TmpLayoutMatches(text, info.characterCount, info.lineCount))
            {
                var lines = info.lineInfo;
                int n = info.lineCount;
                if (lines != null && n > 0 && n <= lines.Length)
                {
                    var starts = new int[n];
                    for (int i = 0; i < n; i++) starts[i] = Math.Max(0, Math.Min(text.Length, lines[i].firstCharacterIndex));
                    starts[0] = 0;
                    return starts;
                }
            }
        }
        catch { /* the line breaks below */ }

        return UiCaretLines.BreakStarts(text);
    }

    /// <summary>Put the kit's owned caret (and the selection's anchor) at a string index, as the kit's own edits do.</summary>
    private static UiTextEditState SetOwnedCaret(TMP_InputField field, int caret, int anchor)
    {
        string text = "";
        try { text = field.text ?? ""; } catch { /* */ }
        var state = UiTextEdit.Normalize(new UiTextEditState(text, caret, anchor));
        var armed = FindArmed(field);
        if (armed != null)
        {
            armed.Owned = state;
            armed.HasOwned = true;
        }

        PushTmpCaret(field, state);
        if (armed != null) NoteOwnedCaretEdit(armed);
        try { SyncOwnedCarets(); } catch { /* */ }
        return state;
    }

    /// <summary>
    /// A UI-layer Up, Down, Home or End on a focused multi-line field: moved by line on the kit's owned caret (Shift keeps
    /// the selection's anchor). Null when the key is not a line move there; the typed-key pump then takes the key.
    /// </summary>
    internal static Dictionary<string, object?>? SyntheticLineKey(UiKey key, bool shift)
    {
        if (!UiCaretLines.IsLineKey(key)) return null;
        var field = FocusedArmedField();
        if (field == null || !IsMultiline(field)) return null;
        var before = ReadFieldState(field);
        int caret = UiCaretLines.Move(key, before.Text, LineStarts(field, before.Text), before.Caret);
        if (caret < 0) return null;
        var after = SetOwnedCaret(field, caret, shift ? before.Anchor : caret);
        return new Dictionary<string, object?>
        {
            ["key"] = key.ToString(),
            ["caretBefore"] = before.Caret,
            ["caret"] = after.Caret,
            ["anchor"] = after.Anchor,
            ["path"] = "line move on the owned caret",
        };
    }

    private static int _caretClickFieldId;
    private static Vector2 _caretClickAt;
    private static int _caretClickFrames;

    /// <summary>A press over a kit text field: once the click has focused it, its caret goes to the pressed character.</summary>
    private static void NoteCaretClick(GameObject? top, Vector2 at)
    {
        _caretClickFieldId = 0;
        TMP_InputField? field = null;
        var t = top == null ? null : top.transform;
        int guard = 0;
        while (t != null && field == null && guard++ < 8)
        {
            try { field = t.GetComponent<TMP_InputField>(); } catch { field = null; }
            t = t.parent;
        }

        if (field == null || FindArmed(field) == null) return;
        _caretClickFieldId = FieldId(field);
        _caretClickAt = at;
        _caretClickFrames = 6;
    }

    /// <summary>Once a frame: place a noted click's caret as soon as its field has the keyboard (at most a few frames).</summary>
    private static void PlaceCaretClick()
    {
        if (_caretClickFieldId == 0) return;
        if (--_caretClickFrames < 0)
        {
            _caretClickFieldId = 0;
            return;
        }

        var field = FocusedArmedField();
        if (field == null || FieldId(field) != _caretClickFieldId) return;
        _caretClickFieldId = 0;
        try
        {
            var tmp = field.textComponent;
            string text = field.text ?? "";
            if (tmp == null) return;
            var info = tmp.textInfo;
            if (info == null || !UiCaretLayout.TmpLayoutMatches(text, info.characterCount, info.lineCount))
            {
                Note("caret click: the field's layout does not match its text; caret left where focus put it");
                return;
            }

            int index = TMP_TextUtilities.GetCursorIndexFromPosition(tmp, new Vector3(_caretClickAt.x, _caretClickAt.y, 0f), null);
            index = Math.Max(0, Math.Min(text.Length, index));
            SetOwnedCaret(field, index, index);
            Note("caret click: caret at " + index);
        }
        catch (Exception e) { Note("caret click failed: " + e.Message); }
    }

    /// <summary>The current screen size in pixels.</summary>
    internal static (int Width, int Height) ScreenSize()
    {
        try { return (Screen.width, Screen.height); }
        catch { return (0, 0); }
    }
}
#endif
