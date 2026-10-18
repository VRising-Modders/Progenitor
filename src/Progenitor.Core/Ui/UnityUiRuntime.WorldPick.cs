#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7 item 3: the pick-in-world prompt band (<see cref="WorldPickBackend"/>). The band is an ordinary
/// kit panel — so it shows, hides and keeps Bootstrap forwarding Escape like every other panel —
/// restyled after each show.
///
/// <para><b>Click-through.</b> The panel's own frame rect is zero-sized just below the bottom screen edge,
/// with its frame image switched off; the visible band is a child placed above the action bar. The hover
/// gate measures the frame rect, so the pointer is never "over" the band and gameplay input is never
/// blocked by it, and every band graphic has <c>raycastTarget = false</c>, so attack clicks pass through.</para>
///
/// <para><b>Look</b> (<see cref="UiWorldPickBandLook"/>): the framework notice band, at least 980 units wide and centered:
/// a black ground at 0.84 between 1-unit gold rules (<see cref="MenuPalette.Gold"/>) on its top and bottom edges, all three
/// fading to clear over the outer sixth at each end (one drawn fade texture). The kit's body font, and the keys on the
/// game's own key cap (<see cref="KeybindArtDonors.TryKeyCap"/>, the Bindings menu's key button; a bordered kit cap when
/// it was not borrowed). The caller's prompt is escaped
/// (<see cref="UiRichText.Literal"/>) and ends in an ellipsis past <see cref="UiWorldPickCopy.MaxPromptWidth"/>.</para>
/// </summary>
internal sealed partial class UnityUiRuntime : IWorldPickBandRuntime
{
    /// <summary>Name of the band's visible child (one per band panel).</summary>
    internal const string WorldPickBandName = UiOwnership.Prefix + "world-pick-band";

    private const float BandHeight = UiWorldPickBandLook.Height;
    private const float HintRowHeight = UiWorldPickBandLook.HintRowHeight;
    private const float BandBottom = UiWorldPickCopy.BandBottom;
    private const float FrameBelowEdge = 8f;
    private const float PromptSize = 24f;
    private const float WordSize = 21f;
    private const float KeySize = 18f;
    private const float CapHeight = 30f;
    private const float CapPadX = 9f;
    private const float GapAfterPrompt = 30f;
    private const float GapAroundSeparator = 10f;
    private const float GapCapWord = 8f;
    private const float GapBetweenKeys = 24f;
    private static Sprite? _bandFade;
    private static Texture2D? _bandFadeTexture;
    private static bool _bandFadeTeardown;

    public void StyleWorldPickBand(IRuntimePanel panel, string prompt, string interactKey, string cancelKey, string cancelWord = UiWorldPickCopy.CancelWord,
        string pickWord = UiWorldPickCopy.PickWord, string count = "", string modifierHint = "")
    {
        if (panel is not UnityPanel host || !host.Alive) return;
        try
        {
            var frame = host.GameObject;
            var frameRt = frame.GetComponent<RectTransform>();
            if (frameRt == null) return;
            // The key cap and the rule are the Bindings menu's; borrow them now if the Keybinds tab never has.
            try { KeybindArtDonors.Prepare(); } catch { /* the kit cap and a flat rule */ }
            try { host.Title?.SetActive(false); } catch { /* */ }
            try { host.ViewportObject?.SetActive(false); } catch { /* */ }
            // The band never scrolls: its frame is zero-sized (below), so a live ScrollRect over the hidden viewport would
            // judge the empty content as overflowing and show its bar ("SHOWN while the last verdict was (fits)"). Turn
            // the ScrollRect off and its bar with it; the panel's read-back then finds the bar hidden, as the layout says.
            try
            {
                var scroll = host.Scroll;
                if (scroll != null)
                {
                    var bar = scroll.verticalScrollbar;
                    scroll.enabled = false;
                    if (bar != null && bar.gameObject.activeSelf) bar.gameObject.SetActive(false);
                }
            }
            catch { /* the bar is a diagnostic's concern only: the band draws the same */ }

            // The frame: zero-sized below the screen edge, its own image off (see the class remarks).
            var frameImage = frame.GetComponent<Image>();
            if (frameImage != null)
            {
                try { frameImage.raycastTarget = false; } catch { /* */ }
                try { frameImage.enabled = false; } catch { /* */ }
            }

            frameRt.anchorMin = new Vector2(0.5f, 0f);
            frameRt.anchorMax = new Vector2(0.5f, 0f);
            frameRt.pivot = new Vector2(0.5f, 0f);
            frameRt.sizeDelta = Vector2.zero;
            frameRt.anchoredPosition = new Vector2(0f, -FrameBelowEdge);

            var old = frame.transform.Find(WorldPickBandName);
            if (old != null) TryDestroyImmediate(old.gameObject);
            var band = new GameObject(WorldPickBandName);
            band.transform.SetParent(frame.transform, false);
            var bandRt = band.GetComponent<RectTransform>() ?? band.AddComponent<RectTransform>();

            // The notice band's look: black fading to clear at both ends (UiWorldPickBandLook).
            var fade = BandFade();
            var ground = TryAdd<Image>(band);
            if (ground != null)
            {
                try { if (fade != null) ground.sprite = fade; } catch { /* */ }
                try { ground.color = new Color(0f, 0f, 0f, UiWorldPickBandLook.GroundAlpha); } catch { /* */ }
                try { ground.raycastTarget = false; } catch { /* */ }
            }

            float x = 0f;
            // Each part with the gap that follows it, so an optional part (a repeating pick's count) needs no index table.
            var parts = new List<(RectTransform Rt, float Width, float GapAfter)>();
            void Add(GameObject? go, float width, float gapAfter)
            {
                if (go == null) return;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return;
                parts.Add((rt, width, gapAfter));
                x += width + gapAfter;
            }

            var text = ToColor(MenuPalette.Text);
            bool counted = !string.IsNullOrEmpty(count);
            Add(BandText(band, UiRichText.Literal(prompt), PromptSize, text, UiWorldPickCopy.MaxPromptWidth, out float promptWidth), promptWidth,
                counted ? GapAroundSeparator : GapAfterPrompt);
            if (counted)
            {
                // P7b: "Pick containers · 3 picked": the separator muted, the count in the gold.
                Add(BandText(band, UiWorldPickCopy.CountSeparator, PromptSize, ToColor(MenuPalette.Muted), 0f, out float sepWidth), sepWidth, GapAroundSeparator);
                Add(BandText(band, UiRichText.Literal(count), PromptSize, ToColor(MenuPalette.Gold), 0f, out float countWidth), countWidth, GapAfterPrompt);
            }

            Add(BandCap(band, interactKey, out float capA), capA, GapCapWord);
            Add(BandText(band, string.IsNullOrEmpty(pickWord) ? UiWorldPickCopy.PickWord : pickWord, WordSize, text, 0f, out float pickWidth), pickWidth, GapBetweenKeys);
            Add(BandCap(band, cancelKey, out float capB), capB, GapCapWord);
            Add(BandText(band, string.IsNullOrEmpty(cancelWord) ? UiWorldPickCopy.CancelWord : cancelWord, WordSize, text, 0f, out float cancelWidth), cancelWidth, 0f);

            // PICKMOD1: the modifier hint's own line under the first (the key on the game's cap, then its words, muted).
            var (hintKeys, hintWords) = UiWorldPickCopy.SplitHint(modifierHint, interactKey);
            int firstRow = parts.Count;
            float x1 = x;
            x = 0f;
            if (hintKeys.Length > 0) Add(BandCap(band, hintKeys, out float capH), capH, GapCapWord);
            if (hintWords.Length > 0)
                Add(BandText(band, UiRichText.Literal(hintWords), WordSize, ToColor(MenuPalette.Muted), UiWorldPickCopy.MaxPromptWidth, out float hintWidth), hintWidth, 0f);
            float x2 = x;
            bool twoRows = parts.Count > firstRow;
            float height = twoRows ? BandHeight + HintRowHeight : BandHeight;

            float width = UiWorldPickBandLook.Width(Mathf.Max(x1, x2));
            bandRt.anchorMin = new Vector2(0.5f, 0f);
            bandRt.anchorMax = new Vector2(0.5f, 0f);
            bandRt.pivot = new Vector2(0.5f, 0f);
            bandRt.sizeDelta = new Vector2(width, height);
            bandRt.anchoredPosition = new Vector2(0f, BandBottom + FrameBelowEdge);

            float cursor = -x1 / 2f;
            float rowY = twoRows ? HintRowHeight / 2f : 0f;
            for (int i = 0; i < parts.Count; i++)
            {
                // The second row is centered in the band's bottom HintRowHeight; the first keeps the top BandHeight.
                if (i == firstRow) { cursor = -x2 / 2f; rowY = -height / 2f + HintRowHeight / 2f; }
                var (rt, w, gap) = parts[i];
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(cursor, rowY);
                cursor += w + gap;
            }

            BandRule(band, width, top: true, fade);
            BandRule(band, width, top: false, fade);
            Plugin.Logger?.LogDiag("Ui: world pick band " + (int)width + "x" + (int)height + " keys=" + interactKey + "/" + cancelKey
                + (twoRows ? " hint=" + hintKeys + "/" + hintWords : ""));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: world pick band style threw: " + e.Message);
        }
    }

    /// <summary>One line of band text; <paramref name="maxWidth"/> above 0 caps it with an ellipsis.</summary>
    private static GameObject? BandText(GameObject parent, string text, float size, Color color, float maxWidth, out float width)
    {
        width = 0f;
        var go = new GameObject("text");
        go.transform.SetParent(parent.transform, false);
        var tmp = TryAdd<TextMeshProUGUI>(go);
        if (tmp == null) return null;
        try { tmp.text = text ?? ""; } catch { /* */ }
        try { tmp.fontSize = size; } catch { /* */ }
        try { tmp.enableAutoSizing = false; } catch { /* */ }
        try { tmp.fontStyle = FontStyles.Normal; } catch { /* */ }
        try { tmp.color = color; } catch { /* */ }
        try { tmp.raycastTarget = false; } catch { /* */ }
        try { tmp.enableWordWrapping = false; } catch { /* */ }
        try { tmp.overflowMode = maxWidth > 0f ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow; } catch { /* */ }
        try { tmp.alignment = TextAlignmentOptions.Left; } catch { /* */ }
        ApplyCachedFont(tmp);
        try { width = Mathf.Ceil(tmp.GetPreferredValues(tmp.text).x); } catch { width = (text ?? "").Length * size * 0.5f; }
        if (width <= 0f) width = (text ?? "").Length * size * 0.5f;
        if (maxWidth > 0f && width > maxWidth) width = maxWidth;
        var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, size * 1.4f);
        return go;
    }

    private static GameObject? BandCap(GameObject parent, string key, out float width)
    {
        width = 0f;
        var cap = new GameObject("key");
        cap.transform.SetParent(parent.transform, false);
        var image = TryAdd<Image>(cap);
        if (image != null)
        {
            if (KeybindArtDonors.TryKeyCap(out var sprite, out var type, out var tint) && sprite != null)
            {
                try { image.sprite = sprite; } catch { /* */ }
                try { image.type = type; } catch { /* */ }
                try { image.color = tint; } catch { /* */ }
            }
            else
            {
                // Kit cap: the Mods palette's bordered chip (the client badge's border on a dark fill).
                try { image.color = ToColor(MenuPalette.ButtonFill); } catch { /* */ }
                var outline = TryAdd<Outline>(cap);
                if (outline != null)
                {
                    try { outline.effectColor = ToColor(MenuPalette.ClientBorder); } catch { /* */ }
                    try { outline.effectDistance = new Vector2(1f, -1f); } catch { /* */ }
                }
            }

            try { image.raycastTarget = false; } catch { /* */ }
        }

        var label = BandText(cap, UiRichText.Literal(key), KeySize, ToColor(MenuPalette.ClientText), 0f, out float textWidth);
        width = Mathf.Max(CapHeight, textWidth + 2f * CapPadX);
        var rt = cap.GetComponent<RectTransform>() ?? cap.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, CapHeight);
        if (label != null)
        {
            var lrt = label.GetComponent<RectTransform>();
            if (lrt != null)
            {
                lrt.anchorMin = new Vector2(0.5f, 0.5f);
                lrt.anchorMax = new Vector2(0.5f, 0.5f);
                lrt.pivot = new Vector2(0.5f, 0.5f);
                lrt.anchoredPosition = Vector2.zero;
            }

            try { label.GetComponent<TextMeshProUGUI>()!.alignment = TextAlignmentOptions.Center; } catch { /* */ }
        }

        return cap;
    }

    /// <summary>A thin gold rule along the band's top or bottom edge, fading to clear at both ends like the ground.</summary>
    private static void BandRule(GameObject parent, float width, bool top, Sprite? fade)
    {
        var rule = new GameObject(top ? "rule-top" : "rule-bottom");
        rule.transform.SetParent(parent.transform, false);
        var image = TryAdd<Image>(rule);
        if (image == null) return;
        try { if (fade != null) image.sprite = fade; } catch { /* */ }
        try { image.color = ToColor(MenuPalette.Gold); } catch { /* */ }
        try { image.raycastTarget = false; } catch { /* */ }
        var rt = rule.GetComponent<RectTransform>() ?? rule.AddComponent<RectTransform>();
        float y = top ? 1f : 0f;
        rt.anchorMin = new Vector2(0.5f, y);
        rt.anchorMax = new Vector2(0.5f, y);
        rt.pivot = new Vector2(0.5f, y);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(width, UiWorldPickBandLook.RuleHeight);
    }

    /// <summary>The band's horizontal fade (one row of coverage, stretched), drawn once per Core generation; null when it cannot be.</summary>
    private static Sprite? BandFade()
    {
        if (_bandFade != null && Alive(_bandFade)) return _bandFade;
        _bandFade = HudSprite("world-pick-band-fade", UiWorldPickBandLook.FadeTextureWidth, 1, (x, _) => UiWorldPickBandLook.FadeAlpha(x), out _bandFadeTexture);
        if (_bandFade != null && !_bandFadeTeardown)
        {
            _bandFadeTeardown = true;
            UiWidgetLifetime.Register("world-pick-band-art", () =>
            {
                FreeHudAsset(_bandFade);
                FreeHudAsset(_bandFadeTexture);
                _bandFade = null;
                _bandFadeTexture = null;
                _bandFadeTeardown = false;
            });
        }

        return _bandFade;
    }
}
#endif
