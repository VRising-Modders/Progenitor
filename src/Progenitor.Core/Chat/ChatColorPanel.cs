#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Inputs;
using Progenitor.Ui;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Chat;

/// <summary>
/// CHAT6 Colors (design approved 2026-10-07): a small panel directly above the chat box. One row per channel: its
/// name, a sample line in its color now, and swatches (the game's color, then the ten palette colors). A click on a
/// swatch recolors the sample and the chat at once and is saved at once; the chosen swatch is outlined, and the
/// hovered one is named in the title. "Use the game's colors" resets every channel; "Done" or a click outside closes.
/// </summary>
internal static class ChatColorPanel
{
    private const float Pad = 10f;
    private const float TitleHeight = 22f;
    private const float RowHeight = 46f;
    private const float LabelWidth = 104f;
    private const float Swatch = 18f;
    private const float SwatchGap = 5f;
    private const float GameSwatchWidth = 40f;
    private const string ResetId = "reset";
    private const string DoneId = "done";

    private static readonly string[] Samples =
    {
        "[Global] Mira: anyone up for a hunt?",
        "[Local] Tov: over here",
        "[Clan] Ash: the castle is under attack",
        "[Whisper] Mira: thanks!",
        "[SYSTEM] The server restarts in 10 minutes",
    };

    private sealed class SwatchCell
    {
        internal int Channel;
        internal string? Hex;
        internal string Name = "";
        internal RectTransform Rect = null!;
        internal GameObject Frame = null!;
    }

    private static GameObject? _root;
    private static IntPtr _windowPtr;
    private static TextMeshProUGUI? _title;
    private static TextMeshProUGUI? _actions;
    private static readonly List<TextMeshProUGUI> SampleTexts = new();
    private static readonly List<SwatchCell> Cells = new();
    private static string _drawnColors = "\u0001";
    private static string _hovered = "";

    internal static bool IsOpen => _root != null && _root.Pointer != IntPtr.Zero;

    internal static void Open(HUDChatWindow window)
    {
        Close();
        try { Build(window); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: the colors panel threw while opening: " + e.Message);
            Close();
        }
    }

    internal static void Close()
    {
        try { if (_root != null && _root.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_root); }
        catch { /* gone with its window */ }
        _root = null;
        _title = null;
        _actions = null;
        _windowPtr = IntPtr.Zero;
        SampleTexts.Clear();
        Cells.Clear();
        _drawnColors = "\u0001";
        _hovered = "";
    }

    /// <summary>Whether the screen point is over the panel (the click guard and the chat's hover test use it).</summary>
    internal static bool Contains(Vector2 point)
    {
        if (!IsOpen) return false;
        var rect = _root!.transform.TryCast<RectTransform>();
        return rect != null && _root.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rect, point, null);
    }

    /// <summary>Each frame from <see cref="ChatTabs.Update"/>: follow the saved colors, the hover and the clicks.</summary>
    internal static void Update(HUDChatWindow window, bool chatShown)
    {
        if (!IsOpen) return;
        if (_windowPtr != window.Pointer || !chatShown)
        {
            Close();
            return;
        }

        string colors = ChatView.Colors?.Value ?? "";
        if (!string.Equals(colors, _drawnColors, StringComparison.Ordinal)) Redraw(colors);
        if (!UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y)) return;
        var point = new Vector2(x, y);

        var over = CellAt(point);
        string hovered = over == null ? "" : over.Name;
        if (!string.Equals(hovered, _hovered, StringComparison.Ordinal))
        {
            _hovered = hovered;
            if (_title != null) _title.text = TitleText();
        }

        if (!UnityInputRuntime.MouseButtonPressedThisFrame(0)) return;
        if (over != null)
        {
            if (ChatView.Colors != null)
                ChatView.Colors.Value = ChatColors.Set(ChatView.Colors.Value, ChatColors.Channels[over.Channel].Key, over.Hex);
            return;
        }

        if (_actions != null && Contains(point))
        {
            int link = TMP_TextUtilities.FindIntersectingLink(_actions, new Vector3(x, y, 0f), null);
            string id = link >= 0 ? _actions.textInfo.linkInfo[link].GetLinkID() : "";
            if (id == ResetId && ChatView.Colors != null) ChatView.Colors.Value = "";
            else if (id == DoneId) Close();
            return;
        }

        // A click anywhere else closes it (the chat box itself included, so its own clicks still work).
        if (!Contains(point)) Close();
    }

    private static SwatchCell? CellAt(Vector2 point)
    {
        foreach (var c in Cells)
            if (c.Rect != null && c.Rect.Pointer != IntPtr.Zero && RectTransformUtility.RectangleContainsScreenPoint(c.Rect, point, null))
                return c;
        return null;
    }

    private static string TitleText() =>
        "Chat colors" + (_hovered.Length > 0 ? "<color=" + ChatFormat.MutedHex + ">  ·  " + UiRichText.Literal(_hovered) + "</color>" : "");

    private static void Redraw(string colors)
    {
        _drawnColors = colors;
        for (int i = 0; i < ChatColors.Channels.Length && i < SampleTexts.Count; i++)
        {
            var kind = ChatColors.Channels[i].Kinds[0];
            string hex = ChatColors.HexFor(colors, kind) ?? ChatView.GameHex(kind);
            if (ColorUtility.TryParseHtmlString(hex, out var c)) SampleTexts[i].color = c;
        }

        foreach (var cell in Cells)
        {
            string? now = ChatColors.HexFor(colors, ChatColors.Channels[cell.Channel].Kinds[0]);
            bool on = cell.Hex == null ? now == null : string.Equals(cell.Hex, now, StringComparison.OrdinalIgnoreCase);
            if (cell.Frame.activeSelf != on) cell.Frame.SetActive(on);
        }

        if (_actions != null) _actions.text = ActionsText(colors.Length > 0);
    }

    private static string ActionsText(bool anyChanged) =>
        (anyChanged ? "<link=\"" + ResetId + "\"><color=" + ChatFormat.MutedHex + ">Use the game's colors</color></link>   " : "")
        + "<link=\"" + DoneId + "\"><b>Done</b></link>";

    // ---- building ------------------------------------------------------------------------------------------

    private static void Build(HUDChatWindow window)
    {
        var prefab = window.ChatMessagePrefab;
        var windowRect = window.transform.TryCast<RectTransform>();
        if (prefab == null || windowRect == null) return;

        float height = Pad * 2 + TitleHeight + ChatColors.Channels.Length * RowHeight;
        var root = new GameObject("progenitor-chat-colors");
        var rect = root.AddComponent<RectTransform>();
        root.transform.SetParent(window.transform, false);
        root.AddComponent<LayoutElement>().ignoreLayout = true;
        // Above the window's top edge, as wide as the window.
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(0f, 6f);
        rect.sizeDelta = new Vector2(0f, height);
        // Its own canvas, sorted above the chat (as the suggestion panel).
        var canvas = root.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        var parentCanvas = window.GetComponentInParent<Canvas>();
        canvas.sortingOrder = (parentCanvas != null ? parentCanvas.sortingOrder : 0) + 6;
        var bg = root.AddComponent<Image>();
        bg.color = new Color(0.05f, 0.05f, 0.07f, 0.96f);
        bg.raycastTarget = false;
        _root = root;
        _windowPtr = window.Pointer;

        _title = Text(prefab.gameObject, root.transform, new Vector2(Pad, -Pad), new Vector2(-Pad, TitleHeight), stretch: true);
        if (_title != null)
        {
            _title.text = TitleText();
            _title.alignment = TextAlignmentOptions.Left;
        }

        _actions = Text(prefab.gameObject, root.transform, new Vector2(Pad, -Pad), new Vector2(-Pad, TitleHeight), stretch: true);
        if (_actions != null) _actions.alignment = TextAlignmentOptions.Right;

        for (int i = 0; i < ChatColors.Channels.Length; i++)
        {
            float top = -(Pad + TitleHeight + i * RowHeight);
            Line(root.transform, top);
            var label = Text(prefab.gameObject, root.transform, new Vector2(Pad, top - 4f), new Vector2(LabelWidth, 20f), stretch: false);
            if (label != null)
            {
                label.text = "<color=" + ChatFormat.MutedHex + ">" + UiRichText.Literal(ChatColors.Channels[i].Name) + "</color>";
                label.enableWordWrapping = true;
            }

            var sample = Text(prefab.gameObject, root.transform, new Vector2(Pad + LabelWidth, top - 2f), new Vector2(-Pad, 20f), stretch: true);
            if (sample != null)
            {
                sample.richText = false;
                sample.text = i < Samples.Length ? Samples[i] : ChatColors.Channels[i].Name;
                SampleTexts.Add(sample);
            }

            float left = Pad + LabelWidth;
            float swTop = top - 24f;
            AddCell(root.transform, prefab.gameObject, i, null, "The game's color", new Vector2(left, swTop), GameSwatchWidth);
            left += GameSwatchWidth + SwatchGap;
            foreach (var p in ChatColors.Palette)
            {
                AddCell(root.transform, prefab.gameObject, i, p.Hex, p.Name, new Vector2(left, swTop), Swatch);
                left += Swatch + SwatchGap;
            }
        }

        Redraw(ChatView.Colors?.Value ?? "");
    }

    private static void AddCell(Transform parent, GameObject prefab, int channel, string? hex, string name, Vector2 topLeft, float width)
    {
        var frame = Box(parent, topLeft + new Vector2(-2f, 2f), new Vector2(width + 4f, Swatch + 4f), Color.white);
        var cell = Box(parent, topLeft, new Vector2(width, Swatch),
            hex != null && ColorUtility.TryParseHtmlString(hex, out var c) ? c : new Color(0.17f, 0.17f, 0.2f, 1f));
        if (hex == null)
        {
            var t = Text(prefab, cell.transform, Vector2.zero, Vector2.zero, stretch: true, fill: true);
            if (t != null)
            {
                t.text = "<color=" + ChatFormat.MutedHex + ">Game</color>";
                t.alignment = TextAlignmentOptions.Center;
                t.fontSize = 12f;
                t.enableAutoSizing = false;
            }
        }

        Cells.Add(new SwatchCell
        {
            Channel = channel,
            Hex = hex,
            Name = name,
            Rect = cell.transform.TryCast<RectTransform>()!,
            Frame = frame,
        });
        frame.SetActive(false);
    }

    private static GameObject Box(Transform parent, Vector2 topLeft, Vector2 size, Color color)
    {
        var go = new GameObject("box");
        var rect = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = topLeft;
        rect.sizeDelta = size;
        var image = go.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return go;
    }

    private static void Line(Transform parent, float top)
    {
        var go = new GameObject("rule");
        var rect = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(0f, top);
        rect.offsetMin = new Vector2(Pad, rect.offsetMin.y);
        rect.offsetMax = new Vector2(-Pad, rect.offsetMax.y);
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, 1f);
        var image = go.AddComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0.08f);
        image.raycastTarget = false;
    }

    /// <summary>
    /// A text from the game's line prefab (its font). <paramref name="stretch"/> spans to the right edge less
    /// <paramref name="size"/>.x; otherwise <paramref name="size"/> is the size. <paramref name="fill"/> fills the parent.
    /// </summary>
    private static TextMeshProUGUI? Text(GameObject prefab, Transform parent, Vector2 topLeft, Vector2 size, bool stretch, bool fill = false)
    {
        var go = UnityEngine.Object.Instantiate(prefab, parent)?.TryCast<GameObject>();
        var text = go?.GetComponent<TextMeshProUGUI>();
        if (go == null || text == null) return null;
        go.AddComponent<LayoutElement>().ignoreLayout = true;
        var rect = go.transform.TryCast<RectTransform>();
        if (rect != null)
        {
            if (fill)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else if (stretch)
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = topLeft;
                rect.offsetMin = new Vector2(topLeft.x, rect.offsetMin.y);
                rect.offsetMax = new Vector2(size.x, rect.offsetMax.y);
                rect.sizeDelta = new Vector2(rect.sizeDelta.x, size.y);
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, topLeft.y);
            }
            else
            {
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(0f, 1f);
                rect.pivot = new Vector2(0f, 1f);
                rect.anchoredPosition = topLeft;
                rect.sizeDelta = size;
            }
        }

        text.richText = true;
        text.color = Color.white;
        text.raycastTarget = false;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.text = "";
        return text;
    }
}
#endif
