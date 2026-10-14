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
/// <para><b>Look.</b> The Mods palette's pane ground (the dialog frame color, <see cref="DialogFrameColors"/>)
/// at reduced alpha, the game's gold category underline beneath it (the Bindings menu header rule,
/// <see cref="KeybindArtDonors.TryRule"/>), the kit's body font, and the keys on the game's own key cap
/// (<see cref="KeybindArtDonors.TryKeyCap"/>, the Bindings menu's key button). Either donor missing falls
/// back to a flat palette gold rule and a bordered kit cap. The caller's prompt is escaped
/// (<see cref="UiRichText.Literal"/>) and ends in an ellipsis past <see cref="UiWorldPickCopy.MaxPromptWidth"/>.</para>
/// </summary>
internal sealed partial class UnityUiRuntime : IWorldPickBandRuntime
{
    /// <summary>Name of the band's visible child (one per band panel).</summary>
    internal const string WorldPickBandName = UiOwnership.Prefix + "world-pick-band";

    private const float BandHeight = 50f;
    private const float BandBottom = UiWorldPickCopy.BandBottom;
    private const float FrameBelowEdge = 8f;
    private const float BandPadX = 28f;
    private const float PromptSize = 24f;
    private const float WordSize = 21f;
    private const float KeySize = 18f;
    private const float CapHeight = 30f;
    private const float CapPadX = 9f;
    private const float GapAfterPrompt = 30f;
    private const float GapAroundSeparator = 10f;
    private const float GapCapWord = 8f;
    private const float GapBetweenKeys = 24f;
    private const float RuleHeight = 2f;
    private const float GroundAlpha = 0.82f;

    public void StyleWorldPickBand(IRuntimePanel panel, string prompt, string interactKey, string cancelKey, string cancelWord = UiWorldPickCopy.CancelWord,
        string pickWord = UiWorldPickCopy.PickWord, string count = "")
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

            var ground = TryAdd<Image>(band);
            if (ground != null)
            {
                try { ground.color = ToColor(DialogFrameColors.Frame.WithAlpha(GroundAlpha)); } catch { /* */ }
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

            float width = Mathf.Ceil(x + 2f * BandPadX);
            bandRt.anchorMin = new Vector2(0.5f, 0f);
            bandRt.anchorMax = new Vector2(0.5f, 0f);
            bandRt.pivot = new Vector2(0.5f, 0f);
            bandRt.sizeDelta = new Vector2(width, BandHeight);
            bandRt.anchoredPosition = new Vector2(0f, BandBottom + FrameBelowEdge);

            float cursor = -x / 2f;
            for (int i = 0; i < parts.Count; i++)
            {
                var (rt, w, gap) = parts[i];
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(cursor, 0f);
                cursor += w + gap;
            }

            BandRule(band, width);
            Plugin.Logger?.LogDiag("Ui: world pick band " + (int)width + "x" + (int)BandHeight + " keys=" + interactKey + "/" + cancelKey);
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

    private static void BandRule(GameObject parent, float width)
    {
        var rule = new GameObject("rule");
        rule.transform.SetParent(parent.transform, false);
        var image = TryAdd<Image>(rule);
        if (image == null) return;
        if (KeybindArtDonors.TryRule(out var sprite, out var type, out var tint) && sprite != null)
        {
            try { image.sprite = sprite; } catch { /* */ }
            try { image.type = type; } catch { /* */ }
            try { image.color = tint; } catch { /* */ }
        }
        else
        {
            try { image.color = ToColor(MenuPalette.Gold); } catch { /* */ }
        }

        try { image.raycastTarget = false; } catch { /* */ }
        var rt = rule.GetComponent<RectTransform>() ?? rule.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0f);
        rt.anchorMax = new Vector2(0.5f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(width, RuleHeight);
    }
}
#endif
