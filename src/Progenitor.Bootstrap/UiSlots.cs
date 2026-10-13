using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine.EventSystems;
using UnityEngine.Events;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime UnityEvent listeners for mod UI. A <c>UnityAction</c> whose method
/// lives in collectible Core (or a plugin ALC) is converted by Il2CppInterop and cached
/// for the process — the same pin as Harmony IL2CPP trampolines (evidence 88, spike step 3).
/// Bootstrap owns the listener instances; Core fills <see cref="TrySetDispatch"/> with a
/// ushort-keyed handler and nulls it on Uninstall. Plugin unload drops the ids it allocated.
/// </summary>
public static class UiSlots
{
    /// <summary>Log that the trampoline table is live. Listeners are created lazily on TryWire.</summary>
    public static void Install() =>
        ProcessHost.Log?.LogInfo("UiSlots: ready (process-lifetime UnityEvent trampolines).");

    /// <summary>Core dispatcher. Null means Core is down; a stray click is ignored.</summary>
    public delegate void Dispatch(ushort widgetId, byte kind, float number, bool flag, string? text);

    /// <summary>Matches <c>Progenitor.Ui.UiSlotKind</c>. Kept numeric so Core/Bootstrap cannot drift by name.</summary>
    public const byte KindClick = 1;
    public const byte KindFloat = 2;
    public const byte KindBool = 3;
    public const byte KindText = 4;
    public const byte KindPointerEnter = 5;
    public const byte KindPointerExit = 6;

    private static Dispatch? _core;
    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, Wired> Live = new();
    private static readonly Stack<ClickSlot> ClickPool = new();
    private static readonly Stack<FloatSlot> FloatPool = new();
    private static readonly Stack<BoolSlot> BoolPool = new();
    private static readonly Stack<TextSlot> TextSlotPool = new();
    private static readonly Stack<PointerSlot> PointerPool = new();

    /// <summary>
    /// Point the trampoline at Core's handler (or null it). The delegate is KeepDelegate'd
    /// under the current Core generation so Uninstall can drop it; the UnityAction bodies
    /// stay Bootstrap-owned.
    /// </summary>
    public static bool TrySetDispatch(Delegate? dispatcher)
    {
        try
        {
            Dispatch? bound = null;
            if (dispatcher != null)
            {
                bound = dispatcher is Dispatch typed
                    ? typed
                    : (Dispatch)Delegate.CreateDelegate(typeof(Dispatch), dispatcher.Target, dispatcher.Method);
                NativePinRegistry.KeepDelegate(bound, "ui.dispatch", CoreLoader.CurrentPinGeneration);
            }

            Volatile.Write(ref _core, bound);
            return true;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("UiSlots: TrySetDispatch threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Null the Core dispatcher. Live UnityActions stay until <see cref="Unwire"/> / <see cref="ClearAll"/>.</summary>
    public static void ClearDispatch() => TrySetDispatch(null);

    /// <summary>
    /// Attach a Bootstrap-owned listener to <paramref name="unityEvent"/> for
    /// <paramref name="widgetId"/>. <paramref name="kind"/> is a <c>Kind*</c> constant.
    /// Replaces any previous listener of that kind on the same id; other kinds stay.
    /// </summary>
    public static bool TryWire(ushort widgetId, byte kind, object? unityEvent)
    {
        if (widgetId == 0 || unityEvent == null) return false;
        UnwireKind(widgetId, kind);
        try
        {
            return kind switch
            {
                KindClick => WireClick(widgetId, unityEvent),
                KindFloat => WireFloat(widgetId, unityEvent),
                KindBool => WireBool(widgetId, unityEvent),
                KindText => WireText(widgetId, unityEvent),
                KindPointerEnter => WirePointer(widgetId, unityEvent, enter: true),
                KindPointerExit => WirePointer(widgetId, unityEvent, enter: false),
                _ => false,
            };
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("UiSlots: TryWire id=" + widgetId + " kind=" + kind + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Remove every listener for <paramref name="widgetId"/> and return slots to the pool.</summary>
    public static void Unwire(ushort widgetId)
    {
        if (widgetId == 0) return;
        Wired? wired;
        lock (Gate)
        {
            if (!Live.TryGetValue(widgetId, out wired)) return;
            Live.Remove(widgetId);
        }

        UnhookAll(widgetId, wired);
        lock (Gate)
            Recycle(wired);
    }

    private static void UnwireKind(ushort widgetId, byte kind)
    {
        Wired? wired;
        lock (Gate)
            Live.TryGetValue(widgetId, out wired);
        if (wired == null) return;
        try
        {
            switch (kind)
            {
                case KindClick:
                    wired.UnhookClick?.Invoke();
                    wired.UnhookClick = null;
                    lock (Gate)
                    {
                        if (wired.Click != null) ClickPool.Push(wired.Click);
                        wired.Click = null;
                    }
                    break;
                case KindFloat:
                    wired.UnhookFloat?.Invoke();
                    wired.UnhookFloat = null;
                    lock (Gate)
                    {
                        if (wired.Float != null) FloatPool.Push(wired.Float);
                        wired.Float = null;
                    }
                    break;
                case KindBool:
                    wired.UnhookBool?.Invoke();
                    wired.UnhookBool = null;
                    lock (Gate)
                    {
                        if (wired.Bool != null) BoolPool.Push(wired.Bool);
                        wired.Bool = null;
                    }
                    break;
                case KindText:
                    wired.UnhookText?.Invoke();
                    wired.UnhookText = null;
                    lock (Gate)
                    {
                        if (wired.Text != null) TextSlotPool.Push(wired.Text);
                        wired.Text = null;
                    }
                    break;
                case KindPointerEnter:
                    wired.UnhookEnter?.Invoke();
                    wired.UnhookEnter = null;
                    lock (Gate)
                    {
                        if (wired.Enter != null) PointerPool.Push(wired.Enter);
                        wired.Enter = null;
                    }
                    break;
                case KindPointerExit:
                    wired.UnhookExit?.Invoke();
                    wired.UnhookExit = null;
                    lock (Gate)
                    {
                        if (wired.Exit != null) PointerPool.Push(wired.Exit);
                        wired.Exit = null;
                    }
                    break;
            }
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("UiSlots: UnwireKind id=" + widgetId + " kind=" + kind + " threw: " + e.Message);
        }
    }

    private static void UnhookAll(ushort widgetId, Wired? wired)
    {
        if (wired == null) return;
        try { wired.UnhookClick?.Invoke(); } catch (Exception e) { WarnUnhook(widgetId, e); }
        try { wired.UnhookFloat?.Invoke(); } catch (Exception e) { WarnUnhook(widgetId, e); }
        try { wired.UnhookBool?.Invoke(); } catch (Exception e) { WarnUnhook(widgetId, e); }
        try { wired.UnhookText?.Invoke(); } catch (Exception e) { WarnUnhook(widgetId, e); }
        try { wired.UnhookEnter?.Invoke(); } catch (Exception e) { WarnUnhook(widgetId, e); }
        try { wired.UnhookExit?.Invoke(); } catch (Exception e) { WarnUnhook(widgetId, e); }
    }

    private static void WarnUnhook(ushort id, Exception e) =>
        ProcessHost.Log?.LogWarning("UiSlots: Unwire id=" + id + " threw: " + e.Message);

    /// <summary>Unwire every id in <paramref name="ids"/> (plugin unload / panel dispose).</summary>
    public static void UnwireMany(ushort[]? ids)
    {
        if (ids == null || ids.Length == 0) return;
        for (int i = 0; i < ids.Length; i++)
            Unwire(ids[i]);
    }

    /// <summary>
    /// Drop every listener and null the Core dispatcher. Core Uninstall and Bootstrap
    /// process-exit both call this; safe to call twice. Pooled <c>ClickSlot</c> /
    /// <c>PointerSlot</c> instances stay alive for process lifetime — they are
    /// Bootstrap-owned UnityActions; <see cref="Invoke"/> returns when <c>_core</c>
    /// is null. Do not clear the pools.
    /// </summary>
    public static void ClearAll()
    {
        ushort[] ids;
        lock (Gate)
        {
            ids = new ushort[Live.Count];
            Live.Keys.CopyTo(ids, 0);
        }

        for (int i = 0; i < ids.Length; i++)
            Unwire(ids[i]);
        Volatile.Write(ref _core, null);
    }

    public static string DescribePins()
    {
        int wired;
        lock (Gate) wired = Live.Count;
        bool dispatch = Volatile.Read(ref _core) != null;
        return "pin-diag UiSlots dispatch=" + (dispatch ? "True" : "False")
            + " wired=" + wired.ToString(CultureInfo.InvariantCulture);
    }

    internal static int WiredCount
    {
        get { lock (Gate) return Live.Count; }
    }

    private static void Invoke(ushort id, byte kind, float number, bool flag, string? text)
    {
        var core = Volatile.Read(ref _core);
        if (core == null) return;
        try { core(id, kind, number, flag, text); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("UiSlots: dispatch id=" + id + " kind=" + kind + " threw: " + e.Message);
        }
    }

    private static bool WireClick(ushort id, object unityEvent)
    {
        var ev = As<UnityEvent>(unityEvent);
        if (ev == null) return false;
        ClearPersistent(ev);
        ClickSlot slot;
        lock (Gate) slot = ClickPool.Count > 0 ? ClickPool.Pop() : new ClickSlot();
        slot.Id = id;
        ev.AddListener(slot.Action);
        Merge(id, w =>
        {
            w.Click = slot;
            w.UnhookClick = () =>
            {
                try { ev.RemoveListener(slot.Action); }
                catch { /* destroyed */ }
            };
        });
        return true;
    }

    private static bool WireFloat(ushort id, object unityEvent)
    {
        var ev = As<UnityEvent<float>>(unityEvent);
        if (ev == null) return false;
        ClearPersistent(ev);
        FloatSlot slot;
        lock (Gate) slot = FloatPool.Count > 0 ? FloatPool.Pop() : new FloatSlot();
        slot.Id = id;
        ev.AddListener(slot.Action);
        Merge(id, w =>
        {
            w.Float = slot;
            w.UnhookFloat = () =>
            {
                try { ev.RemoveListener(slot.Action); }
                catch { /* destroyed */ }
            };
        });
        return true;
    }

    private static bool WireBool(ushort id, object unityEvent)
    {
        var ev = As<UnityEvent<bool>>(unityEvent);
        if (ev == null) return false;
        ClearPersistent(ev);
        BoolSlot slot;
        lock (Gate) slot = BoolPool.Count > 0 ? BoolPool.Pop() : new BoolSlot();
        slot.Id = id;
        ev.AddListener(slot.Action);
        Merge(id, w =>
        {
            w.Bool = slot;
            w.UnhookBool = () =>
            {
                try { ev.RemoveListener(slot.Action); }
                catch { /* destroyed */ }
            };
        });
        return true;
    }

    private static bool WireText(ushort id, object unityEvent)
    {
        var ev = As<UnityEvent<string>>(unityEvent);
        if (ev == null) return false;
        ClearPersistent(ev);
        TextSlot slot;
        lock (Gate) slot = TextSlotPool.Count > 0 ? TextSlotPool.Pop() : new TextSlot();
        slot.Id = id;
        ev.AddListener(slot.Action);
        Merge(id, w =>
        {
            w.Text = slot;
            w.UnhookText = () =>
            {
                try { ev.RemoveListener(slot.Action); }
                catch { /* destroyed */ }
            };
        });
        return true;
    }

    private static bool WirePointer(ushort id, object unityEvent, bool enter)
    {
        var ev = As<UnityEvent<BaseEventData>>(unityEvent);
        if (ev == null) return false;
        ClearPersistent(ev);
        PointerSlot slot;
        lock (Gate) slot = PointerPool.Count > 0 ? PointerPool.Pop() : new PointerSlot();
        slot.Id = id;
        slot.Enter = enter;
        ev.AddListener(slot.Action);
        Merge(id, w =>
        {
            if (enter)
            {
                w.Enter = slot;
                w.UnhookEnter = () =>
                {
                    try { ev.RemoveListener(slot.Action); }
                    catch { /* destroyed */ }
                };
            }
            else
            {
                w.Exit = slot;
                w.UnhookExit = () =>
                {
                    try { ev.RemoveListener(slot.Action); }
                    catch { /* destroyed */ }
                };
            }
        });
        return true;
    }

    private static void Merge(ushort id, Action<Wired> apply)
    {
        lock (Gate)
        {
            if (!Live.TryGetValue(id, out var wired))
            {
                wired = new Wired();
                Live[id] = wired;
            }

            apply(wired);
        }
    }

    private static void Recycle(Wired? wired)
    {
        if (wired == null) return;
        if (wired.Click != null) ClickPool.Push(wired.Click);
        if (wired.Float != null) FloatPool.Push(wired.Float);
        if (wired.Bool != null) BoolPool.Push(wired.Bool);
        if (wired.Text != null) TextSlotPool.Push(wired.Text);
        if (wired.Enter != null) PointerPool.Push(wired.Enter);
        if (wired.Exit != null) PointerPool.Push(wired.Exit);
    }

    private static T? As<T>(object value) where T : Il2CppObjectBase
    {
        if (value is T typed) return typed;
        if (value is Il2CppObjectBase obj)
        {
            try { return obj.TryCast<T>(); }
            catch { return null; }
        }

        return null;
    }

    private static void ClearPersistent(UnityEventBase ev)
    {
        try { ev.RemoveAllListeners(); }
        catch { /* */ }
        try
        {
            int n = ev.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
                ev.SetPersistentListenerState(i, UnityEventCallState.Off);
        }
        catch { /* native persistent calls */ }
    }

    private sealed class Wired
    {
        public ClickSlot? Click;
        public FloatSlot? Float;
        public BoolSlot? Bool;
        public TextSlot? Text;
        public PointerSlot? Enter;
        public PointerSlot? Exit;
        public Action? UnhookClick;
        public Action? UnhookFloat;
        public Action? UnhookBool;
        public Action? UnhookText;
        public Action? UnhookEnter;
        public Action? UnhookExit;
    }

    private sealed class ClickSlot
    {
        public ushort Id;
        public readonly UnityAction Action;
        public ClickSlot()
        {
            System.Action managed = Fire;
            Action = managed;
        }

        private void Fire() => Invoke(Id, KindClick, 0f, false, null);
    }

    private sealed class FloatSlot
    {
        public ushort Id;
        public readonly UnityAction<float> Action;
        public FloatSlot()
        {
            System.Action<float> managed = Fire;
            Action = managed;
        }

        private void Fire(float value) => Invoke(Id, KindFloat, value, false, null);
    }

    private sealed class BoolSlot
    {
        public ushort Id;
        public readonly UnityAction<bool> Action;
        public BoolSlot()
        {
            System.Action<bool> managed = Fire;
            Action = managed;
        }

        private void Fire(bool value) => Invoke(Id, KindBool, 0f, value, null);
    }

    private sealed class TextSlot
    {
        public ushort Id;
        public readonly UnityAction<string> Action;
        public TextSlot()
        {
            System.Action<string> managed = Fire;
            Action = managed;
        }

        private void Fire(string value) => Invoke(Id, KindText, 0f, false, value);
    }

    private sealed class PointerSlot
    {
        public ushort Id;
        public bool Enter;
        public readonly UnityAction<BaseEventData> Action;
        public PointerSlot()
        {
            System.Action<BaseEventData> managed = Fire;
            Action = managed;
        }

        private void Fire(BaseEventData _) =>
            Invoke(Id, Enter ? KindPointerEnter : KindPointerExit, 0f, false, null);
    }
}
