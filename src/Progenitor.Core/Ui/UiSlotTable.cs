using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// ushort-keyed Core handlers that Bootstrap <c>UiSlots</c> trampolines invoke.
/// The UnityAction itself must not live here (collectible ALC pin); this table is
/// ordinary managed state that Uninstall / plugin unload clears.
/// </summary>
internal static class UiSlotTable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, Entry> Entries = new();
    private static ushort _next = 1;

    internal static ushort Alloc(object? owner)
    {
        lock (Gate)
        {
            ushort id = _next;
            if (id == 0) id = 1;
            _next = (ushort)(id + 1);
            if (_next == 0) _next = 1;
            Entries[id] = new Entry { Owner = owner };
            return id;
        }
    }

    internal static void BindClick(ushort id, Action? handler) =>
        Mutate(id, e => e.Click = handler);

    internal static void BindBool(ushort id, Action<bool>? handler) =>
        Mutate(id, e => e.Bool = handler);

    internal static void BindFloat(ushort id, Action<float>? handler) =>
        Mutate(id, e => e.Float = handler);

    internal static void BindText(ushort id, Action<string>? handler) =>
        Mutate(id, e => e.Text = handler);

    internal static void BindPointerEnter(ushort id, Action? handler) =>
        Mutate(id, e => e.PointerEnter = handler);

    internal static void BindPointerExit(ushort id, Action? handler) =>
        Mutate(id, e => e.PointerExit = handler);

    /// <summary>Bootstrap trampoline target. Signature matches <c>UiSlots.Dispatch</c>.</summary>
    internal static void BootstrapDispatch(ushort widgetId, byte kind, float number, bool flag, string? text)
    {
        Entry? entry;
        lock (Gate)
        {
            if (!Entries.TryGetValue(widgetId, out var found)) return;
            entry = found;
        }

        // KEYS8: the click that chose a mouse key in a key capture is not also a click on whatever widget
        // was under the pointer (a row's key cap would start the capture again and drop the chosen key).
        if ((UiSlotKind)kind is UiSlotKind.Click or UiSlotKind.Bool && Keybinds.SwallowsUiClicks)
        {
            Plugin.Logger?.LogDiag("Ui: slot id=" + widgetId + " click dropped: it is the click a key capture took.");
            return;
        }

        try
        {
            switch ((UiSlotKind)kind)
            {
                case UiSlotKind.Click:
                    entry.Click?.Invoke();
                    break;
                case UiSlotKind.Float:
                    entry.Float?.Invoke(number);
                    break;
                case UiSlotKind.Bool:
                    entry.Bool?.Invoke(flag);
                    break;
                case UiSlotKind.Text:
                    entry.Text?.Invoke(text ?? "");
                    break;
                case UiSlotKind.PointerEnter:
                    entry.PointerEnter?.Invoke();
                    break;
                case UiSlotKind.PointerExit:
                    entry.PointerExit?.Invoke();
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: slot id=" + widgetId + " kind=" + kind + " threw: " + e.Message);
        }
    }

    internal static void Release(ushort id)
    {
        if (id == 0) return;
        lock (Gate)
            Entries.Remove(id);
    }

    internal static ushort[] ReleaseOwner(object owner)
    {
        if (owner == null) return Array.Empty<ushort>();
        var ids = new List<ushort>();
        lock (Gate)
        {
            foreach (var kv in Entries)
            {
                if (ReferenceEquals(kv.Value.Owner, owner))
                    ids.Add(kv.Key);
            }

            for (int i = 0; i < ids.Count; i++)
                Entries.Remove(ids[i]);
        }

        return ids.ToArray();
    }

    internal static ushort[] SnapshotIds()
    {
        lock (Gate)
        {
            var ids = new ushort[Entries.Count];
            Entries.Keys.CopyTo(ids, 0);
            return ids;
        }
    }

    internal static void ClearAll()
    {
        lock (Gate)
        {
            Entries.Clear();
            _next = 1;
        }
    }

    internal static int LiveCount
    {
        get { lock (Gate) return Entries.Count; }
    }

    private static void Mutate(ushort id, Action<Entry> mutate)
    {
        if (id == 0) return;
        lock (Gate)
        {
            if (!Entries.TryGetValue(id, out var entry)) return;
            mutate(entry);
        }
    }

    private sealed class Entry
    {
        public object? Owner;
        public Action? Click;
        public Action<bool>? Bool;
        public Action<float>? Float;
        public Action<string>? Text;
        public Action? PointerEnter;
        public Action? PointerExit;
    }
}
