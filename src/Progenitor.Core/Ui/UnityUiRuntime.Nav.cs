using System;
using Progenitor.Ui.GameArt;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// NAV1: what controller navigation (<see cref="UiNav"/>) needs from Unity: a widget's place on screen, the focus marker,
/// a text field's keyboard focus and the right stick's scroll. The devices are read in <c>UnityUiRuntime.cs</c>
/// (<c>SampleNav</c>), the framework's input layer.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    /// <summary>Content units a full right-stick deflection scrolls per second.</summary>
    private const float NavScrollSpeed = 1080f;

    /// <summary>The object a widget draws: a plain widget's or a cell row's (rows are their own runtime class).</summary>
    private static GameObject? NavObject(IRuntimeWidget widget) => widget switch
    {
        UnityWidget { Alive: true } w => w.GameObject,
        UnityCellRow { Alive: true } row => row.GameObject,
        _ => null,
    };

    private static UnityPanel? NavHost(IRuntimeWidget widget) => widget switch
    {
        UnityWidget { Alive: true } w => w.Owner,
        UnityCellRow { Alive: true } row => row.Host,
        _ => null,
    };

    public ScreenRect WidgetRect(IRuntimeWidget widget)
    {
        try
        {
            var go = NavObject(widget);
            if (go == null || !go.activeInHierarchy) return ScreenRect.Unknown;
            var rt = go.GetComponent<RectTransform>();
            return rt == null ? ScreenRect.Unknown : ScreenRectOf(rt);
        }
        catch { return ScreenRect.Unknown; }
    }

    /// <summary>The marker's object under the focused widget.</summary>
    private const string NavMarkName = "progenitor-nav-focus";

    /// <summary>
    /// The marker is the game's lit row band, the highlight its Options rows show under the cursor: a grid row lights its
    /// own band; any other widget (a list entry, a button, a cell, an option line) gets the same band drawn over its face
    /// and under its caption. Live 2026-10-10: a simulated pointer enter changed nothing on a Mods list entry, whose hover
    /// is the framework's own, so the marker is drawn rather than borrowed from the pointer.
    /// </summary>
    public void SetNavFocus(IRuntimeWidget widget, int option, bool on)
    {
        GameObject? target = NavObject(widget);
        if (target == null) return;
        if (option >= 0 && widget is IRuntimePartsWidget parts && parts.Part(UiControlParts.Option(option)) is UnityWidget { Alive: true } part)
            target = part.GameObject;
        else if (NavHost(widget) is { } host && host.HasRowBand(target))
        {
            host.SetGridRowLit(target, on);
            return;
        }

        // A whole list row (a Mods list entry, an Install row) takes the band; a control or a button cell, whose own face
        // would cover a band, is framed (live 2026-10-10: on the page's switch only the band's top edge showed).
        // P7b: a row that asked for it (a data table's soft row, whose own fill covers the band) is framed too.
        if (target != null) DrawNavMark(target, on, frame: widget is not UnityCellRow || NavFramedRow(target));
    }

    /// <summary>Canvas units the frame stands outside the control, and its thickness.</summary>
    private const float NavFrameOutset = 3f;
    private const float NavFrameWidth = 2f;

    private static void DrawNavMark(GameObject go, bool on, bool frame)
    {
        try
        {
            var existing = go.transform.Find(NavMarkName);
            if (!on)
            {
                if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
                return;
            }

            if (existing != null) return;
            if (frame)
            {
                DrawNavFrame(go);
                return;
            }

            var mark = new GameObject(NavMarkName);
            mark.transform.SetParent(go.transform, false);
            // First child: over the widget's own face, under its caption and icons.
            mark.transform.SetSiblingIndex(0);
            Stretch(mark);
            var image = mark.AddComponent<Image>();
            image.raycastTarget = false;
            var hue = SettingsControlLook.SpriteRowBand(true);
            var flat = Progenitor.Ui.GameArt.GameSettingsLook.FlatRowBand();
            bool borrowed = PaintPart(image, SettingsArtPart.RowBand, new Color(hue.R, hue.G, hue.B, hue.A), new Color(flat.R, flat.G, flat.B, flat.A));
            // The band's bright top edge (the game's ListButton top gradient color), so the marker reads on any fill.
            var top = new GameObject(NavMarkName + "-top");
            top.transform.SetParent(mark.transform, false);
            var rt = top.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -2f);
            rt.offsetMax = Vector2.zero;
            var line = top.AddComponent<Image>();
            line.raycastTarget = false;
            var t = Progenitor.Ui.GameArt.GameSettingsLook.RowBandTopLit;
            line.color = new Color(t.R, t.G, t.B, borrowed ? t.A : Math.Min(0.35f, t.A * 2.5f));
        }
        catch { /* the marker is cosmetic */ }
    }

    /// <summary>Four lines in the band's bright top-edge color just outside the control, drawn over its face.</summary>
    private static void DrawNavFrame(GameObject go)
    {
        var mark = new GameObject(NavMarkName);
        mark.transform.SetParent(go.transform, false);
        mark.transform.SetAsLastSibling();
        var rt = mark.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(-NavFrameOutset, -NavFrameOutset);
        rt.offsetMax = new Vector2(NavFrameOutset, NavFrameOutset);
        var t = GameSettingsLook.RowBandTopLit;
        var color = new Color(t.R, t.G, t.B, 0.9f);
        Edge(mark, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -NavFrameWidth), Vector2.zero, color);
        Edge(mark, new Vector2(0f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, NavFrameWidth), color);
        Edge(mark, new Vector2(0f, 0f), new Vector2(0f, 1f), Vector2.zero, new Vector2(NavFrameWidth, 0f), color);
        Edge(mark, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(-NavFrameWidth, 0f), Vector2.zero, color);

        static void Edge(GameObject parent, Vector2 min, Vector2 max, Vector2 offMin, Vector2 offMax, Color c)
        {
            var e = new GameObject(NavMarkName + "-edge");
            e.transform.SetParent(parent.transform, false);
            var ert = e.AddComponent<RectTransform>();
            ert.anchorMin = min;
            ert.anchorMax = max;
            ert.offsetMin = offMin;
            ert.offsetMax = offMax;
            var img = e.AddComponent<Image>();
            img.raycastTarget = false;
            img.color = c;
        }
    }

    public bool FocusTextIn(IRuntimeWidget widget)
    {
        try
        {
            var go = NavObject(widget);
            if (go == null) return false;
            var field = go.GetComponentInChildren<TMP_InputField>(false);
            return field != null && FocusOwnedField(field);
        }
        catch { return false; }
    }

    public void ScrollNear(IRuntimeWidget widget, float amount)
    {
        try
        {
            if (NavHost(widget) is not { } host) return;
            var scroll = host.Scroll;
            if (scroll == null) return;
            RectTransform? content = null;
            RectTransform? viewport = null;
            try { content = scroll.content; } catch { /* */ }
            try { viewport = scroll.viewport; } catch { /* */ }
            if (content == null || viewport == null) return;
            float contentH = ContentHeightOf(content);
            float viewH = ViewportHeightOf(viewport);
            if (!MenuPanelLayout.CanApplyNormalized(contentH, viewH)) return;
            float max = contentH - viewH;
            // Content offset 0 is the top; the stick pushed up shows what is above.
            float dt;
            try { dt = Time.unscaledDeltaTime; } catch { dt = 1f / 60f; }
            if (!(dt > 0f) || dt > 0.1f) dt = 1f / 60f;
            float y = content.anchoredPosition.y - amount * NavScrollSpeed * dt;
            y = y < 0f ? 0f : y > max ? max : y;
            if (y - content.anchoredPosition.y < 0.01f && content.anchoredPosition.y - y < 0.01f) return;
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
            ClearSidebarScrollPreserve();
            UiReveal.RaiseScrolled();
        }
        catch { /* a missed scroll is not a failure */ }
    }
}
