#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

internal sealed partial class UnityUiRuntime
{
    /// <summary>
    /// Disabled panel tabs (<see cref="IVanillaPanelTabs.SetTabEnabled"/>): the state is the host's,
    /// so it outlives every strip rebuild, and is put back on the tab objects each frame. A disabled
    /// tab's button is made non-interactable (its click and controller navigation stop, and a donor
    /// with a Disabled state draws it), or faint (<see cref="VanillaTabDisabledLook"/>) when the donor
    /// draws Disabled like Normal. Only the button's interactable flag and the alpha of a CanvasGroup
    /// on the tab (the clone's own, whose alpha is put back on enable, or one added here) are
    /// touched: no rect, so the caption and art keeping are unaffected. The Redistribution tab
    /// follows tab zero (<see cref="VanillaPanelTabFallback.IsOff"/>). A tab's hover watch is made
    /// only once the tab is first disabled, so a strip that never disables a tab carries no extra
    /// event trigger (one takes the scroll events too).
    /// </summary>
    private sealed partial class NativePanelTabs
    {
        bool[]? _off;
        string[]? _why;
        // Per built tab, in _buttons order.
        readonly List<UiHoverWatch?> _tabHover = new();
        readonly List<string> _tabLook = new();
        readonly List<(CanvasGroup? Group, float Alpha)> _tabFaint = new();
        int _hoverTab = -1;
        Func<bool>? _tipCheck;

        bool[] Off => _off ??= new bool[_captions.Length];
        string[] Why => _why ??= new string[_captions.Length];

        /// <summary>Strip tab <paramref name="index"/> is disabled; the Redistribution tab follows tab zero.</summary>
        bool TabOff(int index) => VanillaPanelTabFallback.IsOff(index, Off);

        string TabWhy(int index) => index >= 0 && _captions.Length > 0 ? Why[index < _captions.Length ? index : 0] ?? "" : "";

        public void SetTabEnabled(int index, bool enabled, string reason)
        {
            if (index < 0 || index >= _captions.Length) return;
            Off[index] = !enabled;
            Why[index] = enabled ? "" : reason ?? "";
            KeepTabStates();
            if (!TabOff(_hoverTab)) HideTabTip();
            // Every tab disabled: the selected tab is no longer highlighted (ApplySelection).
            ApplySelection();
        }

        /// <summary>At build: the tab's disabled look; its hover watch waits until it is first disabled.</summary>
        void WatchTab(int index, GameObject button)
        {
            _tabLook.Add(DonorDrawsDisabled(button) ? VanillaTabDisabledLook.Donor : VanillaTabDisabledLook.Faint);
            _tabFaint.Add((null, 1f));
            _tabHover.Add(null);
            ApplyTabState(index);
        }

        /// <summary>The tab's hover watch (the tooltip for a disabled tab's reason), made once.</summary>
        void EnsureHover(int index, GameObject button)
        {
            if (index >= _tabHover.Count || _tabHover[index] != null) return;
            try { _tabHover[index] = UiHoverWatch.Create(button, inside => OnTabHover(index, inside), null); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel tab hover unavailable: " + e.Message); }
        }

        /// <summary>Every frame: put each tab's enabled state back in case something reset it.</summary>
        void KeepTabStates()
        {
            int n = Math.Min(_buttons.Count, _tabLook.Count);
            for (int i = 0; i < n; i++) ApplyTabState(i);
        }

        void ApplyTabState(int index)
        {
            if (index >= _buttons.Count || index >= _tabLook.Count) return;
            var go = _buttons[index].go;
            if (!Alive(go)) return;
            bool off = TabOff(index);
            if (off) EnsureHover(index, go);
            try
            {
                var stun = go.GetComponent<SimpleStunButton>();
                if (stun != null)
                {
                    if (stun.interactable == off) stun.interactable = !off;
                    if (off && stun.SelectableWhenNotInteractable) stun.SelectableWhenNotInteractable = false;
                }
                else
                {
                    var button = go.GetComponent<Button>();
                    if (button != null && button.interactable == off) button.interactable = !off;
                }

                bool faint = off && _tabLook[index] == VanillaTabDisabledLook.Faint;
                var (group, baseAlpha) = _tabFaint[index];
                if (faint && !Alive(group))
                {
                    group = go.GetComponent<CanvasGroup>();
                    baseAlpha = group != null ? group.alpha : 1f;
                    group ??= go.AddComponent<CanvasGroup>();
                    _tabFaint[index] = (group, baseAlpha);
                }
                if (Alive(group))
                {
                    float alpha = faint ? VanillaTabDisabledLook.FaintAlpha * baseAlpha : baseAlpha;
                    if (Math.Abs(group!.alpha - alpha) > .001f) group.alpha = alpha;
                    // The hover tooltip needs the pointer: the group never blocks or disables it.
                    if (!group.blocksRaycasts) group.blocksRaycasts = true;
                }
            }
            catch { /* a tab that cannot be read keeps the state it has */ }
        }

        /// <summary>
        /// The tab's button draws its Disabled state unlike Normal (<see cref="VanillaTabDisabledLook.Draws"/>)
        /// on any enabled state target of the cloned donor button; false without a readable one.
        /// </summary>
        static bool DonorDrawsDisabled(GameObject tab)
        {
            try
            {
                var stun = tab.GetComponent<SimpleStunButton>();
                if (stun != null)
                {
                    var targets = stun.TargetSettings;
                    if (targets == null) return false;
                    foreach (var target in targets)
                    {
                        if (target == null || !target.Enabled) continue;
                        int transition = (int)target.Transition;
                        bool sprite = false, trigger = false;
                        var colors = target.Colors;
                        if (transition == 2) { try { sprite = target.SpriteState.disabledSprite != null; } catch { /* */ } }
                        if (transition == 3)
                        {
                            try { var t = target.AnimationTriggers; trigger = t != null && !string.IsNullOrEmpty(t.disabledTrigger); }
                            catch { /* */ }
                        }
                        if (VanillaTabDisabledLook.Draws(transition, Tuple(colors.normalColor), Tuple(colors.disabledColor), sprite, trigger)) return true;
                    }

                    return false;
                }

                var button = tab.GetComponent<Button>();
                if (button == null) return false;
                int kind = (int)button.transition;
                bool swap = false, anim = false;
                if (kind == 2) { try { swap = button.spriteState.disabledSprite != null; } catch { /* */ } }
                if (kind == 3) { try { anim = button.animationTriggers != null && !string.IsNullOrEmpty(button.animationTriggers.disabledTrigger); } catch { /* */ } }
                var block = button.colors;
                return VanillaTabDisabledLook.Draws(kind, Tuple(block.normalColor), Tuple(block.disabledColor), swap, anim);
            }
            catch { return false; }

            static (float, float, float, float) Tuple(Color c) => (c.r, c.g, c.b, c.a);
        }

        void OnTabHover(int index, bool inside)
        {
            if (!inside)
            {
                if (_hoverTab == index) HideTabTip();
                return;
            }

            _hoverTab = index;
            string why = TabWhy(index);
            if (!TabOff(index) || string.IsNullOrWhiteSpace(why) || index >= _buttons.Count) { HideTabTip(); _hoverTab = index; return; }
            try
            {
                var go = _buttons[index].go;
                var rt = Alive(go) ? go.GetComponent<RectTransform>() : null;
                if (rt == null) return;
                var anchor = ScreenRectOf(rt);
                if (!anchor.IsKnown) return;
                Func<bool> check = () => _hoverTab == index && TabOff(index) && index < _buttons.Count
                    && Alive(_buttons[index].go) && _buttons[index].go.activeInHierarchy;
                _tipCheck = check;
                _runtime.ShowGameControlTooltip(anchor, why, "", check);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel tab tooltip not shown: " + e.Message); }
        }

        void HideTabTip()
        {
            _hoverTab = -1;
            var check = _tipCheck;
            _tipCheck = null;
            if (check == null || !TooltipOwnedBy(check)) return;
            try { _runtime.HideTooltip(); }
            catch { /* */ }
        }

        /// <summary>
        /// <c>ui_read panels</c> → <c>tabs</c> → <c>tabStates</c>: per tab (the Redistribution tab
        /// included while shown) its enabled state, reason, look, the button's interactable flag and
        /// whether its hover watch exists.
        /// </summary>
        List<object?> TabStates()
        {
            var list = new List<object?>();
            for (int i = 0; i < Math.Max(_captions.Length, _buttons.Count); i++)
            {
                bool? interactable = null;
                try
                {
                    if (i < _buttons.Count && Alive(_buttons[i].go))
                    {
                        var go = _buttons[i].go;
                        var stun = go.GetComponent<SimpleStunButton>();
                        if (stun != null) interactable = stun.interactable;
                        else { var b = go.GetComponent<Button>(); if (b != null) interactable = b.interactable; }
                    }
                }
                catch { /* unreadable */ }

                list.Add(new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["caption"] = i < _captions.Length ? _captions[i] : "Redistribution",
                    ["enabled"] = !TabOff(i),
                    ["reason"] = TabOff(i) ? TabWhy(i) : "",
                    ["look"] = TabOff(i) && i < _tabLook.Count ? _tabLook[i] : null,
                    ["interactable"] = interactable,
                    ["hoverWatch"] = i < _tabHover.Count && _tabHover[i] != null,
                });
            }

            return list;
        }

        /// <summary>With the strip: the hover watches, looks and faint groups go; the enabled state stays.</summary>
        void ReleaseTabStates()
        {
            HideTabTip();
            foreach (var watch in _tabHover)
            {
                try { watch?.Dispose(); }
                catch { /* */ }
            }

            _tabHover.Clear();
            _tabLook.Clear();
            _tabFaint.Clear();
        }
    }
}
#endif
