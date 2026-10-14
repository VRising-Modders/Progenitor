#if !PROGENITOR_TESTS
using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7 <c>ctx.Hud</c>: what the notice band and the Locate pings share. Both draw on their own screen-space canvas under
/// the framework root <c>progenitor-ui-hud</c>, scaled like the game's own UI (the copied <c>CanvasScaler</c>), never
/// raycast (they are not clickable and must not eat a click meant for the world), and are torn down on every unload
/// path through <see cref="UiWidgetLifetime"/> (and the prefix sweep of <c>Ui.Uninstall</c> as a backstop).
/// </summary>
internal sealed partial class UnityUiRuntime
{
    /// <summary>The root every <c>ctx.Hud</c> canvas hangs under.</summary>
    internal static readonly string HudRootName = UiOwnership.RootName("hud");

    private static readonly long HudClockStart = Stopwatch.GetTimestamp();

    /// <summary>Seconds on a monotonic clock of this Core generation (not game time: a paused or slowed game still fades notices).</summary>
    internal static double HudNow() => (Stopwatch.GetTimestamp() - HudClockStart) / (double)Stopwatch.Frequency;

    /// <summary>
    /// A non-interactive screen-space canvas named <paramref name="name"/> under the HUD root, at
    /// <paramref name="sortingOrder"/>, scaled like the game's UI. Null when Unity refused (logged by the helpers).
    /// </summary>
    private static GameObject? CreateHudCanvas(string name, int sortingOrder)
    {
        var root = EnsureRoot(HudRootName);
        if (root == null) return null;
        var go = new GameObject(UiOwnership.Prefix + name);
        try
        {
            go.transform.SetParent(root.transform, false);
            var canvas = TryAdd<Canvas>(go);
            if (canvas == null)
            {
                TryDestroyImmediate(go);
                return null;
            }

            try { canvas.renderMode = RenderMode.ScreenSpaceOverlay; } catch { /* */ }
            try { canvas.overrideSorting = true; } catch { /* */ }
            try { canvas.sortingOrder = sortingOrder; } catch { /* */ }
            try { canvas.pixelPerfect = false; } catch { /* */ }
            var scaler = TryAdd<CanvasScaler>(go);
            if (scaler != null) AdoptGameScaler(scaler);
            var group = TryAdd<CanvasGroup>(go);
            if (group != null)
            {
                try { group.interactable = false; } catch { /* */ }
                try { group.blocksRaycasts = false; } catch { /* */ }
            }

            Stretch(go);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud canvas '" + name + "' could not be built: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>A child with a RectTransform anchored and pivoted at <paramref name="anchor"/>.</summary>
    private static GameObject HudChild(GameObject parent, string name, Vector2 anchor, Vector2 pivot, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        var rt = go.AddComponent<RectTransform>();
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = size;
        return go;
    }

    /// <summary>An Image that never takes a raycast.</summary>
    private static Image? HudImage(GameObject go, Sprite? sprite, Color color)
    {
        var image = TryAdd<Image>(go);
        if (image == null) return null;
        try { image.sprite = sprite; } catch { /* */ }
        try { image.type = Image.Type.Simple; } catch { /* */ }
        try { image.preserveAspect = false; } catch { /* */ }
        try { image.color = color; } catch { /* */ }
        try { image.raycastTarget = false; } catch { /* */ }
        return image;
    }

    /// <summary>A text in the kit's body font (the game's own face), no rich text (plugin text is shown as written).</summary>
    private static TMPro.TextMeshProUGUI? HudText(GameObject go, float size, Color color)
    {
        var tmp = TryAdd<TMPro.TextMeshProUGUI>(go);
        if (tmp == null) return null;
        ApplyBodyFont(tmp);
        try { tmp.richText = false; } catch { /* */ }
        try { tmp.fontSize = size; } catch { /* */ }
        try { tmp.color = color; } catch { /* */ }
        try { tmp.alignment = TMPro.TextAlignmentOptions.Center; } catch { /* */ }
        try { tmp.enableWordWrapping = false; } catch { /* */ }
        try { tmp.overflowMode = TMPro.TextOverflowModes.Ellipsis; } catch { /* */ }
        try { tmp.raycastTarget = false; } catch { /* */ }
        return tmp;
    }

    /// <summary>
    /// A white-on-transparent sprite from a game-free coverage function, made once per Core generation (the caller
    /// keeps and frees it). <paramref name="alpha"/> takes (x, y) bottom-up.
    /// </summary>
    private static Sprite? HudSprite(string name, int width, int height, Func<int, int, float> alpha, out Texture2D? texture)
    {
        texture = null;
        try
        {
            string full = UiOwnership.Prefix + name;
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = full,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, alpha(x, y)));
            t.Apply(false, false);
            var s = Sprite.Create(t, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
            s.name = full;
            s.hideFlags = HideFlags.HideAndDontSave;
            texture = t;
            return s;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: ctx.Hud art '" + name + "' could not be drawn: " + e.Message);
            return null;
        }
    }

    private static void FreeHudAsset(UnityEngine.Object? asset)
    {
        try { if (Alive(asset)) UnityEngine.Object.Destroy(asset); } catch { /* gone */ }
    }
}
#endif
