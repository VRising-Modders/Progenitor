#if !PROGENITOR_TESTS
using System;
using ProjectM.UI;
using TMPro;
using Unity.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// BUILDSEARCH2: a breadcrumb line in the build menu's hover tooltip, "Category > Tag" for a cell and
/// "Category > Tag > Group" for an item inside a group, so a player learns where an item lives whether
/// or not they searched for it. On by default (<c>[BuildSearch] Breadcrumbs</c>).
///
/// <para><b>Where it goes.</b> The tooltip is <c>BuildMenu.FakeTooltip</c>; <c>BuildMenuMapper.UpdateTooltip</c>
/// fills it every frame from the hovered cell (<c>StructureSelectionGroup.HoveredEntry</c>) and forces its
/// spare text lines off (checked in the game's native code, 2026-10-04). The crumb
/// is a Progenitor-owned label placed right after <c>TooltipHeader.SubHeader</c> (the "Group" / "Floor"
/// line), written from the same after-mapper hook as the search, so it is never a frame behind the
/// tooltip. The hovered GUID is the cell's <c>TooltipPrefabGuid</c> when set, else its <c>PrefabGuid</c>,
/// the same choice <c>UpdateTooltip</c> makes; it is placed through <see cref="BuildMenuIndex"/>.</para>
/// </summary>
internal static class BuildMenuCrumb
{
    internal const string LabelName = "progenitor-build-crumb";

        private static readonly Color CrumbColor = new(0x8F / 255f, 0xAF / 255f, 0xCB / 255f, 1f);

    /// <summary>
    /// The crumb is secondary to the card: a muted steel blue (#8FAFCB) at 85% of the subtitle's size
    /// (the user's pick of three mock-ups, 2026-10-04).
    /// </summary>
    internal const float SizeOfSubtitle = 0.85f;

    private static TextMeshProUGUI? _label;
    private static TooltipHeader? _header;

    /// <summary>Retained for <c>GetWorldCorners</c>: a managed array would be copied in and never read back.</summary>
    private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>? _corners;
    private static string _text = "";
    private static int _hoveredGuid;

    // The last crumb, so an unchanged hover costs no index lookup or string build.
    private static int _cachedGuid;
    private static int _cachedCategory = -1;
    private static int _cachedTag = -1;
    private static string _cachedText = "";

    internal static string Text => _text;
    internal static int HoveredGuid => _hoveredGuid;
    internal static bool Shown => _label != null && Alive(_label) && SafeActive(_label.gameObject);

    internal static void Update(World world, BuildMenuMapper mapper, BuildMenu menu, BuildMenu_StructureSelectionGroup group)
    {
        var tooltip = menu.FakeTooltip;
        if (!Alive(tooltip) || !tooltip.gameObject.activeInHierarchy)
        {
            Hide();
            return;
        }

        var hovered = group.HoveredEntry;
        if (!Alive(hovered))
        {
            Hide();
            return;
        }

        int guid = hovered.TooltipPrefabGuid.GuidHash;
        if (guid == 0) guid = hovered.PrefabGuid.GuidHash;
        _hoveredGuid = guid;

        int category = mapper._SelectedCategoryIndex;
        int tag = mapper._SelectedTagIndex;
        string text;
        if (guid == _cachedGuid && category == _cachedCategory && tag == _cachedTag && _cachedText.Length > 0)
        {
            text = _cachedText;
        }
        else
        {
            var index = BuildMenuIndex.For(world, mapper);
            text = index == null ? "" : index.CrumbFor(guid, category, tag);
            _cachedGuid = guid;
            _cachedCategory = category;
            _cachedTag = tag;
            _cachedText = text;
        }
        if (text.Length == 0)
        {
            Hide();
            return;
        }

        var label = EnsureLabel(tooltip.TooltipHeader);
        if (label == null) return;
        if (!string.Equals(_text, text, StringComparison.Ordinal))
        {
            label.text = text;
            _text = text;
        }

        if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
        AlignWithTitle(label, tooltip.TooltipHeader);
    }

    /// <summary>
    /// Start the crumb where the title's text starts. The tooltip's layout puts the row at the card's own
    /// left edge (no padding; live 2026-10-04 the title sat 20 units in), so the row's left margin is the
    /// distance from the row's left edge to the title's, measured in world space and converted to the
    /// row's units. Written only when it changes.
    /// </summary>
    private static void AlignWithTitle(TextMeshProUGUI label, TooltipHeader? header)
    {
        try
        {
            var title = Alive(header) && Alive(header!.Header) ? header.Header.Text : null;
            if (!Alive(title)) return;
            _corners ??= new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
            title!.rectTransform.GetWorldCorners(_corners);
            float titleLeft = _corners[0].x;
            label.rectTransform.GetWorldCorners(_corners);
            float rowLeft = _corners[0].x;
            float scale = label.transform.lossyScale.x;
            if (scale <= 0f) return;
            float inset = (titleLeft - rowLeft) / scale;
            if (inset < 0f || inset > 200f) return;
            var m = label.margin;
            if (Mathf.Abs(m.x - inset) > 0.5f) label.margin = new Vector4(inset, m.y, inset, m.w);
        }
        catch { /* the crumb keeps its last margin */ }
    }

    internal static void Hide()
    {
        _text = "";
        _hoveredGuid = 0;
        if (_label != null && Alive(_label) && SafeActive(_label.gameObject)) _label.gameObject.SetActive(false);
    }

    internal static void Release()
    {
        if (_label != null && Alive(_label))
        {
            try { UnityEngine.Object.Destroy(_label.gameObject); } catch { /* */ }
        }

        _label = null;
        _header = null;
        _cachedGuid = 0;
        _cachedText = "";
        _text = "";
        _hoveredGuid = 0;
    }

    private static TextMeshProUGUI? EnsureLabel(TooltipHeader? header)
    {
        if (!Alive(header)) return null;
        if (_label != null && Alive(_label) && ReferenceEquals(_header, header)) return _label;
        Release();

        // Its own row right under the header, as wide as the tooltip, so a long crumb never runs under the
        // header's icon (user, 2026-10-04: option 2). The row is the next sibling of the header in the
        // header's parent, which lays the tooltip out top to bottom (header, description, costs).
        var sub = header!.SubHeader;
        var subText = Alive(sub) ? sub.Text : null;
        var title = Alive(header.Header) ? header.Header.Text : null;
        var headerRoot = header.transform;
        var parent = headerRoot.parent;
        if (parent == null) return null;

        var go = new GameObject(LabelName);
        go.SetActive(false);
        go.transform.SetParent(parent, false);
        var rt = go.AddComponent<RectTransform>();
        var hrt = headerRoot.GetComponent<RectTransform>();
        if (hrt != null)
        {
            // Same horizontal span as the header; height from the text (the parent's layout places it).
            rt.anchorMin = new Vector2(hrt.anchorMin.x, 1f);
            rt.anchorMax = new Vector2(hrt.anchorMax.x, 1f);
            rt.pivot = new Vector2(hrt.pivot.x, 1f);
            rt.sizeDelta = new Vector2(hrt.sizeDelta.x, 0f);
        }

        var tmp = go.AddComponent<TextMeshProUGUI>();
        var style = Alive(subText) ? subText : title;
        if (Alive(style))
        {
            // The subtitle's font and size, so the line reads as part of the tooltip.
            try { tmp.font = style!.font; } catch { /* */ }
            try { tmp.fontSharedMaterial = style!.fontSharedMaterial; } catch { /* */ }
            try { tmp.fontSize = style!.fontSize * SizeOfSubtitle; } catch { /* */ }
        }
        else
        {
            UnityUiRuntime.ApplyFrameworkFont(tmp);
            tmp.fontSize = 16f * SizeOfSubtitle;
        }

        tmp.alignment = TextAlignmentOptions.TopLeft;
        tmp.color = CrumbColor;
        tmp.raycastTarget = false;
        tmp.enableWordWrapping = true;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.text = "";
        // The tooltip's layout group already insets its rows to the title's left edge (live 2026-10-04: a
        // measured title inset added on top put the crumb about 44 px right of the title).

        // In a layout group the text's own preferred height sizes the row; otherwise size it here.
        if (parent.GetComponent<LayoutGroup>() == null)
        {
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }
        try { go.transform.SetSiblingIndex(headerRoot.GetSiblingIndex() + 1); } catch { /* */ }
        _label = tmp;
        _header = header;
        return tmp;
    }

    internal static string Describe()
    {
        string parent = "";
        try { parent = _label != null && Alive(_label) ? _label.transform.parent.name : ""; } catch { /* */ }
        bool layout = false;
        try { layout = _label != null && Alive(_label) && _label.transform.parent.GetComponent<LayoutGroup>() != null; } catch { /* */ }
        return "parent=" + parent + " layoutGroup=" + layout;
    }

    private static bool Alive(UnityEngine.Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static bool SafeActive(GameObject go)
    {
        try { return go.activeSelf; }
        catch { return false; }
    }
}
#endif
