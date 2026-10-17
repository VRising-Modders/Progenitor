#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using Stunlock.Core;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Progenitor.Ui;

/// <summary>
/// P7: the Unity half of the composed widgets (<see cref="IWidgetRuntime"/>): the tooltip box, pointer and hover watches on
/// a widget's own object, the frame tick a drag runs on, glyph checks against the kit's font, and game item icons.
/// </summary>
internal sealed partial class UnityUiRuntime : IWidgetRuntime
{
    // ---- Tooltip ---------------------------------------------------------------------------------

    /// <summary>The tooltip's own root, under the kit's prefix so the Core teardown's prefix sweep reaches it too.</summary>
    internal const string TooltipRootName = UiOwnership.Prefix + "core-tooltip";

    /// <summary>Above every mod panel and an open dropdown list (<see cref="UnityChoiceWidget.ListSortingOrder"/> is +40).</summary>
    internal static int TooltipSortingOrder => UiOwnership.OverlaySorting + 60;

    private static GameObject? _tipRoot;
    private static RectTransform? _tipCanvas;
    private static RectTransform? _tipBox;
    private static TextMeshProUGUI? _tipTitle;
    private static TextMeshProUGUI? _tipBody;

    /// <summary>The shown tooltip's liveness question, and the frame tick that asks it.</summary>
    private static Func<bool>? _tipStillShown;

    /// <summary>
    /// The shown box belongs to a control on a mod panel and goes when no panel is shown. False for a
    /// tooltip on a control inside a game window (a P6 icon button), which has no mod panel at all:
    /// with the rule applied to it, the box was hidden on the frame after it was shown.
    /// </summary>
    private static bool _tipNeedsPanel = true;

    /// <summary>The kit tooltip box is up.</summary>
    internal static bool TooltipShown
    {
        get { try { return Alive(_tipRoot) && _tipRoot!.activeSelf; } catch { return false; } }
    }

    /// <summary>
    /// Whether the box up now was shown with <paramref name="stillShown"/> as its liveness check, so a control that showed it
    /// can tell its own tooltip from one another control put up since.
    /// </summary>
    internal static bool TooltipOwnedBy(Func<bool> stillShown) => stillShown != null && ReferenceEquals(_tipStillShown, stillShown);

    /// <summary>The shown tooltip's title (empty when none is up).</summary>
    internal static string TooltipTitle
    {
        get { try { return TooltipShown && _tipTitle != null ? (_tipTitle.text ?? "") : ""; } catch { return ""; } }
    }

    /// <summary>
    /// <see cref="ShowTooltip"/> for a control that lives in a game window rather than on a mod
    /// panel: the same box, above every game and mod canvas, kept while <paramref name="stillShown"/>
    /// says so whether or not a mod panel is shown.
    /// </summary>
    internal void ShowGameControlTooltip(ScreenRect anchor, string title, string body, Func<bool> stillShown)
    {
        ShowTooltip(anchor, title, body, stillShown);
        if (TooltipShown) _tipNeedsPanel = false;
    }
    private static IDisposable? _tipWatch;

    public void ShowTooltip(ScreenRect anchor, string title, string body, Func<bool>? stillShown = null)
    {
        if (!anchor.IsKnown || (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)))
        {
            HideTooltip();
            return;
        }

        if (!EnsureTooltip()) return;
        try
        {
            var titleTmp = _tipTitle!;
            var bodyTmp = _tipBody!;
            float inner = UiTooltipLayout.MaxWidth - 2f * UiTooltipLayout.Pad;
            titleTmp.text = UiRichText.Literal(title ?? "");
            bodyTmp.text = UiRichText.Literal(body ?? "");
            titleTmp.gameObject.SetActive(!string.IsNullOrWhiteSpace(title));
            bodyTmp.gameObject.SetActive(!string.IsNullOrWhiteSpace(body));

            Vector2 titlePref = titleTmp.gameObject.activeSelf ? titleTmp.GetPreferredValues(titleTmp.text, inner, 0f) : Vector2.zero;
            Vector2 bodyPref = bodyTmp.gameObject.activeSelf ? bodyTmp.GetPreferredValues(bodyTmp.text, inner, 0f) : Vector2.zero;
            var size = UiTooltipLayout.Size(Math.Min(titlePref.x, inner), titlePref.y, Math.Min(bodyPref.x, inner), bodyPref.y);

            float textWidth = size.Width - 2f * UiTooltipLayout.Pad;
            PlaceTipText(titleTmp, UiTooltipLayout.Pad, textWidth, titlePref.y);
            float bodyTop = UiTooltipLayout.Pad + (titlePref.y > 0f ? titlePref.y + UiTooltipLayout.TitleGap : 0f);
            PlaceTipText(bodyTmp, bodyTop, textWidth, Math.Max(0f, size.Height - bodyTop - UiTooltipLayout.Pad));

            var canvas = _tipCanvas!;
            Rect cr = canvas.rect;
            if (!ToCanvas(canvas, anchor.MinX, anchor.MinY, out var a) || !ToCanvas(canvas, anchor.MaxX, anchor.MaxY, out var b)) return;
            // P7b: kept inside the panel the hovered control is on (the workspace's page, a window) when it fits there.
            var area = UiTooltipLayout.AreaFor(anchor, ShownPanelRects());
            Vector2 am = new(float.NaN, float.NaN), ax = new(float.NaN, float.NaN);
            if (area.IsKnown && (!ToCanvas(canvas, area.MinX, area.MinY, out am) || !ToCanvas(canvas, area.MaxX, area.MaxY, out ax)))
            {
                am = new Vector2(float.NaN, float.NaN);
                ax = am;
            }

            var place = UiTooltipLayout.PlaceIn(a.x, a.y, b.x, b.y, size.Width, size.Height, cr.width, cr.height, am.x, am.y, ax.x, ax.y);
            var box = _tipBox!;
            box.sizeDelta = new Vector2(size.Width, size.Height);
            box.anchoredPosition = new Vector2(place.X, place.Y);
            if (!_tipRoot!.activeSelf) _tipRoot.SetActive(true);
            _tipStillShown = stillShown;
            _tipNeedsPanel = true;
            _tipWatch ??= EveryFrame(WatchTooltip);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: tooltip could not be shown: " + e.Message);
        }
    }

    private static bool AnyPanelShown()
    {
        try { return Ui.ShownPanelsSnapshot().Count > 0; }
        catch { return true; }
    }

    /// <summary>The screen rects of the shown panels (the tooltip's area candidates).</summary>
    private List<ScreenRect> ShownPanelRects()
    {
        var rects = new List<ScreenRect>();
        try
        {
            foreach (var panel in Ui.ShownPanelsSnapshot())
            {
                if (panel.IsDisposed || panel.Runtime is not { Alive: true } runtime) continue;
                var r = WidgetRect(runtime);
                if (r.IsKnown) rects.Add(r);
            }
        }
        catch { /* the canvas is the area */ }

        return rects;
    }

    /// <summary>Once a frame while the box is up: hide it when its control left the screen without a pointer exit.</summary>
    private void WatchTooltip()
    {
        bool keep;
        // P7b: a kit tooltip always belongs to a control on a shown panel; with none shown (Escape hid them all, the
        // workspace closed) it goes, whatever its own liveness check says.
        try { keep = Alive(_tipRoot) && _tipRoot!.activeSelf && (!_tipNeedsPanel || AnyPanelShown()) && (_tipStillShown == null || _tipStillShown()); }
        catch { keep = false; }
        if (!keep) HideTooltip();
    }

    public void HideTooltip()
    {
        _tipStillShown = null;
        _tipNeedsPanel = true;
        var watch = _tipWatch;
        _tipWatch = null;
        try { watch?.Dispose(); }
        catch { /* */ }
        try
        {
            if (Alive(_tipRoot) && _tipRoot!.activeSelf) _tipRoot.SetActive(false);
        }
        catch { /* */ }
    }

    /// <summary>A screen point in the tooltip canvas's bottom-left-origin units.</summary>
    private static bool ToCanvas(RectTransform canvas, float x, float y, out Vector2 local)
    {
        local = default;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvas, new Vector2(x, y), null, out var p)) return false;
        Rect r = canvas.rect;
        local = new Vector2(p.x - r.xMin, p.y - r.yMin);
        return true;
    }

    private static void PlaceTipText(TextMeshProUGUI tmp, float top, float width, float height)
    {
        var rt = tmp.rectTransform;
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(UiTooltipLayout.Pad, -top);
        rt.sizeDelta = new Vector2(width, Math.Max(1f, height));
    }

    private bool EnsureTooltip()
    {
        if (Alive(_tipRoot) && Alive(_tipBox) && _tipTitle != null && _tipBody != null) return true;
        DestroyTooltip();
        try
        {
            var root = new GameObject(TooltipRootName);
            UnityEngine.Object.DontDestroyOnLoad(root);
            var canvas = TryAdd<Canvas>(root);
            if (canvas == null)
            {
                TryDestroyImmediate(root);
                return false;
            }

            try { canvas.renderMode = RenderMode.ScreenSpaceOverlay; } catch { /* */ }
            try { canvas.overrideSorting = true; } catch { /* */ }
            try { canvas.sortingOrder = TooltipSortingOrder; } catch { /* */ }
            try { canvas.pixelPerfect = true; } catch { /* */ }
            var scaler = TryAdd<CanvasScaler>(root);
            if (scaler != null) AdoptGameScaler(scaler);
            // No GraphicRaycaster: the tooltip never takes the pointer, so it can never steal the hover it describes.

            var box = new GameObject("tooltip-box");
            box.transform.SetParent(root.transform, false);
            var boxRt = box.GetComponent<RectTransform>() ?? box.AddComponent<RectTransform>();
            boxRt.anchorMin = Vector2.zero;
            boxRt.anchorMax = Vector2.zero;
            boxRt.pivot = Vector2.zero;
            var back = TryAdd<Image>(box);
            if (back != null)
            {
                // The dropdown description box's look (DROPHOVER1): the game's frame sprite when it slices, else its dark fill.
                ApplyFrame(back, ToColor(DialogFrameColors.Frame));
                try { back.raycastTarget = false; } catch { /* */ }
            }

            // P7b: the title in the game's gold (the gold caption accent of the game's chips and sorted headers).
            _tipTitle = TipText(box, "tooltip-title", UiTooltipLayout.TitleFontSize, ToColor(MenuPalette.ChipOnBorder));
            _tipBody = TipText(box, "tooltip-body", UiTooltipLayout.BodyFontSize, ToColor(MenuPalette.Text));
            _tipRoot = root;
            _tipCanvas = root.GetComponent<RectTransform>();
            _tipBox = boxRt;
            root.SetActive(false);
            UiWidgetLifetime.Register("tooltip", DestroyTooltip);
            return _tipTitle != null && _tipBody != null && _tipCanvas != null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: tooltip box could not be built: " + e.Message);
            DestroyTooltip();
            return false;
        }
    }

    private static TextMeshProUGUI? TipText(GameObject box, string name, float size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(box.transform, false);
        var tmp = TryAdd<TextMeshProUGUI>(go);
        if (tmp == null) return null;
        ApplyControlTmp(tmp, "", size);
        ApplyCachedFont(tmp);
        try
        {
            tmp.enableWordWrapping = true;
            tmp.richText = true;
            tmp.horizontalAlignment = HorizontalAlignmentOptions.Left;
            tmp.verticalAlignment = VerticalAlignmentOptions.Top;
            tmp.color = color;
        }
        catch { /* */ }

        return tmp;
    }

    private static void DestroyTooltip()
    {
        _tipStillShown = null;
        _tipWatch = null; // the widget tick list is cleared by its own teardown
        var root = _tipRoot;
        _tipRoot = null;
        _tipCanvas = null;
        _tipBox = null;
        _tipTitle = null;
        _tipBody = null;
        if (Alive(root)) TryDestroyImmediate(root!);
    }

    // ---- Pointer and hover watches ---------------------------------------------------------------

    public IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner)
    {
        if (row == null || handler == null) return null;
        IRuntimeWidget? cell;
        try { cell = row.CellWidget(cellKey); }
        catch { cell = null; }
        var go = cell == null ? null : NavObject(cell);
        if (go == null) return null;
        var watch = UiPointerWatch.Create(go, handler, owner);
        UiPointerWatchRegistry.Add(owner, watch);
        return watch;
    }

    public IDisposable? WatchWidget(IRuntimeWidget widget, Action<UiPointerEvent> handler, object? owner)
    {
        if (widget == null || handler == null) return null;
        var go = NavObject(widget);
        if (go == null) return null;
        var watch = UiPointerWatch.Create(go, handler, owner);
        UiPointerWatchRegistry.Add(owner, watch);
        return watch;
    }

    public IDisposable? WatchHover(IRuntimeWidget widget, Action<bool> onHover, object? owner)
    {
        if (widget == null || onHover == null) return null;
        var go = NavObject(widget);
        if (go == null) return null;
        var watch = UiHoverWatch.Create(go, onHover, owner);
        UiPointerWatchRegistry.Add(owner, watch);
        return watch;
    }

    // ---- Frame ticks -------------------------------------------------------------------------------

    private static readonly List<Action> WidgetTicks = new();
    private static bool _widgetPumpOn;

    public IDisposable EveryFrame(Action tick)
    {
        if (tick == null) throw new ArgumentNullException(nameof(tick));
        WidgetTicks.Add(tick);
        if (!_widgetPumpOn)
        {
            try
            {
                Plugin.Frame += PumpWidgetTicks;
                _widgetPumpOn = true;
                UiWidgetLifetime.Register("widget-ticks", StopWidgetPump);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget frame tick could not start: " + e.Message); }
        }

        return new ActionDisposable(() =>
        {
            WidgetTicks.Remove(tick);
            if (WidgetTicks.Count == 0) StopWidgetPump();
        });
    }

    private static void PumpWidgetTicks()
    {
        foreach (var tick in WidgetTicks.ToArray())
        {
            try { tick(); }
            catch (Exception e)
            {
                WidgetTicks.Remove(tick);
                Plugin.Logger?.LogWarning("Ui: a widget frame tick threw and was removed: " + e.Message);
            }
        }

        if (WidgetTicks.Count == 0) StopWidgetPump();
    }

    private static void StopWidgetPump()
    {
        WidgetTicks.Clear();
        if (!_widgetPumpOn) return;
        _widgetPumpOn = false;
        try { Plugin.Frame -= PumpWidgetTicks; }
        catch { /* */ }
    }

    // ---- Glyphs, pointer, items --------------------------------------------------------------------

    public string DrawableGlyph(string preferred, string fallback)
    {
        if (string.IsNullOrEmpty(preferred)) return fallback ?? "";
        try
        {
            var font = FindFont();
            if (font == null) return preferred;
            foreach (char c in preferred)
                // tryAddCharacter false: never add a glyph to the game's shared font atlas from a check.
                if (c > 0x7F && !font.HasCharacter(c, true, false)) return fallback ?? "";
        }
        catch { return fallback ?? ""; }

        return preferred;
    }

    public bool PrimaryHeld() => PrimaryPointerHeld();

    public UiClickModifiers ClickModifiers() => HeldClickModifiers();

    private static readonly Dictionary<int, Sprite?> ItemIcons = new();
    private static Unity.Entities.World? _itemIconWorld;

    public object? ItemIcon(int prefab)
    {
        if (prefab == 0) return null;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            if (!ReferenceEquals(world, _itemIconWorld))
            {
                ItemIcons.Clear();
                _itemIconWorld = world;
            }

            if (ItemIcons.TryGetValue(prefab, out var cached) && (cached == null || Alive(cached))) return cached;
            Sprite? icon = null;
            var registry = world.GetExistingSystemManaged<GameDataSystem>()?.ManagedDataRegistry;
            if (registry != null && registry.TryGet<ManagedItemData>(new PrefabGUID(prefab), out var data) && data != null)
                icon = data.Icon;
            ItemIcons[prefab] = icon;
            return icon;
        }
        catch { return null; }
    }

    public string? ItemName(int prefab) => prefab == 0 ? null : Progenitor.Chat.ChatGameData.ItemName(prefab);
}

/// <summary>
/// P7: pointer Enter and Exit on a kit widget's own object, the hover half <see cref="UiPointerWatch"/> does not carry:
/// owned <c>EventTrigger</c> rows wired through the Bootstrap trampoline, removed on dispose.
/// </summary>
internal sealed class UiHoverWatch : IDisposable
{
    private readonly EventTrigger _trigger;
    private readonly bool _created;
    private readonly object? _owner;
    private readonly List<(EventTrigger.Entry Entry, ushort Id)> _rows = new();
    private bool _disposed;

    private UiHoverWatch(EventTrigger trigger, bool created, object? owner)
    {
        _trigger = trigger;
        _created = created;
        _owner = owner;
    }

    internal static UiHoverWatch Create(GameObject target, Action<bool> onHover, object? owner)
    {
        var trigger = target.GetComponent<EventTrigger>();
        bool created = trigger == null;
        trigger ??= target.AddComponent<EventTrigger>();
        var watch = new UiHoverWatch(trigger, created, owner);
        try
        {
            watch.Add(EventTriggerType.PointerEnter, () => onHover(true));
            watch.Add(EventTriggerType.PointerExit, () => onHover(false));
        }
        catch
        {
            watch.Dispose();
            throw;
        }

        return watch;
    }

    private void Add(EventTriggerType type, Action action)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        ushort id = UiSlotTable.Alloc(_owner);
        _rows.Add((entry, id));
        UiSlotTable.BindPointerEnter(id, action);
        _trigger.triggers.Add(entry);
        if (!BootstrapLink.TryWireUi(id, (byte)UiSlotKind.PointerEnter, entry.callback))
            throw new InvalidOperationException("Ui: hover watch trampoline unavailable for " + type);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UiPointerWatchRegistry.Forget(this);
        foreach (var (_, id) in _rows)
        {
            try { BootstrapLink.UnwireUi(id); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: hover watch unwire threw: " + e.Message); }
            UiSlotTable.Release(id);
        }

        if (!UnityAlive.Of(_trigger))
        {
            _rows.Clear();
            return;
        }

        try
        {
            foreach (var (entry, _) in _rows) _trigger.triggers.Remove(entry);
            if (_created && _trigger.triggers.Count <= 0)
            {
                _trigger.enabled = false;
                if (BootstrapLink.MayDestroyUiImmediate()) UnityEngine.Object.DestroyImmediate(_trigger);
                else UnityEngine.Object.Destroy(_trigger);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: hover watch teardown threw: " + e.Message);
        }

        _rows.Clear();
    }
}
#endif
