#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// The Unity half of two row features of the P6 sites: a row's indent (<see cref="VanillaRow.Indent"/>), whose step
/// is measured from the game's own requirement row, and the key-hint row (<see cref="VanillaRow.KeyHint"/>), a copy
/// of the game's own key-hint entry drawn with the game's glyph for the bound key.
/// </summary>
internal sealed partial class UnityUiRuntime
{
    // ------------------------------------------------------------------ indent

    /// <summary>
    /// The tooltip whose layout a site's rows are measured against: the tooltip site's own, else the inventory half's
    /// (the panels clone their rows from that tooltip's description).
    /// </summary>
    private static FakeTooltip? MeasureTooltipOf(VanillaUiSite site)
    {
        try
        {
            if (!PanelViews.TryGetValue(site, out var view) || view == null) return null;
            if (Alive(view.Tooltip)) return view.Tooltip;
            var inv = view.Inventory;
            return Alive(inv) && Alive(inv!.FakeTooltip) ? inv.FakeTooltip : null;
        }
        catch { return null; }
    }

    private static readonly Dictionary<IntPtr, float> IndentSteps = new();

    /// <summary>
    /// One indent level for <paramref name="site"/>'s rows: the width a requirement line gives its icon before its
    /// text, read from the game's first requirement row (<c>RecipeRequiredItems.RequiredItemsList[0]</c>, a
    /// horizontal group): its left padding, the icon frame's width (its layout element's preferred width, else its
    /// rect) and the group's spacing. 32 + 8 on 1.1.13.0. Read once per tooltip. When the tooltip has no requirement
    /// row, two and a half times the row font's size (40 at the game's 16) stands in. 0 when nothing can be read.
    /// </summary>
    private static float IndentStepFor(VanillaUiSite site, float fontSize)
    {
        var tooltip = MeasureTooltipOf(site);
        if (tooltip != null)
        {
            try
            {
                if (IndentSteps.TryGetValue(tooltip.Pointer, out float known)) return known;
                float step = MeasureRequirementIconStep(tooltip);
                if (step > 0f)
                {
                    IndentSteps[tooltip.Pointer] = step;
                    return step;
                }
            }
            catch { /* the fallback below */ }
        }

        return fontSize > 1f ? fontSize * 2.5f : 0f;
    }

    /// <summary>The first requirement row's padding + icon frame width + spacing, or 0 when it cannot be read.</summary>
    private static float MeasureRequirementIconStep(FakeTooltip tooltip)
    {
        var block = tooltip.RecipeRequiredItems;
        if (!Alive(block)) return 0f;
        var list = block.RequiredItemsList;
        if (list == null || list.Count == 0) return 0f;
        var entry = list[0];
        if (!Alive(entry)) return 0f;
        var row = entry.transform;
        var group = row.GetComponent<HorizontalLayoutGroup>();
        var icon = entry.Icon;
        if (group == null || !Alive(icon)) return 0f;

        // The icon's frame: the row's direct child that holds the icon image.
        Transform? frame = icon.transform;
        for (int depth = 0; frame != null && depth < 6 && (frame.parent == null || frame.parent.Pointer != row.Pointer); depth++)
            frame = frame.parent;
        if (frame == null || frame.parent == null || frame.parent.Pointer != row.Pointer) return 0f;

        float width = 0f;
        var element = frame.GetComponent<LayoutElement>();
        if (element != null && element.enabled) width = Math.Max(element.preferredWidth, element.minWidth);
        if (width <= 0f)
        {
            var rt = frame.TryCast<RectTransform>();
            if (rt != null) width = rt.rect.width > 0f ? rt.rect.width : rt.sizeDelta.x;
        }

        if (width <= 0f) return 0f;
        float left = group.padding != null ? group.padding.left : 0f;
        return left + width + group.spacing;
    }

    /// <summary>The indent step <c>ui_read panels</c> reports for a site (0 when nothing is measured yet).</summary>
    internal static float IndentStepReadback(VanillaUiSite site)
    {
        float font = 0f;
        try
        {
            if (PanelViews.TryGetValue(site, out var view) && view != null && Alive(view.BodyDonor)) font = view.BodyDonor!.fontSize;
        }
        catch { font = 0f; }
        return IndentStepFor(site, font);
    }

    public void IndentAttachRow(IRuntimeWidget row, int level)
    {
        try
        {
            if (row is PanelRowWidget text)
            {
                float step = level > 0 ? IndentStepFor(text.Site, text.FontSize) : 0f;
                text.SetIndent(level * step);
            }
            else if (row is KeyHintRowWidget hint)
            {
                float step = level > 0 ? IndentStepFor(hint.Site, hint.FontSize) : 0f;
                hint.SetIndent(level * step);
            }
        }
        catch { /* the row keeps its last indent */ }
    }

    // ------------------------------------------------------------------ key hints

    public bool ControllerGlyphsActive => MapShareHint.ControllerActive();

    internal const string KeyHintRowSuffix = "-keyhint";

    /// <summary>
    /// The game's key-hint entry to copy for <paramref name="site"/>: the <c>InputContextEntryPrefab</c> of an
    /// <c>InputContextsCollection</c> in the site's tooltip (every <c>FakeTooltip</c> has one, <c>InputContexts</c> at
    /// the end of its text column), else one under the window's inventory half, else a live entry of such a row,
    /// else the full-screen menu's hint row (the row the world map shows).
    /// </summary>
    private static readonly Dictionary<VanillaUiSite, InputContextEntry> KeyHintDonors = new();

    /// <summary>The key-hint donor for <paramref name="site"/>, found once and kept while it is alive (<see cref="FindKeyHintDonor"/>).</summary>
    private static InputContextEntry? KeyHintDonorFor(VanillaUiSite site)
    {
        if (KeyHintDonors.TryGetValue(site, out var known) && Alive(known)) return known;
        KeyHintDonors.Remove(site);
        var found = FindKeyHintDonor(site);
        if (found != null) KeyHintDonors[site] = found;
        return found;
    }

    /// <summary>Core unload: forget the key-hint donors and the measured indent steps.</summary>
    internal static void ClearRowCaches()
    {
        KeyHintDonors.Clear();
        IndentSteps.Clear();
    }

    private static InputContextEntry? FindKeyHintDonor(VanillaUiSite site)
    {
        var roots = new List<Component?>();
        roots.Add(MeasureTooltipOf(site));
        try { if (PanelViews.TryGetValue(site, out var view) && view != null) roots.Add(view.Inventory); }
        catch { /* */ }
        foreach (var root in roots)
        {
            if (!Alive(root)) continue;
            try
            {
                var rows = root!.GetComponentsInChildren<InputContextsCollection>(true);
                if (rows == null) continue;
                foreach (var row in rows)
                    if (Alive(row) && Alive(row.InputContextEntryPrefab)) return row.InputContextEntryPrefab;
                foreach (var row in rows)
                {
                    var live = FirstEntry(row);
                    if (live != null) return live;
                }
            }
            catch { /* the next root */ }
        }

        try
        {
            if (ModsWorkspaceChrome.TryGetFullscreenMenu(out var menu, out _) && menu != null && Alive(menu.InputContexts))
            {
                var row = menu.InputContexts;
                if (Alive(row.InputContextEntryPrefab)) return row.InputContextEntryPrefab;
                return FirstEntry(row);
            }
        }
        catch { /* */ }

        return null;
    }

    private static InputContextEntry? FirstEntry(InputContextsCollection? row)
    {
        try
        {
            if (!Alive(row) || !Alive(row!.ContextsParent)) return null;
            var parent = row.ContextsParent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var e = parent.GetChild(i).GetComponent<InputContextEntry>();
                if (Alive(e)) return e;
            }
        }
        catch { /* */ }

        return null;
    }

    public IRuntimeWidget? CloneKeyHintRow(VanillaUiSite site, IRuntimeWidget container)
    {
        if (container is not PanelContainerWidget host || !host.Alive) return null;
        if (host.Anchor == VanillaPanelAnchor.BesideRename || host.Anchor == VanillaPanelAnchor.BesideSort) return null;
        var donor = KeyHintDonorFor(site);
        if (donor == null)
        {
            if (VanillaSiteHealth.LogOnce(site, "key hint donor"))
                Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " key hint not drawn: no key-hint entry of the game to copy (InputContextsCollection.InputContextEntryPrefab).");
            return null;
        }

        GameObject? holder = null;
        GameObject? go = null;
        bool done = false;
        try
        {
            // Under an inactive holder none of the copy's components wakes up before it is stripped.
            holder = new GameObject(host.Name + KeyHintRowSuffix + "-holder");
            holder.SetActive(false);
            go = UnityEngine.Object.Instantiate(donor.gameObject, holder.transform)?.TryCast<GameObject>();
            if (go == null) return null;
            go.name = host.Name + KeyHintRowSuffix;

            var entry = go.GetComponent<InputContextEntry>();
            var interaction = HintTmp(entry?.InteractionText);
            var action = HintTmp(entry?.ActionText);
            var keyText = HintTmp(entry?.KeybindText);
            var comboText = HintTmp(entry?.ComboText);
            var image = entry != null ? entry.KeybindImage : null;
            var comboImage = entry != null ? entry.KeybindImageCombo : null;
            if (action == null || (image == null && keyText == null))
            {
                if (VanillaSiteHealth.LogOnce(site, "key hint parts"))
                    Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " key hint not drawn: the copied key-hint entry has no label or key.");
                return null;
            }

            foreach (var localized in go.GetComponentsInChildren<LocalizedText>(true))
            {
                try { UnityEngine.Object.DestroyImmediate(localized); } catch { /* */ }
            }

            if (entry != null)
            {
                try { UnityEngine.Object.DestroyImmediate(entry); } catch { /* */ }
            }

            // Nothing of the game's may drive the copy: behaviors other than text, image and layout are switched off,
            // and the root's own size fitter too (the container's column lays the row out).
            foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (b == null) continue;
                if (b.TryCast<Graphic>() != null || b.TryCast<LayoutGroup>() != null || b.TryCast<LayoutElement>() != null) continue;
                if (b.TryCast<ContentSizeFitter>() != null && b.gameObject.Pointer != go.Pointer) continue;
                try { b.enabled = false; } catch { /* */ }
            }

            foreach (var g in go.GetComponentsInChildren<Graphic>(true))
            {
                try { g.raycastTarget = false; } catch { /* */ }
            }

            try
            {
                var group = go.GetComponent<CanvasGroup>();
                if (group != null)
                {
                    group.alpha = 1f;
                    group.blocksRaycasts = false;
                    group.interactable = false;
                }
            }
            catch { /* */ }

            // The Hold word in the label's style (the prefab draws its interaction word heavier; Dye all's tip, live
            // 2026-10-08).
            if (interaction != null)
            {
                try
                {
                    interaction.font = action.font;
                    if (action.fontSharedMaterial != null) interaction.fontSharedMaterial = action.fontSharedMaterial;
                    interaction.fontSize = action.fontSize;
                    interaction.fontStyle = action.fontStyle;
                    interaction.color = action.color;
                    interaction.gameObject.SetActive(false);
                }
                catch { /* */ }
            }

            // A tooltip line reads from the left: the copy's own row group starts at its left edge (the game's hint
            // bars center the whole bar, not each entry).
            try
            {
                var rowGroup = go.GetComponent<HorizontalLayoutGroup>();
                if (rowGroup != null) rowGroup.childAlignment = TextAnchor.MiddleLeft;
            }
            catch { /* */ }

            go.transform.SetParent(host.Transform, false);
            go.transform.localScale = Vector3.one;
            go.SetActive(true);
            done = true;
            return new KeyHintRowWidget(go, site, image, comboImage, keyText, comboText, interaction, action);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(site) + " key hint clone threw: " + e.Message);
            return null;
        }
        finally
        {
            if (!done) TryDestroyImmediate(go);
            TryDestroyImmediate(holder);
        }
    }

    private static TextMeshProUGUI? HintTmp(LocalizedText? localized)
    {
        if (localized == null) return null;
        try { return localized.Text ?? localized.GetComponent<TextMeshProUGUI>(); }
        catch { return null; }
    }

    public void ApplyKeyHint(IRuntimeWidget row, VanillaKeyHintParts parts, int keysVersion)
    {
        if (row is KeyHintRowWidget hint) hint.Apply(parts, keysVersion);
    }

    public void PlaceAttachRow(IRuntimeWidget row, IRuntimeWidget? previous, IRuntimeWidget? next)
    {
        try
        {
            var t = RowTransform(row);
            if (t == null || t.parent == null) return;
            var p = RowTransform(previous);
            var n = RowTransform(next);
            if (p != null && p.parent != null && p.parent.Pointer == t.parent.Pointer)
            {
                int at = p.GetSiblingIndex();
                int now = t.GetSiblingIndex();
                int want = now > at ? at + 1 : at;
                if (now != want) t.SetSiblingIndex(want);
            }
            else if (n != null && n.parent != null && n.parent.Pointer == t.parent.Pointer)
            {
                int at = n.GetSiblingIndex();
                int now = t.GetSiblingIndex();
                int want = now > at ? at : at - 1;
                if (now != want) t.SetSiblingIndex(want);
            }
        }
        catch { /* the row stays where it is */ }
    }

    private static Transform? RowTransform(IRuntimeWidget? row) => row switch
    {
        PanelRowWidget text => text.RowTransform,
        KeyHintRowWidget hint => hint.RowTransform,
        _ => null,
    };

    /// <summary>
    /// The game's own glyph for one control path, for the device the player is on: <c>ControlsVisualMapping
    /// .GetInputVisualsMapping(path, controllerType, CurrentGamepadType, out text, out sprite)</c>, the table lookup
    /// the game's hint rows make, on the mapping its input system already loaded (null until the game drew a glyph).
    /// Null when the mapping or the sprite is missing; <paramref name="text"/> is the game's key text when it gives one.
    /// </summary>
    private static Sprite? GameGlyph(string path, bool gamepad, out string text)
    {
        text = "";
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var world = Worlds.Client;
            var system = world != null && world.IsCreated ? ProjectM.InputHelper.GetInputActionSystem() : null;
            var mapping = system?._ControlsVisualMapping;
            if (mapping == null) return null;
            mapping.GetInputVisualsMapping(path, gamepad ? ProjectM.ControllerType.Gamepad : ProjectM.ControllerType.KeyboardAndMouse,
                system!.CurrentGamepadType, out string t, out Sprite sprite);
            text = t ?? "";
            return Alive(sprite) ? sprite : null;
        }
        catch { return null; }
    }

    /// <summary>A key-hint row: a stripped copy of the game's key-hint entry.</summary>
    private sealed class KeyHintRowWidget : IRuntimeWidget, IReleaseVerdict, IKeyHintRowReadback
    {
        private GameObject? _go;
        private readonly Image? _image;
        private readonly Image? _comboImage;
        private readonly TextMeshProUGUI? _keyText;
        private readonly TextMeshProUGUI? _comboText;
        private readonly TextMeshProUGUI? _interaction;
        private readonly TextMeshProUGUI? _action;
        private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;
        private bool _drawn;
        private bool _complete;
        private int _version = int.MinValue;
        private int _retries;
        private LabelTone _tone = LabelTone.Normal;
        private Color? _labelColor;
        private int? _basePad;
        private float _indentPx;

        internal KeyHintRowWidget(
            GameObject go, VanillaUiSite site, Image? image, Image? comboImage, TextMeshProUGUI? keyText,
            TextMeshProUGUI? comboText, TextMeshProUGUI? interaction, TextMeshProUGUI? action)
        {
            _go = go;
            Site = site;
            _image = image;
            _comboImage = comboImage;
            _keyText = keyText;
            _comboText = comboText;
            _interaction = interaction;
            _action = action;
        }

        internal VanillaUiSite Site { get; }

        internal Transform? RowTransform => UnityUiRuntime.Alive(_go) ? _go!.transform : null;

        internal float FontSize
        {
            get { try { return _action != null ? _action.fontSize : 0f; } catch { return 0f; } }
        }

        public VanillaKeyHintParts Parts { get; private set; }

        public string[] Glyphs { get; private set; } = new string[0];

        /// <summary>
        /// Draw <paramref name="parts"/>: the Hold word, the glyphs (the game's sprite for each control path, a
        /// <c>+</c> between two) or, when a glyph cannot be had, the key as text on the entry's own key text; then the
        /// label. A repeat of a complete draw is no write; a draw that fell back to text is tried again (the game's
        /// glyph table loads with its first hint row), at most <see cref="VanillaKeyHint.MaxGlyphRetries"/> times until
        /// the parts or the keybinds' version change.
        /// </summary>
        internal void Apply(VanillaKeyHintParts parts, int keysVersion)
        {
            if (!Alive) return;
            if (!VanillaKeyHint.ShouldDraw(_drawn, _complete, Parts, parts, _version, keysVersion, _retries)) return;
            bool retry = _drawn && parts.Equals(Parts) && keysVersion == _version;
            _retries = retry ? _retries + 1 : 0;
            _version = keysVersion;
            try
            {
                if (_interaction != null)
                {
                    _interaction.text = parts.Prefix;
                    _interaction.gameObject.SetActive(parts.Prefix.Length > 0);
                }

                if (_action != null) _action.text = parts.Label;

                Sprite? first = null, second = null;
                string firstText = "", secondText = "";
                bool two = parts.SecondPath.Length > 0;
                if (parts.HasGlyphs && _image != null)
                {
                    first = GameGlyph(parts.FirstPath, parts.Gamepad, out firstText);
                    if (two) second = _comboImage != null ? GameGlyph(parts.SecondPath, parts.Gamepad, out secondText) : null;
                }

                bool glyphs = first != null && (!two || second != null);
                if (glyphs)
                {
                    _image!.sprite = first;
                    _image.gameObject.SetActive(true);
                    if (_comboImage != null)
                    {
                        if (two) _comboImage.sprite = second;
                        _comboImage.gameObject.SetActive(two);
                    }

                    if (_comboText != null)
                    {
                        _comboText.text = "+";
                        _comboText.gameObject.SetActive(two);
                    }

                    if (_keyText != null) _keyText.gameObject.SetActive(false);
                    Glyphs = two
                        ? new[] { VanillaKeyHint.GlyphWord(first!.name, ""), VanillaKeyHint.GlyphWord(second!.name, "") }
                        : new[] { VanillaKeyHint.GlyphWord(first!.name, "") };
                }
                else
                {
                    // The key's name on the entry's own key text (its cap), the glyph art off.
                    if (_image != null) _image.gameObject.SetActive(false);
                    if (_comboImage != null) _comboImage.gameObject.SetActive(false);
                    if (_comboText != null) _comboText.gameObject.SetActive(false);
                    // The game's own key text when it gave one for every path, else the chord's words.
                    string shown = VanillaKeyHint.FallbackText(parts, firstText, secondText);
                    if (_keyText != null)
                    {
                        _keyText.text = shown;
                        _keyText.gameObject.SetActive(shown.Length > 0);
                    }
                    else if (_action != null && shown.Length > 0)
                    {
                        _action.text = "[" + shown + "] " + parts.Label;
                    }

                    Glyphs = shown.Length > 0 ? new[] { VanillaKeyHint.GlyphWord(null, shown) } : new string[0];
                }

                Parts = parts;
                _drawn = true;
                _complete = glyphs || !parts.HasGlyphs;
            }
            catch { /* the next refresh draws it again */ }
        }

        internal void SetTone(LabelTone tone)
        {
            if (tone == _tone || !Alive || _action == null) return;
            try
            {
                _labelColor ??= _action.color;
                _action.color = tone == LabelTone.Normal ? _labelColor.Value : ToColor(MenuPalette.LabelColor(tone));
                _tone = tone;
            }
            catch { /* */ }
        }

        /// <summary>Inset the entry by <paramref name="px"/>: its own row group's left padding plus the indent.</summary>
        internal void SetIndent(float px)
        {
            if (!Alive || Mathf.Abs(px - _indentPx) < 0.25f) return;
            try
            {
                var group = _go!.GetComponent<HorizontalOrVerticalLayoutGroup>();
                if (group == null || group.padding == null) return;
                _basePad ??= group.padding.left;
                var pad = group.padding;
                pad.left = _basePad.Value + Mathf.RoundToInt(px);
                group.padding = pad;
                _indentPx = px;
                var rt = _go.GetComponent<RectTransform>();
                if (rt != null) LayoutRebuilder.MarkLayoutForRebuild(rt);
            }
            catch { /* */ }
        }

        public UiDestroyVerdict LastRelease => _verdict;

        public string Text
        {
            get => Parts.Label;
            set { }
        }

        public bool Visible
        {
            get { try { return UnityUiRuntime.Alive(_go) && _go!.activeSelf; } catch { return false; } }
            set { try { if (UnityUiRuntime.Alive(_go)) _go!.SetActive(value); } catch { /* */ } }
        }

        public bool BoolValue { get => false; set { } }

        public float FloatValue { get => 0f; set { } }

        public string StringValue { get => Text; set { } }

        public bool Alive => UnityUiRuntime.Alive(_go);

        public object? ClickEvent => null;

        public object? FloatEvent => null;

        public object? BoolEvent => null;

        public object? TextEvent => null;

        public object? PointerEnterEvent => null;

        public object? PointerExitEvent => null;

        public void Release()
        {
            var go = _go;
            if (go == null || !UnityUiRuntime.Alive(go))
            {
                _go = null;
                _verdict = UiDestroyVerdict.Destroyed;
                return;
            }

            _verdict = DestroyWithVerdict(go, immediate: true);
            if (_verdict == UiDestroyVerdict.Destroyed) _go = null;
        }
    }
}
#endif
