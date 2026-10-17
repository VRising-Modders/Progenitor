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
/// CHAT6: the chat box's tab row, quick menu, move, resize and background opacity.
///
/// <list type="bullet">
/// <item><b>Tabs</b> (CHAT12). A row above the chat log drawn like the game's own menu tabs (the full-screen menu's
/// top bar, through <see cref="ChatTabArt"/>): the font, the soft highlight and the underline. A tab at rest is gray;
/// under the pointer it brightens with a fainter highlight and underline; the shown tab is white with the full
/// highlight and underline. A tab with unread lines carries the game's new-unlock star. Then <b>+</b> and the menu
/// glyph, which hover the same way. Left-click a tab to show it; its own send channel and unsent line come with it (CHAT13, <see cref="ChatTabInput"/>).
/// Press and drag a tab sideways to move it (<see cref="ChatTabRow"/>); the order is saved in <c>[Chat] Tabs</c>.
/// Right-click a tab, or click the menu glyph, for the quick menu.</item>
/// <item><b>Quick menu</b> (framework dialogs): Edit this tab, Move left / right (reordering without a drag), Close this tab, New tab, Colors
/// (<see cref="ChatColorPanel"/>), Background opacity, Lock / Unlock position, Reset position and size, All chat
/// settings. It stores no copies of the settings.</item>
/// <item><b>Move</b> by dragging the row (not on a tab); <b>resize</b> by dragging the corner grip; both are stored in
/// <c>[Chat]</c> and put back by Reset. The box is kept on screen.</item>
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

    /// <summary>The tabs, their unread marks and the shown tab; loaded once from <c>[Chat] Tabs</c>.</summary>
    private static readonly ChatTabSet Set = new();

    private static IntPtr _windowPtr;
    private static HUDChatWindow? _window;
    private static GameObject? _strip;
    private static RectTransform? _stripRect;
    private static GameObject? _grip;
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

    /// <summary>The chat's own line font, for the row when the game's menu font is not found or lacks a glyph.</summary>
    private static TMP_FontAsset? _chatFont;
    private static float _chatFontSize;

    private enum Drag { None, Move, Resize, TabPress, TabDrag }
    private static Drag _drag;
    private static Vector2 _dragStartPointer;
    private static Vector2 _dragStartValue;

    /// <summary>How far (canvas units) a resize may grow the box before its top or right edge leaves the screen.</summary>
    private static Vector2 _resizeRoom;

    /// <summary>How far (canvas units) a move may go each way before part of the box leaves the screen.</summary>
    private static Vector2 _moveMin, _moveMax;

    // ---- the row's items ---------------------------------------------------------------------------------

    private const int AddItem = -2;
    private const int MenuItem = -3;

    private sealed class Item
    {
        internal int Tab;               // the tab index, or AddItem / MenuItem
        internal GameObject Go = null!;
        internal RectTransform Rect = null!;
        internal Image? Glow;
        internal TextMeshProUGUI Caption = null!;
        internal Image? Line;
        internal Image? Star;
        internal TextMeshProUGUI? Dot;
        internal float Left;
        internal float Width;
        internal float TextWidth;
        internal ChatTabItemState Drawn = (ChatTabItemState)(-1);
        internal bool DrawnUnread;
        internal int DrawnArt = -1;
    }

    private static readonly List<Item> _items = new();
    private static Image? _marker;

    /// <summary>The row is rebuilt when this changes: the tab names, the art, the font size, the row width.</summary>
    private static string _built = "";
    private static int _hovered = -1;
    private static int _pressItem = -1;
    private static float _grabOffset;
    private static int _dropSlot = -1;

    internal static IReadOnlyList<ChatTab> Tabs => Set.Tabs;

    /// <summary>The shown tab's index in <see cref="Tabs"/>.</summary>
    internal static int ActiveIndex => Set.Active;

    /// <summary>Load the saved tabs once; false while <c>[Chat] Tabs</c> is not bound yet (nothing may change them then).</summary>
    private static bool EnsureLoaded()
    {
        if (Set.Loaded) return true;
        if (!Set.EnsureLoaded(TabsSetting?.Value, ActiveSetting?.Value ?? 0)) return false;
        ChatView.ActiveTab = Set.Tabs[Set.Active];
        return true;
    }

    /// <summary>A new line in the log: tabs that show it and are not the active one get the unread mark.</summary>
    internal static void NoteNew(ChatRecord r)
    {
        if (!EnsureLoaded()) return;
        if (r.Kind == ChatKind.Session || r.Transient) return;
        for (int i = 0; i < Set.Tabs.Count && i < Set.Unread.Length; i++)
        {
            if (i == Set.Active || Set.Unread[i] || !Set.Tabs[i].Shows(r)) continue;
            Set.Unread[i] = true;
        }
    }

    // ---- per frame -----------------------------------------------------------------------------------------

    internal static void Update(HUDChatWindow window, bool chatShown, RectTransform? hudParent)
    {
        if (!EnsureLoaded()) return;
        if (_strip == null || _windowPtr != window.Pointer || _strip.Pointer == IntPtr.Zero) Attach(window, hudParent);
        if (_stripRect == null) return;
        ChatTabArt.Refresh();
        BuildIfNeeded();

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
        else CancelDrag();
        Paint();
    }

    private static bool _inHud = true;

    private static void Attach(HUDChatWindow window, RectTransform? hudParent)
    {
        Detach();
        var scroll = window.ScrollView;
        if (scroll == null || window.ChatMessagePrefab == null) return;
        var host = scroll.transform;

        try
        {
            var lineText = window.ChatMessagePrefab.gameObject.GetComponent<TextMeshProUGUI>();
            _chatFont = lineText != null ? lineText.font : null;
            _chatFontSize = lineText != null ? lineText.fontSize : 0f;
        }
        catch
        {
            _chatFont = null;
            _chatFontSize = 0f;
        }

        var strip = new GameObject(StripName);
        strip.transform.SetParent(host, false);
        var rect = strip.AddComponent<RectTransform>();
        strip.AddComponent<LayoutElement>().ignoreLayout = true;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(0f, 3f);
        rect.sizeDelta = new Vector2(0f, ChatTabRow.Height);

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
                gt.text = "<color=" + ChatFormat.MutedHex + ">◥</color>";
                gt.alignment = TextAlignmentOptions.TopRight;
            }
        }

        // A new window (usually a new world): the game's textures may have been released, so find the art again.
        ChatTabArt.OnAttach();
        _strip = strip;
        _stripRect = rect;
        _grip = grip;
        _window = window;
        _windowPtr = window.Pointer;
        _built = "";
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
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: detaching the tab row threw: " + e.Message); }

        _strip = null;
        _stripRect = null;
        _items.Clear();
        _marker = null;
        _built = "";
        _hovered = -1;
        _grip = null;
        _window = null;
        _windowPtr = IntPtr.Zero;
        _moveTarget = null;
        _sizeTarget = null;
        _windowRect = null;
        _background = null;
        _gameAlpha = -1f;
        _drag = Drag.None;
        _pressItem = -1;
        _dropSlot = -1;
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

    // ---- building the row ------------------------------------------------------------------------------------

    private static float RowWidth()
    {
        try { return _stripRect != null ? _stripRect.rect.width : 0f; }
        catch { return 0f; }
    }

    /// <summary>The key the row was built for: tab names, how many can still be added, the art and the row's width.</summary>
    private static string BuildKey(float width)
    {
        var sb = new System.Text.StringBuilder(64);
        foreach (var t in Set.Tabs) sb.Append(t.Name).Append('\u0001');
        sb.Append(Set.Tabs.Count < ChatTab.MaxTabs ? '+' : '-').Append(ChatTabArt.Version).Append('|')
            .Append(Mathf.RoundToInt(width)).Append('|').Append(Mathf.RoundToInt(_chatFontSize));
        return sb.ToString();
    }

    private static void BuildIfNeeded()
    {
        if (_drag == Drag.TabDrag || _strip == null) return;
        float width = RowWidth();
        string key = BuildKey(width);
        if (key == _built) return;
        _built = key;
        try { Build(width); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: building the tab row threw: " + e.Message); }
    }

    /// <summary>Make one item per tab, then + and the menu glyph, laid out left to right.</summary>
    private static void Build(float rowWidth)
    {
        for (int i = 0; i < _items.Count; i++)
            if (_items[i].Go != null && _items[i].Go.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_items[i].Go);
        _items.Clear();
        if (_marker != null && _marker.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_marker.gameObject);
        _marker = null;
        _hovered = -1;

        float size = ChatTabRow.FontSize(_chatFontSize);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < Set.Tabs.Count; i++) _items.Add(MakeItem(i, Set.Tabs[i].Name, size));
            if (Set.Tabs.Count < ChatTab.MaxTabs) _items.Add(MakeItem(AddItem, "+", size));
            _items.Add(MakeItem(MenuItem, ChatTabStrip.MenuGlyph, size));

            var widths = new float[_items.Count];
            for (int i = 0; i < _items.Count; i++) widths[i] = _items[i].Width;
            var lefts = ChatTabRow.Layout(widths);
            float total = lefts.Length == 0 ? 0f : lefts[lefts.Length - 1] + widths[widths.Length - 1];
            // A row wider than the chat box gets a smaller caption once (never below the minimum).
            if (pass == 0 && rowWidth > 1f && total > rowWidth && size > ChatTabRow.MinFontSize)
            {
                size = Math.Max(ChatTabRow.MinFontSize, (float)Math.Floor(size * rowWidth / total));
                for (int i = 0; i < _items.Count; i++) UnityEngine.Object.Destroy(_items[i].Go);
                _items.Clear();
                continue;
            }

            for (int i = 0; i < _items.Count; i++)
            {
                _items[i].Left = lefts[i];
                _items[i].Rect.anchoredPosition = new Vector2(lefts[i], 0f);
            }

            break;
        }

        _marker = MakeMarker();
    }

    private static Item MakeItem(int tab, string text, float size)
    {
        bool art = ChatTabArt.Found;
        var go = new GameObject(tab >= 0 ? "tab" : tab == AddItem ? "add" : "menu");
        go.transform.SetParent(_strip!.transform, false);
        var rect = go.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);

        var item = new Item { Tab = tab, Go = go, Rect = rect };

        // Behind the caption: the game's soft glow (or, in the kit look, a flat tint over the whole item).
        item.Glow = MakeImage(go.transform, "glow", art ? ChatTabArt.Glow : null, art ? ChatTabArt.GlowType : Image.Type.Simple);

        var captionGo = new GameObject("caption");
        captionGo.transform.SetParent(go.transform, false);
        var cr = captionGo.AddComponent<RectTransform>();
        cr.anchorMin = Vector2.zero;
        cr.anchorMax = Vector2.one;
        cr.offsetMin = Vector2.zero;
        cr.offsetMax = Vector2.zero;
        var caption = captionGo.AddComponent<TextMeshProUGUI>();
        var font = FontFor(text);
        if (font != null) caption.font = font;
        if (art && font != null && font == ChatTabArt.Font && ChatTabArt.FontMaterial != null) caption.fontSharedMaterial = ChatTabArt.FontMaterial;
        caption.fontStyle = art ? ChatTabArt.FontStyle : FontStyles.Normal;
        caption.characterSpacing = art ? ChatTabArt.CharacterSpacing : 0f;
        caption.enableAutoSizing = false;
        caption.fontSize = tab >= 0 ? size : size + 2f;
        caption.enableWordWrapping = false;
        caption.overflowMode = TextOverflowModes.Overflow;
        caption.richText = false;
        caption.alignment = TextAlignmentOptions.Center;
        caption.raycastTarget = false;
        caption.text = text;
        item.Caption = caption;

        float textWidth;
        try { textWidth = caption.GetPreferredValues(text).x; }
        catch { textWidth = text.Length * size * 0.6f; }
        item.TextWidth = textWidth;
        item.Width = Mathf.Ceil(textWidth + 2f * ChatTabRow.Pad);
        rect.sizeDelta = new Vector2(item.Width, ChatTabRow.Height);

        if (item.Glow != null)
        {
            var gr = item.Glow.rectTransform;
            gr.anchorMin = gr.anchorMax = new Vector2(0.5f, 0.5f);
            gr.pivot = new Vector2(0.5f, 0.5f);
            gr.anchoredPosition = Vector2.zero;
            gr.sizeDelta = art && ChatTabArt.Glow != null
                ? new Vector2(item.Width * ChatTabArt.GlowRatio.x, ChatTabRow.Height * ChatTabArt.GlowRatio.y)
                : new Vector2(item.Width, ChatTabRow.Height);
            gr.SetAsFirstSibling();
        }

        // Under the caption: the game's underline, as wide as the words.
        item.Line = MakeImage(go.transform, "line", art ? ChatTabArt.Line : null, art ? ChatTabArt.LineType : Image.Type.Simple);
        if (item.Line != null)
        {
            var lr = item.Line.rectTransform;
            lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 0f);
            lr.pivot = new Vector2(0.5f, 0f);
            float h = art ? Mathf.Max(2f, Mathf.Round(caption.fontSize * ChatTabArt.LineRatio)) : 2f;
            lr.sizeDelta = new Vector2(Mathf.Ceil(textWidth + 6f), h);
            lr.anchoredPosition = new Vector2(0f, 1f);
        }

        if (tab >= 0)
        {
            // Unread: the game's new-unlock star at the top right, or a gold dot in the kit look.
            if (art && ChatTabArt.Star != null)
            {
                item.Star = MakeImage(go.transform, "unread", ChatTabArt.Star, Image.Type.Simple);
                if (item.Star != null)
                {
                    var sr = item.Star.rectTransform;
                    sr.anchorMin = sr.anchorMax = new Vector2(1f, 1f);
                    sr.pivot = new Vector2(0.5f, 0.5f);
                    float s = Mathf.Round(size * 0.8f);
                    sr.sizeDelta = new Vector2(s, s);
                    sr.anchoredPosition = new Vector2(-ChatTabRow.Pad * 0.5f, -s * 0.35f);
                    item.Star.preserveAspect = true;
                }
            }
            else
            {
                var dotGo = new GameObject("unread");
                dotGo.transform.SetParent(go.transform, false);
                var dr = dotGo.AddComponent<RectTransform>();
                dr.anchorMin = dr.anchorMax = new Vector2(1f, 1f);
                dr.pivot = new Vector2(0.5f, 0.5f);
                dr.sizeDelta = new Vector2(12f, 12f);
                dr.anchoredPosition = new Vector2(-ChatTabRow.Pad * 0.5f, -5f);
                var dot = dotGo.AddComponent<TextMeshProUGUI>();
                if (_chatFont != null) dot.font = _chatFont;
                dot.fontSize = size;
                dot.alignment = TextAlignmentOptions.Center;
                dot.raycastTarget = false;
                dot.enableWordWrapping = false;
                dot.text = "•";
                dot.color = UnreadColor;
                item.Dot = dot;
            }
        }

        return item;
    }

    private static readonly Color UnreadColor = new(0.91f, 0.70f, 0.25f, 1f);

    /// <summary>The game's menu font when it has the caption's glyphs, else the chat's own font.</summary>
    private static TMP_FontAsset? FontFor(string text)
    {
        var font = ChatTabArt.Found ? ChatTabArt.Font : null;
        if (font == null) return _chatFont;
        try
        {
            foreach (char c in text)
                if (!char.IsWhiteSpace(c) && !font.HasCharacter(c, true, false)) return _chatFont ?? font;
        }
        catch { /* keep the menu font */ }
        return font;
    }

    private static Image? MakeImage(Transform parent, string name, Sprite? sprite, Image.Type type)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        if (sprite != null)
        {
            image.sprite = sprite;
            image.type = type;
        }

        image.color = new Color(1f, 1f, 1f, 0f);
        return image;
    }

    /// <summary>The insertion marker while a tab is dragged: the game's underline stood upright, or a thin bar.</summary>
    private static Image? MakeMarker()
    {
        bool art = ChatTabArt.Found;
        var marker = MakeImage(_strip!.transform, "drop", art ? ChatTabArt.Line : null, art ? ChatTabArt.LineType : Image.Type.Simple);
        if (marker == null) return null;
        var mr = marker.rectTransform;
        mr.anchorMin = mr.anchorMax = new Vector2(0f, 0.5f);
        mr.pivot = new Vector2(0.5f, 0.5f);
        if (art)
        {
            mr.sizeDelta = new Vector2(ChatTabRow.Height - 2f, 3f);
            mr.localEulerAngles = new Vector3(0f, 0f, 90f);
        }
        else mr.sizeDelta = new Vector2(2f, ChatTabRow.Height - 4f);
        marker.gameObject.SetActive(false);
        return marker;
    }

    // ---- painting -------------------------------------------------------------------------------------------

    /// <summary>Each frame: give every item the look of its state; only items whose state changed are touched.</summary>
    private static void Paint()
    {
        int dragged = _drag == Drag.TabDrag ? _pressItem : -1;
        int art = ChatTabArt.Version;
        var colors = ChatTabArt.Found ? ChatTabArt.Colors ?? new ChatTabDonorColors() : null;
        double now = ChatView.SentNow;
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var state = ChatTabRow.StateOf(i, Set.Tabs.Count, Set.Active, _hovered, dragged);
            // CHAT14: the tab a line you just sent went to pulses with the hover look.
            if (state == ChatTabItemState.Normal && item.Tab >= 0 && item.Tab < Set.Tabs.Count
                && ChatView.Sent.PulseLit(Set.Tabs[item.Tab], now)) state = ChatTabItemState.Hover;
            bool unread = item.Tab >= 0 && item.Tab != Set.Active && item.Tab < Set.Unread.Length && Set.Unread[item.Tab];
            if (state == item.Drawn && unread == item.DrawnUnread && art == item.DrawnArt) continue;
            item.Drawn = state;
            item.DrawnUnread = unread;
            item.DrawnArt = art;
            var style = ChatTabRow.Style(state, colors);
            float a = style.ItemAlpha;
            try
            {
                item.Caption.color = new Color(style.CaptionR, style.CaptionG, style.CaptionB, style.CaptionA * a);
                if (item.Glow != null) item.Glow.color = new Color(1f, 1f, 1f, style.GlowAlpha * a);
                if (item.Line != null) item.Line.color = new Color(1f, 1f, 1f, style.LineAlpha * a);
                if (item.Star != null) item.Star.gameObject.SetActive(unread);
                if (item.Dot != null) item.Dot.gameObject.SetActive(unread);
            }
            catch { /* the item keeps its last look */ }
        }
    }

    // ---- pointer ------------------------------------------------------------------------------------------------

    /// <summary>Whether the screen point is over the row or the grip (the click guard covers both).</summary>
    internal static bool PointerOver(Vector2 point) =>
        Contains(_strip, point) || Contains(_grip, point) || ChatColorPanel.Contains(point);

    private static bool Contains(GameObject? go, Vector2 point)
    {
        if (go == null || go.Pointer == IntPtr.Zero || !go.activeInHierarchy) return false;
        var rect = go.transform.TryCast<RectTransform>();
        return rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, point, null);
    }

    /// <summary>The item under the screen point, or -1.</summary>
    private static int ItemAt(Vector2 point)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var r = _items[i].Rect;
            if (r != null && r.Pointer != IntPtr.Zero && RectTransformUtility.RectangleContainsScreenPoint(r, point, null)) return i;
        }

        return -1;
    }

    /// <summary>The screen point in the row's own units (its left edge is 0).</summary>
    private static float RowX(Vector2 point)
    {
        if (_stripRect == null) return 0f;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_stripRect, point, null, out var local)) return 0f;
        // The row's pivot is its left edge, so local x is already measured from it.
        return local.x;
    }

    private static void CancelDrag()
    {
        if (_drag == Drag.TabDrag || _drag == Drag.TabPress) EndTabDrag(drop: false);
        _drag = Drag.None;
        _hovered = -1;
    }

    private static void HandleInput()
    {
        if (!UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y))
        {
            _hovered = -1;
            return;
        }

        var point = new Vector2(x, y);
        // The colors panel handles its own clicks.
        if (_drag == Drag.None && ChatColorPanel.Contains(point))
        {
            _hovered = -1;
            return;
        }

        if (_drag != Drag.None)
        {
            bool held = UnityInputRuntime.MouseButtonHeld(0);
            if (_drag == Drag.TabPress || _drag == Drag.TabDrag)
            {
                TabDragStep(point, held);
                return;
            }

            if (!held)
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

        int over = ItemAt(point);
        _hovered = over;

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

        if (!Contains(_strip, point)) return;
        int tab = over >= 0 ? _items[over].Tab : -1;
        if (right)
        {
            OpenMenu(tab >= 0 ? tab : Set.Active);
            return;
        }

        if (tab >= 0)
        {
            // A press on a tab: a click selects it on release, a drag past the threshold moves it.
            _drag = Drag.TabPress;
            _pressItem = over;
            _dragStartPointer = point;
            _grabOffset = RowX(point) - _items[over].Left;
            return;
        }

        if (over >= 0 && _items[over].Tab == AddItem) NewTab();
        else if (over >= 0 && _items[over].Tab == MenuItem) OpenMenu(Set.Active);
        else if (!(Locked?.Value ?? false))
        {
            _drag = Drag.Move;
            _dragStartPointer = point;
            _dragStartValue = new Vector2(OffsetX?.Value ?? 0f, OffsetY?.Value ?? 0f);
            MoveRange();
        }
    }

    /// <summary>One frame of a press on a tab: becomes a drag past the threshold; a release clicks or drops.</summary>
    private static void TabDragStep(Vector2 point, bool held)
    {
        if (_pressItem < 0 || _pressItem >= _items.Count || _items[_pressItem].Tab < 0)
        {
            _drag = Drag.None;
            _pressItem = -1;
            return;
        }

        if (_drag == Drag.TabPress)
        {
            float scale = CanvasScale();
            if (!held)
            {
                int tab = _items[_pressItem].Tab;
                _drag = Drag.None;
                _pressItem = -1;
                Select(tab);
                return;
            }

            if (!ChatTabRow.PassedThreshold(_dragStartPointer.x / scale, _dragStartPointer.y / scale, point.x / scale, point.y / scale)) return;
            _drag = Drag.TabDrag;
            try { _items[_pressItem].Rect.SetAsLastSibling(); } catch { /* drawn under its neighbors */ }
        }

        if (!held)
        {
            EndTabDrag(drop: true);
            return;
        }

        // The tab follows the pointer along the row; the marker shows where it would land.
        var item = _items[_pressItem];
        float x = RowX(point);
        float rowWidth = RowWidth();
        float leftEdge = Mathf.Clamp(x - _grabOffset, -item.Width * 0.5f, Mathf.Max(0f, rowWidth - item.Width * 0.5f));
        try { item.Rect.anchoredPosition = new Vector2(leftEdge, 0f); } catch { /* */ }

        TabLayout(out var lefts, out var widths);
        int slot = ChatTabRow.DropSlot(lefts, widths, leftEdge + item.Width * 0.5f);
        int from = item.Tab;
        _dropSlot = ChatTabRow.TargetIndex(from, slot) == from ? -1 : slot;
        if (_marker != null)
        {
            try
            {
                bool show = _dropSlot >= 0;
                if (_marker.gameObject.activeSelf != show) _marker.gameObject.SetActive(show);
                if (show)
                {
                    _marker.rectTransform.anchoredPosition = new Vector2(ChatTabRow.MarkerX(lefts, widths, _dropSlot), 0f);
                    _marker.color = new Color(1f, 1f, 1f, 0.9f);
                    _marker.rectTransform.SetAsLastSibling();
                }
            }
            catch { /* no marker this frame */ }
        }
    }

    /// <summary>The tabs' resting layout (the + and menu items excluded).</summary>
    private static void TabLayout(out float[] lefts, out float[] widths)
    {
        int n = Math.Min(Set.Tabs.Count, _items.Count);
        lefts = new float[n];
        widths = new float[n];
        for (int i = 0; i < n; i++)
        {
            lefts[i] = _items[i].Left;
            widths[i] = _items[i].Width;
        }
    }

    private static void EndTabDrag(bool drop)
    {
        int from = _pressItem >= 0 && _pressItem < _items.Count ? _items[_pressItem].Tab : -1;
        int slot = _dropSlot;
        _drag = Drag.None;
        _pressItem = -1;
        _dropSlot = -1;
        if (_marker != null && _marker.Pointer != IntPtr.Zero) _marker.gameObject.SetActive(false);
        if (drop && from >= 0 && slot >= 0 && MoveTab(from, ChatTabRow.TargetIndex(from, slot))) return;
        // Not moved: put every item back where it was.
        _built = "";
    }

    /// <summary>Move a tab in the row and save the order; the shown tab and the unread marks go with their tabs.</summary>
    internal static bool MoveTab(int from, int to)
    {
        // Refused until the saved tabs are loaded, so the defaults never overwrite them.
        if (!EnsureLoaded()) return false;
        // A move from the menu or the bridge during a drag ends the drag: the dragged item's index would be stale.
        if (_drag == Drag.TabPress || _drag == Drag.TabDrag)
        {
            _drag = Drag.None;
            _pressItem = -1;
            _dropSlot = -1;
            if (_marker != null && _marker.Pointer != IntPtr.Zero) _marker.gameObject.SetActive(false);
            _built = "";
        }

        if (!Set.Move(from, to)) return false;
        if (TabsSetting != null) TabsSetting.Value = ChatTab.Save(Set.Tabs);
        if (ActiveSetting != null) ActiveSetting.Value = Set.Active;
        _built = "";
        Plugin.Logger?.LogInfo("Chat: moved tab " + Set.Tabs[Math.Max(0, Math.Min(Set.Tabs.Count - 1, to))].Name + " from " + from + " to " + to + ".");
        return true;
    }

    /// <summary>The row as drawn, for the Dev bridge's <c>chat_tabs</c> read.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        bool loaded = EnsureLoaded();
        var items = new List<object?>();
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var d = new Dictionary<string, object?>
            {
                ["kind"] = item.Tab >= 0 ? "tab" : item.Tab == AddItem ? "add" : "menu",
                ["caption"] = item.Caption != null && item.Caption.Pointer != IntPtr.Zero ? item.Caption.text : "",
                ["state"] = item.Drawn.ToString(),
            };
            if (item.Tab >= 0)
            {
                d["index"] = item.Tab;
                d["unread"] = item.DrawnUnread;
            }

            if (TryScreenRect(item.Go, out var min, out var max))
            {
                d["minX"] = min.x; d["minY"] = min.y; d["maxX"] = max.x; d["maxY"] = max.y;
                d["centerX"] = (min.x + max.x) * 0.5f; d["centerY"] = (min.y + max.y) * 0.5f;
            }

            try
            {
                d["font"] = item.Caption != null && item.Caption.font != null ? item.Caption.font.name : "";
                d["fontSize"] = item.Caption != null ? item.Caption.fontSize : 0f;
            }
            catch { /* */ }
            items.Add(d);
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["loaded"] = loaded,
            ["attached"] = _stripRect != null,
            ["art"] = ChatTabArt.Source,
            ["artFont"] = ChatTabArt.Font != null ? ChatTabArt.Font.name : "",
            ["artGlow"] = ChatTabArt.Glow != null ? ChatTabArt.Glow.name : "",
            ["artLine"] = ChatTabArt.Line != null ? ChatTabArt.Line.name : "",
            ["artStar"] = ChatTabArt.Star != null ? ChatTabArt.Star.name : "",
            ["active"] = Set.Active,
            ["hovered"] = _hovered,
            ["drag"] = _drag.ToString(),
            ["saved"] = TabsSetting?.Value ?? "",
            ["items"] = items,
            ["tabs"] = DescribeTabs(),
            ["input"] = DescribeInput(),
        };
    }

    /// <summary>CHAT13, for the bridge: each tab's name, scope, remembered and effective send channel, and draft.</summary>
    private static List<object?> DescribeTabs()
    {
        var list = new List<object?>();
        string partner = ChatWindow.WhisperPartner;
        for (int i = 0; i < Set.Tabs.Count; i++)
        {
            var t = Set.Tabs[i];
            list.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = t.Name,
                ["scope"] = (int)t.Scope,
                ["sendTo"] = t.SendTo is ChatSend s ? ChatTab.SendWord(s) : "",
                ["sendNow"] = ChatTabInput.EffectiveFor(t, partner) is ChatSend n ? ChatTab.SendWord(n) : "keep",
                ["draft"] = t.Draft,
                ["holdsLine"] = ReferenceEquals(t, _lineOwner),
            });
        }

        return list;
    }

    /// <summary>CHAT13, for the bridge: the input line, the game's channel, focus and the whisper partner.</summary>
    private static Dictionary<string, object?> DescribeInput()
    {
        var d = new Dictionary<string, object?> { ["partner"] = ChatWindow.WhisperPartner };
        try
        {
            var system = Worlds.Client?.GetExistingSystemManaged<ClientChatSystem>();
            var window = system?._ChatWindow;
            if (system == null || window == null || window.Pointer == IntPtr.Zero) return d;
            d["mode"] = (int)system._DefaultMode;
            d["modeName"] = system._DefaultMode.ToString();
            d["whisperTarget"] = system._WhisperUserName ?? "";
            d["focused"] = window.IsChatFocused;
            d["text"] = window.ChatInputField?.text ?? "";
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }

    /// <summary>CHAT13, for the bridge's <c>chat_tabs select</c>: show a tab as a click does. False when not loaded or out of range.</summary>
    internal static bool SelectTab(int index)
    {
        if (!EnsureLoaded() || index < 0 || index >= Set.Tabs.Count) return false;
        Select(index);
        return true;
    }

    /// <summary>CHAT13, for the bridge's <c>chat_tabs line</c>: put text in the input line, as typing would leave it.</summary>
    internal static bool SetInputLine(string text)
    {
        var field = Worlds.Client?.GetExistingSystemManaged<ClientChatSystem>()?._ChatWindow?.ChatInputField;
        if (field == null || field.Pointer == IntPtr.Zero) return false;
        field.text = text ?? "";
        field.MoveTextEnd(false);
        return true;
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
        if (index < 0 || index >= Set.Tabs.Count) return;
        Set.Active = index;
        Set.Unread[index] = false;
        ChatView.ActiveTab = Set.Tabs[index];
        if (ActiveSetting != null) ActiveSetting.Value = index;
        try
        {
            var world = Worlds.Client;
            var system = world?.GetExistingSystemManaged<ClientChatSystem>();
            if (system == null || system.Pointer == IntPtr.Zero) return;
            ApplyInput(system, Set.Tabs[index]);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: switching the send channel threw: " + e.Message); }
    }

    // ---- CHAT13: the input line per tab --------------------------------------------------------------------

    private static readonly ChatTabInputTracker InputTracker = new();

    /// <summary>The tab whose line is in the input field (null until the first tab is applied in a world).</summary>
    private static ChatTab? _lineOwner;

    /// <summary>
    /// The line the input held when it lost focus to a press on the tab row (the game may empty an unfocused line), so
    /// the tab switch that follows keeps it as the leaving tab's draft; with the frame it was taken.
    /// </summary>
    private static string? _lostLine;
    private static int _lostFrame;
    private const int LostLineFrames = 120;

    /// <summary>
    /// Show the shown tab's line in the input: the leaving tab keeps its draft and the arriving tab's comes back
    /// (<see cref="ChatTabInput.SwapDraft"/>), and the game's channel becomes the tab's (<see cref="ChatTabInput.ModeFor"/>).
    /// Deferred to <see cref="UpdateInput"/> while an input method composition is active.
    /// </summary>
    private static void ApplyInput(ClientChatSystem system, ChatTab tab)
    {
        string partner = ChatWindow.WhisperPartner;
        var window = system._ChatWindow;
        var field = window != null && window.Pointer != IntPtr.Zero ? window.ChatInputField : null;
        if (field != null && field.Pointer != IntPtr.Zero && !ReferenceEquals(_lineOwner, tab))
        {
            if (Composing(field)) return;
            string current = field.text ?? "";
            if (current.Length == 0 && _lostLine != null && Time.frameCount - _lostFrame <= LostLineFrames) current = _lostLine;
            _lostLine = null;
            string next = ChatTabInput.SwapDraft(_lineOwner, tab, current);
            if (!string.Equals(next, field.text ?? "", StringComparison.Ordinal))
            {
                field.text = next;
                field.MoveTextEnd(false);
            }

            // A swapped line was never sent: recall must not take it, and Up starts from the newest line again.
            ChatWindow.ForgetTypedLine();
            InputTracker.TabChanged();
            _lineOwner = tab;
        }

        SetMode(system, ChatTabInput.ModeFor(tab, partner));
    }

    private static void SetMode(ClientChatSystem system, int channel)
    {
        if (channel < 0) return;
        if ((int)system._DefaultMode != channel)
        {
            system._DefaultMode = (ChatMessageType)channel;
            var window = system._ChatWindow;
            if (window != null && window.Pointer != IntPtr.Zero) window.SetPlaceholderMode((ChatMessageType)channel, system._WhisperUserName ?? "");
        }

        InputTracker.Applied(channel);
    }

    /// <summary>An input method composition is in progress (a swap now would cut it).</summary>
    private static bool Composing(TMP_InputField field)
    {
        try { return field.m_IsCompositionActive; }
        catch { return false; }
    }

    /// <summary>
    /// CHAT13, each frame from the chat update (after a pending insert has landed): the first frame in a world, or a
    /// deferred switch, shows the shown tab's line and channel; opening the input puts the tab's channel back if the
    /// game changed it while the input was closed (or a pick outside the tab's channels ended with the last opening);
    /// a pick the tab may remember is saved; an empty line opened on a whisper tab gets <c>/w partner </c>.
    /// </summary>
    internal static void UpdateInput(ClientChatSystem system, HUDChatWindow window, bool insertPending)
    {
        if (!EnsureLoaded() || Set.Active < 0 || Set.Active >= Set.Tabs.Count) return;
        var tab = Set.Tabs[Set.Active];
        var field = window.ChatInputField;
        if (field == null || field.Pointer == IntPtr.Zero) return;
        if (_lineOwner == null) _lineOwner = tab;
        if (!ReferenceEquals(_lineOwner, tab) || !_appliedInWorld)
        {
            _appliedInWorld = true;
            ApplyInput(system, tab);
        }

        bool focused = window.IsChatFocused;
        if (focused) _lostLine = null;
        if (InputTracker.Observe(tab, (int)system._DefaultMode, focused, out bool opened) && TabsSetting != null)
            TabsSetting.Value = ChatTab.Save(Set.Tabs);
        if (opened) SetMode(system, ChatTabInput.ModeFor(tab, ChatWindow.WhisperPartner));
        string? line = InputTracker.Prefill(tab, ChatWindow.WhisperPartner, focused, field.text, insertPending);
        if (line != null)
        {
            field.text = line;
            field.MoveTextEnd(false);
        }
    }

    private static bool _appliedInWorld;

    /// <summary>
    /// CHAT13, from the recall's focus check: the input lost focus while the pointer is pressing the tab row. That is a
    /// tab click, not a send: keep the line for the tab switch and tell the caller not to recall it.
    /// </summary>
    internal static bool FocusLostToTabRow(string typed)
    {
        bool press = _drag == Drag.TabPress || _drag == Drag.TabDrag;
        if (!press)
        {
            try
            {
                press = UnityInputRuntime.AnyMouseButtonHeld()
                    && UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y)
                    && PointerOver(new Vector2(x, y));
            }
            catch { press = false; }
        }

        if (!press) return false;
        _lostLine = typed ?? "";
        try { _lostFrame = Time.frameCount; } catch { _lostFrame = 0; }
        return true;
    }

    /// <summary>A new world (or Core unload): the next frame applies the shown tab again; drafts belong to the old server.</summary>
    internal static void ForgetInput()
    {
        _lineOwner = null;
        _appliedInWorld = false;
        _lostLine = null;
        InputTracker.Reset();
        foreach (var t in Set.Tabs) t.Draft = "";
    }

    private static void SaveTabs()
    {
        if (TabsSetting != null) TabsSetting.Value = ChatTab.Save(Set.Tabs);
        if (Set.Active >= Set.Tabs.Count) Set.Active = Set.Tabs.Count - 1;
        Set.Unread = new bool[ChatTab.MaxTabs];
        Select(Set.Active);
    }

    private static UiDialog Choose(string title, string body, IReadOnlyList<string> options, Action<int> pick) =>
        UiDialogs.Choose(typeof(ChatTabs), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter, title, body, options, pick, "Cancel", null);

    private static void OpenMenu(int tab)
    {
        var options = new List<string>();
        var actions = new List<Action>();
        bool exists = tab >= 0 && tab < Set.Tabs.Count;
        bool editable = exists && !Set.Tabs[tab].Fixed;
        if (editable)
        {
            options.Add("Edit this tab");
            actions.Add(() => EditTab(tab));
        }

        // Reordering without a drag.
        if (exists && tab > 0)
        {
            options.Add("Move left");
            actions.Add(() => MoveTab(tab, tab - 1));
        }

        if (exists && tab < Set.Tabs.Count - 1)
        {
            options.Add("Move right");
            actions.Add(() => MoveTab(tab, tab + 1));
        }

        if (editable)
        {
            options.Add("Close this tab");
            actions.Add(() =>
            {
                Set.Tabs.RemoveAt(tab);
                Set.Active = ChatTabRow.ActiveAfterClose(Set.Active, tab, Set.Tabs.Count);
                SaveTabs();
            });
        }

        if (Set.Tabs.Count < ChatTab.MaxTabs)
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

        string title = tab >= 0 && tab < Set.Tabs.Count ? "Chat · " + Set.Tabs[tab].Name : "Chat";
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
        if (Set.Tabs.Count >= ChatTab.MaxTabs) return;
        var presets = new List<ChatTab>();
        foreach (var p in ChatTab.Presets())
        {
            bool taken = false;
            foreach (var t in Set.Tabs) if (string.Equals(t.Name, p.Name, StringComparison.OrdinalIgnoreCase)) taken = true;
            if (!taken) presets.Add(p);
        }

        var options = new List<string>();
        foreach (var p in presets) options.Add(p.Name);
        options.Add("Custom…");
        Choose("New tab", "Pick a ready-made tab, or make your own.", options, i =>
        {
            if (i >= 0 && i < presets.Count)
            {
                Set.Tabs.Add(presets[i]);
                Set.Active = Set.Tabs.Count - 1;
                SaveTabs();
            }
            else if (i == presets.Count)
            {
                OpenTabForm(ChatTabForm.Fresh(Set.Tabs.Count), isNew: true);
            }
        });
    }

    private static void EditTab(int index)
    {
        if (index < 0 || index >= Set.Tabs.Count || Set.Tabs[index].Fixed) return;
        OpenTabForm(Set.Tabs[index], isNew: false);
    }

    /// <summary>
    /// FORMDIALOG1: the tab editor, one dialog: the name, the "Show messages from" check boxes, Save and Cancel
    /// (<see cref="ChatTabForm"/>). Nothing changes until Save; a tab closed while its editor is open is not brought back.
    /// </summary>
    private static UiDialog OpenTabForm(ChatTab tab, bool isNew)
    {
        // A tab closed (or the tabs read again) while its editor is open, or a full row for a new one, is refused under
        // the field (ChatTabForm.Refusal) rather than dropped silently; Save checks again in case the row changed since.
        var options = ChatTabForm.Options(tab);
        options.Validate = r => ChatTabForm.Refusal(Set.Tabs, tab, isNew, r.Text, r.States);
        return UiDialogs.Form(typeof(ChatTabs), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter,
            ChatTabForm.Title(isNew), options, r =>
            {
                if (ChatTabForm.Refusal(Set.Tabs, tab, isNew, r.Text, r.States) != null) return;
                if (!ChatTabForm.Apply(tab, r.Text, r.States)) return;
                if (isNew)
                {
                    Set.Tabs.Add(tab);
                    Set.Active = Set.Tabs.Count - 1;
                }

                SaveTabs();
            });
    }

    /// <summary>
    /// FORMDIALOG1, bridge (<c>chat_tabs edit</c>): open the editor of tab <paramref name="index"/>, or of a new custom tab
    /// when <paramref name="index"/> is -1, as the quick menu does. Refused for the fixed All tab, a tab that does not
    /// exist, a full row of tabs, or before the saved tabs are read.
    /// </summary>
    internal static Dictionary<string, object?> OpenEditorForBridge(int index)
    {
        if (!EnsureLoaded()) throw new InvalidOperationException("the chat tabs are not read yet (the chat has not updated this session)");
        if (index == -1)
        {
            if (Set.Tabs.Count >= ChatTab.MaxTabs) throw new InvalidOperationException("the tab row is full (" + ChatTab.MaxTabs + " tabs)");
            OpenTabForm(ChatTabForm.Fresh(Set.Tabs.Count), isNew: true);
        }
        else
        {
            if (index < 0 || index >= Set.Tabs.Count) throw new ArgumentException("no tab " + index + " (there are " + Set.Tabs.Count + ")");
            if (Set.Tabs[index].Fixed) throw new ArgumentException("tab " + index + " (" + Set.Tabs[index].Name + ") cannot be edited");
            OpenTabForm(Set.Tabs[index], isNew: false);
        }

        return UiDialog.Describe();
    }
}
#endif
