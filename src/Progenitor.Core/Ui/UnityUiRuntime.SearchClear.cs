#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace Progenitor.Ui;

/// <summary>
/// SEARCHCLEAR1 (requested 2026-10-09): the clear button inside the workspace search boxes (the Mods
/// search and the Install search), and the focus half of their Escape.
///
/// <para>The button is a child of the field named <see cref="MenuRowStyle.SearchClearName"/>: the kit's
/// clear glyph (<see cref="MenuRowStyle.SearchClearGlyph"/>) in the search mark's color, at the right of
/// the box where the empty box draws its search mark, shown only while the box has text
/// (<see cref="SyncSearchClear"/>, from every place the empty-box hint is synced). Its click target is the
/// whole height of the box and <see cref="MenuRowStyle.SearchClearWidth"/> wide; the typed text stops
/// short of it.</para>
///
/// <para>It has no <c>Button</c>, no raycast target and no Bootstrap slot: the shown-input pump owns
/// clicks on fields (<see cref="FocusFieldAt"/> tests the button before the field), and the pump tints the
/// glyph brighter under the pointer and darker while held (<see cref="SyncSearchClearTints"/>). A
/// <c>Button</c> would also become the event system's selection on a press and deselect the field.
/// A click empties the box through the owned edit path (<see cref="WriteFieldState"/>: the field's
/// value-changed event, so the pane re-filters as for typing) and gives it focus with the caret at the
/// start, so the player can type the next search at once.</para>
/// </summary>
internal sealed partial class UnityUiRuntime
{
    /// <summary>Clicks on a clear button since Core loaded (pointer and bridge), for the readback.</summary>
    internal static long SearchClearPresses;

    /// <summary>Build the clear button on <paramref name="control"/> (the field's own object), hidden while empty.</summary>
    private static void AttachSearchClear(GameObject control, TMP_InputField field)
    {
        if (control == null || field == null) return;
        GameObject? go = null;
        try
        {
            go = new GameObject(MenuRowStyle.SearchClearName);
            go.transform.SetParent(control.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(MenuRowStyle.SearchClearWidth, 0f);

            var tmp = TryAdd<TextMeshProUGUI>(go);
            if (tmp == null)
            {
                TryDestroyImmediate(go);
                return;
            }

            var font = FindFont();
            try { tmp.text = MenuRowStyle.PickSearchClearGlyph(s => FontHasString(font, s)); } catch { /* */ }
            try { tmp.fontSize = MenuRowStyle.SearchClearFontSize; } catch { /* */ }
            try { tmp.alignment = TextAlignmentOptions.Center; } catch { /* */ }
            var c = MenuRowStyle.SearchClearColor(hovered: false, pressed: false);
            try { tmp.color = new Color(c.R, c.G, c.B, c.A); } catch { /* */ }
            try { tmp.richText = false; } catch { /* */ }
            try { tmp.enableWordWrapping = false; } catch { /* */ }
            try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
            try { tmp.enableAutoSizing = false; } catch { /* */ }
            // The pump hit-tests the rect; a raycast target here would take the pointer from the field.
            try { tmp.raycastTarget = false; } catch { /* */ }
            ApplyCachedFont(tmp);

            // The typed text stops short of the button (the viewport is the text's clip).
            try
            {
                var viewport = field.textViewport;
                if (viewport != null)
                    viewport.offsetMax = new Vector2(-MenuRowStyle.SearchTextRightInset(true, InputPadX), viewport.offsetMax.y);
            }
            catch { /* */ }

            bool show = false;
            try { show = MenuRowStyle.SearchClearVisible(field.text); } catch { /* */ }
            try { go.SetActive(show); } catch { /* */ }
            try { go.transform.SetAsLastSibling(); } catch { /* */ }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building the search clear button threw: " + e.Message);
            TryDestroyImmediate(go);
        }
    }

    /// <summary>Show the clear button while <paramref name="field"/> has text (with the empty-box hint's sync).</summary>
    private static void SyncSearchClear(TMP_InputField field, string? text)
    {
        if (field == null) return;
        GameObject? clear = null;
        try
        {
            var t = field.transform.Find(MenuRowStyle.SearchClearName);
            clear = t == null ? null : t.gameObject;
        }
        catch { clear = null; }
        if (clear == null) return;
        bool show = MenuRowStyle.SearchClearVisible(text);
        try { if (clear.activeSelf != show) clear.SetActive(show); } catch { /* */ }
        if (show)
        {
            try { clear.transform.SetAsLastSibling(); } catch { /* */ }
        }
    }

    /// <summary>A workspace search box: the Mods search row, or a field that carries a clear button.</summary>
    private static bool IsWorkspaceSearch(TMP_InputField? field)
    {
        if (field == null) return false;
        if (IsSearchInput(field)) return true;
        try { return field.transform.Find(MenuRowStyle.SearchClearName) != null; }
        catch { return false; }
    }

    /// <summary>True when <paramref name="go"/> is inside a panel that is shown now (not parked or hidden).</summary>
    private static bool InShownPanel(GameObject? go)
    {
        if (go == null) return false;
        try
        {
            for (var t = go.transform; t != null; t = t.parent)
            {
                for (int i = 0; i < ShownPanels.Count; i++)
                {
                    var panel = ShownPanels[i];
                    if (panel == null || !panel.Alive || panel.GameObject == null) continue;
                    if (panel.GameObject.transform == t) return true;
                }
            }
        }
        catch { /* */ }
        return false;
    }

    /// <summary>The clear button of an armed field that is drawn now (shown, in a shown panel).</summary>
    private static bool ClearDrawn(ArmedField armed)
    {
        if (armed?.Clear == null || armed.Field == null) return false;
        try
        {
            if (!Alive(armed.Clear) || !armed.Clear.activeInHierarchy) return false;
        }
        catch { return false; }
        return InShownPanel(armed.Field.gameObject);
    }

    /// <summary>
    /// A press at (<paramref name="x"/>, <paramref name="y"/>) on a drawn clear button: empty that box and
    /// focus it. False when the press is not on one (the field and the rest take it as before).
    /// </summary>
    private static bool TryPressSearchClearAt(float x, float y, Transform? dialogRoot)
    {
        for (int i = 0; i < _armedFields.Count; i++)
        {
            var armed = _armedFields[i];
            if (armed?.Clear == null || !ClearDrawn(armed)) continue;
            if (!Hit(armed.Clear, x, y, dialogRoot)) continue;
            ClearSearchFromButton(armed.Field, "pointer");
            return true;
        }

        return false;
    }

    /// <summary>
    /// The clear button's action: the box is emptied through the owned edit path (its value-changed event
    /// fires once, so the pane re-filters) and gets focus with the caret at the start.
    /// </summary>
    private static void ClearSearchFromButton(TMP_InputField field, string via)
    {
        if (field == null) return;
        SearchClearPresses++;
        FocusOwnedField(field);
        var before = ReadFieldState(field);
        if (before.Text.Length > 0) WriteFieldState(field, before, new UiTextEditState("", 0, 0));
        else SeedOwnedCaret(field, 0);
        try { Plugin.Logger?.LogDiag("Ui: search-clear via=" + via + " field=" + DescribeSearchFieldId(field) + " length=" + before.Text.Length); }
        catch { /* */ }
    }

    /// <summary>
    /// Per pump frame: each drawn clear button is brighter while the pointer is over it and darker while
    /// the primary button is held on it. Writes only when the state changes.
    /// </summary>
    private static void SyncSearchClearTints(PointerSample pointer, bool held)
    {
        for (int i = 0; i < _armedFields.Count; i++)
        {
            var armed = _armedFields[i];
            if (armed?.Clear == null || armed.ClearText == null) continue;
            int want = 0;
            if (pointer.Kind == PointerKind.Inside && ClearDrawn(armed) && Hit(armed.Clear, pointer.X, pointer.Y, OpenDialogRoot()))
                want = held ? 2 : 1;
            if (want == armed.ClearTint) continue;
            armed.ClearTint = want;
            var c = MenuRowStyle.SearchClearColor(hovered: want == 1, pressed: want == 2);
            try { armed.ClearText.color = new Color(c.R, c.G, c.B, c.A); } catch { /* */ }
        }
    }

    /// <summary>Escape on an empty workspace search box: it gives up focus, as a click outside it does.</summary>
    public bool LeaveSearchField()
    {
        var field = _ownedFocusField;
        if (field == null || !IsWorkspaceSearch(field)) return false;
        BlurExternalField(field);
        try { Plugin.Logger?.LogDiag("Ui: search-escape left field=" + DescribeSearchFieldId(field)); }
        catch { /* */ }
        return true;
    }

    private static string DescribeSearchFieldId(TMP_InputField field) =>
        IsSearchInput(field) ? MenuHotkeyGuard.ModsSearch : MenuHotkeyGuard.InstallSearch;

    /// <summary>The drawn workspace search box: the focused one, else the first armed one in a shown panel.</summary>
    private static ArmedField? DrawnWorkspaceSearch()
    {
        PruneArmedFields();
        ArmedField? first = null;
        for (int i = 0; i < _armedFields.Count; i++)
        {
            var armed = _armedFields[i];
            if (armed?.Field == null || !IsWorkspaceSearch(armed.Field) || !InShownPanel(armed.Field.gameObject)) continue;
            if (ReferenceEquals(armed.Field, _ownedFocusField)) return armed;
            first ??= armed;
        }

        return first;
    }

    /// <summary>
    /// Bridge (<c>mods_ui widget_click {label: "search-clear"}</c>): press the drawn search box's clear
    /// button through the same action a click takes. Refused while the box is empty (no button drawn).
    /// </summary>
    internal static Dictionary<string, object?> PressSearchClearForBridge()
    {
        var armed = DrawnWorkspaceSearch();
        if (armed == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no workspace search box is shown" };
        if (!ClearDrawn(armed))
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "the search box is empty; its clear button is not shown", ["searchClear"] = DescribeSearchClear() };
        string before = "";
        try { before = armed.Field.text ?? ""; } catch { /* */ }
        ClearSearchFromButton(armed.Field, "bridge");
        var d = DescribeSearchClear();
        d["ok"] = true;
        d["clearedLength"] = before.Length;
        d["handler"] = "UnityUiRuntime.ClearSearchFromButton (the pointer path's action)";
        return d;
    }

    /// <summary>
    /// Readback (<c>status.focus.searchClear</c>, <c>widget_read.searchClear</c>): the drawn search box
    /// (<c>mods-search</c> or <c>install-search</c>), its text, whether it has focus, and its clear button:
    /// <c>{shown, glyph, rect, tint}</c> (tint <c>rest</c>, <c>hover</c>, <c>pressed</c>).
    /// </summary>
    internal static Dictionary<string, object?> DescribeSearchClear()
    {
        var d = new Dictionary<string, object?> { ["presses"] = SearchClearPresses };
        ArmedField? armed;
        try { armed = DrawnWorkspaceSearch(); }
        catch { armed = null; }
        if (armed == null)
        {
            d["field"] = MenuHotkeyGuard.NoField;
            return d;
        }

        d["field"] = DescribeSearchFieldId(armed.Field);
        string text = "";
        try { text = armed.Field.text ?? ""; } catch { /* */ }
        d["text"] = text;
        d["focused"] = ReferenceEquals(armed.Field, _ownedFocusField);
        bool shown = ClearDrawn(armed);
        d["shown"] = shown;
        d["glyph"] = armed.ClearText != null ? SafeText(armed.ClearText) : null;
        d["tint"] = armed.ClearTint switch { 1 => "hover", 2 => "pressed", _ => "rest" };
        if (shown && armed.Clear != null)
        {
            try
            {
                var rt = armed.Clear.GetComponent<RectTransform>();
                var r = rt == null ? ScreenRect.Unknown : ScreenRectOf(rt);
                if (r.IsKnown)
                    d["rect"] = new Dictionary<string, object?> { ["x"] = r.MinX, ["y"] = r.MinY, ["width"] = r.MaxX - r.MinX, ["height"] = r.MaxY - r.MinY };
            }
            catch { /* */ }
        }

        return d;
    }

    private static string SafeText(TMP_Text tmp)
    {
        try { return tmp.text ?? ""; }
        catch { return ""; }
    }
}
#endif
