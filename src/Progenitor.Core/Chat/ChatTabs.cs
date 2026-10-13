#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Inputs;
using Progenitor.Ui;
using ProjectM.Network;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Chat;

/// <summary>
/// CHAT6: the chat box's tab strip, quick menu, move, resize and background opacity.
///
/// <list type="bullet">
/// <item><b>Tabs.</b> A line of text above the chat log, made from the game's own line prefab: <b>All</b> (fixed), then
/// the player's tabs (default <b>Clan</b>), <b>+</b> and the menu glyph. Left-click a tab to show it; typing in a
/// single-channel tab sends to that channel. A dot marks a tab with unread lines. Right-click a tab, or click the menu
/// glyph, for the quick menu.</item>
/// <item><b>Quick menu</b> (framework dialogs): Edit this tab, Close this tab, New tab, Colors (<see cref="ChatColorPanel"/>), Background opacity, Lock /
/// Unlock position, Reset position and size, All chat settings. It stores no copies of the settings.</item>
/// <item><b>Move</b> by dragging the strip (not on a tab name); <b>resize</b> by dragging the corner grip; both are
/// stored in <c>[Chat]</c> and put back by Reset. The box is kept on screen.</item>
/// </list>
/// </summary>
internal static class ChatTabs
{
    internal const string StripName = "progenitor-chat-tabs";
    internal const string GripName = "progenitor-chat-grip";

    // ---- settings (bound by ChatWindow) ------------------------------------------------------------------
    internal static ConfigEntry<string>? TabsSetting;
    internal static ConfigEntry<int>? ActiveSetting;
    internal static ConfigEntry<float>? OffsetX;
    internal static ConfigEntry<float>? OffsetY;
    internal static ConfigEntry<float>? Width;
    internal static ConfigEntry<float>? Height;
    internal static ConfigEntry<int>? Opacity;
    internal static ConfigEntry<bool>? Locked;

    private static List<ChatTab> _tabs = new() { ChatTab.All(), ChatTab.Clan() };
    private static bool[] _unread = new bool[ChatTab.MaxTabs];
    private static int _active;
    private static bool _loaded;

    private static IntPtr _windowPtr;
    private static HUDChatWindow? _window;
    private static GameObject? _strip;
    private static TextMeshProUGUI? _stripText;
    private static GameObject? _grip;
    private static string _drawn = "";
    private static RectTransform? _moveTarget;
    private static Vector2 _gamePosition;
    /// <summary>
    /// The messages area (the game's <c>TextArea</c>: a fixed 512 x 212 rect pinned to the bottom center of the
    /// window, live 2026-10-07) is what resizing changes; the window (fixed 556 x 351, pivot bottom-left) grows by the
    /// same amounts so its stretched input line keeps the messages' width and the messages' left edge stays put.
    /// </summary>
    private static RectTransform? _sizeTarget;
    private static Vector2 _gameSize;
    private static RectTransform? _windowRect;
    private static Vector2 _gameWindowSize;
    private static Graphic? _background;
    private static float _gameAlpha = -1f;

    private enum Drag { None, Move, Resize }
    private static Drag _drag;
    private static Vector2 _dragStartPointer;
    private static Vector2 _dragStartValue;

    /// <summary>How far (canvas units) a resize may grow the box before its top or right edge leaves the screen.</summary>
    private static Vector2 _resizeRoom;

    /// <summary>How far (canvas units) a move may go each way before part of the box leaves the screen.</summary>
    private static Vector2 _moveMin, _moveMax;

    internal static IReadOnlyList<ChatTab> Tabs => _tabs;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _tabs = ChatTab.Load(TabsSetting?.Value ?? ChatTab.DefaultSaved);
        _active = Math.Max(0, Math.Min(_tabs.Count - 1, ActiveSetting?.Value ?? 0));
        ChatView.ActiveTab = _tabs[_active];
    }

    /// <summary>A new line in the log: tabs that show it and are not the active one get the unread dot.</summary>
    internal static void NoteNew(ChatRecord r)
    {
        EnsureLoaded();
        if (r.Kind == ChatKind.Session || r.Transient) return;
        bool changed = false;
        for (int i = 0; i < _tabs.Count && i < _unread.Length; i++)
        {
            if (i == _active || _unread[i] || !_tabs[i].Shows(r)) continue;
            _unread[i] = true;
            changed = true;
        }

        if (changed) _drawn = "";
    }

    // ---- per frame -----------------------------------------------------------------------------------------

    internal static void Update(HUDChatWindow window, bool chatShown, RectTransform? hudParent)
    {
        EnsureLoaded();
        if (_strip == null || _windowPtr != window.Pointer || _strip.Pointer == IntPtr.Zero) Attach(window, hudParent);
        if (_stripText == null) return;
        if (_drawn.Length == 0)
        {
            string text = ChatTabStrip.Render(_tabs, _active, _unread, _tabs.Count < ChatTab.MaxTabs);
            _stripText.text = text;
            _drawn = text;
        }

        // The game moves the window into the clan menu and back; the stored position and size are for the HUD only.
        bool inHud = hudParent != null && window.transform.parent != null && window.transform.parent.Pointer == hudParent.Pointer;
        if (inHud != _inHud)
        {
            _inHud = inHud;
            _appliedValid = false;
        }

        ApplyLayout();
        ChatColorPanel.Update(window, chatShown);
        if (chatShown) HandleInput();
        else _drag = Drag.None;
    }

    private static bool _inHud = true;

    private static void Attach(HUDChatWindow window, RectTransform? hudParent)
    {
        Detach();
        var scroll = window.ScrollView;
        if (scroll == null || window.ChatMessagePrefab == null) return;
        var host = scroll.transform;
        var strip = UnityEngine.Object.Instantiate(window.ChatMessagePrefab.gameObject, host)?.TryCast<GameObject>();
        if (strip == null) return;
        strip.name = StripName;
        var layout = strip.AddComponent<LayoutElement>();
        layout.ignoreLayout = true;
        var rect = strip.transform.TryCast<RectTransform>();
        if (rect != null)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 0f);
            rect.anchoredPosition = new Vector2(0f, 4f);
            rect.sizeDelta = new Vector2(0f, 24f);
        }

        var text = strip.GetComponent<TextMeshProUGUI>();
        if (text == null)
        {
            UnityEngine.Object.Destroy(strip);
            return;
        }
        text.richText = true;
        text.enableWordWrapping = false;
        text.color = Color.white;

        var grip = UnityEngine.Object.Instantiate(window.ChatMessagePrefab.gameObject, host)?.TryCast<GameObject>();
        if (grip != null)
        {
            grip.name = GripName;
            grip.AddComponent<LayoutElement>().ignoreLayout = true;
            var g = grip.transform.TryCast<RectTransform>();
            if (g != null)
            {
                // Top-right: the box grows upward from the input line and to the right.
                g.anchorMin = new Vector2(1f, 1f);
                g.anchorMax = new Vector2(1f, 1f);
                g.pivot = new Vector2(1f, 1f);
                g.anchoredPosition = Vector2.zero;
                g.sizeDelta = new Vector2(18f, 18f);
            }

            var gt = grip.GetComponent<TextMeshProUGUI>();
            if (gt != null)
            {
                gt.text = "<color=" + ChatFormat.MutedHex + ">◢</color>";
                gt.text = "<color=" + ChatFormat.MutedHex + ">◥</color>";
                gt.alignment = TextAlignmentOptions.TopRight;
            }
        }

        _strip = strip;
        _stripText = text;
        _grip = grip;
        _window = window;
        _windowPtr = window.Pointer;
        _drawn = "";
        _appliedValid = false;

        // Move the HUD anchor the game positions the window under (it never sets a position itself, research
        // chat-client-feasibility-2026-10-07); resize the window's own rect; the background is the log's graphic.
        _moveTarget = hudParent;
        _gamePosition = _moveTarget != null ? _moveTarget.anchoredPosition : Vector2.zero;
        _sizeTarget = window.ScrollView.transform.parent?.TryCast<RectTransform>();
        _gameSize = _sizeTarget != null ? _sizeTarget.sizeDelta : Vector2.zero;
        _windowRect = window.transform.TryCast<RectTransform>();
        _gameWindowSize = _windowRect != null ? _windowRect.sizeDelta : Vector2.zero;
        _background = window.ScrollViewGraphic;
        _gameAlpha = _background != null ? _background.color.a : -1f;
    }

    internal static void Detach()
    {
        ChatColorPanel.Close();
        try
        {
            if (_moveTarget != null && _moveTarget.Pointer != IntPtr.Zero) _moveTarget.anchoredPosition = _gamePosition;
            if (_sizeTarget != null && _sizeTarget.Pointer != IntPtr.Zero) _sizeTarget.sizeDelta = _gameSize;
            if (_windowRect != null && _windowRect.Pointer != IntPtr.Zero) _windowRect.sizeDelta = _gameWindowSize;
            if (_background != null && _background.Pointer != IntPtr.Zero && _gameAlpha >= 0f)
            {
                var c = _background.color;
                _background.color = new Color(c.r, c.g, c.b, _gameAlpha);
            }

            if (_strip != null && _strip.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_strip);
            if (_grip != null && _grip.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_grip);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: detaching the tab strip threw: " + e.Message); }

        _strip = null;
        _stripText = null;
        _grip = null;
        _window = null;
        _windowPtr = IntPtr.Zero;
        _moveTarget = null;
        _sizeTarget = null;
        _windowRect = null;
        _background = null;
        _gameAlpha = -1f;
        _drag = Drag.None;
    }

    private static bool _appliedValid;
    private static float _aox, _aoy, _aw, _ah;
    private static int _aop;

    /// <summary>Put the stored offset, size and opacity on the window (only when one of them changed).</summary>
    private static void ApplyLayout()
    {
        float ox = OffsetX?.Value ?? 0f, oy = OffsetY?.Value ?? 0f, w = Width?.Value ?? 0f, h = Height?.Value ?? 0f;
        int opacity = Opacity?.Value ?? -1;
        if (_appliedValid && ox == _aox && oy == _aoy && w == _aw && h == _ah && opacity == _aop) return;
        _appliedValid = true;
        _aox = ox; _aoy = oy; _aw = w; _ah = h; _aop = opacity;
        if (_moveTarget != null) _moveTarget.anchoredPosition = _gamePosition + new Vector2(ox, oy);
        var logSize = _inHud ? new Vector2(w > 0f ? w : _gameSize.x, h > 0f ? h : _gameSize.y) : _gameSize;
        if (_sizeTarget != null) _sizeTarget.sizeDelta = logSize;
        if (_windowRect != null) _windowRect.sizeDelta = _gameWindowSize + (logSize - _gameSize);
        if (_background != null && _gameAlpha >= 0f)
        {
            var c = _background.color;
            _background.color = new Color(c.r, c.g, c.b, opacity < 0 ? _gameAlpha : Mathf.Clamp01(opacity / 100f));
        }
    }

    /// <summary>Whether the screen point is over the strip or the grip (the click guard covers both).</summary>
    internal static bool PointerOver(Vector2 point) =>
        Contains(_strip, point) || Contains(_grip, point) || ChatColorPanel.Contains(point);

    private static bool Contains(GameObject? go, Vector2 point)
    {
        if (go == null || go.Pointer == IntPtr.Zero || !go.activeInHierarchy) return false;
        var rect = go.transform.TryCast<RectTransform>();
        return rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, point, null);
    }

    private static void HandleInput()
    {
        if (!UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y)) return;
        var point = new Vector2(x, y);
        // The colors panel handles its own clicks.
        if (_drag == Drag.None && ChatColorPanel.Contains(point)) return;
        if (_drag != Drag.None)
        {
            if (!UnityInputRuntime.MouseButtonHeld(0))
            {
                _drag = Drag.None;
                KeepOnScreen();
                SaveConfig();
                return;
            }

            float scale = CanvasScale();
            var delta = (point - _dragStartPointer) / scale;
            if (_drag == Drag.Move && OffsetX != null && OffsetY != null)
            {
                // The whole box stays on screen: the tab line at the top, the channel hint at the bottom.
                OffsetX.Value = _dragStartValue.x + Mathf.Clamp(delta.x, _moveMin.x, Mathf.Max(_moveMin.x, _moveMax.x));
                OffsetY.Value = _dragStartValue.y + Mathf.Clamp(delta.y, _moveMin.y, Mathf.Max(_moveMin.y, _moveMax.y));
            }
            else if (_drag == Drag.Resize && Width != null && Height != null)
            {
                // The box grows up and to the right: its top and right edge stop at the screen's edge.
                Width.Value = Mathf.Clamp(_dragStartValue.x + Mathf.Min(delta.x, _resizeRoom.x), 240f, 1600f);
                Height.Value = Mathf.Clamp(_dragStartValue.y + Mathf.Min(delta.y, _resizeRoom.y), 120f, 1000f);
            }

            return;
        }

        bool left = UnityInputRuntime.MouseButtonPressedThisFrame(0);
        bool right = UnityInputRuntime.MouseButtonPressedThisFrame(1);
        if (!left && !right) return;

        if (left && Contains(_grip, point) && !(Locked?.Value ?? false))
        {
            _drag = Drag.Resize;
            _dragStartPointer = point;
            float w = Width?.Value ?? 0f, h = Height?.Value ?? 0f;
            _dragStartValue = new Vector2(w > 0f ? w : _gameSize.x, h > 0f ? h : _gameSize.y);
            _resizeRoom = ResizeRoom();
            return;
        }

        if (!Contains(_strip, point) || _stripText == null) return;
        int link = TMP_TextUtilities.FindIntersectingLink(_stripText, new Vector3(x, y, 0f), null);
        string id = link >= 0 ? _stripText.textInfo.linkInfo[link].GetLinkID() : "";
        int tab = ChatTabStrip.TabOf(id);
        if (right)
        {
            OpenMenu(tab >= 0 ? tab : _active);
            return;
        }

        if (tab >= 0) Select(tab);
        else if (id == ChatTabStrip.AddId) NewTab();
        else if (id == ChatTabStrip.MenuId) OpenMenu(_active);
        else if (!(Locked?.Value ?? false))
        {
            _drag = Drag.Move;
            _dragStartPointer = point;
            _dragStartValue = new Vector2(OffsetX?.Value ?? 0f, OffsetY?.Value ?? 0f);
            MoveRange();
        }
    }

    private static float CanvasScale()
    {
        try
        {
            var canvas = _window?.GetComponentInParent<Canvas>();
            float s = canvas != null ? canvas.rootCanvas.scaleFactor : 1f;
            return s > 0.01f ? s : 1f;
        }
        catch { return 1f; }
    }

    /// <summary>
    /// The box on screen: the strip, the grip and every shown part of the window's content (the messages, the input line
    /// and the game's "Tab Cycle Chat Channel" line below it). False when nothing is shown.
    /// </summary>
    private static bool TryBoxBounds(out Vector2 min, out Vector2 max)
    {
        var lo = new Vector2(float.MaxValue, float.MaxValue);
        var hi = new Vector2(float.MinValue, float.MinValue);
        bool any = false;
        void Add(Transform? t)
        {
            if (t == null || t.Pointer == IntPtr.Zero || !TryScreenRect(t.gameObject, out var a, out var b)) return;
            lo = Vector2.Min(lo, a);
            hi = Vector2.Max(hi, b);
            any = true;
        }

        Add(_strip?.transform);
        Add(_grip?.transform);
        var content = _sizeTarget != null && _sizeTarget.Pointer != IntPtr.Zero ? _sizeTarget.parent : null;
        if (content != null)
            for (int i = 0; i < content.childCount; i++) Add(content.GetChild(i));
        min = lo;
        max = hi;
        return any;
    }

    /// <summary>
    /// The room left above and to the right of the box, in canvas units, so a resize (which grows the box up and to the
    /// right) stops at the screen's edge. Never negative.
    /// </summary>
    private static Vector2 ResizeRoom()
    {
        if (!TryBoxBounds(out _, out var max)) return Vector2.zero;
        float scale = CanvasScale();
        return new Vector2(Mathf.Max(0f, (Screen.width - max.x) / scale), Mathf.Max(0f, (Screen.height - max.y) / scale));
    }

    /// <summary>The move range from where the box is now: each edge may reach the screen's edge, never past it.</summary>
    private static void MoveRange()
    {
        if (!TryBoxBounds(out var min, out var max))
        {
            _moveMin = new Vector2(float.MinValue, float.MinValue);
            _moveMax = new Vector2(float.MaxValue, float.MaxValue);
            return;
        }

        float scale = CanvasScale();
        _moveMin = new Vector2(Mathf.Min(0f, -min.x / scale), Mathf.Min(0f, -min.y / scale));
        _moveMax = new Vector2(Mathf.Max(0f, (Screen.width - max.x) / scale), Mathf.Max(0f, (Screen.height - max.y) / scale));
    }

    private static bool TryScreenRect(GameObject? go, out Vector2 min, out Vector2 max)
    {
        min = max = Vector2.zero;
        if (go == null || go.Pointer == IntPtr.Zero || !go.activeInHierarchy) return false;
        var rect = go.transform.TryCast<RectTransform>();
        if (rect == null) return false;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rect.GetWorldCorners(corners);
        min = RectTransformUtility.WorldToScreenPoint(null, corners[0]);
        max = RectTransformUtility.WorldToScreenPoint(null, corners[2]);
        return true;
    }

    /// <summary>
    /// Keep the whole box on screen while it is moved: its top (the tab line) and its bottom (the game's channel hint
    /// under the input line) stay inside the screen, and its sides too. A box taller than the screen keeps its top.
    /// </summary>
    private static void KeepOnScreen()
    {
        if (OffsetX == null || OffsetY == null || !TryBoxBounds(out var min, out var max)) return;
        float scale = CanvasScale();
        float dx = 0f, dy = 0f;
        if (min.x < 0f) dx = -min.x;
        else if (max.x > Screen.width) dx = Mathf.Max(Screen.width - max.x, -min.x);
        if (max.y > Screen.height) dy = Screen.height - max.y;
        else if (min.y < 0f) dy = Mathf.Min(-min.y, Screen.height - max.y);
        if (dx != 0f) OffsetX.Value += dx / scale;
        if (dy != 0f) OffsetY.Value += dy / scale;
    }

    private static void SaveConfig()
    {
        try { OffsetX?.ConfigFile?.Save(); } catch { /* saved on the next change */ }
    }

    // ---- tabs ----------------------------------------------------------------------------------------------

    internal static void Select(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _active = index;
        _unread[index] = false;
        ChatView.ActiveTab = _tabs[index];
        if (ActiveSetting != null) ActiveSetting.Value = index;
        _drawn = "";
        int channel = _tabs[index].SendChannel();
        if (channel < 0) return;
        try
        {
            var world = Worlds.Client;
            var system = world?.GetExistingSystemManaged<ClientChatSystem>();
            if (system == null) return;
            system._DefaultMode = (ChatMessageType)channel;
            system._ChatWindow?.SetPlaceholderMode((ChatMessageType)channel, system._WhisperUserName ?? "");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: switching the send channel threw: " + e.Message); }
    }

    private static void SaveTabs()
    {
        if (TabsSetting != null) TabsSetting.Value = ChatTab.Save(_tabs);
        if (_active >= _tabs.Count) _active = _tabs.Count - 1;
        _unread = new bool[ChatTab.MaxTabs];
        Select(_active);
    }

    private static UiDialog Choose(string title, string body, IReadOnlyList<string> options, Action<int> pick) =>
        UiDialogs.Choose(typeof(ChatTabs), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter, title, body, options, pick, "Cancel", null);

    private static void OpenMenu(int tab)
    {
        var options = new List<string>();
        var actions = new List<Action>();
        bool editable = tab > 0 && tab < _tabs.Count;
        if (editable)
        {
            options.Add("Edit this tab");
            actions.Add(() => EditTab(tab));
            options.Add("Close this tab");
            actions.Add(() => { _tabs.RemoveAt(tab); if (_active >= tab) _active = Math.Max(0, _active - 1); SaveTabs(); });
        }

        if (_tabs.Count < ChatTab.MaxTabs)
        {
            options.Add("New tab");
            actions.Add(NewTab);
        }

        options.Add("Colors");
        actions.Add(() => { if (_window != null && _window.Pointer != IntPtr.Zero) ChatColorPanel.Open(_window); });
        options.Add("Background opacity");
        actions.Add(PickOpacity);
        bool locked = Locked?.Value ?? false;
        options.Add(locked ? "Unlock position" : "Lock position");
        actions.Add(() => { if (Locked != null) Locked.Value = !locked; });
        options.Add("Reset position and size");
        actions.Add(() =>
        {
            if (OffsetX != null) OffsetX.Value = 0f;
            if (OffsetY != null) OffsetY.Value = 0f;
            if (Width != null) Width.Value = 0f;
            if (Height != null) Height.Value = 0f;
        });
        options.Add("All chat settings");
        actions.Add(ModsMenu.OpenChatSettings);

        string title = tab >= 0 && tab < _tabs.Count ? "Chat · " + _tabs[tab].Name : "Chat";
        Choose(title, "", options, i => { if (i >= 0 && i < actions.Count) actions[i](); });
    }

    private static void PickOpacity()
    {
        var levels = new[] { -1, 0, 25, 50, 75, 100 };
        var options = new List<string> { "The game's own" };
        for (int i = 1; i < levels.Length; i++) options.Add(levels[i] + "%");
        Choose("Background opacity", "How see-through the chat box's background is.", options,
            i => { if (Opacity != null && i >= 0 && i < levels.Length) Opacity.Value = levels[i]; });
    }

    private static void NewTab()
    {
        if (_tabs.Count >= ChatTab.MaxTabs) return;
        var presets = new List<ChatTab>();
        foreach (var p in ChatTab.Presets())
        {
            bool taken = false;
            foreach (var t in _tabs) if (string.Equals(t.Name, p.Name, StringComparison.OrdinalIgnoreCase)) taken = true;
            if (!taken) presets.Add(p);
        }

        var options = new List<string>();
        foreach (var p in presets) options.Add(p.Name);
        options.Add("Custom…");
        Choose("New tab", "Pick a ready-made tab, or make your own.", options, i =>
        {
            if (i >= 0 && i < presets.Count)
            {
                _tabs.Add(presets[i]);
                _active = _tabs.Count - 1;
                SaveTabs();
            }
            else if (i == presets.Count)
            {
                var fresh = new ChatTab { Name = "Tab " + _tabs.Count, Scope = ChatScope.Global };
                NameTab(fresh, isNew: true);
            }
        });
    }

    private static void EditTab(int index)
    {
        if (index <= 0 || index >= _tabs.Count) return;
        NameTab(_tabs[index], isNew: false);
    }

    private static void NameTab(ChatTab tab, bool isNew)
    {
        UiDialogs.Prompt(typeof(ChatTabs), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter,
            isNew ? "New tab" : "Edit tab", "The tab's name.", tab.Name,
            name =>
            {
                tab.Name = ChatTab.Clean(name);
                EditScope(tab, isNew);
            },
            "Trade", n => ChatTab.Clean(n).Length == 0 ? "Enter a name." : null, "Next", ChatTab.MaxNameLength, "Cancel", null);
    }

    /// <summary>"Show messages from": pick a line to switch it; Done keeps the tab.</summary>
    private static void EditScope(ChatTab tab, bool isNew)
    {
        var options = new List<string>();
        foreach (var c in ChatTabStrip.ScopeChoices) options.Add(ChatTabStrip.ScopeLine(tab.Scope, c));
        options.Add("Done");
        Choose(tab.Name, "Show messages from:", options, i =>
        {
            if (i >= 0 && i < ChatTabStrip.ScopeChoices.Length)
            {
                tab.Scope ^= ChatTabStrip.ScopeChoices[i].Scope;
                EditScope(tab, isNew);
                return;
            }

            if (tab.Scope == ChatScope.None) tab.Scope = ChatScope.Global;
            if (isNew)
            {
                _tabs.Add(tab);
                _active = _tabs.Count - 1;
            }

            SaveTabs();
        });
    }
}
#endif
