#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// The title screen's Collect logs tile (<see cref="TitleLogsTile"/>): one more icon at the end of the game's social icon
/// row. It is a child of <c>LinkButtonSet.LinksParentNode</c>, so the title hold that hides <c>Content</c> hides it too and
/// the row's own layout spaces it. Its size comes from a native sibling (rect and <c>LayoutElement</c>). Its background is
/// a native icon's plain tile image when an icon keeps its tile and glyph in separate images
/// (<see cref="TitleLogsTile.PickBackground"/>; by component reference, never by sprite name:
/// game-art-name-match-red-button), else a drawn 9-sliced rounded tile (<see cref="RoundedTile"/>): live 2026-10-10 the
/// borrowed icon image had its glyph baked in, and the mail icon's envelope showed behind the zip. The drawn tile is tinted the
/// icons' slate blue (<see cref="TitleLogsTile.TileBlue"/>); a borrowed native tile keeps its own color. The
/// glyph is drawn in code (<see cref="ZipGlyph"/>), tinted gold.
/// The click and the hover go through the UI kit's slot table (the Bootstrap trampoline), like the MODS entry; no input is
/// polled. Attached with the MODS entry, dropped with it (<see cref="Drop"/>), and so released on every Core unload.
/// </summary>
internal static partial class NativeModsEntry
{
    private static GameObject? _logsTile;
    private static Image? _logsTileBackground;
    private static Image? _logsTileGlyph;
    private static TextMeshProUGUI? _logsTileLine;
    private static LogsTileWidget? _logsTileWidget;
    private static ushort _logsTileSlot;
    private static Texture2D? _logsGlyphTexture;
    private static Sprite? _logsGlyphSprite;
    private static Texture2D? _logsTileTexture;
    private static Sprite? _logsTileSprite;
    private static List<object?> _logsTileIcons = new();
    private static string _logsTileLinePlace = "";
    private static string _logsTileNavigation = "";
    private static bool _logsTileHovered;

    /// <summary>A native icon's tooltip offset (<c>LinkButton.TooltipOffset</c>), so the tooltip sits where the game's do.</summary>
    private static Vector3 _logsTileTooltipOffset;

    /// <summary>False once the game's tooltip could not be raised: the hover text falls back to the line above the row.</summary>
    private static bool _logsTileNativeTooltip = true;
    private static bool _logsTileWaiting;
    private static float _logsTileShownUntil;
    private static string _logsTileLastLine = "";
    private static string _logsTileAttachNote = "";
    private static int _logsTileRetry;

    /// <summary>Per tick while the title view is held by this generation: keep the tile attached and its line current.</summary>
    private static void TickLogsTile(MainMenuNewView view)
    {
        try
        {
            if (Alive(_logsTile)) UpdateLogsTileLine(view);
            else if (_logsTileRetry-- <= 0)
            {
                _logsTileRetry = 30; // a failed attach tries again every 30 frames, like the entry's own scan
                AttachLogsTile(view);
            }
        }
        catch (Exception e)
        {
            _logsTileAttachNote = "tick threw: " + e.Message;
        }
    }

    private static void AttachLogsTile(MainMenuNewView view)
    {
        DropLogsTile();
        var links = view.LinkButtonSet;
        if (links == null || !Alive(links)) { _logsTileAttachNote = "no LinkButtonSet"; return; }
        RectTransform? parent = links.LinksParentNode;
        if (parent == null) parent = AsRect(links.transform);
        if (parent == null) { _logsTileAttachNote = "no LinksParentNode"; return; }

        // A native icon to size from; every icon's images (which is the tile, which the glyph) for the background pick
        // and the readback.
        RectTransform? templateRect = null;
        LayoutElement? templateLayout = null;
        var iconImages = new List<IReadOnlyList<TitleLogsTile.IconImage>>();
        var iconRefs = new List<List<Image>>();
        _logsTileIcons = new List<object?>();
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child == null || TitleLogsTile.IsOwnTile(child.name) || !child.gameObject.activeSelf) continue;
            var rt = AsRect(child);
            if (rt == null || rt.rect.width <= 1f || rt.rect.height <= 1f) continue;
            if (templateRect == null)
            {
                templateRect = rt;
                try { templateLayout = child.GetComponent<LayoutElement>(); } catch { templateLayout = null; }
                // The icon's own tooltip placement (the game adds it to the button's position each hovered frame).
                try
                {
                    var link = child.GetComponent<LinkButton>();
                    _logsTileTooltipOffset = link != null ? link.TooltipOffset : Vector3.zero;
                }
                catch { _logsTileTooltipOffset = Vector3.zero; }
            }
            var (models, refs, described) = ReadIconImages(rt);
            iconImages.Add(models);
            iconRefs.Add(refs);
            _logsTileIcons.Add(described);
        }
        if (templateRect == null) { _logsTileAttachNote = "no native icon to size from"; return; }
        var pick = TitleLogsTile.PickBackground(iconImages);
        Image? templateImage = pick is { } at ? iconRefs[at.Icon][at.Image] : null;

        var go = new GameObject(TitleLogsTile.ObjectName);
        bool done = false;
        try
        {
            var rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.SetAsLastSibling();
            rect.anchorMin = templateRect.anchorMin;
            rect.anchorMax = templateRect.anchorMax;
            rect.pivot = templateRect.pivot;
            // A stretched template's size is its sizeDelta against its anchors, not its rect: copy that, and take the
            // layout sizes only from its LayoutElement (TitleLogsTile.LayoutAsk).
            bool stretchX = TitleLogsTile.Stretched(templateRect.anchorMin.x, templateRect.anchorMax.x);
            bool stretchY = TitleLogsTile.Stretched(templateRect.anchorMin.y, templateRect.anchorMax.y);
            var size = templateRect.rect.size;
            rect.sizeDelta = new Vector2(stretchX ? templateRect.sizeDelta.x : size.x, stretchY ? templateRect.sizeDelta.y : size.y);
            rect.localScale = templateRect.localScale;

            var layout = go.AddComponent<LayoutElement>();
            layout.preferredWidth = TitleLogsTile.LayoutAsk(templateLayout != null ? templateLayout.preferredWidth : -1f, size.x, stretchX);
            layout.preferredHeight = TitleLogsTile.LayoutAsk(templateLayout != null ? templateLayout.preferredHeight : -1f, size.y, stretchY);
            layout.minWidth = TitleLogsTile.LayoutAsk(templateLayout != null ? templateLayout.minWidth : -1f, size.x, stretchX);
            layout.minHeight = TitleLogsTile.LayoutAsk(templateLayout != null ? templateLayout.minHeight : -1f, size.y, stretchY);

            var background = go.AddComponent<Image>();
            bool nativeTile = false;
            if (templateImage != null && Alive(templateImage.sprite))
            {
                background.sprite = templateImage.sprite;
                background.type = templateImage.type;
                background.pixelsPerUnitMultiplier = templateImage.pixelsPerUnitMultiplier;
                nativeTile = true;
            }
            else if (LogsTileSprite() is { } drawn)
            {
                background.sprite = drawn;
                background.type = Image.Type.Sliced;
                background.pixelsPerUnitMultiplier = RoundedTile.SliceMultiplier(size.y);
            }
            // The icons' own blue (user, 2026-10-10): a native plain tile keeps its color; the drawn tile is tinted to it.
            background.color = ColorOf(nativeTile ? TitleLogsTile.NativeTileTint : TitleLogsTile.TileBlue);
            background.raycastTarget = true;

            var glyphGo = new GameObject("glyph");
            var glyphRect = glyphGo.AddComponent<RectTransform>();
            glyphRect.SetParent(rect, false);
            glyphRect.anchorMin = new Vector2(TitleLogsTile.GlyphInset, TitleLogsTile.GlyphInset);
            glyphRect.anchorMax = new Vector2(1f - TitleLogsTile.GlyphInset, 1f - TitleLogsTile.GlyphInset);
            glyphRect.offsetMin = Vector2.zero;
            glyphRect.offsetMax = Vector2.zero;
            var glyph = glyphGo.AddComponent<Image>();
            glyph.sprite = LogsGlyphSprite();
            glyph.preserveAspect = true;
            glyph.color = ColorOf(TitleLogsTile.Gold);
            glyph.raycastTarget = false;

            // The one-line message above the row: outside the row's layout, anchored to the tile's top-left.
            var lineGo = new GameObject("line");
            var lineRect = lineGo.AddComponent<RectTransform>();
            lineRect.SetParent(rect, false);
            var lineLayout = lineGo.AddComponent<LayoutElement>();
            lineLayout.ignoreLayout = true;
            // Placed by PlaceLogsTileLine: left on the native icon row, just above it (TitleLogsTile.PlaceLine). The
            // anchor is the tile's pivot, so a local point is the anchored position.
            lineRect.anchorMin = rect.pivot;
            lineRect.anchorMax = rect.pivot;
            lineRect.pivot = new Vector2(0f, 0f);
            lineRect.sizeDelta = new Vector2(TitleLogsTile.LineFallbackWidth, TitleLogsTile.LineHeight * 2f);
            var line = lineGo.AddComponent<TextMeshProUGUI>();
            // The kit's body font with normal spacing, not the menu's wide-spaced caption style (live 2026-10-10).
            UnityUiRuntime.ApplyBodyFont(line);
            line.characterSpacing = 0f;
            line.wordSpacing = 0f;
            line.fontStyle = FontStyles.Normal;
            line.fontSize = TitleLogsTile.LineFontSize;
            line.color = new Color(MenuPalette.Muted.R, MenuPalette.Muted.G, MenuPalette.Muted.B, 1f);
            line.alignment = TextAlignmentOptions.Bottom;
            line.enableWordWrapping = true;
            line.overflowMode = TextOverflowModes.Ellipsis;
            line.raycastTarget = false;
            line.text = "";
            lineGo.SetActive(false);

            var button = go.AddComponent<Button>();
            button.targetGraphic = background;
            // Controller navigation as the native link buttons have it (None when none says), so the tile does not
            // change how a pad moves through the title menu.
            button.navigation = NativeLinkNavigation(parent);
            var colors = button.colors;
            colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            button.colors = colors;

            // Click and hover through the slot table, as the MODS entry wires its own.
            AttachHoverTrigger(go, out var enter, out var exit);
            var widget = new LogsTileWidget(go, button.onClick, enter, exit);
            ushort id = UiSlotTable.Alloc(Owner);
            var runtime = Ui.CurrentRuntime();
            if (!runtime.TryWire(id, UiSlotKind.Click, widget))
            {
                UiSlotTable.Release(id);
                _logsTileAttachNote = "click could not be wired";
                return;
            }
            if (enter != null) runtime.TryWire(id, UiSlotKind.PointerEnter, widget);
            if (exit != null) runtime.TryWire(id, UiSlotKind.PointerExit, widget);
            UiSlotTable.BindClick(id, OnLogsTileClicked);
            UiSlotTable.BindPointerEnter(id, () => _logsTileHovered = true);
            UiSlotTable.BindPointerExit(id, () => _logsTileHovered = false);

            _logsTile = go;
            _logsTileBackground = background;
            _logsTileGlyph = glyph;
            _logsTileLine = line;
            _logsTileWidget = widget;
            _logsTileSlot = id;
            _logsTileAttachNote = templateImage != null && Alive(templateImage.sprite)
                ? "native background " + templateImage.gameObject.name
                : Alive(background.sprite) ? "drawn background" : "flat background";
            done = true;
            Plugin.Logger?.LogInfo("Ui: Collect logs tile attached to the title icon row (" + _logsTileAttachNote + ").");
        }
        finally
        {
            if (!done) DestroyOwned(go);
        }
    }

    private static void OnLogsTileClicked()
    {
        try { Support.SupportActions.Collect(); }
        catch (Exception e) { Plugin.Logger?.LogInfo("Ui: Collect logs from the title tile: " + e.Message); }
        _logsTileWaiting = true;
        _logsTileShownUntil = Time.unscaledTime + TitleLogsTile.MessageSeconds;
        UpdateLogsTileLine(_main);
    }

    private static void UpdateLogsTileLine(MainMenuNewView? view)
    {
        if (!Alive(_logsTileLine)) return;
        bool running = Support.SupportActions.Running;
        float now = Time.unscaledTime;
        if (_logsTileWaiting && !running)
        {
            // Finished (or refused): its line stays up for the message time from now.
            _logsTileWaiting = false;
            _logsTileShownUntil = now + TitleLogsTile.MessageSeconds;
        }
        // Per frame: the collect's progress and its shortened outcome are only read while the line would show them.
        string runningLine = running ? Support.SupportActions.RunningLine : "";
        string status = !running && now < _logsTileShownUntil ? ShortStatus() : "";
        // The hover text is the game's own tooltip, as on the official icons (user, 2026-10-10); the line above the row
        // keeps the collect's progress and outcome, and the hover text only when the game's tooltip cannot be raised.
        if (_logsTileHovered && _logsTileNativeTooltip) RaiseLogsTileTooltip();
        string text = TitleLogsTile.Line(now, _logsTileHovered && !_logsTileNativeTooltip, running, runningLine, status, _logsTileShownUntil);
        if (text == _logsTileLastLine) return;
        _logsTileLastLine = text;
        // A failure or a refusal is one line; a save is two (where it went, then where to post it) when two fit.
        string shown = PlaceLogsTileLine(view, text);
        _logsTileLine!.text = shown;
        _logsTileLine.gameObject.SetActive(shown.Length > 0);
    }

    /// <summary>
    /// The game's tooltip for the tile, set each hovered frame as <c>LinkButton.Update</c> does for the official icons: the
    /// text at the tile's position plus a native icon's <c>TooltipOffset</c>, pivot bottom left, no width limit
    /// (<c>TooltipManagerComponent</c> draws it and clears it every frame nobody sets it). A failure turns the line back on.
    /// </summary>
    private static void RaiseLogsTileTooltip()
    {
        try
        {
            if (!Alive(_logsTile)) return;
            Vector3 at = _logsTile!.transform.position + _logsTileTooltipOffset;
            TooltipManagerComponent.SetTooltip(TitleLogsTile.Tooltip, new Vector2(at.x, at.y), TitleLogsTile.TooltipPivot);
        }
        catch (Exception e)
        {
            _logsTileNativeTooltip = false;
            _logsTileLastLine = "";
            Plugin.Logger?.LogInfo("Ui: the game's tooltip for the Collect logs tile threw (" + e.Message + "); the line above the row shows the hover text.");
        }
    }

    /// <summary>The collect's outcome, shortened for this spot (<see cref="TitleLogsTile.ResultText"/>).</summary>
    private static string ShortStatus() => TitleLogsTile.CurrentResultText();

    /// <summary>
    /// Place the line centered over the tile, just above the icon row, sized from its measured text (the final font and
    /// size), inside the 16:9 content, and with its second line only when two fit under the menu's last entry (EXIT, the
    /// rect the MODS entry's clearance measures). Answers the text to show (the second line dropped when it does not fit).
    /// </summary>
    private static string PlaceLogsTileLine(MainMenuNewView? view, string text)
    {
        _logsTileLinePlace = "";
        string firstOnly = text.IndexOf('\n') is int cut && cut >= 0 ? text.Substring(0, cut) : text;
        if (!Alive(_logsTileLine) || !Alive(_logsTile) || view == null || !Alive(view) || text.Length == 0) return firstOnly;
        try
        {
            var tileRect = AsRect(_logsTile!.transform);
            var icons = TryReadIconRow(view);
            if (tileRect == null || icons is not MenuRect row) return firstOnly;
            var tileScreen = ReadScreenRect(tileRect);
            float pxPerUnit = tileRect.rect.width > 1f && tileScreen.Width > 1f ? tileScreen.Width / tileRect.rect.width : 1f;
            float? exitBottom = null;
            try
            {
                var collection = view.MenuCollection;
                var buttons = collection != null && Alive(collection.ButtonsParent) ? collection.ButtonsParent : null;
                if (TryReadLastEntryRect(buttons, skip: null) is MenuRect exit && exit.Height > 1f) exitBottom = exit.Bottom;
            }
            catch { exitBottom = null; }
            // Measure with the final font and size (live 2026-10-10: a width read too small cut "...to your Deskto..."):
            // each line on its own, unwrapped, in the line's local units (the tile's), then in screen pixels.
            var line = _logsTileLine!;
            UnityUiRuntime.ApplyBodyFont(line);
            line.fontSize = TitleLogsTile.LineFontSize;
            line.characterSpacing = 0f;
            line.enableWordWrapping = false;
            string second = text.Length > firstOnly.Length ? text.Substring(firstOnly.Length + 1) : "";
            Vector2 firstSize = line.GetPreferredValues(firstOnly, float.PositiveInfinity, float.PositiveInfinity);
            float secondWidth = second.Length > 0 ? line.GetPreferredValues(second, float.PositiveInfinity, float.PositiveInfinity).x : 0f;
            float lineHeight = firstSize.y > 1f ? firstSize.y : TitleLogsTile.LineHeight;
            const float pad = 4f; // TMP's own margin and rounding
            var place = TitleLogsTile.PlaceLine(row.Left, row.Top, row.Width, exitBottom, pxPerUnit, lineHeight);
            var (contentLeft, contentRight) = TitleLogsTile.ContentSpan(Screen.width, Screen.height);
            float available = !float.IsNaN(contentLeft) && contentRight > contentLeft ? contentRight - contentLeft : place.Width;
            var fit = TitleLogsTile.FitLines(text, (firstSize.x + pad) * pxPerUnit, (secondWidth + pad) * pxPerUnit, available, place.MaxLines);
            place = TitleLogsTile.CenterOverTile(place, tileScreen.X + tileScreen.Width / 2f, fit.Width, contentLeft, contentRight);
            line.overflowMode = fit.Ellipsis ? TextOverflowModes.Ellipsis : TextOverflowModes.Overflow;
            Camera? cam = CanvasEventCamera(tileRect);
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(tileRect, new Vector2(place.Left, place.Bottom), cam, out var local))
                return fit.Text;
            var lineRect = line.rectTransform;
            lineRect.anchoredPosition = local;
            lineRect.sizeDelta = new Vector2(place.Width / pxPerUnit, lineHeight * fit.Lines + 2f);
            _logsTileLinePlace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "left={0:0} bottom={1:0} width={2:0} lines={3} exitBottom={4} measured={5:0.#}x{6:0.#} units pxPerUnit={7:0.###} ellipsis={8}",
                place.Left, place.Bottom, place.Width, fit.Lines,
                exitBottom is float e ? e.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "none",
                firstSize.x, firstSize.y, pxPerUnit, fit.Ellipsis);
            return fit.Text;
        }
        catch (Exception e)
        {
            _logsTileLinePlace = "place threw: " + e.Message;
            return firstOnly;
        }
    }

    /// <summary>The navigation of the first native link button in the row (a copy), else <see cref="Navigation.Mode.None"/>.</summary>
    private static Navigation NativeLinkNavigation(RectTransform row)
    {
        var none = new Navigation { mode = Navigation.Mode.None };
        try
        {
            for (int i = 0; i < row.childCount; i++)
            {
                var child = row.GetChild(i);
                if (child == null || TitleLogsTile.IsOwnTile(child.name)) continue;
                Selectable? selectable = null;
                try { selectable = child.GetComponentInChildren<Selectable>(true); } catch { selectable = null; }
                if (selectable == null) continue;
                var native = selectable.navigation;
                // Explicit links point at the native neighbors; the tile takes only the mode (Explicit without targets is None).
                var mode = native.mode == Navigation.Mode.Explicit ? Navigation.Mode.None : native.mode;
                _logsTileNavigation = native.mode.ToString();
                return new Navigation { mode = mode };
            }
        }
        catch { /* None */ }
        _logsTileNavigation = "none found";
        return none;
    }

    /// <summary>A native icon's images (itself and its children): the pick's model, the components, and the readback rows.</summary>
    private static (List<TitleLogsTile.IconImage> Models, List<Image> Refs, Dictionary<string, object?> Described) ReadIconImages(RectTransform icon)
    {
        var models = new List<TitleLogsTile.IconImage>();
        var refs = new List<Image>();
        var rows = new List<object?>();
        float iw = Math.Max(1f, icon.rect.width), ih = Math.Max(1f, icon.rect.height);
        Image[]? found = null;
        try { found = icon.GetComponentsInChildren<Image>(true); }
        catch { found = null; }
        if (found != null)
        {
            for (int k = 0; k < found.Length; k++)
            {
                var image = found[k];
                if (image == null) continue;
                string path = PathUnder(image.transform, icon);
                var rt = AsRect(image.transform);
                float w = rt != null ? rt.rect.width / iw : 0f, h = rt != null ? rt.rect.height / ih : 0f;
                Sprite? sprite = null;
                try { sprite = image.sprite; } catch { sprite = null; }
                bool hasSprite = Alive(sprite);
                models.Add(new TitleLogsTile.IconImage(path, hasSprite, w, h));
                refs.Add(image);
                var row = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = path,
                    ["active"] = image.gameObject.activeInHierarchy,
                    ["enabled"] = image.enabled,
                    ["w"] = rt != null ? Math.Round(rt.rect.width, 1) : null,
                    ["h"] = rt != null ? Math.Round(rt.rect.height, 1) : null,
                    ["type"] = image.type.ToString(),
                    ["color"] = TileHex(image.color),
                };
                if (hasSprite)
                {
                    try
                    {
                        row["sprite"] = sprite!.name;
                        row["spriteW"] = Math.Round(sprite.rect.width, 1);
                        row["spriteH"] = Math.Round(sprite.rect.height, 1);
                        var b = sprite.border;
                        if (b.x + b.y + b.z + b.w > 0f) row["spriteBorder"] = new[] { b.x, b.y, b.z, b.w };
                    }
                    catch { /* best-effort */ }
                }
                rows.Add(row);
            }
        }
        var children = new List<object?>();
        for (int c = 0; c < icon.childCount; c++)
        {
            var child = icon.GetChild(c);
            if (child != null) children.Add(child.name);
        }
        var described = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = icon.name,
            ["w"] = Math.Round(icon.rect.width, 1),
            ["h"] = Math.Round(icon.rect.height, 1),
            ["children"] = children,
            ["images"] = rows,
        };
        return (models, refs, described);
    }

    /// <summary>The path of <paramref name="t"/> under <paramref name="root"/> ("" for the root itself).</summary>
    private static string PathUnder(Transform t, Transform root)
    {
        var parts = new List<string>();
        var at = t;
        for (int guard = 0; at != null && guard < 16; guard++)
        {
            if (at.Pointer == root.Pointer) break;
            parts.Insert(0, at.name);
            at = at.parent;
        }
        return string.Join("/", parts);
    }

    /// <summary>The drawn rounded tile (<see cref="RoundedTile"/>), 9-sliced at its corner radius.</summary>
    private static Sprite? LogsTileSprite()
    {
        if (Alive(_logsTileSprite) && Alive(_logsTileTexture)) return _logsTileSprite;
        // One of the pair died (a scene unload, a destroy elsewhere): free the survivor before drawing again.
        FreeDrawn(ref _logsTileSprite, ref _logsTileTexture);
        try
        {
            int size = RoundedTile.TextureSize;
            _logsTileTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = TitleLogsTile.ObjectName + "-tile",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    _logsTileTexture.SetPixel(x, y, new Color(1f, 1f, 1f, RoundedTile.Alpha(x, y, size, RoundedTile.Radius)));
            _logsTileTexture.Apply(false, false);
            float r = RoundedTile.Radius;
            _logsTileSprite = Sprite.Create(_logsTileTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            _logsTileSprite.name = TitleLogsTile.ObjectName + "-tile";
            _logsTileSprite.hideFlags = HideFlags.HideAndDontSave;
            return _logsTileSprite;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogInfo("Ui: Collect logs tile background could not be drawn: " + e.Message);
            return null;
        }
    }

    private static void FreeDrawn(ref Sprite? sprite, ref Texture2D? texture)
    {
        try { if (Alive(sprite)) UnityEngine.Object.Destroy(sprite); } catch { /* gone */ }
        try { if (Alive(texture)) UnityEngine.Object.Destroy(texture); } catch { /* gone */ }
        sprite = null;
        texture = null;
    }

    private static Sprite LogsGlyphSprite()
    {
        if (Alive(_logsGlyphSprite) && Alive(_logsGlyphTexture)) return _logsGlyphSprite!;
        FreeDrawn(ref _logsGlyphSprite, ref _logsGlyphTexture);
        int size = ZipGlyph.TextureSize;
        _logsGlyphTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = TitleLogsTile.ObjectName + "-glyph",
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                _logsGlyphTexture.SetPixel(x, y, new Color(1f, 1f, 1f, ZipGlyph.Alpha(x, y, size)));
        _logsGlyphTexture.Apply(false, false);
        _logsGlyphSprite = Sprite.Create(_logsGlyphTexture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        _logsGlyphSprite.name = TitleLogsTile.ObjectName + "-glyph";
        _logsGlyphSprite.hideFlags = HideFlags.HideAndDontSave;
        return _logsGlyphSprite;
    }

    /// <summary>Release the tile: its slot, its object and the drawn glyph. Called from <see cref="Drop"/> (every unload reason too).</summary>
    private static void DropLogsTile()
    {
        if (_logsTileSlot != 0)
        {
            try { Ui.CurrentRuntime().Unwire(_logsTileSlot); } catch { /* runtime gone */ }
            try { UiSlotTable.Release(_logsTileSlot); } catch { /* table cleared */ }
            _logsTileSlot = 0;
        }
        if (_logsTile != null) DestroyOwned(_logsTile);
        _logsTile = null;
        _logsTileBackground = null;
        _logsTileGlyph = null;
        _logsTileLine = null;
        _logsTileWidget = null;
        _logsTileHovered = false;
        _logsTileNativeTooltip = true;
        _logsTileWaiting = false;
        _logsTileLastLine = "";
        try { if (Alive(_logsGlyphSprite)) UnityEngine.Object.Destroy(_logsGlyphSprite); } catch { /* gone */ }
        try { if (Alive(_logsGlyphTexture)) UnityEngine.Object.Destroy(_logsGlyphTexture); } catch { /* gone */ }
        _logsGlyphSprite = null;
        _logsGlyphTexture = null;
        try { if (Alive(_logsTileSprite)) UnityEngine.Object.Destroy(_logsTileSprite); } catch { /* gone */ }
        try { if (Alive(_logsTileTexture)) UnityEngine.Object.Destroy(_logsTileTexture); } catch { /* gone */ }
        _logsTileSprite = null;
        _logsTileTexture = null;
    }

    /// <summary>
    /// <c>mods_ui scene</c>'s <c>logsTile</c>: whether it is attached, its rect (client pixels, top-left origin, the space
    /// <c>mods_ui click_at</c> takes: click its <c>center</c> to press it focus-free), the tint, the line it shows and
    /// how it was built.
    /// </summary>
    internal static Dictionary<string, object?> DescribeLogsTile()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["present"] = Alive(_logsTile),
            ["name"] = TitleLogsTile.ObjectName,
            ["note"] = _logsTileAttachNote,
            // The native icons it measured and chose its background from: each one's children and images.
            ["icons"] = _logsTileIcons,
        };
        if (!Alive(_logsTile)) return d;
        try
        {
            var rect = ReadScreenRect(AsRect(_logsTile!.transform));
            int height = Screen.height;
            float top = height - rect.Top;
            d["rect"] = new Dictionary<string, object?> { ["x"] = rect.X, ["y"] = top, ["w"] = rect.Width, ["h"] = rect.Height };
            d["center"] = new Dictionary<string, object?> { ["x"] = rect.X + rect.Width / 2f, ["y"] = top + rect.Height / 2f };
            d["activeInHierarchy"] = _logsTile.activeInHierarchy;
            d["siblingIndex"] = _logsTile.transform.GetSiblingIndex();
            d["siblings"] = _logsTile.transform.parent != null ? _logsTile.transform.parent.childCount : 0;
            if (_logsTileBackground != null) d["tint"] = TileHex(_logsTileBackground.color);
            if (_logsTileGlyph != null) d["glyphTint"] = TileHex(_logsTileGlyph.color);
            if (_logsTileBackground != null)
            {
                try
                {
                    var s = _logsTileBackground.sprite;
                    d["background"] = new Dictionary<string, object?>
                    {
                        ["sprite"] = Alive(s) ? s!.name : null,
                        ["type"] = _logsTileBackground.type.ToString(),
                        ["pixelsPerUnitMultiplier"] = Math.Round(_logsTileBackground.pixelsPerUnitMultiplier, 3),
                    };
                }
                catch { /* best-effort */ }
            }
            d["hovered"] = _logsTileHovered;
            d["line"] = _logsTileLastLine;
            d["lineShown"] = _logsTileLine != null && _logsTileLine.gameObject.activeSelf;
            d["linePlace"] = _logsTileLinePlace;
            d["nativeNavigation"] = _logsTileNavigation;
            try { if (_logsTile.GetComponent<Button>() is { } b) d["navigation"] = b.navigation.mode.ToString(); } catch { /* best-effort */ }
            if (_logsTileLine != null && _logsTileLine.gameObject.activeSelf)
            {
                var lr = ReadScreenRect(_logsTileLine.rectTransform);
                d["lineRect"] = new Dictionary<string, object?> { ["x"] = lr.X, ["y"] = height - lr.Top, ["w"] = lr.Width, ["h"] = lr.Height };
                d["lineSpacing"] = _logsTileLine.characterSpacing;
            }
        }
        catch (Exception e) { d["error"] = e.Message; }
        return d;
    }

    private static Color ColorOf(float[] rgba) => new(rgba[0], rgba[1], rgba[2], rgba.Length > 3 ? rgba[3] : 1f);

    private static string TileHex(Color c) =>
        "#" + ((int)(Mathf.Clamp01(c.r) * 255)).ToString("X2") + ((int)(Mathf.Clamp01(c.g) * 255)).ToString("X2")
        + ((int)(Mathf.Clamp01(c.b) * 255)).ToString("X2") + ((int)(Mathf.Clamp01(c.a) * 255)).ToString("X2");

    /// <summary>The tile's events for the slot table (click and hover), as <c>MenuButtonWidget</c> exposes the entry's.</summary>
    private sealed class LogsTileWidget : IRuntimeWidget
    {
        private readonly GameObject _go;
        private readonly object _click;
        private readonly object? _enter;
        private readonly object? _exit;

        internal LogsTileWidget(GameObject go, object click, object? enter, object? exit)
        {
            _go = go;
            _click = click;
            _enter = enter;
            _exit = exit;
        }

        public string Text { get => TitleLogsTile.Tooltip; set { } }
        public bool Visible { get => Alive && _go.activeSelf; set { if (Alive) _go.SetActive(value); } }
        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public string StringValue { get; set; } = "";
        public bool Alive => NativeModsEntry.Alive(_go);
        public object? ClickEvent => _click;
        public object? FloatEvent => null;
        public object? BoolEvent => null;
        public object? TextEvent => null;
        public object? PointerEnterEvent => _enter;
        public object? PointerExitEvent => _exit;

        // The tile object is destroyed by DropLogsTile (with the glyph); the widget owns nothing else.
        public void Release() { }
    }
}
#endif
