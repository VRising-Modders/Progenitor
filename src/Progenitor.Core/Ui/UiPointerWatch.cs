using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Progenitor.Ui;

/// <summary>
/// One <see cref="Ui.WatchPointer"/> handle: owned <c>EventTrigger.Entry</c> rows (PointerDown,
/// PointerUp, PointerExit) on a game UI object, each wired through a Bootstrap-owned
/// <c>BaseEventData</c> trampoline. That is the seam the native-tab observer already uses
/// (<c>NativeModsEntry.ObserveNativeTabs</c>): the UnityAction lives in Bootstrap, the handler in
/// <see cref="UiSlotTable"/>, so a collectible plugin or Core generation never hands IL2CPP a
/// delegate of its own. The trampoline routing slot is the PointerEnter kind, which carries no
/// payload; the event registered with Unity is the one named here.
///
/// <para>Nothing the game owns is changed except the trigger list: an existing
/// <c>EventTrigger</c> keeps its rows, ours are removed on dispose, and a trigger this watch
/// added is destroyed once it holds no rows. Note that any <c>EventTrigger</c> handles every
/// pointer interface, so a watched object no longer lets scroll or drag events it did not handle
/// bubble to its parent.</para>
/// </summary>
internal sealed class UiPointerWatch : IDisposable
{
    private readonly EventTrigger _trigger;
    private readonly bool _created;
    private readonly object? _owner;
    private readonly List<(EventTrigger.Entry Entry, ushort Id)> _rows = new();
    private bool _disposed;

    private UiPointerWatch(EventTrigger trigger, bool created, object? owner)
    {
        _trigger = trigger;
        _created = created;
        _owner = owner;
    }

    /// <summary>Wire Down, Up and Exit on <paramref name="target"/> to <paramref name="handler"/>.</summary>
    internal static UiPointerWatch Create(GameObject target, Action<UiPointerEvent> handler, object? owner)
    {
        var trigger = target.GetComponent<EventTrigger>();
        bool created = trigger == null;
        trigger ??= target.AddComponent<EventTrigger>();
        var watch = new UiPointerWatch(trigger, created, owner);
        try
        {
            watch.Add(EventTriggerType.PointerDown,
                () => handler(new UiPointerEvent(UiPointerEventKind.Down, UnityUiRuntime.PrimaryPointerHeld())));
            watch.Add(EventTriggerType.PointerUp,
                () => handler(new UiPointerEvent(UiPointerEventKind.Up, UnityUiRuntime.PrimaryPointerHeld())));
            watch.Add(EventTriggerType.PointerExit,
                () => handler(new UiPointerEvent(UiPointerEventKind.Exit, UnityUiRuntime.PrimaryPointerHeld())));
        }
        catch
        {
            // Never leave a half-wired watch, or an empty trigger we added, on a game object.
            watch.Dispose();
            throw;
        }

        return watch;
    }

    private void Add(EventTriggerType type, Action action)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        ushort id = UiSlotTable.Alloc(_owner);
        _rows.Add((entry, id));
        UiSlotTable.BindPointerEnter(id, action);
        _trigger.triggers.Add(entry);
        if (!BootstrapLink.TryWireUi(id, (byte)UiSlotKind.PointerEnter, entry.callback))
            throw new InvalidOperationException("Ui: pointer watch trampoline unavailable for " + type);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UiPointerWatchRegistry.Forget(this);
        foreach (var (_, id) in _rows)
        {
            try { BootstrapLink.UnwireUi(id); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: pointer watch unwire threw: " + e.Message); }
            UiSlotTable.Release(id);
        }

        if (!Alive(_trigger))
        {
            _rows.Clear();
            return;
        }

        try
        {
            foreach (var (entry, _) in _rows) _trigger.triggers.Remove(entry);
            if (_created && _trigger.triggers.Count <= 0)
            {
                _trigger.enabled = false;
                if (BootstrapLink.MayDestroyUiImmediate()) UnityEngine.Object.DestroyImmediate(_trigger);
                else UnityEngine.Object.Destroy(_trigger);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: pointer watch teardown threw: " + e.Message);
        }

        _rows.Clear();
    }

    private static bool Alive(UnityEngine.Object? value) => UnityAlive.Of(value);
}
