#if !PROGENITOR_TESTS
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7 item 3: the pick-in-world prompt band (<see cref="WorldPickBackend"/>). The band is an ordinary
/// kit panel — so it shows, hides, hover-routes input and forwards Escape like every other panel —
/// restyled after its first show: self-placed at the bottom center above the action bar, no title,
/// no frame art, a soft dark ground with the game's gold category underline beneath it (the Bindings
/// menu header rule, <see cref="KeybindArtDonors.TryRule"/>), the kit's body font, and the two keys
/// drawn on the game's own key cap (<see cref="KeybindArtDonors.TryKeyCap"/>, the Bindings menu's key
/// button). Either donor missing falls back to a flat gold rule and a bordered kit cap.
/// </summary>
internal sealed partial class UnityUiRuntime : IWorldPickBandRuntime
{
    /// <summary>Name of the band's content child (one per band panel).</summary>
    internal const string WorldPickBandName = UiOwnership.Prefix + "world-pick-band";

    private const float BandHeight = 50f;
    private const float BandBottom = 190f;
    private const float BandPadX = 28f;
    private const float PromptSize = 24f;
    private const float WordSize = 21f;
    private const float KeySize = 18f;
    private const float CapHeight = 30f;
    private const float CapPadX = 9f;
    private const float GapAfterPrompt = 30f;
    private const float GapCapWord = 8f;
    private const float GapBetweenKeys = 24f;
    private const float RuleHeight = 2f;

    public void StyleWorldPickBand(IRuntimePanel panel, string prompt, string interactKey, string cancelKey)
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

            // The frame's own image becomes the band's ground: no 9-slice window art, a soft dark fill.
            var ground = frame.GetComponent<Image>();
            if (ground != null)
            {
                try { ground.sprite = null; } catch { /* */ }
                try { ground.type = Image.Type.Simple; } catch { /* */ }
                try { ground.color = new Color(0.035f, 0.027f, 0.032f, 0.78f); } catch { /* */ }
                try { ground.raycastTarget = true; } catch { /* */ }
            }

            var old = frame.transform.Find(WorldPickBandName);
            if (old != null) TryDestroyImmediate(old.gameObject);
            var row = new GameObject(WorldPickBandName);
            row.transform.SetParent(frame.transform, false);
            Stretch(row);

            float x = 0f;
            var parts = new System.Collections.Generic.List<(RectTransform Rt, float Width)>();
            void Add(GameObject? go, float width)
            {
                if (go == null) return;
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return;
                parts.Add((rt, width));
                x += width;
            }

            Add(BandText(row, prompt, PromptSize, ToColor(MenuPalette.Text), out float promptWidth), promptWidth);
            x += GapAfterPrompt;
            Add(BandCap(row, interactKey, out float capA), capA);
            x += GapCapWord;
            Add(BandText(row, "pick", WordSize, ToColor(MenuPalette.Text), out float pickWidth), pickWidth);
            x += GapBetweenKeys;
            Add(BandCap(row, cancelKey, out float capB), capB);
            x += GapCapWord;
            Add(BandText(row, "cancel", WordSize, ToColor(MenuPalette.Text), out float cancelWidth), cancelWidth);

            float width = Mathf.Ceil(x + 2f * BandPadX);
            frameRt.anchorMin = new Vector2(0.5f, 0f);
            frameRt.anchorMax = new Vector2(0.5f, 0f);
            frameRt.pivot = new Vector2(0.5f, 0f);
            frameRt.sizeDelta = new Vector2(width, BandHeight);
            frameRt.anchoredPosition = new Vector2(0f, BandBottom);

            // Lay the parts out left to right, centered vertically, using the gaps added above.
            float cursor = -x / 2f;
            float[] gaps = { GapAfterPrompt, GapCapWord, GapBetweenKeys, GapCapWord, 0f };
            for (int i = 0; i < parts.Count; i++)
            {
                var (rt, w) = parts[i];
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.anchoredPosition = new Vector2(cursor, 0f);
                cursor += w + (i < gaps.Length ? gaps[i] : 0f);
            }

            BandRule(row, width);
            Plugin.Logger?.LogDiag("Ui: world pick band " + (int)width + "x" + (int)BandHeight + " keys=" + interactKey + "/" + cancelKey);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: world pick band style threw: " + e.Message);
        }
    }

    private static GameObject? BandText(GameObject parent, string text, float size, Color color, out float width)
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
        try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
        try { tmp.alignment = TextAlignmentOptions.Left; } catch { /* */ }
        ApplyCachedFont(tmp);
        try { width = Mathf.Ceil(tmp.GetPreferredValues(tmp.text).x); } catch { width = (text ?? "").Length * size * 0.5f; }
        if (width <= 0f) width = (text ?? "").Length * size * 0.5f;
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

        var label = BandText(cap, key, KeySize, ToColor(MenuPalette.ClientText), out float textWidth);
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
            try { image.color = ToColor(MenuPalette.Gold.WithAlpha(0.85f)); } catch { /* */ }
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
