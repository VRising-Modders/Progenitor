#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Install;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// AUTOUPDATE2 (decided 2026-10-09): the title screen's update line is a subtitle under MODS made exactly like the
/// game's own subtitle under CONTINUE (<c>MenuButton.AdditionalText</c>, the last server or save): a clone of that
/// element, so the font, size, color, letter spacing and centering are the game's. It reads <c>1 MOD UPDATE AVAILABLE</c>
/// / <c>N MOD UPDATES AVAILABLE</c>, or the automatic update notice (<see cref="ClientAutoUpdate.Notice"/>), and is absent
/// when there is nothing to say. While it shows, the MODS row takes CONTINUE's row height and caption placement, so the
/// title list's own <c>VerticalLayoutGroup</c> balances the column as it does for CONTINUE; hidden, the row is exactly as
/// attached. The in-game MODS tab has no subtitle; the Install tab's update list says the same there.
/// </summary>
internal static partial class NativeModsEntry
{
    internal const string BadgeObjectName = "progenitor-mods-subtitle";
    private const float BadgeCheckSeconds = 0.5f;
    private const double PrimeAfterSeconds = 3.0;

    /// <summary>A small dot before the line in the game's accent red (a user choice to make from captures; off by default).</summary>
    internal static bool SubtitleDot { get; set; }

    /// <summary>The game's accent red (the section header rule) for the optional dot.</summary>
    internal const string AccentRedHex = "#B6302A";

    /// <summary>The share of the subtitle's extra height the list moves up (the rest moves CONTINUE through EXIT down).</summary>
    internal const float SubtitleLiftShare = 1f / 3f;

    private static TextMeshProUGUI? _badge;
    private static string _badgeShown = "";
    private static float _badgeNextCheck;
    private static bool _updatesPrimed;
    private static double _badgeFirstTick = -1;
    private static RowBaseline? _rowBase;
    private static string _subtitleSource = "";
    private static string _lastAuto = "";

    /// <summary>The MODS row as attached: restored exactly when the subtitle hides.</summary>
    private sealed class RowBaseline
    {
        public GameObject Row = null!;
        public float MinHeight;
        public float PreferredHeight;
        public Vector2 SizeDelta;
        public Vector2 CapAnchorMin, CapAnchorMax, CapPivot, CapPos, CapSize;
        public bool Grown;

    }

    /// <summary>AUTOUPDATE2: the title screen's main menu is up (the apply window's "at the title screen").</summary>
    internal static bool AtTitleScreen => Alive(_main);

    private static void TickUpdateBadge()
    {
        float now;
        try { now = Time.unscaledTime; }
        catch { return; }
        if (now < _badgeNextCheck) return;
        _badgeNextCheck = now + BadgeCheckSeconds;
        if (_badgeFirstTick < 0) _badgeFirstTick = now;

        // Once per Core generation, a few seconds in: serve the cached index (no request) and run the first pass, so the
        // line is right before the player opens anything.
        if (!_updatesPrimed && now - _badgeFirstTick >= PrimeAfterSeconds)
        {
            _updatesPrimed = true;
            try { ModsMenu.PrimeUpdates(); }
            catch (Exception e) { Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "prime failed: " + e.Message); }
        }

        // AUTOUPDATE2: Automatic mode, only in the apply window (the title screen; the window check says the rest).
        if (_updatesPrimed && AtTitleScreen)
        {
            // AUTOUPDATE2 (phase 1 open issue 3): a changed [Install] DevThunderstoreUrl rebuilds the index source.
            try { ModsMenu.FollowIndexSource(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Install: index source check threw: " + e.Message); }

            try { _lastAuto = ClientAutoUpdate.Tick(); }
            catch (Exception e) { _lastAuto = "threw: " + e.Message; }
        }

        try { ApplyBadge(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "subtitle update failed: " + e.Message);
            _badgeNextCheck = now + 10f;
        }
    }

    /// <summary>The line under MODS now: updates waiting, else mods updated, else a failure; empty when there is nothing to say.</summary>
    internal static string SubtitleText()
    {
        // Decided 2026-10-09, in this order: updates waiting for the player, then mods updated automatically, then a
        // failure; nothing at all otherwise. Never a mod's name or a version (those are on the Install tab and the mod page).
        // AUTOUPDATE5: a Progenitor update waiting for the player counts as one more, with the same generic words.
        string waiting = ClientUpdates.BadgeTextFor(ClientUpdates.BadgeCount + SelfUpdates.WaitingCount);
        if (waiting.Length > 0) return waiting;
        var notice = ClientAutoUpdate.Notice;
        return notice?.Line ?? "";
    }

    private static void ApplyBadge()
    {
        string text = SubtitleText();
        var root = Alive(_main) ? _widget?.Root : null;
        if (root == null || text.Length == 0 || ModsMenu.IsOpen)
        {
            HideBadge();
            return;
        }

        var source = SubtitleSource(out var continueButton);
        if (source == null)
        {
            HideBadge();
            return;
        }

        if (_badge == null || !Alive(_badge.gameObject) || _badge.transform.parent != root.transform)
        {
            DestroyBadge();
            _badge = CloneSubtitle(source, root);
            if (_badge == null) return;
        }

        string shown = Styled(text, source);
        if (!string.Equals(_badgeShown, shown, StringComparison.Ordinal) || !_badge.gameObject.activeSelf)
        {
            _badge.text = shown;
            _badgeShown = shown;
            GrowRow(root, continueButton, source);
            _badge.gameObject.SetActive(true);
            RebuildList(root);
        }
    }

    /// <summary>The game's case for that line (upper case, unless the TMP style already makes it so), and the optional dot.</summary>
    private static string Styled(string text, TextMeshProUGUI source)
    {
        string t = text;
        try
        {
            if ((source.fontStyle & FontStyles.UpperCase) == 0) t = t.ToUpperInvariant();
        }
        catch { t = t.ToUpperInvariant(); }
        if (SubtitleDot) t = "<color=" + AccentRedHex + ">•</color> " + t;
        return t;
    }

    /// <summary>
    /// CONTINUE's subtitle, live (its row is the layout reference), else the menu's button prefab's (CONTINUE is hidden
    /// when there is nothing to continue).
    /// </summary>
    private static TextMeshProUGUI? SubtitleSource(out MenuButton? continueButton)
    {
        continueButton = null;
        try
        {
            var collection = _main?.MenuCollection;
            if (collection == null) return null;
            var list = collection.InstancedButtonsNoDivider;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var b = list[i];
                    if (!Alive(b) || !b.gameObject.activeInHierarchy) continue;
                    var extra = b.AdditionalText;
                    if (extra == null || !Alive(extra.gameObject)) continue;
                    if (!extra.gameObject.activeInHierarchy || string.IsNullOrWhiteSpace(extra.text)) continue;
                    continueButton = b;
                    _subtitleSource = "live:" + b.gameObject.name;
                    return extra;
                }

            var prefab = collection.MenuButtonPrefab;
            if (prefab != null && prefab.AdditionalText != null)
            {
                _subtitleSource = "prefab";
                return prefab.AdditionalText;
            }
        }
        catch { /* no subtitle reference: no line */ }

        _subtitleSource = "none";
        return null;
    }

    private static TextMeshProUGUI? CloneSubtitle(TextMeshProUGUI source, GameObject row)
    {
        GameObject? go = null;
        try
        {
            go = UnityEngine.Object.Instantiate(source.gameObject, row.transform, false);
            go.name = BadgeObjectName;
            go.SetActive(false);
            // Only the text stays: a localization or layout component copied from the game's element would rewrite it.
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n is "RectTransform" or "CanvasRenderer" or "TextMeshProUGUI") continue;
                try { UnityEngine.Object.Destroy(c); }
                catch { /* stays inert */ }
            }

            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                try { UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject); }
                catch { /* */ }
            }

            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) { DestroyOwned(go); return null; }
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            // The line is short and generic (no mod names); an ellipsis keeps anything longer inside the box.
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            var layout = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            CopyRect(source.rectTransform, tmp.rectTransform);
            return tmp;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(ClientUpdates.LogPrefix + "the subtitle could not be cloned: " + e.Message);
            if (go != null) DestroyOwned(go);
            return null;
        }
    }

    private static void CopyRect(RectTransform from, RectTransform to)
    {
        to.anchorMin = from.anchorMin;
        to.anchorMax = from.anchorMax;
        to.pivot = from.pivot;
        to.anchoredPosition = from.anchoredPosition;
        to.sizeDelta = from.sizeDelta;
    }

    /// <summary>
    /// The MODS row takes CONTINUE's height and caption placement while the subtitle shows, so the list's layout group
    /// spaces and centers the column as it does for CONTINUE. The row as attached is remembered first.
    /// </summary>
    private static void GrowRow(GameObject row, MenuButton? continueButton, TextMeshProUGUI source)
    {
        var caption = _widget?.Caption;
        var layout = row.GetComponent<LayoutElement>();
        var rect = row.GetComponent<RectTransform>();
        if (caption == null || layout == null || rect == null) return;
        if (_rowBase == null || _rowBase.Row != row)
        {
            var c = caption.rectTransform;
            _rowBase = new RowBaseline
            {
                Row = row,
                MinHeight = layout.minHeight,
                PreferredHeight = layout.preferredHeight,
                SizeDelta = rect.sizeDelta,
                CapAnchorMin = c.anchorMin, CapAnchorMax = c.anchorMax, CapPivot = c.pivot, CapPos = c.anchoredPosition, CapSize = c.sizeDelta,
            };
        }

        RectTransform? refRow = null;
        RectTransform? refCaption = null;
        try
        {
            var reference = continueButton ?? _main?.MenuCollection?.MenuButtonPrefab;
            refRow = reference != null ? reference.GetComponent<RectTransform>() : null;
            refCaption = reference?.ButtonText != null ? reference.ButtonText.rectTransform : null;
        }
        catch { /* the row stays as attached */ }

        float height = refRow != null && refRow.rect.height > 1f ? refRow.rect.height : _rowBase.PreferredHeight;
        if (height < _rowBase.PreferredHeight) height = _rowBase.PreferredHeight;
        layout.minHeight = height;
        layout.preferredHeight = height;
        rect.sizeDelta = new Vector2(rect.sizeDelta.x, height);
        if (refCaption != null) CopyRect(refCaption, caption.rectTransform);
        if (_badge != null) CopyRect(source.rectTransform, _badge.rectTransform);
        _rowBase.Grown = true;

        // The list is top-aligned (UpperCenter), so a taller first row would push CONTINUE through EXIT down by the whole
        // growth. Part of it lifts the list instead (the way the attach lifts it for the MODS row itself), in the
        // proportion of the free space the title gives above MODS (under RISING) and below EXIT (above the icons), about
        // one to two (live 2026-10-09: an even split put MODS against RISING's rule), so both gaps shrink alike. The
        // canvas scales with the screen height, so the proportion holds at every resolution. Restored exactly on hide.
        try
        {
            var list = row.transform.parent != null ? row.transform.parent.GetComponent<RectTransform>() : null;
            float lift = (height - _rowBase.PreferredHeight) * SubtitleLiftShare;
            if (list != null && lift > 0.5f) LiftList(list, lift);
        }
        catch { /* the list stays where the attach put it */ }
    }

    // Review L2: the list's offsets as attached (the baseline every lift is computed from), kept apart from the row so a
    // row that died still gets its list back, and the offsets this badge set (so a list someone else moved meanwhile, such
    // as the entry's own restore on a drop, is left alone).
    private static RectTransform? _liftList;
    private static Vector2 _liftBaseMin, _liftBaseMax, _liftSetMin, _liftSetMax;
    private static float _liftApplied;

    /// <summary>Lift the list by <paramref name="lift"/> from its baseline (absolute, never stacked on an earlier lift).</summary>
    private static void LiftList(RectTransform list, float lift)
    {
        if (_liftList == null || !Alive(_liftList) || _liftList != list || _liftApplied <= 0f)
        {
            RestoreList();
            _liftList = list;
            _liftBaseMin = list.offsetMin;
            _liftBaseMax = list.offsetMax;
        }

        _liftSetMin = new Vector2(_liftBaseMin.x, _liftBaseMin.y + lift);
        _liftSetMax = new Vector2(_liftBaseMax.x, _liftBaseMax.y + lift);
        list.offsetMin = _liftSetMin;
        list.offsetMax = _liftSetMax;
        _liftApplied = lift;
    }

    /// <summary>The list back at its baseline, when it is still where this badge put it.</summary>
    private static void RestoreList()
    {
        var list = _liftList;
        float applied = _liftApplied;
        _liftApplied = 0f;
        if (list == null || applied <= 0f) return;
        try
        {
            if (!Alive(list)) return;
            if ((list.offsetMin - _liftSetMin).sqrMagnitude > 0.01f || (list.offsetMax - _liftSetMax).sqrMagnitude > 0.01f) return;
            list.offsetMin = _liftBaseMin;
            list.offsetMax = _liftBaseMax;
            LayoutRebuilder.ForceRebuildLayoutImmediate(list);
        }
        catch { /* the list went with its view */ }
    }

    /// <summary>The row exactly as attached (the game's own layout with no subtitle).</summary>
    private static void RestoreRow()
    {
        // Review L2: the list first, whether or not the row is still alive.
        RestoreList();
        var b = _rowBase;
        if (b == null || !b.Grown) return;
        b.Grown = false;
        try
        {
            if (!Alive(b.Row)) return;
            var layout = b.Row.GetComponent<LayoutElement>();
            var rect = b.Row.GetComponent<RectTransform>();
            if (layout != null)
            {
                layout.minHeight = b.MinHeight;
                layout.preferredHeight = b.PreferredHeight;
            }

            if (rect != null) rect.sizeDelta = b.SizeDelta;
            var caption = _widget?.Caption;
            if (caption != null)
            {
                var c = caption.rectTransform;
                c.anchorMin = b.CapAnchorMin;
                c.anchorMax = b.CapAnchorMax;
                c.pivot = b.CapPivot;
                c.anchoredPosition = b.CapPos;
                c.sizeDelta = b.CapSize;
            }

            RebuildList(b.Row);
        }
        catch { /* the entry is being rebuilt */ }
    }

    private static void RebuildList(GameObject row)
    {
        try
        {
            var parent = row.transform.parent != null ? row.transform.parent.GetComponent<RectTransform>() : null;
            if (parent != null) LayoutRebuilder.ForceRebuildLayoutImmediate(parent);
        }
        catch { /* the next layout pass */ }
    }

    private static void HideBadge()
    {
        if (_badge != null)
        {
            try
            {
                if (Alive(_badge.gameObject) && _badge.gameObject.activeSelf) _badge.gameObject.SetActive(false);
            }
            catch { _badge = null; }
        }

        _badgeShown = "";
        RestoreRow();
    }

    private static void DestroyBadge()
    {
        RestoreRow();
        var old = _badge;
        _badge = null;
        _badgeShown = "";
        _rowBase = null;
        if (old == null) return;
        try
        {
            if (Alive(old.gameObject)) DestroyOwned(old.gameObject);
        }
        catch { /* gone with its parent */ }
    }

    private static Dictionary<string, object?>? RectOf(RectTransform? r)
    {
        if (r == null) return null;
        // A managed Vector3[] cannot be handed to GetWorldCorners through this interop; two points do the same.
        var rect = r.rect;
        var min = r.TransformPoint(new Vector3(rect.xMin, rect.yMin, 0f));
        var max = r.TransformPoint(new Vector3(rect.xMax, rect.yMax, 0f));
        return new Dictionary<string, object?>
        {
            ["x"] = min.x,
            ["y"] = min.y,
            ["w"] = max.x - min.x,
            ["h"] = max.y - min.y,
        };
    }

    private static string AnchorsOf(RectTransform r) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "min({0},{1}) max({2},{3}) pivot({4},{5}) pos({6},{7}) size({8},{9})",
            r.anchorMin.x, r.anchorMin.y, r.anchorMax.x, r.anchorMax.y, r.pivot.x, r.pivot.y, r.anchoredPosition.x, r.anchoredPosition.y, r.sizeDelta.x, r.sizeDelta.y);

    private static string LayoutOf(GameObject go)
    {
        var le = go.GetComponent<LayoutElement>();
        var r = go.GetComponent<RectTransform>();
        return (le == null ? "no LayoutElement" : string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "le min {0} pref {1} flex {2} ignore {3}", le.minHeight, le.preferredHeight, le.flexibleHeight, le.ignoreLayout))
            + (r == null ? "" : " rect " + r.rect.width + "x" + r.rect.height + " " + AnchorsOf(r));
    }

    private static string Hex(Color c) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}",
            (int)Math.Round(c.r * 255), (int)Math.Round(c.g * 255), (int)Math.Round(c.b * 255), (int)Math.Round(c.a * 255));

    /// <summary>Bridge readback: the subtitle as drawn, its source, and the rows around it (screen rects).</summary>
    internal static Dictionary<string, object?> DescribeBadge()
    {
        var d = new Dictionary<string, object?>
        {
            ["site"] = DescribeOwned().Site,
            ["wanted"] = SubtitleText(),
            ["shown"] = false,
            ["text"] = "",
            ["primed"] = _updatesPrimed,
            ["source"] = _subtitleSource,
            ["dot"] = SubtitleDot,
            ["rowGrown"] = _rowBase?.Grown ?? false,
            ["listLift"] = _liftApplied,
            ["autoTick"] = _lastAuto,
        };
        try
        {
            var b = _badge;
            if (b != null && Alive(b.gameObject))
            {
                d["shown"] = b.gameObject.activeInHierarchy;
                d["text"] = b.text;
                d["object"] = b.gameObject.name;
                d["parent"] = b.transform.parent != null ? b.transform.parent.name : null;
                d["rect"] = RectOf(b.rectTransform);
                d["fontSize"] = b.fontSize;
                d["characterSpacing"] = b.characterSpacing;
                d["color"] = Hex(b.color);
            }

            var caption = _widget?.Caption;
            if (caption != null)
            {
                d["captionText"] = caption.text;
                d["captionRect"] = RectOf(caption.rectTransform);
                d["rowRect"] = RectOf(_widget?.Root?.GetComponent<RectTransform>());
                d["captionAnchors"] = AnchorsOf(caption.rectTransform);
                if (_widget?.Root != null) d["rowLayout"] = LayoutOf(_widget.Root);
            }

            var rows = new List<object?>();
            var list = _main?.MenuCollection?.InstancedButtonsNoDivider;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                {
                    var mb = list[i];
                    if (!Alive(mb) || !mb.gameObject.activeInHierarchy) continue;
                    var extra = mb.AdditionalText;
                    try
                    {
                    rows.Add(new Dictionary<string, object?>
                    {
                        ["name"] = mb.gameObject.name,
                        ["caption"] = mb.ButtonText != null ? mb.ButtonText.text : null,
                        ["row"] = RectOf(mb.GetComponent<RectTransform>()),
                        ["captionRect"] = mb.ButtonText != null ? RectOf(mb.ButtonText.rectTransform) : null,
                        ["subtitle"] = extra != null && extra.gameObject.activeInHierarchy ? extra.text : null,
                        ["subtitleRect"] = extra != null && extra.gameObject.activeInHierarchy ? RectOf(extra.rectTransform) : null,
                        ["subtitleSize"] = extra != null ? extra.fontSize : null,
                        ["subtitleSpacing"] = extra != null ? extra.characterSpacing : null,
                        ["subtitleColor"] = extra != null ? Hex(extra.color) : null,
                        ["subtitleStyle"] = extra != null ? (int)extra.fontStyle : null,
                        ["subtitleAlign"] = extra != null ? (int)extra.alignment : null,
                        ["subtitleAnchors"] = extra != null ? AnchorsOf(extra.rectTransform) : null,
                        ["captionAnchors"] = mb.ButtonText != null ? AnchorsOf(mb.ButtonText.rectTransform) : null,
                        ["rowLayout"] = LayoutOf(mb.gameObject),
                    });
                    }
                    catch (Exception e) { rows.Add(mb.gameObject.name + ": " + e.Message); }
                }
            d["menuRows"] = rows;
            var parent = _widget?.Root?.transform.parent;
            if (parent != null)
            {
                d["listRect"] = RectOf(parent.GetComponent<RectTransform>());
                var vlg = parent.GetComponent<VerticalLayoutGroup>();
                if (vlg != null)
                    d["listGroup"] = new Dictionary<string, object?>
                    {
                        ["spacing"] = vlg.spacing,
                        ["childAlignment"] = vlg.childAlignment.ToString(),
                        ["controlHeight"] = vlg.childControlHeight,
                        ["forceExpandHeight"] = vlg.childForceExpandHeight,
                        ["padTop"] = vlg.padding.top,
                        ["padBottom"] = vlg.padding.bottom,
                    };
            }
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }
}
#endif
