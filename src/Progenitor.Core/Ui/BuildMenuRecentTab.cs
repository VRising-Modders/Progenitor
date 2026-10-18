#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Progenitor.Ui;

/// <summary>An owned sibling of the game's categories, styled by a native category clone.</summary>
internal sealed class BuildMenuRecentTab : IDisposable
{
    private GameObject? _root;
    private SimpleStunButton? _button;
    private readonly Dictionary<IntPtr, IDisposable> _watches = new();
    private readonly HashSet<IntPtr> _seen = new();
    private readonly List<IntPtr> _removed = new();
    private ushort _clickSlot;
    private Action? _choose;
    private Action? _leave;
    private IntPtr _parent;
    private bool _searchDisabled;
    private CanvasGroup? _fade;

    internal void Update(BuildMenu menu, bool selected, Action choose, Action leave)
    {
        if (!UnityAlive.Of(menu)) { Dispose(); return; }
        var group = menu.CategorySelectionGroup;
        if (!UnityAlive.Of(group)) { Dispose(); return; }
        var entries = group.Entries;
        if (entries == null || entries.Count == 0) { Dispose(); return; }
        var donor = entries[0];
        if (!UnityAlive.Of(donor)) { Dispose(); return; }
        var parent = donor.transform.parent;
        if (!UnityAlive.Of(parent)) { Dispose(); return; }
        if (!UnityAlive.Of(_root) || !UnityAlive.Of(_button) || parent.Pointer != _parent)
        {
            Dispose();
            var holder = new GameObject("progenitor-recent-staging");
            holder.SetActive(false);
            bool built = false;
            try
            {
                _root = UnityEngine.Object.Instantiate(donor.gameObject, holder.transform, false);
                if (!UnityAlive.Of(_root)) throw new InvalidOperationException("Recent category clone is missing.");
                _root.SetActive(false);
                _root.name = "progenitor-build-recent";
                var entry = _root.GetComponent<BuildMenu_CategoryEntry>();
                if (!UnityAlive.Of(entry) || !UnityAlive.Of(entry.Name))
                    throw new InvalidOperationException("Recent category donor has no label.");
                var label = entry.Name.Text ?? entry.Name.GetComponent<TMPro.TextMeshProUGUI>();
                _button = entry.Button;
                if (!UnityAlive.Of(label) || !UnityAlive.Of(_button))
                    throw new InvalidOperationException("Recent category donor has no text or button.");
                _button.onClick = new SimpleStunButton.ButtonClickedEvent();
                _button.onDoubleClick = new SimpleStunButton.ButtonClickedEvent();
                if (entry.Highlight != null) entry.Highlight.gameObject.SetActive(false);
                if (entry.Icon != null) entry.Icon.gameObject.SetActive(false);
                // Remove copied selection/localization drivers while the clone is inactive.
                // Deferred destruction would allow them to survive its first activation.
                foreach (var driver in _root.GetComponentsInChildren<GridSelectionEntry>(true))
                    UnityEngine.Object.DestroyImmediate(driver);
                foreach (var localized in _root.GetComponentsInChildren<LocalizedText>(true))
                    UnityEngine.Object.DestroyImmediate(localized);
                foreach (var trigger in _root.GetComponentsInChildren<EventTrigger>(true))
                    UnityEngine.Object.DestroyImmediate(trigger);
                label.text = "Recent";
                _button.enabled = true;
                _button.interactable = true;
                // Both pointer clicks and Submit use the same process-lifetime trampoline.
                _clickSlot = UiSlotTable.Alloc(this);
                UiSlotTable.BindClick(_clickSlot, () => { if (!_searchDisabled) _choose?.Invoke(); });
                if (!BootstrapLink.TryWireUi(_clickSlot, (byte)UiSlotKind.Click, _button.onClick))
                    throw new InvalidOperationException("Recent category click trampoline is unavailable.");
                _root.transform.SetParent(parent, false);
                _root.transform.SetAsLastSibling();
                _parent = parent.Pointer;
                built = true;
            }
            finally
            {
                if (!built) Dispose();
                UnityEngine.Object.Destroy(holder);
            }
        }
        _choose = choose;
        _leave = leave;
        // Either native pool may grow or replace entries without replacing the category parent.
        _seen.Clear();
        for (int i = 0; i < entries.Count; i++)
            if (UnityAlive.Of(entries[i])) WatchOriginal(entries[i], entries[i].Button);
        var tags = menu.TagSelectionGroup;
        if (UnityAlive.Of(tags) && tags.Entries != null)
            for (int i = 0; i < tags.Entries.Count; i++)
            {
                var tag = tags.Entries[i];
                if (UnityAlive.Of(tag)) WatchOriginal(tag, tag.Button);
            }
        _removed.Clear();
        foreach (var pair in _watches)
            if (!_seen.Contains(pair.Key)) _removed.Add(pair.Key);
        foreach (var id in _removed)
        {
            DropWatch(_watches[id]);
            _watches.Remove(id);
        }
        _button!.ForceSelect = selected && !_searchDisabled;
        _root!.SetActive(true);
        if (selected)
            for (int i = 0; i < entries.Count; i++)
                if (UnityAlive.Of(entries[i]) && UnityAlive.Of(entries[i].Button)) entries[i].Button.ForceSelect = false;
    }

    internal void SetSearchDisabled(bool disabled)
    {
        _searchDisabled = disabled;
        if (!UnityAlive.Of(_button) || !UnityAlive.Of(_root)) return;
        _button!.SelectableWhenNotInteractable = false;
        _button.ForceHighlight = false;
        if (disabled) _button.ForceSelect = false;
        _button.interactable = !disabled;
        var events = EventSystem.current;
        var selectedObject = events?.currentSelectedGameObject;
        if (disabled && UnityAlive.Of(selectedObject)
            && (selectedObject!.Pointer == _root!.Pointer || selectedObject.transform.IsChildOf(_root.transform)))
            events!.SetSelectedGameObject(null);
        if (!UnityAlive.Of(_fade)) _fade = _root!.GetComponent<CanvasGroup>() ?? _root.AddComponent<CanvasGroup>();
        _fade!.alpha = disabled ? 0.45f : 1f;
        _fade.interactable = !disabled;
    }

    private void WatchOriginal(Behaviour original, SimpleStunButton? button)
    {
        _seen.Add(original.Pointer);
        if (_watches.ContainsKey(original.Pointer)) return;
        _watches.Add(original.Pointer, Ui.WatchPointer(original.gameObject, e =>
        {
            // EventTrigger remains enabled when Tools disables the native entry for search.
            if (e.Kind != UiPointerEventKind.Down || !e.PrimaryHeld || !UnityAlive.Of(original)
                || !original.isActiveAndEnabled) return;
            if (UnityAlive.Of(button) && !button!.IsInteractable()) return;
            _leave?.Invoke();
        }, this));
    }

    public void Dispose()
    {
        _choose = null;
        _leave = null;
        if (_clickSlot != 0)
        {
            // Inert first, even if removing the native listener fails during teardown.
            UiSlotTable.Release(_clickSlot);
            try { BootstrapLink.UnwireUi(_clickSlot); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Recent category unwire failed: " + e.Message); }
            _clickSlot = 0;
        }
        foreach (var watch in _watches.Values) DropWatch(watch);
        _watches.Clear();
        _seen.Clear();
        _removed.Clear();
        if (UnityAlive.Of(_root))
        {
            _root!.SetActive(false);
            UnityEngine.Object.Destroy(_root);
        }
        _root = null;
        _button = null;
        _fade = null;
        _searchDisabled = false;
        _parent = IntPtr.Zero;
    }

    private static void DropWatch(IDisposable watch)
    {
        try { watch.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Recent category watch cleanup failed: " + e.Message); }
    }
}
#endif
