#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Il2CppInterop.Runtime;
using ProjectM.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Video;

namespace Progenitor.Ui;

/// <summary>
/// Client Unity backend for <see cref="Ui"/>. Clones the cataloged vanilla pieces from
/// evidence 88 (PassiveFrame_Normal panel, SimpleStunButton, Nocturne Serif TMP font)
/// onto a mod-owned overlay. Restructured from the spike; not a paste of the snippet.
/// </summary>
internal sealed partial class UnityUiRuntime : IUiRuntime
{
    public const float PanelWidth = UnityUiRuntimeMetrics.PanelWidth;

    /// <summary>
    /// Starting height only. Since evidence 163 the panel is resized to its content on
    /// <see cref="Show"/> (<see cref="PanelLayout.PanelHeightFor"/>), so this is what the frame
    /// is built at before any row exists, not what the player sees.
    /// </summary>
    public const float PanelHeight = 360f;

    public const float RowHeight = UnityUiRuntimeMetrics.RowHeight;
    public const float RowWidth = UnityUiRuntimeMetrics.RowWidth;

    private int _overlaySerial;

    /// <summary>
    /// Job LIST4 item 3: install the scroll-into-view measurement the handle layer calls
    /// through. Done from the static constructor because the first thing any pane does is build
    /// through this runtime, and a reveal asked for before the hook existed would silently
    /// answer "nothing to measure".
    /// </summary>
    static UnityUiRuntime()
    {
        UiReveal.Hook = TryRevealBlock;
        UiReveal.Anchor = TryAnchorRow;
        // INSTALLUX1 item 7: the read-only offset the Install pane's icon window follows.
        UiReveal.Offset = TryReadScrollOffset;
    }

    /// <summary>
    /// INSTALLUX1 item 7: how far down its content a panel's list is scrolled, in px. Read-only:
    /// it never writes the ScrollRect, so asking for it cannot move the list under the player.
    /// </summary>
    private static bool TryReadScrollOffset(IRuntimePanel panel, out float offsetY)
    {
        offsetY = 0f;
        if (panel is not UnityPanel host || !host.Alive) return false;
        var scroll = host.Scroll;
        if (scroll == null) return false;
        try
        {
            var content = scroll.content;
            if (content == null) return false;
            offsetY = content.anchoredPosition.y;
            if (offsetY < 0f) offsetY = 0f;
            return true;
        }
        catch
        {
            offsetY = 0f;
            return false;
        }
    }

    private static readonly List<UnityPanel> ShownPanels = new();

    /// <summary>
    /// INSTALLSIDES1: the live <see cref="CellKind.ScrollText"/> blocks (the Install drawer). The wheel
    /// pump asks these first: a notch over a block that can still move scrolls the block, and only a
    /// block at its end (or one whose text fits) lets the notch through to the list. Entries leave
    /// with their row (<see cref="UnityCellRow.Release"/>) and dead ones are pruned on every pass.
    /// </summary>
    private static readonly List<ScrollRect> InnerScrolls = new();

    /// <summary>WINDOWS1: an inner scroll block the wheel moves while the pointer is over it (the info column).</summary>
    private static void RegisterInnerScroll(ScrollRect scroll)
    {
        if (scroll == null) return;
        ForgetInnerScroll(scroll);
        InnerScrolls.Add(scroll);
    }

    private static void ForgetInnerScroll(ScrollRect? scroll)
    {
        if (scroll == null) return;
        for (int i = InnerScrolls.Count - 1; i >= 0; i--)
        {
            var s = InnerScrolls[i];
            bool same;
            try { same = s == null || s.Pointer == scroll.Pointer; }
            catch { same = true; }
            if (same) InnerScrolls.RemoveAt(i);
        }
    }

    /// <summary>
    /// INSTALLSIDES1: write one notch onto the scroll block under the pointer, when one is there and
    /// can move that way. False hands the notch to the list.
    /// </summary>
    private static bool TryScrollInner(PointerSample pointer, float deltaY, UnityPanel? hovered)
    {
        if (pointer.Kind != PointerKind.Inside || InnerScrolls.Count == 0) return false;
        // WINDOWS1 review: only a scroll block of the panel the wheel is over (ResolveHoveredPanel, draw order first).
        Transform? hoveredRoot = null;
        try { hoveredRoot = hovered?.GameObject != null ? hovered.GameObject.transform : null; }
        catch { hoveredRoot = null; }
        if (hoveredRoot == null) return false;
        for (int i = InnerScrolls.Count - 1; i >= 0; i--)
        {
            ScrollRect? scroll = InnerScrolls[i];
            bool alive;
            try { alive = scroll != null && Alive(scroll) && scroll.gameObject.activeInHierarchy; }
            catch { alive = false; }
            if (!alive)
            {
                bool dead;
                try { dead = scroll == null || !Alive(scroll); }
                catch { dead = true; }
                if (dead) InnerScrolls.RemoveAt(i);
                continue;
            }

            RectTransform? viewport = null, content = null;
            try { viewport = scroll!.viewport; content = scroll.content; }
            catch { /* */ }
            if (viewport == null || content == null) continue;
            bool mine;
            try { mine = scroll!.transform.IsChildOf(hoveredRoot); }
            catch { mine = false; }
            if (!mine) continue;
            ScreenRect rect;
            try { rect = ScreenRectOf(viewport); }
            catch { continue; }
            if (!rect.IsKnown || !rect.Contains(pointer.X, pointer.Y)) continue;
            float contentH = ContentHeightOf(content);
            float viewH = ViewportHeightOf(viewport);
            if (!MenuPanelLayout.CanApplyNormalized(contentH, viewH)) return false;
            float pos = MenuPanelLayout.NormalizedFromOffset(content.anchoredPosition.y, contentH, viewH);
            return ApplyScrollOffset(scroll!, deltaY, contentH, viewH, pos, out _);
        }

        return false;
    }

    private sealed class ArmedField
    {
        public TMP_InputField Field = null!;
        public UiTextEditState Owned;
        public bool HasOwned;

        // SEARCHCLEAR1: a workspace search box's clear button (null for every other field), its glyph and
        // the tint last written (0 rest, 1 hover, 2 pressed; -1 none yet), so an idle frame writes nothing.
        public GameObject? Clear;
        public TextMeshProUGUI? ClearText;
        public int ClearTint = -1;
        public GameObject? Caret;
        public Image? CaretGraphic;
        public long BlinkOriginMs;
        public int LastPlacedCaret = int.MinValue;
        public string LastPlacedText = "";

        // TEXTAREA1: the last caret measurement, reused while text, caret and text rect are
        // unchanged so a long text area is not re-wrapped every frame.
        public string MeasuredText = "";
        public int MeasuredCaret = int.MinValue;
        public float MeasuredKey = float.NaN;
        public float MeasuredX;
        public float MeasuredY;
        public float MeasuredHeight;
        public UiCaretSpot MeasuredSpot;

        // TEXTAREA2: where the caret's numbers came from ("tmp-lines", "measured", "stale").
        public string MeasuredSource = "";

        // TEXTAREA2: a hidden label that measures prefixes, so measuring never touches the field's
        // own text component (whose GetPreferredValues would re-parse a 4,096-character text).
        public TextMeshProUGUI? Measurer;

        // TEXTAREA2: the bar's last written placement; an idle focused field writes nothing.
        public bool Placed;
        public Vector2 PlacedAnchored;
        public Vector2 PlacedSize;

        // TEXTFILTER1: which characters this field accepts while typing.
        public UiCharFilter Filter;

        // TEXTEXPAND1: the field takes focus, caret moves, selection and copy, but no edit.
        public bool ReadOnly;

        // TEXTEXPAND1: the wheel or the scroll bar moved the text away from the caret: the caret
        // placement does not pull it back (and the caret hides while outside) until it or the text
        // changes.
        public bool ViewHeld;
        public bool CaretOutOfView;

        // TEXTEXPAND1: the expanded editor's scroll bar, or null.
        public EditorScrollBar? Bar;

        // TEXTUNDO1: this field's undo and redo steps (cleared on blur and on an outside change).
        public readonly UiTextUndoHistory Undo = new();
    }

    private static readonly List<ArmedField> _armedFields = new();
    private static int _searchOwnedCaret;
    private static int _searchOwnedAnchor;
    private static bool _searchHasOwned;
    private static TMP_InputField? _ownedFocusField;
    private static TMP_InputField? _repeatField;
    private static readonly UiKeyRepeat TypeRepeats = new();

    // TEXTUNDO1: the command each sampled key last decided to, so a modifier change mid-hold ends its repeat.
    private static readonly Dictionary<UiKey, int> RepeatCommands = new();

    /// <summary>
    /// Named keys only. Unity <c>Key.Digit0 = 50</c>, <c>LeftShift = 51</c>,
    /// <c>LeftCtrl = 55</c> — <c>(Key)(Digit0 + i)</c> is how Ctrl typed <c>5</c>.
    /// </summary>
    private static readonly UiKey[] TypedSampleKeys =
    {
        UiKey.Escape,
        UiKey.LeftShift, UiKey.RightShift, UiKey.LeftAlt, UiKey.RightAlt,
        UiKey.LeftCtrl, UiKey.RightCtrl, UiKey.LeftMeta, UiKey.RightMeta,
        UiKey.Backspace, UiKey.Delete, UiKey.LeftArrow, UiKey.RightArrow, UiKey.Home, UiKey.End,
        UiKey.Space, UiKey.Period, UiKey.Comma, UiKey.Minus, UiKey.Slash,
        // TEXTINPUT1: the rest of the US punctuation block and the numpad, for the physical-key
        // fallback (the Bootstrap text queue, when present, types these instead).
        UiKey.Backquote, UiKey.Equals, UiKey.LeftBracket, UiKey.RightBracket, UiKey.Backslash,
        UiKey.Semicolon, UiKey.Quote,
        UiKey.Numpad0, UiKey.Numpad1, UiKey.Numpad2, UiKey.Numpad3, UiKey.Numpad4,
        UiKey.Numpad5, UiKey.Numpad6, UiKey.Numpad7, UiKey.Numpad8, UiKey.Numpad9,
        UiKey.NumpadDivide, UiKey.NumpadMultiply, UiKey.NumpadPlus, UiKey.NumpadMinus,
        UiKey.NumpadPeriod, UiKey.NumpadEquals,
        UiKey.A, UiKey.B, UiKey.C, UiKey.D, UiKey.E, UiKey.F, UiKey.G, UiKey.H, UiKey.I, UiKey.J,
        UiKey.K, UiKey.L, UiKey.M, UiKey.N, UiKey.O, UiKey.P, UiKey.Q, UiKey.R, UiKey.S, UiKey.T,
        UiKey.U, UiKey.V, UiKey.W, UiKey.X, UiKey.Y, UiKey.Z,
        UiKey.Digit0, UiKey.Digit1, UiKey.Digit2, UiKey.Digit3, UiKey.Digit4,
        UiKey.Digit5, UiKey.Digit6, UiKey.Digit7, UiKey.Digit8, UiKey.Digit9,
        // SETTINGS1 stage 2: Enter starts a new line in a multi-line field (a single-line field
        // decides nothing for it, and TMP's own end-of-edit still commits that one).
        UiKey.Enter, UiKey.NumpadEnter,
    };
    private static readonly KeyEdgeTracker TypeEdges = new();
    private static bool _shownInputArmed;
    private static int _lastShownInputFrame = int.MinValue;
    private static bool _leftHeld;
    private static long _lastShownInputLogMs = long.MinValue;
    private static float _lastObservedPos = float.NaN;
    private static float _lastWheelDelta;
    private static bool _lastWheelHadDelta;
    private static float _pendingSidebarNormalized = float.NaN;
    private static float _pendingSidebarOffsetY;
    private static IntPtr _pendingContentRect;
    private static float _pendingContentOffsetY;
    private static long _pendingContentUntilMs;
    private static readonly List<UnityPanel> DeferredLayoutPanels = new();
    private static bool _layoutBatchBound;

    public bool IsClient => !Plugin.IsServerProcess;

    private static void EnsureLayoutBatchBound()
    {
        if (_layoutBatchBound) return;
        _layoutBatchBound = true;
        UiLayoutBatch.BindFlush(FlushDeferredLayouts);
    }

    private static void FlushDeferredLayouts()
    {
        for (int i = 0; i < DeferredLayoutPanels.Count; i++)
        {
            try { DeferredLayoutPanels[i].FlushDeferredLayout(); }
            catch { /* a failed panel must not skip the rest of the drain */ }
        }
        DeferredLayoutPanels.Clear();
    }

    /// <summary>Build an owned, sanitized native button without editing a MenuCollection.</summary>
    internal static IRuntimeWidget? CreateEscapeModsButton(
        EscapeMenuView view, RectTransform parent, Rect bounds, out GameObject? created)
    {
        created = null;
        GameObject? clone = null;
        try
        {
            // Clone the button leaf, never the EscapeMenuView or its collection.
            var donor = view.MenuCollection?.MenuButtonPrefab?.StunButton?.gameObject;
            if (donor == null) return null;
            clone = CloneButton(donor, parent.gameObject);
            if (clone == null) return null;
            // Set the cloned caption before checking whether the leaf can render a button.
            var caption = SetButtonText(clone, "Mods");
            if (caption != null)
            {
                var labelRect = caption.GetComponent<RectTransform>();
                if (labelRect != null)
                {
                    labelRect.anchorMin = Vector2.zero;
                    labelRect.anchorMax = Vector2.one;
                    labelRect.offsetMin = Vector2.zero;
                    labelRect.offsetMax = Vector2.zero;
                }
                var label = caption.GetComponent<TextMeshProUGUI>();
                var nativeLabel = view.MenuCollection?.MenuButtonPrefab?.ButtonText;
                if (label != null && nativeLabel != null)
                {
                    label.font = nativeLabel.font;
                    label.fontSize = nativeLabel.fontSize;
                    label.color = new Color(0.72f, 0.88f, 0.88f, 1f);
                }
            }
            if (!ButtonOperable(clone))
            {
                TryDestroyImmediate(clone);
                return null;
            }
            clone.name = EscapeModsEntry.ObjectName;
            var rect = clone.GetComponent<RectTransform>();
            if (rect == null) { TryDestroyImmediate(clone); return null; }
            var layout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            rect.anchorMin = parent.pivot;
            rect.anchorMax = parent.pivot;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(bounds.width, bounds.height);
            rect.anchoredPosition = bounds.center;
            rect.localScale = Vector3.one;
            var click = FindClickEvent(clone);
            if (click == null) { TryDestroyImmediate(clone); return null; }
            created = clone;
            // Activate only after the caller has successfully wired the Bootstrap slot.
            return new UnityWidget(null, clone, click, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Mods menu button build failed: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    public IRuntimePanel? CreatePanel(string title, string rootName)
    {
        try
        {
            // One sweep of the scene per panel, not one per row: everything below caches what it
            // finds (the frame sprite, the three donors) and this is the moment those answers are
            // allowed to be stale.
            ResetSceneCaches();

            var root = EnsureRoot(rootName);
            if (root == null) return null;
            // One overlay canvas per panel so Hide of A cannot blank sibling B
            // on the same plugin root (R24 #3). The panel GameObject is still
            // deactivated on Hide so EventTrigger cannot fire across a Core swap.
            var canvas = CreateOverlay(root);
            if (canvas == null) return null;

            var panelGo = new GameObject("panel");
            panelGo.transform.SetParent(canvas.transform, false);
            PlaceRect(panelGo, Vector2.zero, PanelWidth, PanelHeight);
            var image = TryAdd<Image>(panelGo);
            // A sprite with no 9-slice border does not slice — Image.Type.Sliced silently
            // falls back to stretching it — so the vanilla frame is only used when it can
            // survive the panel being resized to its content. Otherwise the panel wears a
            // flat ground, which looks deliberate at any height instead of like a frame
            // pulled out of shape (evidence 164). The rule now lives in ApplyFrame, because
            // the checkbox and the text field want exactly the same decision made about them.
            if (image != null) ApplyFrame(image, new Color(0.08f, 0.08f, 0.1f, 0.92f));

            DisableForeignBehaviours(panelGo);

            // The title is anchored to the panel's top edge, not offset from its center: the
            // panel is resized to its content on Show, so anything positioned from the center
            // would drift as rows are added (evidence 163).
            var titleGo = MakeLabel(panelGo, title, Vector2.zero, 28f);
            if (titleGo != null) PlaceTop(titleGo, 18f, RowWidth, RowHeight);

            // Rows go in a clipped, scrollable content rect rather than straight onto the frame.
            var viewport = MakeViewport(panelGo);
            var content = viewport == null ? null : MakeContent(viewport);
            GameObject? scrollbar = null;
            if (viewport != null && content != null) scrollbar = AddScroll(panelGo, viewport, content);

            // No owner: the overlay canvas is not a row, so writing to it must not trigger a
            // layout pass over rows it does not have.
            var overlayWidget = new UnityWidget(null, canvas, null, null, null, null, null, null);
            var panel = new UnityPanel(panelGo, titleGo, canvas, overlayWidget, viewport, content, scrollbar);
            panel.Text = title;
            return panel;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ui: CreatePanel threw: " + e);
            return null;
        }
    }

    public IRuntimeWidget? AddButton(IRuntimePanel panel, string text)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        if (host.Grid && !host.Inline && !host.IsStrip && !host.IsSidebar) return AddGridButton(host, "", text);
        try
        {
            // The fifth row type, and until R43 finding 3 the one that still reserved a constant
            // 40 px under whatever caption a plugin passed to IUiPanel.Button. A button's caption
            // lives inside a cloned vanilla subtree rather than in a label this code made, so
            // CloneOrCreateButton hands the TMP's own GameObject back to be measured like any
            // other row's label.
            var go = CloneOrCreateButton(host.InlineHostOrRowParent, text, out var caption, out var source, host.CaptionWantsLeft(text), host.IsStrip);
            if (go == null) return null;
            if (host.TryAttachInline(go))
            {
                // Settings organization live 2026-10-06: an inline Apply beside a settings-grid field kept the cloned
                // vanilla button's large caption; it takes the grid buttons' size (Show, View mods, Open).
                if (host.Grid) UseGridButtonCaption(caption);
                EnsurePointerEvents(go, out var inlineEnter, out var inlineExit);
                var inlineWidget = new UnityWidget(host, go, FindClickEvent(go), floatEvent: null, boolEvent: null, textEvent: null, inlineEnter, inlineExit);
                // INLINEAPPLY1 (user review 2026-10-06): an inline button on the settings grid (Apply beside a field, a mod
                // author's own) is the game's steel button like every other grid button, through the shared path that
                // retries until the borrowed art arrives (BUTTONART1), never the cloned donor's flat gray block.
                if (host.Grid) SetGameButton(inlineWidget);
                return inlineWidget;
            }
            if (host.IsStrip) PaintWorkspaceTab(go, host.ResolvedTone());
            else if (host.IsSidebar) PaintSidebarRow(go, host.ResolvedTone());
            host.AddRow(go, caption, hasControl: false, kind: "button", source: source);
            if (host.IsStrip) PrepareStripButtonCaption(caption);
            EnsurePointerEvents(go, out var enter, out var exit);
            return new UnityWidget(host, go, FindClickEvent(go), floatEvent: null, boolEvent: null, textEvent: null, enter, exit);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddButton threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// SETTINGS1 stage 2: a button on the settings grid — a normal-size button in the fixed control
    /// column, its caption inside it; the caption column is left for the help line. The button is the
    /// row's first child, so <see cref="UnityWidget.Text"/> reads its caption.
    /// </summary>
    public IRuntimeWidget? AddGridButton(IRuntimePanel panel, string name, string caption)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        if (!host.Grid) return AddButton(panel, caption);
        return AddGridButton(host, name, caption);
    }

    public IRuntimeWidget? AddPageButton(IRuntimePanel panel, string caption)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        // Review 2026-10-11: inside an inline block that already has a field or toggle row (BeginInline; Input; Button),
        // the button sits beside that control, as off a page; a row of page buttons would drop it under the field.
        if (host.HasInlineHost)
        {
            var inline = AddButton(panel, caption ?? "");
            if (inline != null) SetGameButton(inline);
            return inline;
        }

        try
        {
            var row = host.OpenPageButtonRow();
            bool fresh = row == null;
            if (row == null)
            {
                row = new GameObject("page-button-row");
                row.transform.SetParent(host.RowParent.transform, false);
                PlaceTop(row, 0f, host.ContentRowWidth, RowHeight);
            }

            var button = CloneOrCreateButton(row, caption ?? "", out var tmp, out var source, false, false);
            if (button == null) return null;
            UseGridButtonCaption(tmp);
            host.AddPageButton(row, button, caption ?? "");
            if (fresh) host.AddRow(row, null, hasControl: false, kind: "page-buttons", source: source);
            else host.Relayout();
            EnsurePointerEvents(button, out var enter, out var exit);
            var widget = new UnityWidget(host, button, FindClickEvent(button), floatEvent: null, boolEvent: null, textEvent: null, enter, exit);
            // The game's standard button (BUTTONART1: the borrowed steel art, retried until it arrives), caption centered.
            SetGameButton(widget);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddPageButton threw: " + e.Message);
            return null;
        }
    }

    /// <summary>The control font size every settings-grid control caption uses (a button's too).</summary>
    private const float GridControlFontSize = 16f;

    private IRuntimeWidget? AddGridButton(UnityPanel host, string name, string text)
    {
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("grid-button-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, host.ContentRowWidth, RowHeight);
            // The button first, so the row's first TMP (UnityWidget.Text, the bridge's label) is its
            // caption; the name follows in the caption column.
            var button = CloneOrCreateButton(go, text ?? "", out var caption, out var source, false, false);
            if (button == null) return null;
            FitInlineButtonCaption(button);
            // Live 2026-09-29: a cloned vanilla button kept its own large caption; a grid button
            // uses the control font size like every other control in the column.
            UseGridButtonCaption(caption);
            try
            {
                var rt = button.GetComponent<RectTransform>() ?? button.AddComponent<RectTransform>();
                rt.sizeDelta = new Vector2(columns.ControlWidth, ModSettingsGrid.ControlHeight);
            }
            catch { /* the relayout places it from the column model anyway */ }
            var label = string.IsNullOrEmpty(name) ? null : MakeLabel(go, name, Vector2.zero, 18f, left: true);
            host.AddRow(go, label, hasControl: true, kind: "button", source: source, control: button);
            EnsurePointerEvents(button, out var enter, out var exit);
            return new UnityWidget(host, go, FindClickEvent(button), null, null, null, enter, exit);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddGridButton threw: " + e.Message);
            return null;
        }
    }

    /// <summary>A grid button's caption: the control font size, no auto-size, middle-aligned.</summary>
    private static void UseGridButtonCaption(GameObject? caption)
    {
        try
        {
            var tmp = caption != null ? caption.GetComponent<TextMeshProUGUI>() : null;
            if (tmp == null) return;
            tmp.enableAutoSizing = false;
            tmp.fontSize = GridControlFontSize;
            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
        }
        catch { /* the caption keeps its size */ }
    }

    /// <summary>SETTINGS1 stage 2: how far a grayed row is dimmed (<see cref="IRuntimeWidget.Interactable"/>).</summary>
    internal const float GreyedAlpha = 0.45f;

    /// <summary>
    /// Strip captions are created at <see cref="RowWidth"/> (the whole strip). Left
    /// as raycast targets they overlap the neighbor to the right, so EventSystem
    /// delivers button i's click to button i+1 (Mods center → Install). Stretch to
    /// the button and drop TMP raycasts; the row Image is the hit target.
    /// </summary>
    private static void PrepareStripButtonCaption(GameObject? caption)
    {
        if (caption == null) return;
        try { Stretch(caption); }
        catch { /* */ }
        try
        {
            var tmp = caption.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                tmp.raycastTarget = false;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.margin = Vector4.zero;
            }
        }
        catch { /* */ }
    }

    /// <summary>
    /// Inline action buttons are sized after the caption TMP is created at
    /// <see cref="RowWidth"/>. Stretch so "Apply" / "Clear" sit inside their
    /// own 72 px rects instead of drawing as "ApplyClear" at the row origin
    /// while empty boxes sit on the right (gen 17).
    /// </summary>
    private static void FitInlineButtonCaption(GameObject? button)
    {
        if (button == null) return;
        try
        {
            var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null) PrepareStripButtonCaption(tmp.gameObject);
        }
        catch { /* the button rect is still the click target */ }
    }

    /// <summary>
    /// A row whose caption sits in the label column and whose checkbox sits in the control column.
    ///
    /// <para>
    /// Evidence 176. Until this, the row was one bare <c>Toggle</c> component and a caption pushed
    /// 80 px sideways to leave room for a checkbox that was never built. A <c>Toggle</c> owns no
    /// graphic of its own: the pointer reaches it only through a raycastable <c>Graphic</c> in its
    /// hierarchy (<c>targetGraphic</c>) and its state is shown only by the alpha
    /// <c>Toggle.PlayEffect</c> writes to <c>graphic</c>. With neither there was nothing to click
    /// and nothing that could have looked different if it had been clicked — while
    /// <c>Toggle.isOn</c> still read back correctly, so every check the framework had said yes.
    /// </para>
    ///
    /// <para>
    /// The caption is created <b>before</b> the control on purpose: <see cref="UnityWidget.Text"/>
    /// reads and writes a row's caption through <c>GetComponentInChildren&lt;TMP&gt;</c>, and a
    /// control that brings its own text (a cloned donor, a field's text component) must not be the
    /// first one found.
    /// </para>
    /// </summary>
    /// <summary>
    /// One full-width band of positioned cells (LIST1). The row object carries the fill (the
    /// raycast target and the selected color), a Button for the whole-row click, and the
    /// left stripe; each cell is a child placed by <see cref="MenuPanelLayout.CellColumns"/>
    /// from the panel's own layout pass, so a resize re-resolves the columns the same way
    /// the pinned listing header's do (UIHDR2). The header variant has no row click; its
    /// sortable cells are clickable instead.
    /// </summary>
    public IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
    {
        if (panel is not UnityPanel host || !host.Alive || spec == null) return null;
        GameObject? go = null;
        try
        {
            go = new GameObject(header ? "cellheader" : "cellrow");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, host.ContentRowWidth, spec.Height);
            var fill = TryAdd<Image>(go);
            if (fill == null) { TryDestroyImmediate(go); return null; }
            try { fill.sprite = null; } catch { /* */ }
            // SETTINGS1 stage 2: a band row (the mod page's status bar) is a solid bar in the existing
            // hover color, with no row-wide click or hover tint.
            try
            {
                fill.color = ToColor(header ? MenuPalette.HeaderFill
                    : spec.Section != CellSection.None ? MenuPalette.SectionBand(spec.Section).Fill
                    : spec.Band ? MenuPalette.RowHover : MenuPalette.RowFill);
            }
            catch { /* */ }
            try { fill.maskable = true; } catch { /* */ }
            try { fill.raycastTarget = true; } catch { /* */ }

            object? click = null;
            // RADIOCLICK1: the other rows get theirs from OnRowClick (UnityCellRow.EnsureRowClick).
            if (CellRowSpec.RowClickAtBuild(spec, header))
            {
                var button = TryAdd<Button>(go);
                if (button != null)
                {
                    try { button.targetGraphic = fill; } catch { /* */ }
                    PaintCellRowButton(button, selected: false, spec.Soft, spec.QuietHover);
                    click = button.onClick;
                }
            }

            // KEYS3: a row that reports hover gets the kit's existing pointer trigger (the one buttons
            // carry); every other row has none.
            object? enter = null, exit = null;
            if (!header && spec.Hover) EnsurePointerEvents(go, out enter, out exit);

            var row = new UnityCellRow(host, go, fill, click, spec, header, enter, exit);
            row.Build();
            host.RegisterCellRow(go, row, header);
            return row;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddCellRow threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    internal static Color ToColor(PaletteColor c) => new(c.R, c.G, c.B, c.A);

    /// <summary>
    /// Row hover / press tints on the row fill; the fill color itself carries the selected state.
    /// SETTINGS1 stage 3: a <see cref="CellRowSpec.Soft"/> row (the Mods list) hovers with a subtle
    /// light tint and is selected with a soft accent tint, never the near-black hover.
    /// </summary>
    private static void PaintCellRowButton(Button button, bool selected, bool soft = false, bool quietHover = false)
    {
        try
        {
            var colors = button.colors;
            var fill = soft ? ModsListLayout.RowFill(selected) : MenuPalette.RowFillFor(selected);
            var hover = quietHover && !selected ? MenuPalette.RowHoverQuiet
                : soft ? ModsListLayout.RowHover(selected) : selected ? MenuPalette.RowSelected : MenuPalette.RowHover;
            // Keep the graphic opaque white so a clear resting row can still
            // reveal its hover ground. Multiplying an alpha-zero graphic cannot.
            if (button.targetGraphic != null) button.targetGraphic.color = Color.white;
            colors.normalColor = ToColor(fill);
            colors.highlightedColor = ToColor(hover);
            colors.pressedColor = colors.highlightedColor;
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = colors.normalColor;
            // Newly created Graphics start with a white CanvasRenderer tint. Snap
            // the first state instead of fading that white across the whole row.
            colors.fadeDuration = 0f;
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }
        catch { /* */ }

    }

    /// <summary>
    /// KIT1: a sidebar row with a red marker in front of its caption and a state dot at its right
    /// edge. The row itself is the ordinary <see cref="AddButton"/> row, so it keeps the sidebar
    /// fill, the hover, the selected stripe and the click slot; the two parts are children placed
    /// against the row's own edges and the caption's margins are inset around them.
    /// </summary>
    public IRuntimeWidget? AddMarkedButton(IRuntimePanel panel, string caption, string marker, LabelTone? dot)
    {
        var widget = AddButton(panel, caption);
        if (widget is UnityWidget row) StyleMarkedRow(row.GameObject, marker, dot);
        return widget;
    }

    public void StyleMarkedButton(IRuntimeWidget widget, string marker, LabelTone? dot)
    {
        if (widget is UnityWidget row && row.Alive) StyleMarkedRow(row.GameObject, marker, dot);
    }

    private static void StyleMarkedRow(GameObject? go, string? marker, LabelTone? dot)
    {
        if (go == null || !Alive(go)) return;
        bool hasMarker = !string.IsNullOrEmpty(marker);
        try
        {
            var existing = go.transform.Find(MenuRowStyle.RowMarkerName);
            GameObject? markerGo = existing != null ? existing.gameObject : null;
            if (markerGo == null && hasMarker)
            {
                markerGo = MakeLabel(go, marker!, Vector2.zero, MenuRowStyle.RowMarkerFontSize, left: true);
                if (markerGo != null) markerGo.name = MenuRowStyle.RowMarkerName;
            }
            if (markerGo != null)
            {
                AnchorToSide(markerGo, left: true, size: MenuRowStyle.RowMarkerWidth, pad: MenuRowStyle.RowMarkPad);
                var tmp = markerGo.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    try { tmp.text = marker ?? ""; } catch { /* */ }
                    try { tmp.color = ToColor(MenuPalette.Red); } catch { /* */ }
                    try { tmp.fontStyle = FontStyles.Bold; } catch { /* */ }
                    try { tmp.raycastTarget = false; } catch { /* */ }
                    try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Left; } catch { /* */ }
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                }
                try { markerGo.SetActive(hasMarker); } catch { /* */ }
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row marker: " + e.Message); }

        try
        {
            var existing = go.transform.Find(MenuRowStyle.RowDotName);
            GameObject? dotGo = existing != null ? existing.gameObject : null;
            if (dotGo == null && dot != null)
            {
                dotGo = new GameObject(MenuRowStyle.RowDotName);
                dotGo.transform.SetParent(go.transform, false);
                var image = TryAdd<Image>(dotGo);
                if (image != null)
                {
                    try { image.raycastTarget = false; } catch { /* */ }
                    try { image.maskable = true; } catch { /* */ }
                }
            }
            if (dotGo != null)
            {
                AnchorToSide(dotGo, left: false, size: MenuRowStyle.RowDotSize, pad: MenuRowStyle.RowMarkPad, stretch: false);
                var image = dotGo.GetComponent<Image>();
                if (image != null && dot != null)
                {
                    try { image.color = ToColor(MenuPalette.LabelColor(dot.Value)); } catch { /* */ }
                }
                try { dotGo.SetActive(dot != null); } catch { /* */ }
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row dot: " + e.Message); }
    }

    /// <summary>
    /// Anchor <paramref name="go"/> against one side of its parent: full height when
    /// <paramref name="stretch"/> (the marker's text line), a fixed square otherwise (the dot).
    /// </summary>
    private static void AnchorToSide(GameObject go, bool left, float size, float pad, bool stretch = true)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            float x = left ? 0f : 1f;
            rt.anchorMin = new Vector2(x, stretch ? 0f : 0.5f);
            rt.anchorMax = new Vector2(x, stretch ? 1f : 0.5f);
            rt.pivot = new Vector2(x, 0.5f);
            rt.sizeDelta = new Vector2(size, stretch ? 0f : size);
            rt.anchoredPosition = new Vector2(left ? pad : -pad, 0f);
        }
        catch { /* the row still draws; only the part is misplaced */ }
    }

    /// <summary>
    /// KIT1: a small square icon-only button. <paramref name="attachToHeading"/> puts it in the
    /// previous row's right-hand column, which is where the mock's exit link sits; the panel refuses
    /// when there is no row to attach to or that row already owns a control, and the link then keeps
    /// the right-aligned row of its own that it was built on.
    /// </summary>
    public IRuntimeWidget? AddIconLink(IRuntimePanel panel, string glyph, string tooltip, bool attachToHeading)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = CloneOrCreateButton(host.RowParent, glyph ?? "", out var caption, out var source, left: false);
            if (go == null) return null;
            if (caption != null)
            {
                var tmp = caption.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    try { tmp.fontSize = MenuPanelLayout.IconLinkFontSize; } catch { /* */ }
                    try { tmp.enableWordWrapping = false; } catch { /* */ }
                    try { tmp.color = ToColor(MenuPalette.Gold); } catch { /* */ }
                    try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                    try { tmp.enableAutoSizing = false; } catch { /* */ }
                    try { tmp.margin = Vector4.zero; } catch { /* */ }
                }
            }
            NameIconLink(go, tooltip);
            EnsurePointerEvents(go, out var enter, out var exit);
            if (!host.AttachRightControl(go, MenuPanelLayout.IconLinkSize))
                host.AddRow(go, caption, hasControl: false, kind: "link", source: source);
            return new UnityWidget(host, go, FindClickEvent(go), null, null, null, enter, exit);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddIconLink threw: " + e.Message);
            return null;
        }
    }

    public bool SetIconSprite(IRuntimeWidget widget, object? sprite)
    {
        if (widget is not UnityWidget link || !link.Alive) return false;
        try
        {
            var image = link.GameObject.GetComponent<Image>();
            if (image == null) return false;
            var asSprite = AsSprite(sprite);
            image.sprite = asSprite;
            var caption = FindIn<TextMeshProUGUI>(link.GameObject);
            if (caption != null) caption.gameObject.SetActive(sprite == null);
            return asSprite != null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: icon sprite: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Name the link's object so a capture and the interaction probe can tell which link it is.
    ///
    /// <para>There is deliberately <b>no</b> hover tooltip: the vanilla clone carries no tooltip
    /// component this code may set a string on, and building one would be a new widget kind rather
    /// than a nicety. The tooltip string lives in the readback instead
    /// (<see cref="IUiIconLink.Describe"/>), which is where a reader and the bridge look — so an
    /// icon whose meaning is not obvious is still never a mystery.</para>
    /// </summary>
    private static void NameIconLink(GameObject go, string? tooltip)
    {
        try { go.name = string.IsNullOrEmpty(tooltip) ? "icon-link" : "icon-link: " + tooltip; }
        catch { /* the link still draws under whatever name the clone brought */ }
    }

    public IRuntimeWidget? AddTonedLabel(IRuntimePanel panel, string text, LabelTone tone)
    {
        var widget = AddLabel(panel, text);
        if (widget != null) StyleLabel(widget, tone);
        return widget;
    }

    public void StyleLabel(IRuntimeWidget widget, LabelTone tone)
    {
        if (widget is not UnityWidget label || !label.Alive) return;
        try
        {
            var tmp = FindIn<TextMeshProUGUI>(label.GameObject);
            if (tmp != null) tmp.color = ToColor(MenuPalette.LabelColor(tone));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: label tone: " + e.Message); }
    }

    public IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        if (host.Grid && !host.Inline) return AddGridCheckbox(host, text, initial);
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("toggle-row");
            go.transform.SetParent(host.RowParent.transform, false);
            // Provisional: the row cannot be placed until its label has been measured,
            // and the label cannot be measured until it is parented (evidence 164).
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 20f, left: host.IsLeftAligned);

            var control = MakeToggle(go, columns, initial, out var toggle, out var controlSource, host.IsMenuPane);
            host.AddRow(go, label, hasControl: control != null, kind: "toggle", source: controlSource, control: control);
            if (control == null || toggle == null)
                return new UnityWidget(host, go, null, null, null, null, null, null);

            return new UnityWidget(host, go, null, null, toggle.onValueChanged, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddToggle threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddLabel(IRuntimePanel panel, string text)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            // Measured, not assumed: a config entry's description wraps to two or three
            // lines, and a constant row step is what made the rows on screen overlap each
            // other into unreadable text (evidence 163).
            if (IsRedundantPaneTitle(host, text))
                return SkipRedundantPaneTitle(host);
            var go = MakeLabel(host.RowParent, text, Vector2.zero, UiTextStyles.BodySize, left: host.IsLeftAligned);
            if (go == null) return null;
            host.AddRow(go, go, hasControl: false, kind: "label", source: ControlSource.None);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddLabel threw: " + e.Message);
            return null;
        }
    }

    public void SetContentAlignment(IRuntimePanel panel, string alignment)
    {
        if (panel is UnityPanel host) host.ContentAlignment = SettingsAlign.Normalize(alignment);
    }

    public IRuntimeWidget? AddHeading(IRuntimePanel panel, string text)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = new GameObject("heading-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, host.ContentRowWidth, RowHeight);
            var label = MakeLabel(
                go, text, Vector2.zero, SettingsAlign.HeadingFontSize, left: host.IsLeftAligned);
            try
            {
                var tmp = label != null ? label.GetComponent<TextMeshProUGUI>() : null;
                if (tmp != null) tmp.fontStyle = FontStyles.Bold;
            }
            catch { /* */ }
            var rule = new GameObject("heading-rule");
            rule.transform.SetParent(go.transform, false);
            var image = TryAdd<Image>(rule);
            if (image != null)
            {
                try { image.color = new Color(1f, 1f, 1f, 0.22f); } catch { /* */ }
                try { image.raycastTarget = false; } catch { /* */ }
            }
            host.AddRow(go, label, hasControl: false, kind: "heading", source: ControlSource.None);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddHeading threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddMutedLabel(IRuntimePanel panel, string text)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = MakeLabel(
                host.RowParent, text, Vector2.zero, SettingsAlign.NoteFontSize,
                left: host.IsLeftAligned, muted: true);
            if (go == null) return null;
            host.AddRow(go, go, hasControl: false, kind: "note", source: ControlSource.None);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddMutedLabel threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// DIALOGS1: a dialog's title or body line. A plain label row with the dialog's own kind, so the
    /// layout pass measures it like every caption (TMP's wrapped height) but gives it the dialog's
    /// columns (<see cref="DialogLayout.TitleWidth"/> / <see cref="DialogLayout.BodyWidth"/>, centered)
    /// and never the heading's rule or extra top space. The title is bold in the Mods palette's text
    /// color; the body is the palette's muted color. Never dropped as a redundant pane title — a
    /// dialog's title is its pane's title on purpose.
    /// </summary>
    public IRuntimeWidget? AddDialogText(IRuntimePanel panel, string text, bool title) =>
        AddDialogTextRow(panel, text, title, note: false);

    /// <summary>DIALOGS3: the note under the facts — body text, a <see cref="DialogLayout.NoteTopMargin"/> band above it.</summary>
    public IRuntimeWidget? AddDialogNote(IRuntimePanel panel, string text) =>
        AddDialogTextRow(panel, text, title: false, note: true);

    private IRuntimeWidget? AddDialogTextRow(IRuntimePanel panel, string text, bool title, bool note)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = MakeLabel(
                host.RowParent, text, Vector2.zero,
                title ? DialogLayout.TitleFontSize : DialogLayout.BodyFontSize,
                left: false, muted: !title);
            if (go == null) return null;
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
            {
                try { tmp.enableWordWrapping = true; } catch { /* */ }
                // DIALOGS2: the game's popup text — title and body both 0.9 gray, regular weight, centered.
                try { tmp.color = new Color(DialogLayout.TextGrey, DialogLayout.TextGrey, DialogLayout.TextGrey, 1f); } catch { /* */ }
                try { tmp.fontStyle = FontStyles.Normal; } catch { /* */ }
                if (title)
                {
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Top; } catch { /* */ }
                }
                else if (note)
                {
                    // DIALOGS3: the row's extra band is the margin above the text, not air around it.
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Bottom; } catch { /* */ }
                }
            }

            // DIALOGS2: under the title, the game's thin steel rule with its center ornament (Divider_Big_02).
            if (title) AddDialogTitleRule(go);
            string kind = title ? DialogLayout.KindTitle : note ? DialogLayout.KindNote : DialogLayout.KindBody;
            host.AddRow(go, go, hasControl: false, kind: kind, source: ControlSource.None);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddDialogText threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddFact(IRuntimePanel panel, string name, string value)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = new GameObject("fact-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, host.ContentRowWidth, RowHeight);
            var label = MakeLabel(go, name ?? "", Vector2.zero, 18f, left: true);
            var valueGo = MakeLabel(go, value ?? "", Vector2.zero, 18f, left: host.IsLeftAligned);
            host.AddRow(go, label, hasControl: valueGo != null, kind: "fact", source: ControlSource.None, control: valueGo);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddFact threw: " + e.Message);
            return null;
        }
    }

    public void BeginInlineControls(IRuntimePanel panel)
    {
        if (panel is UnityPanel host) host.BeginInline();
    }

    public void EndInlineControls(IRuntimePanel panel)
    {
        if (panel is UnityPanel host) host.EndInline();
    }

    /// <summary>
    /// A row whose caption sits in the label column and whose bar sits in the control column.
    ///
    /// <para>
    /// Evidence 176. Until this, the row was one bare <c>Slider</c> component and a caption pushed
    /// 140 px sideways to leave room for a bar that was never built: no <c>fillRect</c>, no
    /// <c>handleRect</c>, no track. It held a value and drew nothing to grab.
    /// </para>
    /// </summary>
    public IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("slider-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);

            GameObject? control;
            Slider? slider;
            ControlSource controlSource;
            if (host.Grid)
            {
                // Live 2026-10-10 (controls sample): on the settings grid a bare slider was left where it was built, over
                // its own caption, because the grid's relayout moves a row's control container and this row had none. It
                // now sits in a container across the control column, its track the slider field's width so the two line up.
                float cw = columns.ControlWidth;
                float share = SliderFieldTrackShare;
                var container = NewControl(go, "slider", columns, cw, RowHeight);
                var trackCols = new RowColumns(columns.LabelWidth, columns.LabelX, cw * share, columns.ControlLeft + cw * share * 0.5f);
                var sliderGo = MakeSlider(container, trackCols, min, max, initial, out slider, out controlSource, host.IsMenuPane);
                if (sliderGo != null) AnchorInside(sliderGo, 0f, share, 0f, 0f);
                Activate(container);
                control = sliderGo == null ? null : container;
            }
            else
                control = MakeSlider(go, columns, min, max, initial, out slider, out controlSource, host.IsMenuPane);
            host.AddRow(go, label, hasControl: control != null, kind: "slider", source: controlSource, control: control);
            if (control == null || slider == null)
                return new UnityWidget(host, go, null, null, null, null, null, null);

            return new UnityWidget(host, go, null, slider.onValueChanged, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddSlider threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// A row whose caption sits in the label column and whose text field sits in the control
    /// column.
    ///
    /// <para>
    /// Evidence 176. Until this, the row was one bare <c>TMP_InputField</c> and a caption pushed
    /// 140 px sideways to leave room for a field that was never built. A field with no
    /// <c>textComponent</c> silently accepts nothing — every character it processes is written
    /// into that component — and with no background <c>Image</c> it was not even a raycast target,
    /// so it could never become the <c>EventSystem</c>'s selected object and could not be focused
    /// at all. That last part had a second casualty: <c>UnityKeyInputSource.TextFieldFocused</c>,
    /// the guard that stops a mod keybind firing while the player is typing (R35/R38), asks the
    /// <c>EventSystem</c> for its selected object and looks for a <c>TMP_InputField</c> on it. It
    /// was correct code protecting a case that could not occur.
    /// </para>
    /// </summary>
    IRuntimeWidget? IUiRuntime.AddInput(IRuntimePanel panel, string text, string initial)
        => AddInput(panel, text, initial, false);

    public IRuntimeWidget? AddSearchInput(IRuntimePanel panel, string text, string initial)
    {
        return AddInput(panel, text, initial, true);
    }

    public bool WireInputSubmit(IRuntimeWidget input, ushort slot)
    {
        if (input is not UnityWidget widget || widget.GameObject == null) return false;
        var field = FindIn<TMP_InputField>(widget.GameObject);
        return field != null && BootstrapLink.TryWireUi(slot, (byte)UiSlotKind.Text, field.onSubmit);
    }

    public void FocusInput(IRuntimeWidget input, bool selectAll)
    {
        if (input is not UnityWidget widget || widget.GameObject == null || !widget.GameObject.activeInHierarchy) return;
        var field = FindIn<TMP_InputField>(widget.GameObject);
        if (field == null) return;
        FocusOwnedField(field);
        if (selectAll) { field.selectionAnchorPosition = 0; field.selectionFocusPosition = (field.text ?? "").Length; }
    }

    public IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial, bool liveSearch = false)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            bool searchField = host.IsSidebar && host.ResolvedTone() == MenuRowTone.Search;
            var columns = searchField
                ? MenuPanelLayout.SearchFieldColumns(host.ContentRowWidth)
                : host.Columns(hasControl: true);
            var go = new GameObject(searchField ? MenuRowStyle.SearchRowName : "input-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, searchField ? host.ContentRowWidth : RowWidth, RowHeight);
            // Live 2026-10-06: an inline settings-grid field (Apply at its right) keeps its name in the name column; only a
            // search field and a plain inline field (the Configuration folder path) are caption-less.
            GameObject? label = ModSettingsRowGrid.InputDrawsCaption(searchField, host.Inline, host.Grid)
                ? MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned)
                : null;

            var control = MakeInput(
                go, columns, initial, out var field, out var controlSource, host.IsMenuPane || searchField,
                placeholder: null,
                searchField: searchField);
            host.AddRow(go, label, hasControl: control != null, kind: "input", source: controlSource, control: control, searchField: searchField);
            if (control == null || field == null)
                return new UnityWidget(host, go, null, null, null, null, null, null);
            if (searchField)
            {
                AttachSearchHints(control, field, initial);
                PaintSearchField(control, field);
                BringSearchHintToFront(control);
                // SEARCHCLEAR1: the clear button at the right while the box has text.
                AttachSearchClear(control, field);
            }

            // onEndEdit, never onValueChanged — for settings editors. Both are UnityEvent<string>,
            // so Bootstrap wires either the same way, but onValueChanged fires once per character:
            // the panel then parsed, wrote, saved the whole cfg and pushed the parsed value back
            // into the field on every keystroke, which makes "1.5" and "-3" literally untypeable
            // ("1." parses as 1 and the dot is erased) — R34 finding 1. onEndEdit fires once,
            // on Enter or when the field loses focus, which is the commit the panel wants.
            //
            // The Mods sidebar Search field is the exception: it must filter on every character.
            // The Bootstrap text slot's UnityAction is created once and AddListener'd onto
            // whichever event is supplied at first TryWire (UiHandles.AddChild). A later
            // TryWire onto a different TMP event recycles that same UnityAction and does not
            // replace the native listener, so the live event has to be the one bound here.
            object textEvent = field.onEndEdit;
            if (host.UsesLiveText || liveSearch)
                textEvent = LiveTextEvent(field);
            ArmTextField(go, field);
            return new UnityWidget(host, go, null, null, null, textEvent, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddInput threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddChoice(
        IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("choice-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);
            var state = new UiChoiceState(options, value);
            var control = BuildChoiceControl(go, columns, state, out var source, out var valueClick, out var prevGo, out var nextGo);
            host.AddRow(go, label, hasControl: control != null, kind: "choice", source: source, control: control);
            // DROPDOWN1: the value box opens the list under it (each option and the click-catcher behind it are parts);
            // the off-grid selector's arrows step back and forth (the dropdown skin hides them). Each part has its own
            // slot (IRuntimePartsWidget); the value box is the row's click.
            var widget = new UnityChoiceWidget(host, go, valueClick);
            widget.AddPart(UiControlParts.Previous, prevGo);
            widget.AddPart(UiControlParts.Next, nextGo);
            if (control != null) widget.BuildList(control, options);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddChoice threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddNumeric(
        IRuntimePanel panel, string text, double value, double min, double max, double step, int decimals, string units)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("numeric-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);
            var state = new UiNumericState(value, min, max, step, decimals, units);
            var control = BuildNumericControl(go, columns, state, out var field, out var source, out var minusGo, out var plusGo);
            if (field != null) ArmTextField(go, field);
            // TEXTFILTER1: a number box types digits, one leading sign and (with decimals) one point.
            SetFieldFilter(field, UiCharFilters.ForDecimals(decimals));
            host.AddRow(go, label, hasControl: control != null, kind: "numeric", source: source, control: control);
            object? textEvent = field == null ? null : field.onEndEdit;
            // The field commits on end-of-edit (the row's Text slot); each stepper is a part.
            var widget = new UnityPartsWidget(host, go, null, null, null, textEvent, null, null);
            widget.AddPart(UiControlParts.Minus, minusGo);
            widget.AddPart(UiControlParts.Plus, plusGo);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddNumeric threw: " + e.Message);
            return null;
        }
    }

    /// <summary>The slider field's track as a share of the control column (its value box takes the rest).</summary>
    private const float SliderFieldTrackShare = 0.62f;

    public IRuntimeWidget? AddSliderEntry(
        IRuntimePanel panel, string text, float min, float max, float step, float initial)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("slider-entry-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);

            float cw = columns.ControlWidth;
            float sliderW = cw * SliderFieldTrackShare;
            float inputW = cw - sliderW - 6f;
            if (inputW < 48f)
            {
                inputW = 48f;
                sliderW = cw - inputW - 6f;
            }

            var sliderCols = new RowColumns(
                columns.LabelWidth, columns.LabelX, sliderW, columns.ControlLeft + sliderW * 0.5f);
            var inputCols = new RowColumns(
                columns.LabelWidth, columns.LabelX, inputW, columns.ControlLeft + sliderW + 6f + inputW * 0.5f);

            // SETTINGS1: one container spans the control column and is the row's control, so the menu
            // pane's relayout (which stretches a row's control across the column) moves the pair as
            // one; the slider and the field are anchored inside it by fraction, not by build-time x.
            var container = NewControl(go, "slider-entry", columns, cw, RowHeight);
            var sliderGo = MakeSlider(container, sliderCols, min, max, initial, out var slider, out var sliderSource, host.IsMenuPane);
            var inputGo = MakeInput(
                container, inputCols, UiNumericParse.Format(initial, UiSliderEntryState.StepDecimals(step)),
                out var field, out var inputSource, host.IsMenuPane);
            if (field != null) ArmTextField(go, field);
            SetFieldFilter(field, UiCharFilters.ForDecimals(UiSliderEntryState.StepDecimals(step)));
            float split = cw > 0f ? sliderW / cw : 0.62f;
            if (sliderGo != null) AnchorInside(sliderGo, 0f, split, 0f, 0f);
            if (inputGo != null) AnchorInside(inputGo, split, 1f, 6f, 0f);
            Activate(container);

            ControlSource source = sliderSource;
            if (ControlBuildPlan.IsFailure(sliderSource) && !ControlBuildPlan.IsFailure(inputSource))
                source = inputSource;
            GameObject? control = sliderGo == null && inputGo == null ? null : container;
            host.AddRow(go, label, hasControl: control != null, kind: "slider-entry", source: source, control: control);
            // slider.onValueChanged fires per drag sample: the handle previews each sample and
            // commits once the slider has settled (UiSliderEntryHandle.Sample), never per frame.
            // The exact field commits on end-of-edit through the same id's Text slot.
            object? floatEvent = slider == null ? null : slider.onValueChanged;
            object? textEvent = field == null ? null : field.onEndEdit;
            return new UnityWidget(host, go, null, floatEvent, null, textEvent, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddSliderEntry threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddColorPicker(IRuntimePanel panel, string text, UiColor initial, bool alpha)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("color-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);
            var state = new UiColorState(initial, alpha);
            var control = BuildColorControl(go, columns, state, out var field, out var source);
            if (field != null) ArmTextField(go, field);
            // TEXTFILTER1: a hex box types one leading # and hex digits, #RRGGBB (#RRGGBBAA with alpha).
            SetFieldFilter(field, UiCharFilter.Hex);
            try { if (field != null) field.characterLimit = UiCharFilters.HexMaxLength(alpha); } catch { /* */ }
            host.AddRow(go, label, hasControl: control != null, kind: "color", source: source, control: control);
            object? textEvent = field == null ? null : field.onEndEdit;
            return new UnityWidget(host, go, null, null, null, textEvent, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddColorPicker threw: " + e.Message);
            return null;
        }
    }

    public IRuntimeWidget? AddKeybindPicker(IRuntimePanel panel, string text, string pluginId, string bindId)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("keybind-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);
            var state = new UiKeybindPickerState(pluginId, bindId);
            var control = BuildKeybindControl(go, columns, state, out var source);
            host.AddRow(go, label, hasControl: control != null, kind: "keybind", source: source, control: control);
            // A click on the control starts the capture (UiKeybindPickerHandle.Attach).
            object? click = control == null ? null : FindClickEvent(control);
            return new UnityWidget(host, go, click, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddKeybindPicker threw: " + e.Message);
            return null;
        }
    }

    // ---- SETTINGS1 stage 2 -------------------------------------------------------------------

    public void SetSettingsGrid(IRuntimePanel panel, bool on)
    {
        if (panel is UnityPanel host) host.Grid = on;
    }

    public void SetRowHelp(IRuntimeWidget row, string help)
    {
        if (row is not UnityWidget widget || !widget.Alive || widget.Owner is not { } host) return;
        try { host.SetRowHelp(widget.GameObject, help ?? ""); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: SetRowHelp threw: " + e.Message); }
    }

    /// <summary>
    /// TEXTAREA1: TMP's <c>characterLimit</c> on the row's field. The typed-key pump reads it back
    /// (<see cref="FieldMaxLength"/>), so typing and paste stop there as TMP's own input would.
    /// </summary>
    public void SetInputLimit(IRuntimeWidget input, int characters)
    {
        if (input is not UnityWidget widget || widget.GameObject == null) return;
        try
        {
            var field = FindIn<TMP_InputField>(widget.GameObject);
            if (field != null && field.characterLimit != characters) field.characterLimit = characters;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: SetInputLimit threw: " + e.Message); }
    }

    /// <summary>Settings organization review: TMP's password content type (the field draws asterisks).</summary>
    public void SetInputMasked(IRuntimeWidget input, bool masked)
    {
        if (input is not UnityWidget widget || widget.GameObject == null) return;
        try
        {
            var field = FindIn<TMP_InputField>(widget.GameObject);
            if (field == null) return;
            field.contentType = masked ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            field.ForceLabelUpdate();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: SetInputMasked threw: " + e.Message); }
    }

    /// <summary>
    /// NOPROGMSG1: the field's muted hint while empty, under the same hint root the search field uses (the typed-key pump's
    /// <c>SyncSearchHint</c> hides it once there is text). In a text area it wraps from the top-left.
    /// </summary>
    public void SetInputPlaceholder(IRuntimeWidget input, string text)
    {
        if (input is not UnityWidget widget || widget.GameObject == null) return;
        try
        {
            var field = FindIn<TMP_InputField>(widget.GameObject);
            if (field == null) return;
            var fieldGo = field.gameObject;
            var old = fieldGo.transform.Find(MenuRowStyle.SearchHintName);
            if (old != null) TryDestroyImmediate(old.gameObject);
            if (string.IsNullOrEmpty(text)) return;
            var root = new GameObject(MenuRowStyle.SearchHintName);
            root.transform.SetParent(fieldGo.transform, false);
            Stretch(root);
            AttachPlaceholder(root, field, text);
            bool multi = false;
            try { multi = field.lineType != TMP_InputField.LineType.SingleLine; } catch { /* */ }
            var tmp = root.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null)
            {
                try
                {
                    tmp.fontSize = GridControlFontSize;
                    tmp.enableWordWrapping = multi;
                    tmp.overflowMode = multi ? TextOverflowModes.Truncate : TextOverflowModes.Ellipsis;
                    tmp.verticalAlignment = multi ? VerticalAlignmentOptions.Top : VerticalAlignmentOptions.Middle;
                }
                catch { /* the hint keeps the search look */ }
            }

            bool show = MenuRowStyle.SearchHintVisible(field.text);
            try { root.SetActive(show); } catch { /* */ }
            try { root.transform.SetAsLastSibling(); } catch { /* */ }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: SetInputPlaceholder threw: " + e.Message); }
    }

    /// <summary>TEXTFILTER1: the row field's character filter, read by every typing path.</summary>
    public void SetInputFilter(IRuntimeWidget input, UiCharFilter filter)
    {
        if (input is not UnityWidget widget || widget.GameObject == null) return;
        try { SetFieldFilter(FindIn<TMP_InputField>(widget.GameObject), filter); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: SetInputFilter threw: " + e.Message); }
    }

    /// <summary>
    /// SETTINGS1 stage 2: the reset link in <paramref name="row"/>'s gutter. Built like
    /// <see cref="AddIconLink"/>; a row this panel does not know gets the stage 1 placement.
    /// </summary>
    public IRuntimeWidget? AddGutterLink(IRuntimePanel panel, IRuntimeWidget row, string glyph, string tooltip)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        if (row is not UnityWidget target || !target.Alive) return AddIconLink(panel, glyph, tooltip, attachToHeading: true);
        try
        {
            var go = CloneOrCreateButton(host.RowParent, glyph ?? "", out var caption, out var source, left: false);
            if (go == null) return null;
            if (caption != null)
            {
                var tmp = caption.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    try { tmp.fontSize = MenuPanelLayout.IconLinkFontSize; } catch { /* */ }
                    try { tmp.enableWordWrapping = false; } catch { /* */ }
                    try { tmp.color = ToColor(MenuPalette.Gold); } catch { /* */ }
                    try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                    try { tmp.enableAutoSizing = false; } catch { /* */ }
                    try { tmp.margin = Vector4.zero; } catch { /* */ }
                }
            }
            NameIconLink(go, tooltip);
            if (host.Grid) SkinResetArrow(go, caption);
            EnsurePointerEvents(go, out var enter, out var exit);
            if (!host.AttachGutter(target.GameObject, go, host.Grid ? ModSettingsRowGrid.ResetSize : MenuPanelLayout.IconLinkSize))
                host.AddRow(go, caption, hasControl: false, kind: "link", source: source);
            return new UnityWidget(host, go, FindClickEvent(go), null, null, null, enter, exit);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddGutterLink threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// TEXTEXPAND1: the expand button in the bottom-right corner of <paramref name="row"/>'s text field;
    /// the field's text viewport is narrowed so no text runs under it. A row without a field (a
    /// read-only value) gets the gutter link instead.
    /// </summary>
    public IRuntimeWidget? AddCornerLink(IRuntimePanel panel, IRuntimeWidget row, string glyph, string tooltip)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        TMP_InputField? field = null;
        if (row is UnityWidget target && target.Alive && target.GameObject != null)
        {
            try { field = FindIn<TMP_InputField>(target.GameObject); }
            catch { field = null; }
        }

        if (field == null) return AddGutterLink(panel, row, ChooseGlyph(glyph, null), tooltip);
        GameObject? go = null;
        try
        {
            go = CloneOrCreateButton(host.RowParent, glyph ?? "", out var caption, out _, left: false);
            if (go == null) return AddGutterLink(panel, row, glyph ?? "", tooltip);
            go.transform.SetParent(field.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.sizeDelta = new Vector2(UiTextExpandLayout.ButtonSize, UiTextExpandLayout.ButtonSize);
            rt.anchoredPosition = new Vector2(-UiTextExpandLayout.ButtonInset, UiTextExpandLayout.ButtonInset);
            var tmp = caption != null ? caption.GetComponent<TextMeshProUGUI>() : null;
            if (tmp != null)
            {
                try { tmp.text = ChooseGlyph(glyph, tmp); } catch { /* */ }
                try { tmp.fontSize = 16f; } catch { /* */ }
                try { tmp.enableWordWrapping = false; } catch { /* */ }
                try { tmp.enableAutoSizing = false; } catch { /* */ }
                try { tmp.color = ToColor(MenuPalette.Gold); } catch { /* */ }
                try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
                try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                try { tmp.margin = Vector4.zero; } catch { /* */ }
                try
                {
                    var crt = tmp.rectTransform;
                    crt.anchorMin = Vector2.zero;
                    crt.anchorMax = Vector2.one;
                    crt.offsetMin = Vector2.zero;
                    crt.offsetMax = Vector2.zero;
                }
                catch { /* */ }
            }

            NameIconLink(go, tooltip);
            EnsurePointerEvents(go, out var enter, out var exit);
            KeepTextClearOfCorner(field);
            CornerButtons.Add(go);
            Activate(go);
            try { go.transform.SetAsLastSibling(); } catch { /* */ }
            return new UnityWidget(host, go, FindClickEvent(go), null, null, null, enter, exit);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddCornerLink threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>
    /// SETTINGS1 stage 2: a text field <paramref name="lines"/> lines tall. The field is built like
    /// <see cref="AddInput"/>, then made multi-line (TMP <c>MultiLineNewline</c>, so Enter does not end
    /// the edit; the typed-key pump inserts the new line) and given the height its lines need. Its
    /// text event is the end-of-edit, raised when the field loses focus.
    /// </summary>
    public IRuntimeWidget? AddTextArea(IRuntimePanel panel, string text, string initial, int lines)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            int n = Math.Max(2, Math.Min(12, lines));
            float height = TextAreaHeight(n);
            var columns = host.Columns(hasControl: true);
            var go = new GameObject("textarea-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, height);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);
            var control = MakeInput(go, columns, initial, out var field, out var source, host.IsMenuPane);
            if (control == null || field == null)
            {
                host.AddRow(go, label, hasControl: false, kind: "textarea", source: source);
                return new UnityWidget(host, go, null, null, null, null, null, null);
            }

            try { field.lineType = TMP_InputField.LineType.MultiLineNewline; } catch { /* */ }
            try
            {
                var tmp = field.textComponent;
                if (tmp != null)
                {
                    tmp.enableWordWrapping = true;
                    tmp.verticalAlignment = VerticalAlignmentOptions.Top;
                }
            }
            catch { /* */ }
            try
            {
                var rt = control.GetComponent<RectTransform>();
                if (rt != null) rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
            }
            catch { /* */ }
            ArmTextField(go, field);
            host.SetMinRowHeight(go, height);
            host.AddRow(go, label, hasControl: true, kind: "textarea", source: source, control: control);
            return new UnityWidget(host, go, null, null, null, field.onEndEdit, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddTextArea threw: " + e.Message);
            return null;
        }
    }

    /// <summary>A text area's height for <paramref name="lines"/> lines of the 18 px field text.</summary>
    internal static float TextAreaHeight(int lines) => Math.Max(InputHeight, lines * 22f + 2f * InputPadY + 4f);

    /// <summary>Height of one option in a list-layout options control.</summary>
    private const float OptionLineHeight = 30f;

    /// <summary>
    /// SETTINGS1 stage 2: a radio group or multi-select. The control column holds one framed button
    /// per option — side by side for <see cref="UiOptionsLayout.Segmented"/>, one per line for
    /// <see cref="UiOptionsLayout.List"/> (the row grows to hold them). Each option is a part keyed
    /// <see cref="UiOptionsRules.PartKey"/>; the handle repaints them through <see cref="SetOptionStates"/>.
    /// </summary>
    public IRuntimeWidget? AddOptions(
        IRuntimePanel panel, string text, IReadOnlyList<UiChoiceOption> options, bool multi, UiOptionsLayout layout)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            int n = options?.Count ?? 0;
            bool list = layout == UiOptionsLayout.List;
            // RADIO1: a radio list's lines are taller when an option carries an explanation.
            bool radioList = list && !multi;
            float line = radioList ? UiOptionsRules.LineHeight(options, multi: false) : OptionLineHeight;
            float height = list ? Math.Max(RowHeight, n * line + 4f) : RowHeight;
            var columns = host.Columns(hasControl: true);
            var go = new GameObject(multi ? "multiselect-row" : "radio-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, RowWidth, height);
            var label = MakeLabel(go, text, Vector2.zero, 18f, left: host.IsLeftAligned);
            var container = NewControl(go, multi ? "multiselect" : "radio", columns, columns.ControlWidth, height);
            var widget = new UnityOptionsWidget(host, go, multi, layout);
            var radioLines = new List<(GameObject Go, TextMeshProUGUI? Description)>();
            for (int i = 0; i < n; i++)
            {
                if (radioList)
                {
                    var radio = BuildRadioLine(container, i, n, options![i].Label, options[i].Description);
                    if (radio.Go == null) continue;
                    widget.AddRadioLine(UiOptionsRules.PartKey(i), radio.Go, radio.Mark, radio.Caption, radio.Description, options[i].Label);
                    radioLines.Add((radio.Go, radio.Description));
                    continue;
                }

                var option = BuildOptionButton(container, i, n, list, options![i].Label);
                if (option.Go == null) continue;
                widget.AddOption(UiOptionsRules.PartKey(i), option.Go, option.Fill, option.Caption, option.Mark, options[i].Label);
            }

            Activate(container);
            // RADIODESC1: a described radio list's lines take the heights their wrapped explanations need.
            if (radioList && UiOptionsRules.AnyDescription(options))
            {
                height = Math.Max(RowHeight, PlaceRadioLines(radioLines, columns.ControlWidth));
                try
                {
                    var rowRt = go.GetComponent<RectTransform>();
                    if (rowRt != null) rowRt.sizeDelta = new Vector2(rowRt.sizeDelta.x, height);
                    var ctlRt = container.GetComponent<RectTransform>();
                    if (ctlRt != null) ctlRt.sizeDelta = new Vector2(ctlRt.sizeDelta.x, height);
                }
                catch { /* the next layout pass sizes them from the row height */ }
            }

            if (list) host.SetMinRowHeight(go, height);
            host.AddRow(go, label, hasControl: true, kind: multi ? "multiselect" : "radio", source: ControlSource.Built, control: container);
            return widget;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddOptions threw: " + e.Message);
            return null;
        }
    }

    /// <summary>
    /// RADIO1: one line of a radio list, anchored by fraction inside the options container: a transparent click
    /// target holding the radio circle (a game-font glyph, filled for the chosen option), the label after it and,
    /// when given, a one-line muted explanation under the label. The state is painted by
    /// <see cref="UnityOptionsWidget.Apply"/>.
    /// </summary>
    private static (GameObject? Go, TextMeshProUGUI? Mark, TextMeshProUGUI? Caption, TextMeshProUGUI? Description) BuildRadioLine(
        GameObject container, int index, int count, string label, string description)
    {
        try
        {
            var go = new GameObject("option-" + index.ToString(CultureInfo.InvariantCulture));
            go.transform.SetParent(container.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            float c = Math.Max(1, count);
            rt.anchorMin = new Vector2(0f, 1f - (index + 1) / c);
            rt.anchorMax = new Vector2(1f, 1f - index / c);
            rt.offsetMin = new Vector2(0f, 2f);
            rt.offsetMax = new Vector2(0f, -2f);

            var image = TryAdd<Image>(go);
            if (image == null) return (null, null, null, null);
            try { image.sprite = null; } catch { /* */ }
            try { image.color = new Color(0f, 0f, 0f, 0.01f); } catch { /* */ }
            try { image.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button == null) return (null, null, null, null);
            try { button.targetGraphic = image; } catch { /* */ }

            bool described = !string.IsNullOrEmpty(description);
            // RADIOCLICK1: the cell-row radio list's metrics (UiRadioList): a described line puts its label in the top
            // LineHeight px and the explanation DescriptionGap px under it; the rest of the pitch is the option gap. A
            // plain line centers its label in the line.
            if (described)
            {
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }

            TextMeshProUGUI? Text(string name, string text, float size, float top, float height, float left)
            {
                var textGo = new GameObject(name);
                textGo.transform.SetParent(go.transform, false);
                var trt = textGo.GetComponent<RectTransform>() ?? textGo.AddComponent<RectTransform>();
                if (height > 0f)
                {
                    trt.anchorMin = new Vector2(0f, 1f);
                    trt.anchorMax = new Vector2(1f, 1f);
                    trt.pivot = new Vector2(0.5f, 1f);
                    trt.offsetMin = new Vector2(left, -(top + height));
                    trt.offsetMax = new Vector2(-4f, -top);
                }
                else
                {
                    trt.anchorMin = Vector2.zero;
                    trt.anchorMax = Vector2.one;
                    trt.offsetMin = new Vector2(left, 0f);
                    trt.offsetMax = new Vector2(-4f, 0f);
                }
                var tmp = TryAdd<TextMeshProUGUI>(textGo);
                if (tmp == null) return null;
                ApplyControlTmp(tmp, text, size);
                ApplyCachedFont(tmp);
                ApplyHorizontalAlign(tmp, left: true);
                try { tmp.enableWordWrapping = false; } catch { /* */ }
                try { tmp.raycastTarget = false; } catch { /* */ }
                ClampCaptionText(textGo);
                return tmp;
            }

            float labelHeight = described ? UiRadioList.LineHeight : 0f;
            var mark = Text("radio-mark", UiOptionsRules.RadioMark(false), UiRadioList.MarkFontSize, 0f, labelHeight, RadioMarkInset);
            var caption = Text("caption", label ?? "", UiRadioList.LabelFontSize, 0f, labelHeight, RadioCaptionInset);
            var detail = described
                ? Text("description", description, UiRadioList.AboutFontSize, UiRadioList.LineHeight + UiRadioList.DescriptionGap,
                    UiRadioList.AboutLineHeight, RadioCaptionInset)
                : null;
            // RADIODESC1: the explanation wraps (up to UiRadioList.MaxAboutLines; PlaceRadioLines sizes it to its text).
            if (detail != null)
            {
                try { detail.enableWordWrapping = true; } catch { /* */ }
                try { detail.verticalAlignment = VerticalAlignmentOptions.Top; } catch { /* */ }
            }
            FitRadioMark(mark);
            return (go, mark, caption, detail);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a radio line threw: " + e.Message);
            return (null, null, null, null);
        }
    }

    /// <summary>
    /// RADIODESC1 (live 2026-10-06: the Server tab's radio explanations were cut to one line): measure each explanation
    /// at the line's text width (TMP's preferred height, word-wrapped), give it that many lines
    /// (<see cref="UiOptionsRules.RadioDescriptionLines"/>, 1 to 4), and stack the lines from the container's top
    /// (<see cref="UiOptionsRules.RadioLinePitch"/>, <see cref="UiOptionsRules.RadioLineTops"/>). Returns the list's
    /// height (<see cref="UiOptionsRules.RadioListHeight"/>).
    /// </summary>
    private static float PlaceRadioLines(List<(GameObject Go, TextMeshProUGUI? Description)> lines, float controlWidth)
    {
        float textWidth = Math.Max(40f, controlWidth - RadioCaptionInset - 4f);
        var pitches = new List<float>(lines.Count);
        var counts = new List<int>(lines.Count);
        foreach (var (_, description) in lines)
        {
            int count = 0;
            if (description != null)
            {
                float measured = 0f;
                try { measured = description.GetPreferredValues(description.text, textWidth, 0f).y; }
                catch { measured = 0f; }
                count = UiOptionsRules.RadioDescriptionLines(measured);
            }

            counts.Add(count);
            pitches.Add(UiOptionsRules.RadioLinePitch(description != null, count));
        }

        var tops = UiOptionsRules.RadioLineTops(pitches);
        for (int i = 0; i < lines.Count; i++)
        {
            try
            {
                var rt = lines[i].Go.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.offsetMin = new Vector2(0f, -(tops[i] + pitches[i]));
                    rt.offsetMax = new Vector2(0f, -tops[i]);
                }

                var d = lines[i].Description;
                if (d != null)
                {
                    var drt = d.rectTransform;
                    float top = UiRadioList.LineHeight + UiRadioList.DescriptionGap;
                    drt.offsetMin = new Vector2(drt.offsetMin.x, -(top + counts[i] * UiRadioList.AboutLineHeight));
                    drt.offsetMax = new Vector2(drt.offsetMax.x, -top);
                }
            }
            catch { /* this line keeps its fraction */ }
        }

        return UiOptionsRules.RadioListHeight(pitches);
    }

    /// <summary>RADIO1: where a radio line's label and explanation start (after the circle): the cell-row list's inset.</summary>
    private const float RadioCaptionInset = UiRadioList.LabelInset;

    /// <summary>RADIOCLICK1: the circle's left edge, so it sits in the cell-row list's circle column.</summary>
    private const float RadioMarkInset = 4f;

    /// <summary>
    /// RADIOCLICK3 (live 2026-10-06, Server tab: no circle on any settings-grid radio line): the settings grid's circle
    /// is drawn as the cell-row list's, which does show: its own <see cref="UiRadioList.MarkWidth"/> box in the circle
    /// column (not a line-wide caption rect), centered, drawn whole (overflow, never the ellipsis clamp), the glyph form
    /// the font has from the start (<see cref="DrawableGlyph"/>, not only after the first state write), and in front of
    /// the line's other text.
    /// </summary>
    private static void FitRadioMark(TextMeshProUGUI? mark)
    {
        if (mark == null) return;
        try
        {
            var rt = mark.rectTransform;
            rt.anchorMin = new Vector2(0f, rt.anchorMin.y);
            rt.anchorMax = new Vector2(0f, rt.anchorMax.y);
            rt.pivot = new Vector2(0f, rt.pivot.y);
            rt.offsetMin = new Vector2(RadioMarkLeft, rt.offsetMin.y);
            rt.offsetMax = new Vector2(RadioMarkLeft + UiRadioList.MarkWidth, rt.offsetMax.y);
        }
        catch { /* the line-wide rect still holds the glyph */ }
        try { mark.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
        try { mark.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
        // RADIOART1: the circle is drawn (a ring and the chosen dot), not a font glyph; the mark keeps its box.
        if (AddRadioCircle(mark.gameObject)) { try { mark.text = ""; } catch { /* */ } }
        else { try { mark.text = DrawableGlyph(mark, UiOptionsRules.RadioMark(false)); } catch { /* */ } }
        try { mark.transform.SetAsLastSibling(); } catch { /* */ }
    }

    private static Texture2D? _radioRingTexture, _radioDotTexture;
    private static Sprite? _radioRingSprite, _radioDotSprite;

    /// <summary>RADIOART1: the ring and the dot, drawn once per Core generation (freed in <see cref="DestroyByPrefix"/>).</summary>
    private static Sprite? RadioSprite(bool dot)
    {
        ref Sprite? sprite = ref dot ? ref _radioDotSprite : ref _radioRingSprite;
        ref Texture2D? texture = ref dot ? ref _radioDotTexture : ref _radioRingTexture;
        if (Alive(sprite) && Alive(texture)) return sprite;
        try
        {
            int size = RadioGlyph.TextureSize;
            string name = UiOwnership.Prefix + (dot ? "radio-dot" : "radio-ring");
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, dot ? RadioGlyph.DotAlpha(x, y, size) : RadioGlyph.RingAlpha(x, y, size)));
            t.Apply(false, false);
            var s = Sprite.Create(t, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            s.name = name;
            s.hideFlags = HideFlags.HideAndDontSave;
            texture = t;
            sprite = s;
            return s;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogInfo("Ui: the radio circle could not be drawn (" + e.Message + "); the glyph stays.");
            return null;
        }
    }

    private static void FreeRadioSprites()
    {
        foreach (var o in new UnityEngine.Object?[] { _radioRingSprite, _radioDotSprite, _radioRingTexture, _radioDotTexture })
        {
            try { if (Alive(o)) UnityEngine.Object.Destroy(o); } catch { /* gone */ }
        }

        _radioRingSprite = _radioDotSprite = null;
        _radioRingTexture = _radioDotTexture = null;
    }

    /// <summary>The ring and the dot as children of the mark's box, centered, the size of the game's checkbox frame.</summary>
    private static bool AddRadioCircle(GameObject markGo)
    {
        var ring = RadioSprite(dot: false);
        var dot = RadioSprite(dot: true);
        if (ring == null || dot == null) return false;
        try
        {
            float size = Progenitor.Ui.GameArt.SettingsControlLook.ListBoxSize;
            Image? Add(string name, Sprite sprite, float grey)
            {
                var go = new GameObject(name);
                go.transform.SetParent(markGo.transform, false);
                var rt = go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(size, size);
                var image = go.AddComponent<Image>();
                image.sprite = sprite;
                image.preserveAspect = true;
                image.raycastTarget = false;
                image.color = Grey(grey);
                return image;
            }

            Add("radio-ring", ring, RadioGlyph.RingGrey(false));
            var dotImage = Add("radio-dot", dot, RadioGlyph.DotGrey);
            if (dotImage != null) dotImage.enabled = false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>RADIOART1: paint a drawn circle for <paramref name="chosen"/>; false when the mark has none (the glyph path).</summary>
    private static bool PaintRadioCircle(TextMeshProUGUI mark, bool chosen)
    {
        try
        {
            var ring = mark.transform.Find("radio-ring");
            var dot = mark.transform.Find("radio-dot");
            if (ring == null || dot == null) return false;
            var ringImage = ring.GetComponent<Image>();
            var dotImage = dot.GetComponent<Image>();
            if (ringImage != null) ringImage.color = Grey(RadioGlyph.RingGrey(chosen));
            if (dotImage != null) dotImage.enabled = chosen;
            return true;
        }
        catch { return false; }
    }

    /// <summary>RADIOCLICK3: the circle box's left edge (the box ends before <see cref="RadioCaptionInset"/>).</summary>
    private const float RadioMarkLeft = 0f;

    /// <summary>
    /// One option anchored by fraction inside the options container. A segmented option is a framed
    /// button whose fill carries the state. A list option is a line with the kit's check box at its
    /// left (the toggle's framed box and filled mark, <see cref="BuildToggle"/>) and the label after it.
    /// </summary>
    private static (GameObject? Go, Image? Fill, TextMeshProUGUI? Caption, Image? Mark) BuildOptionButton(
        GameObject container, int index, int count, bool list, string label)
    {
        try
        {
            var go = new GameObject("option-" + index.ToString(CultureInfo.InvariantCulture));
            go.transform.SetParent(container.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            float c = Math.Max(1, count);
            if (list)
            {
                rt.anchorMin = new Vector2(0f, 1f - (index + 1) / c);
                rt.anchorMax = new Vector2(1f, 1f - index / c);
                rt.offsetMin = new Vector2(0f, 2f);
                rt.offsetMax = new Vector2(0f, -2f);
            }
            else
            {
                rt.anchorMin = new Vector2(index / c, 0f);
                rt.anchorMax = new Vector2((index + 1) / c, 1f);
                rt.offsetMin = new Vector2(index == 0 ? 0f : 2f, 4f);
                rt.offsetMax = new Vector2(index == count - 1 ? 0f : -2f, -4f);
            }

            var image = TryAdd<Image>(go);
            if (image == null) return (null, null, null, null);
            if (list)
            {
                // The line itself is only the click target; the box carries the state.
                try { image.sprite = null; } catch { /* */ }
                try { image.color = new Color(0f, 0f, 0f, 0.01f); } catch { /* */ }
            }
            else ApplyFrame(image, OptionFill(false));
            try { image.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button == null) return (null, null, null, null);
            try { button.targetGraphic = image; } catch { /* */ }

            Image? mark = null;
            if (list)
            {
                var box = new GameObject("check-box");
                box.transform.SetParent(go.transform, false);
                var boxRt = box.GetComponent<RectTransform>() ?? box.AddComponent<RectTransform>();
                boxRt.anchorMin = new Vector2(0f, 0.5f);
                boxRt.anchorMax = new Vector2(0f, 0.5f);
                boxRt.pivot = new Vector2(0f, 0.5f);
                boxRt.anchoredPosition = new Vector2(2f, 0f);
                boxRt.sizeDelta = new Vector2(OptionBoxSize, OptionBoxSize);
                var boxImage = TryAdd<Image>(box);
                if (boxImage != null)
                {
                    ApplyFrame(boxImage, new Color(0.16f, 0.16f, 0.20f, 0.95f));
                    try { boxImage.raycastTarget = false; } catch { /* */ }
                }

                var markGo = new GameObject("check-mark");
                markGo.transform.SetParent(box.transform, false);
                PlaceRect(markGo, Vector2.zero, OptionBoxSize - 8f, OptionBoxSize - 8f);
                mark = TryAdd<Image>(markGo);
                if (mark != null)
                {
                    try { mark.sprite = null; } catch { /* */ }
                    try { mark.color = new Color(0.93f, 0.86f, 0.62f, 1f); } catch { /* */ }
                    try { mark.raycastTarget = false; } catch { /* */ }
                    try { mark.enabled = false; } catch { /* */ }
                }
            }

            var textGo = new GameObject("caption");
            textGo.transform.SetParent(go.transform, false);
            Stretch(textGo);
            var tmp = TryAdd<TextMeshProUGUI>(textGo);
            if (tmp != null)
            {
                ApplyControlTmp(tmp, label ?? "", 16f);
                ApplyCachedFont(tmp);
                if (list)
                {
                    ApplyHorizontalAlign(tmp, left: true);
                    try { tmp.margin = new Vector4(OptionBoxSize + 10f, 0f, 4f, 0f); } catch { /* */ }
                }

                try { tmp.enableWordWrapping = false; } catch { /* */ }
                // One line, clipped by the caption clamp (the only other overflow writer).
                ClampCaptionText(textGo);
            }

            return (go, image, tmp, mark);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building an option threw: " + e.Message);
            return (null, null, null, null);
        }
    }

    /// <summary>A list option's check box.</summary>
    private const float OptionBoxSize = 18f;

    /// <summary>
    /// A segmented option's fill. Live 2026-09-29 the selected segment (the selected-row brown) was
    /// hard to tell from the rest; it now takes the kit's accent (<see cref="MenuPalette.Gold"/>) with
    /// bright text, and the others the control fill with muted text.
    /// </summary>
    private static Color OptionFill(bool selected) =>
        selected ? ToColor(MenuPalette.Gold) : new Color(0.16f, 0.16f, 0.20f, 0.95f);

    /// <summary>
    /// CONTROLS1: a segmented option on the settings grid. With the game's value box drawn (a sprite on the
    /// image) the state is the frame's tint and the index bar under it; without it a quiet steel fill. Never
    /// the saturated flat blue the first game look used (live 2026-10-03).
    /// </summary>
    private static Color GameOptionFill(Image fill, bool selected)
    {
        bool framed = false;
        try { framed = fill != null && fill.sprite != null; } catch { framed = false; }
        return framed
            ? Grey(Progenitor.Ui.GameArt.SettingsControlLook.SegmentFrameGrey(selected))
            : Rgba(Progenitor.Ui.GameArt.SettingsControlLook.SegmentKitFill(selected));
    }

    private static void PaintSegmentIndicator(GameObject segment, bool selected)
    {
        try
        {
            var bar = segment.transform.Find(SegmentIndicatorName);
            var image = bar != null ? bar.GetComponent<Image>() : null;
            if (image != null) image.color = Rgba(Progenitor.Ui.GameArt.SettingsControlLook.SegmentIndicator(selected));
        }
        catch { /* */ }
    }

    public void SetOptionStates(IRuntimeWidget widget, IReadOnlyList<bool> selected)
    {
        if (widget is UnityOptionsWidget options) options.Apply(selected);
    }

    /// <summary>
    /// SETTINGS1 stage 2: a caption and a read-only value in the control column. The value label is
    /// named <c>value-text</c>, which is where <see cref="UnityWidget.StringValue"/> writes.
    /// </summary>
    public IRuntimeWidget? AddInfo(IRuntimePanel panel, string text, string value)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = new GameObject("info-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, host.ContentRowWidth, RowHeight);
            var label = MakeLabel(go, text ?? "", Vector2.zero, 18f, left: true);
            var valueGo = MakeLabel(go, value ?? "", Vector2.zero, 16f, left: true);
            if (valueGo != null)
            {
                valueGo.name = "value-text";
                // TEXTAREA1: an info value is data (a "Locked · value" row shows a server-pushed
                // setting to every player), so markup in it stays characters.
                try
                {
                    var tmp = valueGo.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.richText = false;
                }
                catch { /* */ }
            }
            host.AddRow(go, label, hasControl: valueGo != null, kind: "info", source: ControlSource.None, control: valueGo);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddInfo threw: " + e.Message);
            return null;
        }
    }

    /// <summary>SETTINGS1 stage 2: a thin rule across the row, in a short row of its own.</summary>
    public IRuntimeWidget? AddDivider(IRuntimePanel panel)
    {
        if (panel is not UnityPanel host || !host.Alive) return null;
        try
        {
            var go = new GameObject("divider-row");
            go.transform.SetParent(host.RowParent.transform, false);
            PlaceTop(go, 0f, host.ContentRowWidth, DividerRowHeight);
            var rule = new GameObject("divider-rule");
            rule.transform.SetParent(go.transform, false);
            var image = TryAdd<Image>(rule);
            if (image != null)
            {
                try { image.color = ToColor(MenuPalette.RowRule); } catch { /* */ }
                try { image.raycastTarget = false; } catch { /* */ }
            }

            host.SetFixedRowHeight(go, DividerRowHeight);
            host.AddRow(go, null, hasControl: false, kind: "divider", source: ControlSource.None);
            return new UnityWidget(host, go, null, null, null, null, null, null);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddDivider threw: " + e.Message);
            return null;
        }
    }

    private const float DividerRowHeight = 14f;

    /// <summary>
    /// Per-keystroke TMP event for the Mods search field. Called only when
    /// <see cref="UnityPanel.UsesLiveText"/> is set, so settings <see cref="AddInput"/> still
    /// compiles as an onEndEdit-only body.
    /// </summary>
    private static object LiveTextEvent(TMP_InputField field) => field.onValueChanged;

    /// <summary>
    /// Jobs FUI20, FUI21. The spot is stored and then applied to whatever size the frame currently
    /// has. For the panel being shown that size is still the pre-layout one and the real placement
    /// comes moments later from <see cref="UnityPanel.ApplyLayout"/>, which is the pass that both
    /// sizes the frame to its content and places it. For the panels that were already open — the
    /// re-flow that happens because a new panel joined the set or an old one left — the frame is
    /// already the right size, and <see cref="UnityPanel.Reposition"/> moves it without touching a
    /// single row.
    /// </summary>
    public void TopPanel(IRuntimePanel panel, bool top)
    {
        if (panel is UnityPanel host) { host.TopAligned = top; host.Reposition(); }
    }

    public void PlacePanel(IRuntimePanel panel, PanelSpot spot)
    {
        if (panel is not UnityPanel host) return;
        try
        {
            host.Spot = spot;
            // WINDOWS1 live fix: the row panel shown last draws on top (its canvas order from its layer).
            host.ApplyRowLayer();
            // None is a dismiss, not a center placement. HideCore logs
            // `placed rank=-1/0` then Dispose; without this deactivation a
            // deferred Destroy left the overlay drawing over the next world.
            if (!spot.IsPlaced)
            {
                try { host.Visible = false; } catch { /* */ }
                try { host.CanvasVisible = false; } catch { /* */ }
            }
            host.Reposition();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: PlacePanel threw: " + e.Message); }
    }

    public void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar) =>
        ConfigureMenuPanel(panel, sidebar, MenuPaneMode.Split);

    public void ConfigureMenuPanel(IRuntimePanel panel, bool sidebar, MenuPaneMode mode)
    {
        try { ModsWorkspaceChrome.Ensure(); } catch { /* */ }
        if (panel is UnityPanel host && host.Alive) host.ConfigureMenu(sidebar, mode);
    }

    public void ConfigureMenuStrip(IRuntimePanel panel)
    {
        try { ModsWorkspaceChrome.Ensure(); } catch { /* */ }
        if (panel is UnityPanel host && host.Alive) host.ConfigureStrip();
    }

    public void EnsureModsWorkspaceChrome()
    {
        try { ModsWorkspaceChrome.Ensure(); } catch { /* */ }
    }

    public void ReleaseModsWorkspaceChrome()
    {
        try { ModsWorkspaceChrome.Release(); } catch { /* */ }
    }

    public void PinMenuRows(IRuntimePanel panel, MenuRowPin pin)
    {
        if (panel is UnityPanel host && host.Alive) host.SetNextPin(pin);
    }

    public void StyleMenuRow(IRuntimePanel panel, MenuRowTone tone)
    {
        if (panel is UnityPanel host && host.Alive) host.SetNextTone(tone);
    }

    public void HidePaneTitle(IRuntimePanel panel)
    {
        if (panel is UnityPanel host && host.Alive) host.HidePaneTitle();
    }

    /// <summary>
    /// CONSENT2: hand this panel's frame rect to the panel itself. See
    /// <see cref="IUiRuntime.SelfPlacePanel"/> for why the consent prompt asks.
    /// </summary>
    public void SelfPlacePanel(IRuntimePanel panel, bool selfPlaced)
    {
        if (panel is UnityPanel host && host.Alive) host.SelfPlaced = selfPlaced;
    }

    /// <summary>P7b: this panel never scrolls. See <see cref="IUiRuntime.NoScrollPanel"/>.</summary>
    public void NoScrollPanel(IRuntimePanel panel)
    {
        if (panel is UnityPanel host && host.Alive) host.TurnOffScroll();
    }

    /// <summary>
    /// INSTALLUX2 item 1: the consent prompt's chrome as a kit seam — an opaque frame on the Mods
    /// palette, a raycast-blocking backdrop on this panel's own overlay canvas, centered rather than
    /// flowed with the settings panels, and hidden (CanvasGroup alpha 0, never
    /// <c>SetActive</c> — the panel captures input and is Escape-able throughout) until the
    /// runtime's own layout pass has sized and placed it. See <see cref="IUiRuntime.DialogChrome"/>.
    /// </summary>
    public void DialogChrome(IRuntimePanel panel)
    {
        if (panel is UnityPanel host && host.Alive) host.MakeDialog();
    }

    public void GameWindowChrome(IRuntimePanel panel)
    {
        if (panel is UnityPanel host && host.Alive) host.MakeGameWindow();
    }

    /// <summary>WINDOWS1: a standard window's frame (<see cref="IUiRuntime.WindowChrome"/>).</summary>
    public void WindowChrome(IRuntimePanel panel, UiWindowSpec spec)
    {
        if (panel is UnityPanel host && host.Alive && spec != null) host.MakeStandardWindow(spec);
    }

    /// <summary>WIDEDIALOG1: the dialog frame's asked-for width; the next layout pass sizes it.</summary>
    public void SetDialogWidth(IRuntimePanel panel, float frameWidth)
    {
        if (panel is UnityPanel host && host.Alive) host.SetDialogFrameWidth(frameWidth);
    }

    /// <summary>The version footer (<see cref="IUiRuntime.SetPanelFooter"/>).</summary>
    public void SetPanelFooter(IRuntimePanel panel, string text)
    {
        if (panel is UnityPanel host && host.Alive) host.SetFooter(text);
    }

    public void SetSoftHide(IRuntimePanel panel, bool soft)
    {
        if (panel is UnityPanel host) host.SoftHide = soft;
    }

    public void Show(IRuntimePanel panel)
    {
        if (panel is not UnityPanel host) return;
        // HOSTS1 round 5: a soft-hidden pane comes back with its objects still active.
        if (host.SoftHide) { try { host.SetCanvasEnabled(true); } catch { /* */ } }
        long showStart = System.Diagnostics.Stopwatch.GetTimestamp();
        // Every row is in by now, so this is the first moment the panel knows how tall it
        // should be. Before evidence 163 the frame was a fixed 360 px and rows seven and
        // beyond were simply drawn over the world.
        try { host.ApplyLayout(); } catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel layout threw: " + e.Message); }
        // HOSTS1 round 11: the layout (text measurement) apart from the activation (OnEnable of every row).
        WorkspaceOpenTiming.AddDetail("show-apply-layout", showStart);
        showStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try { host.CanvasVisible = true; } catch { /* */ }
        try { host.Visible = true; } catch { /* */ }
        WorkspaceOpenTiming.AddDetail("show-activate", showStart);
        // TABCAPS1: every strip show checks its tab captions (and once more on the next frame).
        if (host.IsStrip) VerifyStripOnShow(host);
        // BUTTONART1: game buttons built before their art (a parked list, a page built during a swap's
        // preparation) take it at the next show.
        try { RepaintPendingGameButtons(); } catch { /* */ }
        try { NoteShown(host, true); } catch { /* */ }

        // Job FUI30. Armed, not taken: the canvas has had no update yet in this frame — which is
        // why this panel's own layout line reads ruler=constant — so a probe here would report a
        // zero-sized canvas and pre-OnEnable rows as if they were the screen the player sees.
        try { ArmProbe(host); } catch { /* */ }
    }

    public void Hide(IRuntimePanel panel)
    {
        if (panel is not UnityPanel host) return;

        // Last honest moment for the scrollbar read-back (R45 finding 22): a panel that was shown
        // once and never re-laid out or repositioned would otherwise take its pending verdict to
        // the grave unchecked. Read before the deactivation, not after — the bar's own flag does
        // not change when its parent is deactivated, but the panel it describes is about to.
        try { host.ReadBackScrollbarNow("a hide"); } catch { /* */ }

        // The same last-honest-moment rule, for the same reason and one line later: a panel shown
        // and closed inside one frame — or one whose plugin never registered for the hover pass, so
        // ReadPointer is never called — would otherwise take its armed probe away unspent. Read
        // before the deactivation below, or every row would report Inactive truthfully and
        // uselessly.
        try { FireArmedProbe("a hide"); } catch { /* */ }

        // Job FUI32: a hidden panel is not owed a re-measure. Its overlay is about to be
        // deactivated, so the canvas it is waiting on will never be sized, and the next Show runs
        // ApplyLayout and arms the wait again against whatever canvas exists then.
        try { DropSettle(host); } catch { /* */ }

        // HOSTS1 round 5: a soft-hidden pane (the Mods list kept between opens) only switches its canvas
        // and raycaster off; its objects stay active, so the next show rebuilds no text meshes. A Core
        // swap disposes it (ModsMenu.Uninstall), so no stale EventTrigger outlives the generation.
        if (host.SoftHide)
        {
            try { host.SetCanvasEnabled(false); } catch { /* */ }
            try { NoteShown(host, false); } catch { /* */ }
            return;
        }

        // Deactivate this panel's GameObject (EventTrigger cannot fire across a
        // Core swap, 957d317) and its own overlay canvas (full-screen raycaster
        // would otherwise eat clicks). Sibling panels keep their own canvas.
        try { host.Visible = false; } catch { /* */ }
        try { host.CanvasVisible = false; } catch { /* */ }
        try { NoteShown(host, false); } catch { /* */ }
    }

    /// <summary>
    /// The shown workspace pane's ScrollRect. Sidebar identity, not leftmost world x.
    /// </summary>
    /// <summary>GameObject name of a pane's pinned-header band.</summary>
    internal const string HeaderBandName = "header-band";

    /// <summary>
    /// The shown detail pane's pinned-header band and scroll viewport, for
    /// <c>mods_ui rects</c>. Both null when no detail pane is shown; the band is
    /// null until a header pin has been laid out.
    /// </summary>
    internal static void FindShownDetailHeader(out GameObject? band, out GameObject? viewport)
    {
        band = null;
        viewport = null;
        for (int i = 0; i < ShownPanels.Count; i++)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive) continue;
            if (!panel.IsMenuPane || panel.IsSidebar || panel.IsStrip) continue;
            band = panel.HeaderBand;
            viewport = panel.ViewportObject;
            return;
        }
    }

    /// <summary>The GameObject a widget handle was built on, or null.</summary>
    internal static GameObject? WidgetGameObject(IRuntimeWidget? runtime) =>
        runtime is UnityWidget widget ? widget.GameObject : null;

    internal static ScrollRect? FindShownScroll(bool sidebar)
    {
        for (int i = 0; i < ShownPanels.Count; i++)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive) continue;
            if (sidebar)
            {
                if (panel.IsSidebar) return panel.Scroll;
            }
            // User report 2026-10-10: the Keybinds tab shows its description column first, so the content scroll
            // read, restored and reported the column's (never scrolled) instead of the key list's.
            else if (panel.IsMenuPane && !panel.IsSidebar && !panel.IsStrip && !panel.IsAside)
            {
                return panel.Scroll;
            }
        }

        return null;
    }

    /// <summary>
    /// Read-only title/Play scene probe. Never <c>SetActive</c>s or <c>GoToView</c>s.
    /// MenuParent walk is depth ≤ <see cref="ModsMenuScene.MaxDepth"/> and
    /// <see cref="ModsMenuScene.WalkCap"/> objects.
    /// </summary>
    internal static object DescribeMenuScene()
    {
        try
        {
            object? view = null;
            string? viewWhy = null;
            try
            {
                var loc = ViewManager.GetCurrentView();
                view = ModsMenuScene.View(loc.ToString(), (int)loc);
            }
            catch (Exception e)
            {
                viewWhy = ModsMenuScene.WhyViewThrewPrefix + e.Message;
            }

            if (view == null && viewWhy == null)
                viewWhy = ModsMenuScene.WhyNoViewManager;

            var ambience = ModsMenuScene.MissingObject();
            var music = ModsMenuScene.MissingObject();
            bool menuParentPresent = false;
            string menuParentPath = "";
            var menuChildren = new List<Dictionary<string, object?>>();
            int walked = 0;
            bool truncated = false;
            try
            {
                var canvas = FindMainMenuCanvas();
                if (canvas != null)
                {
                    ambience = DescribeGameObject(canvas.AmbienceVolumeGameObject);
                    music = DescribeGameObject(canvas.MusicVolumeGameObject);
                    var parent = canvas.MenuParent;
                    if (parent != null)
                    {
                        menuParentPresent = true;
                        menuParentPath = TransformPath(parent.transform);
                        WalkMenuParent(parent.transform, menuParentPath, 0, menuChildren, ref walked, ref truncated);
                    }
                }
            }
            catch { /* volumes / MenuParent stay at their last good read */ }

            var titleView = ModsMenuScene.TitleView(false, false, null, null);
            try { titleView = DescribeTitleView(); }
            catch { /* missing title view is a fact */ }

            var cameras = new List<Dictionary<string, object?>>();
            try { CollectCameras(cameras); }
            catch { /* camera list may be partial */ }

            var roots = new List<Dictionary<string, object?>>();
            try { CollectSceneRoots(roots); }
            catch { /* root list may be partial */ }

            int frame = 0;
            try { frame = Time.frameCount; } catch { }

            Dictionary<string, object?> playBackdrop;
            try { playBackdrop = NativeModsEntry.DescribePlayBackdrop(); }
            catch { playBackdrop = NativeModsEntry.PlayBackdropHold.Describe(default); }

            return ModsMenuScene.Shape(
                view,
                viewWhy,
                ambience,
                music,
                titleView,
                menuParentPresent,
                menuParentPath,
                menuChildren,
                walked,
                truncated,
                cameras,
                roots,
                ModsMenuScene.Stamp(frame, 0, 0),
                playBackdrop);
        }
        catch (Exception e)
        {
            return ModsMenuScene.Failed(e.Message);
        }
    }

    /// <summary>
    /// Read-only pointer probe for <c>mods_ui hit</c>. <paramref name="x"/> /
    /// <paramref name="y"/> are client-area pixels (same space as
    /// <c>mods_ui rects</c>). Does not click, focus, or write Unity.
    /// </summary>
    internal static object DescribePointerHit(float x, float y)
    {
        int screenHeight = 0;
        try
        {
            float h = UnityPanel.ScreenHeightForHit();
            if (h > 0.5f) screenHeight = (int)h;
        }
        catch { }
        float unityX = x, unityY = y;
        if (screenHeight > 0)
            ModsMenuTabDiag.ClientPointToUnity(x, y, screenHeight, out unityX, out unityY);

        Dictionary<string, object?>? stripSlot = null;
        string? stripSlotWhy = ModsMenuTabDiag.WhyNoStrip;
        var stripRowHit = new List<Dictionary<string, object?>>();
        Dictionary<string, object?>? clickWouldInvoke = null;
        string? clickWouldInvokeWhy = ModsMenuTabDiag.WhyNoHit;
        Dictionary<string, object?>? hoverRow = null;
        Dictionary<string, object?>? focusField = null;
        string? focusFieldWhy = ModsMenuTabDiag.WhyNoHit;
        var armedFields = new List<Dictionary<string, object?>>();
        Dictionary<string, object?>? uguiRaycast = null;
        string? uguiRaycastWhy = ModsMenuTabDiag.WhyNoHit;

        try { DescribeStripHit(unityX, unityY, screenHeight, out stripSlot, out stripSlotWhy, out stripRowHit, out clickWouldInvoke, out clickWouldInvokeWhy); }
        catch (Exception e) { stripSlotWhy = "read-threw:" + e.Message; }

        // Job LIST4 item 2. The strip pass above answers for the tab strip only, so every cell
        // row's button — the chooser chips, the filter chips, the Install column — read back as
        // no-hit however well the real pointer reached it. The cell pass runs whether or not the
        // strip matched, and only names clickWouldInvoke when the strip did not.
        try { DescribeCellHit(unityX, unityY, screenHeight, stripRowHit, ref clickWouldInvoke, ref clickWouldInvokeWhy); }
        catch (Exception e) { clickWouldInvokeWhy = "read-threw:" + e.Message; }

        try { hoverRow = DescribeHoverRow(unityX, unityY); }
        catch (Exception e) { hoverRow = ModsMenuTabDiag.HoverRow(null, "read-threw:" + e.Message); }

        try { DescribeFocusHit(unityX, unityY, screenHeight, out focusField, out focusFieldWhy, out armedFields); }
        catch (Exception e) { focusFieldWhy = "read-threw:" + e.Message; }

        try { DescribeUguiRaycast(unityX, unityY, out uguiRaycast, out uguiRaycastWhy); }
        catch (Exception e) { uguiRaycastWhy = "read-threw:" + e.Message; }

        return ModsMenuTabDiag.HitShape(
            x, y, stripSlot, stripSlotWhy, stripRowHit,
            clickWouldInvoke, clickWouldInvokeWhy,
            hoverRow ?? ModsMenuTabDiag.HoverRow(null, ModsMenuTabDiag.WhyNoHit),
            focusField, focusFieldWhy, armedFields,
            uguiRaycast, uguiRaycastWhy,
            unityX, unityY);
    }

    /// <summary>
    /// RADIOCLICK1 <c>mods_ui click_at {x, y}</c> or <c>{row}</c>: a click through the pointer route, without OS input
    /// or focus. The event system's raycast picks the topmost UI object under the point (client pixels, as
    /// <c>mods_ui hit</c>; or the center of the visible cell row <paramref name="rowKey"/>), the click handler is
    /// resolved the way the input module resolves it (the first object up the parent chain that handles a click), and
    /// that handler's own pointer-click runs. A row whose fill catches the ray but which has no click handler gets
    /// <c>invoked: false</c> and the object that swallowed the click, which is what a player's click did on a radio
    /// line before RADIOCLICK1, where <c>widget_click</c> (it fires the slot directly) said it worked.
    /// </summary>
    internal static object ClickAtPoint(float x, float y, string? rowKey)
    {
        int screenHeight = 0;
        try
        {
            float h = UnityPanel.ScreenHeightForHit();
            if (h > 0.5f) screenHeight = (int)h;
        }
        catch { }

        var d = new Dictionary<string, object?> { ["row"] = rowKey ?? "" };
        float unityX = x, unityY = y;
        if (!string.IsNullOrWhiteSpace(rowKey))
        {
            if (!UiCellRowHandle.TryRowRect(rowKey, out var rect))
            {
                d["error"] = "no visible cell row '" + rowKey + "' with a readable rect";
                d["invoked"] = false;
                return d;
            }

            unityX = (rect.MinX + rect.MaxX) * 0.5f;
            unityY = (rect.MinY + rect.MaxY) * 0.5f;
            x = unityX;
            y = screenHeight > 0 ? screenHeight - unityY : unityY;
        }
        else if (screenHeight > 0) ModsMenuTabDiag.ClientPointToUnity(x, y, screenHeight, out unityX, out unityY);

        d["x"] = Math.Round(x, 1);
        d["y"] = Math.Round(y, 1);
        d["unityX"] = Math.Round(unityX, 1);
        d["unityY"] = Math.Round(unityY, 1);
        d["invoked"] = false;

        EventSystem? es = null;
        try { es = EventSystem.current; } catch { }
        if (es == null)
        {
            d["error"] = "EventSystem.current is null; the pointer route is unavailable";
            return d;
        }

        try
        {
            var ev = new PointerEventData(es) { position = new Vector2(unityX, unityY), button = PointerEventData.InputButton.Left };
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ev, results);
            if (results == null || results.Count == 0)
            {
                d["hit"] = null;
                d["why"] = ModsMenuTabDiag.WhyNoHit;
                return d;
            }

            GameObject? hit = null;
            try { hit = results[0].gameObject; } catch { }
            d["hit"] = hit == null ? null : SafePath(hit);
            d["ours"] = IsOurs(hit);
            if (hit == null)
            {
                d["why"] = ModsMenuTabDiag.WhyNoHit;
                return d;
            }

            try { ev.pointerCurrentRaycast = results[0]; ev.pointerPressRaycast = results[0]; } catch { }
            var t = hit.transform;
            int guard = 0;
            while (t != null && guard++ < 64)
            {
                var go = t.gameObject;
                string? kind = ClickHandlerKind(go);
                if (kind != null)
                {
                    d["handler"] = SafePath(go);
                    d["handlerKind"] = kind;
                    bool invoked = InvokePointerClick(go, kind, ev);
                    d["invoked"] = invoked;
                    if (!invoked) d["why"] = "the handler did not take the click (not interactable, or an input field)";
                    return d;
                }

                t = t.parent;
            }

            d["handler"] = null;
            d["why"] = "no click handler on the hit object or its parents: the click is swallowed";
            return d;
        }
        catch (Exception e)
        {
            d["error"] = e.Message;
            return d;
        }
    }

    private static string SafePath(GameObject go)
    {
        try { return TransformPath(go.transform); }
        catch { return SafeName(go); }
    }

    /// <summary>RADIOCLICK1: which click handler <paramref name="go"/> carries (the kinds this UI uses), or null.</summary>
    private static string? ClickHandlerKind(GameObject go)
    {
        try { if (go.GetComponent<SimpleStunButton>() != null) return "stun-button"; } catch { }
        try { if (go.GetComponent<Button>() != null) return "button"; } catch { }
        try { if (go.GetComponent<Toggle>() != null) return "toggle"; } catch { }
        try { if (go.GetComponent<TMP_InputField>() != null) return "input"; } catch { }
        try { if (go.GetComponent<EventTrigger>() != null) return "event-trigger"; } catch { }
        return null;
    }

    private static bool InvokePointerClick(GameObject go, string kind, PointerEventData ev)
    {
        try
        {
            switch (kind)
            {
                case "stun-button":
                {
                    var stun = go.GetComponent<SimpleStunButton>();
                    if (stun == null || !stun.IsActive() || !stun.IsInteractable()) return false;
                    stun.onClick?.Invoke();
                    return true;
                }
                case "button":
                {
                    var button = go.GetComponent<Button>();
                    if (button == null || !button.IsActive() || !button.IsInteractable()) return false;
                    button.OnPointerClick(ev);
                    return true;
                }
                case "toggle":
                {
                    var toggle = go.GetComponent<Toggle>();
                    if (toggle == null || !toggle.IsActive() || !toggle.IsInteractable()) return false;
                    toggle.OnPointerClick(ev);
                    return true;
                }
                case "event-trigger":
                {
                    var trigger = go.GetComponent<EventTrigger>();
                    if (trigger == null) return false;
                    trigger.OnPointerClick(ev);
                    return true;
                }
                default:
                    return false;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: click_at " + kind + ": " + e.Message);
            return false;
        }
    }

    private static void DescribeStripHit(
        float unityX, float unityY, int screenHeight,
        out Dictionary<string, object?>? stripSlot,
        out string? stripSlotWhy,
        out List<Dictionary<string, object?>> stripRowHit,
        out Dictionary<string, object?>? clickWouldInvoke,
        out string? clickWouldInvokeWhy)
    {
        stripSlot = null;
        stripSlotWhy = ModsMenuTabDiag.WhyNoStrip;
        var rows = new List<Dictionary<string, object?>>();
        stripRowHit = rows;
        clickWouldInvoke = null;
        clickWouldInvokeWhy = ModsMenuTabDiag.WhyNoHit;

        UnityPanel? stripPanel = null;
        for (int i = ShownPanels.Count - 1; i >= 0; i--)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive || !panel.Visible || !panel.IsStrip) continue;
            stripPanel = panel;
            break;
        }

        if (stripPanel == null) return;

        var originRect = ScreenRect.Unknown;
        var slots = new List<Dictionary<string, object?>>();
        int index = -1;
        if (TryStripHitIndex(stripPanel, unityX, unityY, out index))
        {
            string? name = index >= 0 && index < ModsTabStrip.Order.Length
                ? ModsTabStrip.Caption(ModsTabStrip.Order[index])
                : null;
            try
            {
                var panelRect = ScreenRectOfPanel(stripPanel);
                if (panelRect.IsKnown) originRect = panelRect;
            }
            catch { }

            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            int buttons = 0;
            stripPanel.ForEachRow((go, kind, _) =>
            {
                if (go == null || !string.Equals(kind, "button", StringComparison.Ordinal)) return;
                try
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null) return;
                    var rect = ScreenRectOf(rt);
                    if (!rect.IsKnown) return;
                    buttons++;
                    if (rect.MinX < minX) minX = rect.MinX;
                    if (rect.MinY < minY) minY = rect.MinY;
                    if (rect.MaxX > maxX) maxX = rect.MaxX;
                    if (rect.MaxY > maxY) maxY = rect.MaxY;
                }
                catch { /* */ }
            });
            var strip = buttons > 0
                ? ModsTabStrip.GeometryFromLeftOrigin(minX, minY, maxX - minX, maxY - minY)
                : ModsTabStrip.GeometryFromLeftOrigin(
                    originRect.MinX, originRect.MinY,
                    originRect.MaxX - originRect.MinX, originRect.MaxY - originRect.MinY);
            var origin = ModsMenuTabDiag.Rect(
                strip.X - strip.Width * 0.5f,
                screenHeight > 0 ? screenHeight - (strip.Y + strip.Height * 0.5f) : strip.Y - strip.Height * 0.5f,
                strip.Width, strip.Height);
            for (int s = 0; s < ModsTabStrip.Order.Length; s++)
            {
                var button = MenuPanelLayout.StripButton(strip, s, ModsTabStrip.Order.Length);
                slots.Add(ModsMenuTabDiag.StripRow(
                    s, ModsTabStrip.Caption(ModsTabStrip.Order[s]),
                    ModsMenuTabDiag.CentreToClient(button.X, button.Y, button.Width, button.Height, screenHeight)));
            }

            stripSlot = ModsMenuTabDiag.StripSlot(index, name, origin, slots);
            stripSlotWhy = null;
        }
        else stripSlotWhy = ModsMenuTabDiag.WhyNoHit;

        int rowIndex = 0;
        stripPanel.ForEachRow((go, kind, _) =>
        {
            if (rows.Count >= ModsMenuTabDiag.StripRowsCap) return;
            if (go == null || !string.Equals(kind, "button", StringComparison.Ordinal)) return;
            int thisIndex = rowIndex++;
            try
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return;
                var rect = ScreenRectOf(rt);
                if (!rect.IsKnown || !rect.Contains(unityX, unityY)) return;
                rows.Add(ModsMenuTabDiag.StripRow(
                    thisIndex, RowCaption(go),
                    ModsMenuTabDiag.UnityRectToClient(rect.MinX, rect.MinY, rect.MaxX, rect.MaxY, screenHeight)));
            }
            catch { /* */ }
        });

        if (index >= 0)
        {
            GameObject? hit = null;
            int n = 0;
            stripPanel.ForEachRow((go, kind, _) =>
            {
                if (hit != null || go == null) return;
                if (!string.Equals(kind, "button", StringComparison.Ordinal)) return;
                if (n == index) hit = go;
                n++;
            });
            if (hit != null)
            {
                clickWouldInvoke = ModsMenuTabDiag.ClickWouldInvoke(index, RowCaption(hit));
                clickWouldInvokeWhy = null;
            }
            else clickWouldInvokeWhy = ModsMenuTabDiag.WhyNoHit;
        }
    }

    /// <summary>
    /// Job LIST4 item 2. The cell-row half of the pointer probe: every clickable cell of every
    /// live cell row on a shown panel whose screen rect contains the point is added to
    /// <paramref name="rows"/> as <c>&lt;rowKey&gt;/&lt;cellKey&gt;</c> — the same label
    /// <c>widget_click</c> takes — and the topmost one names <c>clickWouldInvoke</c> when the tab
    /// strip did not claim the point.
    ///
    /// <para>This is what the live <c>mods_ui hit</c> at the chooser chip was missing. It reported
    /// <c>clickWouldInvoke: null, why: no-hit</c> at the chip's own rect center while the real
    /// mouse click installed, because the only thing it ever looked at was the tab strip's rows;
    /// the probe's silence was never evidence about the pointer path.</para>
    /// </summary>
    private static void DescribeCellHit(
        float unityX,
        float unityY,
        int screenHeight,
        List<Dictionary<string, object?>> rows,
        ref Dictionary<string, object?>? clickWouldInvoke,
        ref string? clickWouldInvokeWhy)
    {
        bool named = clickWouldInvoke != null;
        Dictionary<string, object?>? found = null;
        for (int i = ShownPanels.Count - 1; i >= 0; i--)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive || !panel.Visible) continue;
            panel.ForEachCellButton((rowKey, cellKey, go) =>
            {
                if (rows.Count >= ModsMenuTabDiag.StripRowsCap) return;
                try
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null) return;
                    var rect = ScreenRectOf(rt);
                    if (!rect.IsKnown || !rect.Contains(unityX, unityY)) return;
                    string label = rowKey + "/" + cellKey;
                    rows.Add(ModsMenuTabDiag.StripRow(
                        -1, label,
                        ModsMenuTabDiag.UnityRectToClient(rect.MinX, rect.MinY, rect.MaxX, rect.MaxY, screenHeight)));
                    if (named) return;
                    found = ModsMenuTabDiag.ClickWouldInvoke(-1, label);
                    named = true;
                }
                catch { /* one unreadable cell must not cost the rest of the probe */ }
            });
        }

        if (found == null) return;
        clickWouldInvoke = found;
        clickWouldInvokeWhy = null;
    }

    private static Dictionary<string, object?> DescribeHoverRow(float unityX, float unityY)
    {
        var pointer = PointerSample.At(unityX, unityY);
        if (pointer.Kind != PointerKind.Inside)
            return ModsMenuTabDiag.HoverRow(null, "pointer-unreadable");
        UnityPanel? hit = ResolveHoveredPanel(pointer);
        if (hit == null) return ModsMenuTabDiag.HoverRow(null, ModsMenuTabDiag.WhyNoHit);
        string? name = null;
        try { name = hit.Text; } catch { name = null; }
        if (string.IsNullOrEmpty(name)) name = "panel";
        return ModsMenuTabDiag.HoverRow(name, null);
    }

    private static void DescribeFocusHit(
        float unityX, float unityY, int screenHeight,
        out Dictionary<string, object?>? focusField,
        out string? focusFieldWhy,
        out List<Dictionary<string, object?>> armedFields)
    {
        focusField = null;
        focusFieldWhy = ModsMenuTabDiag.WhyNoHit;
        armedFields = new List<Dictionary<string, object?>>();
        try { PruneArmedFields(); } catch { }

        TMP_InputField? found = null;
        string? foundWhy = null;
        float best = float.MaxValue;
        void Consider(TMP_InputField? field, string why)
        {
            if (field == null || found != null && best == 0f) return;
            try
            {
                var rt = field.GetComponent<RectTransform>();
                if (rt == null) return;
                var rect = ScreenRectOf(rt);
                if (!rect.IsKnown || !rect.Contains(unityX, unityY)) return;
                float cx = (rect.MinX + rect.MaxX) * 0.5f;
                float cy = (rect.MinY + rect.MaxY) * 0.5f;
                float dx = unityX - cx;
                float dy = unityY - cy;
                float d = dx * dx + dy * dy;
                if (d < best)
                {
                    best = d;
                    found = field;
                    foundWhy = why;
                }
            }
            catch { /* */ }
        }

        for (int i = 0; i < _armedFields.Count; i++)
            Consider(_armedFields[i].Field, ModsMenuTabDiag.WhyArmedRect);

        if (found == null)
        {
            for (int i = 0; i < ShownPanels.Count; i++)
            {
                var panel = ShownPanels[i];
                if (panel == null || !panel.Alive) continue;
                panel.ForEachRow((go, kind, _) =>
                {
                    if (kind != "input" || go == null) return;
                    try { Consider(go.GetComponentInChildren<TMP_InputField>(true), ModsMenuTabDiag.WhyRowInput); }
                    catch { /* */ }
                });
            }
        }

        string tab = CurrentWorkspaceTab();
        for (int i = 0; i < _armedFields.Count && armedFields.Count < ModsMenuTabDiag.ArmedFieldsCap; i++)
        {
            var row = DescribeArmedField(_armedFields[i].Field, tab, screenHeight);
            if (row != null) armedFields.Add(row);
        }

        if (found == null) return;
        focusField = DescribeArmedField(found, tab, screenHeight);
        if (focusField != null) focusField["why"] = foundWhy;
        focusFieldWhy = null;
        if (focusField == null) focusFieldWhy = foundWhy ?? ModsMenuTabDiag.WhyNoHit;
    }

    private static Dictionary<string, object?>? DescribeArmedField(TMP_InputField? field, string tab, int screenHeight)
    {
        if (field == null) return null;
        try
        {
            var rt = field.GetComponent<RectTransform>();
            Dictionary<string, object?>? rect = null;
            if (rt != null)
            {
                var sr = ScreenRectOf(rt);
                if (sr.IsKnown)
                    rect = ModsMenuTabDiag.UnityRectToClient(sr.MinX, sr.MinY, sr.MaxX, sr.MaxY, screenHeight);
            }

            bool? readOnly = null, isFocused = null, interactable = null;
            try { readOnly = field.readOnly; } catch { }
            try { isFocused = field.isFocused || ReferenceEquals(_ownedFocusField, field); } catch { }
            try { interactable = field.interactable; } catch { }
            string id = IsSearchInput(field) ? "search" : FieldCaption(field);
            return ModsMenuTabDiag.ArmedField(id, rect, readOnly, isFocused, interactable, OwnerTabOf(field, tab));
        }
        catch
        {
            return null;
        }
    }

    private static string CurrentWorkspaceTab()
    {
        try { return ModsMenu.CurrentTabStatusName() ?? ""; }
        catch { return ""; }
    }

    private static string OwnerTabOf(TMP_InputField field, string currentTab)
    {
        try
        {
            var t = field.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                for (int i = 0; i < ShownPanels.Count; i++)
                {
                    var panel = ShownPanels[i];
                    if (panel == null || !panel.Alive) continue;
                    if (panel.GameObject == t.gameObject)
                    {
                        if (panel.IsStrip) return "strip";
                        if (panel.IsSidebar) return string.IsNullOrEmpty(currentTab) ? "mods" : currentTab;
                        return "content";
                    }
                }
                t = t.parent;
            }
        }
        catch { /* */ }
        return string.IsNullOrEmpty(currentTab) ? "mods" : currentTab;
    }

    private static void DescribeUguiRaycast(
        float unityX, float unityY,
        out Dictionary<string, object?>? uguiRaycast,
        out string? uguiRaycastWhy)
    {
        uguiRaycast = null;
        uguiRaycastWhy = ModsMenuTabDiag.WhyNoEventSystem;
        EventSystem? es = null;
        try { es = EventSystem.current; } catch { }
        if (es == null) return;

        try
        {
            var ev = new PointerEventData(es) { position = new Vector2(unityX, unityY) };
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ev, results);
            if (results == null || results.Count == 0)
            {
                uguiRaycastWhy = ModsMenuTabDiag.WhyNoHit;
                return;
            }

            int n = results.Count < ModsMenuTabDiag.RaycastCap ? results.Count : ModsMenuTabDiag.RaycastCap;
            RaycastResult top = results[0];
            GameObject? go = null;
            try { go = top.gameObject; } catch { }
            string path = "";
            try { path = go == null ? "" : TransformPath(go.transform); } catch { path = SafeName(go); }
            int? order = null;
            try
            {
                var canvas = go == null ? null : go.GetComponentInParent<Canvas>();
                if (canvas != null) order = canvas.sortingOrder;
            }
            catch { }
            bool ours = IsOurs(go);
            uguiRaycast = ModsMenuTabDiag.UguiRaycast(path, order, ours);
            uguiRaycastWhy = null;
            _ = n;
        }
        catch (Exception e)
        {
            uguiRaycastWhy = "read-threw:" + e.Message;
        }
    }

    private static string RowCaption(GameObject go)
    {
        try
        {
            var tmp = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
        }
        catch { /* */ }
        try { return go.name ?? "button"; }
        catch { return "button"; }
    }

    private static MainMenuCanvasBase? FindMainMenuCanvas()
    {
        MainMenuCanvasBase? active = null;
        MainMenuCanvasBase? any = null;
        ForEach<MainMenuCanvasBase>(c =>
        {
            any ??= c;
            try
            {
                if (active == null && c.gameObject != null && c.gameObject.activeInHierarchy)
                    active = c;
            }
            catch { /* keep scanning */ }
        });
        return active ?? any;
    }

    private static Dictionary<string, object?> DescribeGameObject(GameObject? go)
    {
        if (go == null) return ModsMenuScene.MissingObject();
        bool activeSelf = false, activeInHierarchy = false;
        try { activeSelf = go.activeSelf; } catch { }
        try { activeInHierarchy = go.activeInHierarchy; } catch { }
        return ModsMenuScene.GameObjectState(true, activeSelf, activeInHierarchy);
    }

    private static Dictionary<string, object?> DescribeTitleView()
    {
        MainMenuNewView? active = null;
        MainMenuNewView? any = null;
        ForEach<MainMenuNewView>(c =>
        {
            any ??= c;
            try
            {
                if (active == null && c.gameObject != null && c.gameObject.activeInHierarchy)
                    active = c;
            }
            catch { /* keep scanning */ }
        });
        var view = active ?? any;
        if (view == null) return ModsMenuScene.TitleView(false, false, null, null);

        bool activeSelf = false;
        try { if (view.gameObject != null) activeSelf = view.gameObject.activeSelf; } catch { }

        bool? videoImageEnabled = null;
        try
        {
            if (view.VideoImage != null) videoImageEnabled = view.VideoImage.enabled;
        }
        catch { }

        var video = new List<object>();
        int stunPlayerId = 0;
        try
        {
            var stun = view.VideoPlayer;
            if (stun != null)
            {
                bool? playing = null, prepared = null, enabled = null;
                try { playing = stun.isPlaying; } catch { }
                try { prepared = stun.isPrepared; } catch { }
                try { enabled = stun.enabled; } catch { }
                video.Add(ModsMenuScene.Video("StunVideoPlayer", playing, prepared, enabled));
                try
                {
                    if (stun.Player != null) stunPlayerId = stun.Player.GetInstanceID();
                }
                catch { }
            }
        }
        catch { }

        try
        {
            if (view.gameObject != null)
            {
                ForEachIn<VideoPlayer>(view.gameObject, vp =>
                {
                    if (vp == null) return;
                    try
                    {
                        if (stunPlayerId != 0 && vp.GetInstanceID() == stunPlayerId) return;
                    }
                    catch { }
                    bool? playing = null, prepared = null, enabled = null;
                    try { playing = vp.isPlaying; } catch { }
                    try { prepared = vp.isPrepared; } catch { }
                    try { enabled = vp.enabled; } catch { }
                    video.Add(ModsMenuScene.Video("VideoPlayer", playing, prepared, enabled));
                });
            }
        }
        catch { }

        return ModsMenuScene.TitleView(true, activeSelf, videoImageEnabled, video);
    }

    private static void WalkMenuParent(
        Transform t,
        string path,
        int depth,
        List<Dictionary<string, object?>> rows,
        ref int walked,
        ref bool truncated)
    {
        if (t == null) return;
        if (!ModsMenuScene.TryBeginVisit(walked, ModsMenuScene.WalkCap, out truncated)) return;

        bool activeSelf = false, activeInHierarchy = false;
        try
        {
            var go = t.gameObject;
            if (go != null)
            {
                try { activeSelf = go.activeSelf; } catch { }
                try { activeInHierarchy = go.activeInHierarchy; } catch { }
            }
        }
        catch { }

        var typeNames = new List<string>();
        float? canvasGroupAlpha = null;
        string sprite = "", material = "", shader = "";
        try { CollectInterestingTypes(t.gameObject, typeNames, out canvasGroupAlpha, ref sprite, ref material, ref shader); }
        catch { }

        rows.Add(ModsMenuScene.NodeRow(
            path, activeSelf, activeInHierarchy, typeNames, canvasGroupAlpha, sprite, material, shader));
        walked++;
        if (depth >= ModsMenuScene.MaxDepth) return;

        int n = 0;
        try { n = t.childCount; } catch { return; }
        for (int i = 0; i < n; i++)
        {
            Transform? child = null;
            try { child = t.GetChild(i); } catch { continue; }
            if (child == null) continue;
            string name = SafeName(child.gameObject);
            if (string.IsNullOrEmpty(name)) name = i.ToString(CultureInfo.InvariantCulture);
            string childPath = string.IsNullOrEmpty(path) ? name : path + "/" + name;
            WalkMenuParent(child, childPath, depth + 1, rows, ref walked, ref truncated);
            if (truncated) return;
        }
    }

    private static void CollectInterestingTypes(
        GameObject? go, List<string> typeNames, out float? canvasGroupAlpha,
        ref string sprite, ref string material, ref string shader)
    {
        canvasGroupAlpha = null;
        if (go == null) return;
        try
        {
            var comps = go.GetComponents<Component>();
            if (comps == null) return;
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                string tn = "";
                try { tn = c.GetIl2CppType()?.Name ?? c.GetType().Name; }
                catch
                {
                    try { tn = c.GetType().Name; }
                    catch { tn = ""; }
                }
                if (!ModsMenuScene.TypeMatters(tn)) continue;
                bool seen = false;
                for (int j = 0; j < typeNames.Count; j++)
                {
                    if (string.Equals(typeNames[j], tn, StringComparison.Ordinal)) { seen = true; break; }
                }
                if (!seen) typeNames.Add(tn);
                if (canvasGroupAlpha == null
                    && string.Equals(tn, "CanvasGroup", StringComparison.Ordinal))
                {
                    try
                    {
                        var group = c.TryCast<CanvasGroup>();
                        if (group != null) canvasGroupAlpha = group.alpha;
                    }
                    catch { /* type name still stands */ }
                }

                if (ModsMenuScene.ReportsGraphicAssets(tn))
                    ReadGraphicAssets(c, tn, ref sprite, ref material, ref shader);
            }
        }
        catch { /* unreadable components stay omitted */ }
    }

    private static void ReadGraphicAssets(
        Component c, string typeName, ref string sprite, ref string material, ref string shader)
    {
        try
        {
            if (string.Equals(typeName, "RawImage", StringComparison.Ordinal)
                || typeName.EndsWith("RawImage", StringComparison.Ordinal))
            {
                var raw = c.TryCast<RawImage>();
                if (raw == null) return;
                try { if (raw.texture != null && string.IsNullOrEmpty(sprite)) sprite = raw.texture.name ?? ""; }
                catch { /* */ }
                ReadMaterial(raw.material, ref material, ref shader);
                return;
            }
        }
        catch { /* */ }

        try
        {
            var image = c.TryCast<Image>();
            if (image == null) return;
            try { if (image.sprite != null && string.IsNullOrEmpty(sprite)) sprite = image.sprite.name ?? ""; }
            catch { /* */ }
            ReadMaterial(image.material, ref material, ref shader);
        }
        catch { /* */ }
    }

    private static void ReadMaterial(Material? mat, ref string material, ref string shader)
    {
        if (mat == null) return;
        try { if (string.IsNullOrEmpty(material)) material = mat.name ?? ""; } catch { /* */ }
        try { if (mat.shader != null && string.IsNullOrEmpty(shader)) shader = mat.shader.name ?? ""; } catch { /* */ }
    }

    private static void CollectCameras(List<Dictionary<string, object?>> cameras)
    {
        ForEach<Camera>(cam =>
        {
            if (cam == null) return;
            if (cameras.Count >= ModsMenuScene.WalkCap) return;
            string name = "";
            bool enabled = false;
            string clearFlags = "";
            int cullingMask = 0;
            float depth = 0f;
            bool hasTargetTexture = false;
            try { name = SafeName(cam.gameObject); } catch { }
            try { enabled = cam.enabled; } catch { }
            try { clearFlags = cam.clearFlags.ToString(); } catch { }
            try { cullingMask = cam.cullingMask; } catch { }
            try { depth = cam.depth; } catch { }
            try { hasTargetTexture = cam.targetTexture != null; } catch { }
            cameras.Add(ModsMenuScene.Camera(name, enabled, clearFlags, cullingMask, depth, hasTargetTexture));
        });
    }

    private static void CollectSceneRoots(List<Dictionary<string, object?>> roots)
    {
        int scenes = 0;
        try { scenes = SceneManager.sceneCount; }
        catch { return; }
        for (int i = 0; i < scenes; i++)
        {
            Scene scene;
            try { scene = SceneManager.GetSceneAt(i); }
            catch { continue; }
            try
            {
                if (!scene.IsValid() || !scene.isLoaded) continue;
            }
            catch { continue; }
            try
            {
                var list = scene.GetRootGameObjects();
                if (list == null) continue;
                for (int r = 0; r < list.Length; r++)
                {
                    if (roots.Count >= ModsMenuScene.WalkCap) return;
                    var go = list[r];
                    if (go == null) continue;
                    string name = SafeName(go);
                    bool activeSelf = false;
                    try { activeSelf = go.activeSelf; } catch { }
                    roots.Add(ModsMenuScene.Root(name, activeSelf));
                }
            }
            catch { continue; }
        }
    }

    private static string TransformPath(Transform? t)
    {
        if (t == null) return "";
        try
        {
            var parts = new List<string>();
            var cur = t;
            int guard = 0;
            while (cur != null && guard++ < 64)
            {
                parts.Add(SafeName(cur.gameObject));
                cur = cur.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch
        {
            return SafeName(t.gameObject);
        }
    }

    private static void NoteShown(UnityPanel panel, bool shown)
    {
        if (shown)
        {
            if (!ShownPanels.Contains(panel)) ShownPanels.Add(panel);
        }
        else
        {
            ShownPanels.Remove(panel);
            // BUILDSEARCH1: the last panel hiding used to disarm the pump, and the disarm dropped a
            // panel field's focus. An external field (the build-menu search) can keep the pump armed
            // now, so the panel field's focus is dropped here, or a hidden field would go on taking
            // keys and holding the menu-hotkey guard.
            if (ShownPanels.Count == 0 && _ownedFocusField != null && !IsExternal(_ownedFocusField))
            {
                ClearUndo(_ownedFocusField, "blur");
                _ownedFocusField = null;
                TypeRepeats.Reset();
                _repeatField = null;
                MenuHotkeyGuard.Release();
                HideAllOwnedCarets();
                ModsMenu.NoteEscapeReleased();
            }
        }

        SyncShownInputPump();
    }

    /// <summary>
    /// Arm a <c>Plugin.Frame</c> pump for wheel and typing whenever any panel is shown.
    /// Distinct from <see cref="UiHoverGate"/>, which stays unarmed for an all-WhileShown
    /// set because the gameplay-block answer is constant.
    /// </summary>
    private static void SyncShownInputPump()
    {
        // BUILDSEARCH1: a Core field hosted in a game screen keeps the pump armed with no panel shown.
        bool want = UiShownInput.ShouldArm(ShownPanels.Count + ExternalFieldCount);
        if (want == _shownInputArmed) return;
        if (want)
        {
            try { Plugin.Frame += PumpShownInput; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: shown-input subscribe threw: " + e.Message);
                return;
            }

            _shownInputArmed = true;
            return;
        }

        _shownInputArmed = false;
        // NAV1: the focus holds a handle (and so its plugin's owner); with no panel shown nothing would drop it.
        try { UiNav.Release(); }
        catch { /* */ }
        try { Plugin.Frame -= PumpShownInput; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: shown-input unsubscribe threw: " + e.Message); }
        _leftHeld = false;
        _lastShownInputFrame = int.MinValue;
        _lastObservedPos = float.NaN;
        _lastWheelDelta = 0f;
        _lastWheelHadDelta = false;
        TypeRepeats.Reset();
        _repeatField = null;
        ClearUndo(_ownedFocusField, "blur");
        _ownedFocusField = null;
        // Job KEY2: the pump is unsubscribing, so nothing will recompute focus after this line.
        MenuHotkeyGuard.Release();
        HideAllOwnedCarets();
        ModsMenu.NoteEscapeReleased();
    }

    internal static void ArmSidebarScrollPreserve(float normalized, float offsetY)
    {
        _pendingSidebarNormalized = MenuPanelLayout.ClampNormalized(normalized);
        _pendingSidebarOffsetY = offsetY;
    }

    internal static bool PeekSidebarScrollPreserve(out float normalized, out float offsetY)
    {
        normalized = _pendingSidebarNormalized;
        offsetY = _pendingSidebarOffsetY;
        return !float.IsNaN(normalized);
    }

    /// <summary>
    /// User report 2026-10-10 (a Keybinds save jumped the list to the top): the content pane's restored offset, kept
    /// for the settle re-layout of that one content rect, as the sidebar's is. It lapses after a short time so it never
    /// fights the wheel.
    /// </summary>
    internal static void ArmContentScrollPreserve(RectTransform content, float offsetY)
    {
        _pendingContentRect = content == null ? IntPtr.Zero : content.Pointer;
        _pendingContentOffsetY = offsetY;
        _pendingContentUntilMs = Environment.TickCount64 + 1500;
    }

    private static bool PeekContentScrollPreserve(RectTransform rt, out float offsetY)
    {
        offsetY = _pendingContentOffsetY;
        if (_pendingContentRect == IntPtr.Zero || rt == null) return false;
        if (Environment.TickCount64 > _pendingContentUntilMs)
        {
            _pendingContentRect = IntPtr.Zero;
            return false;
        }

        return rt.Pointer == _pendingContentRect;
    }

    internal static void ClearSidebarScrollPreserve()
    {
        _pendingSidebarNormalized = float.NaN;
        _pendingSidebarOffsetY = 0f;
    }

    public bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget)
    {
        object? ev = kind switch
        {
            UiSlotKind.Click => widget.ClickEvent,
            UiSlotKind.Float => widget.FloatEvent,
            UiSlotKind.Bool => widget.BoolEvent,
            UiSlotKind.Text => widget.TextEvent,
            UiSlotKind.PointerEnter => widget.PointerEnterEvent,
            UiSlotKind.PointerExit => widget.PointerExitEvent,
            _ => null,
        };
        if (ev == null) return false;
        return BootstrapLink.TryWireUi(widgetId, (byte)kind, ev);
    }

    public void Unwire(ushort widgetId) => BootstrapLink.UnwireUi(widgetId);

    public void UnwireMany(IReadOnlyList<ushort> ids)
    {
        if (ids == null || ids.Count == 0) return;
        var arr = new ushort[ids.Count];
        for (int i = 0; i < ids.Count; i++)
            arr[i] = ids[i];
        BootstrapLink.UnwireUiMany(arr);
    }

    public int DestroyRoot(string rootName)
    {
        if (string.IsNullOrEmpty(rootName)) return 0;
        GameObject? found = null;
        ForEach<Transform>(t =>
        {
            if (found != null) return;
            try
            {
                var go = t.gameObject;
                if (go == null) return;
                if (t.parent != null) return;
                if ((go.name ?? "").Equals(rootName, StringComparison.OrdinalIgnoreCase))
                    found = go;
            }
            catch { /* */ }
        });
        if (found == null) return 0;
        bool immediate = BootstrapLink.MayDestroyUiImmediate();
        return TryDestroy(found, immediate) ? 1 : 0;
    }

    public int DestroyByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return 0;
        // RADIOART1: the drawn radio circles go with the generation that drew them.
        FreeRadioSprites();
        int n = 0;
        var doomed = new List<GameObject>();
        ForEach<Transform>(t =>
        {
            try
            {
                var go = t.gameObject;
                if (go == null) return;
                if (t.parent != null) return;
                if (!(go.name ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return;
                doomed.Add(go);
            }
            catch { /* */ }
        });

        bool immediate = BootstrapLink.MayDestroyUiImmediate();
        for (int i = 0; i < doomed.Count; i++)
        {
            if (TryDestroy(doomed[i], immediate))
                n++;
        }

        // Core Uninstall is the only caller: the listing-icon textures (LIST1) go with the roots.
        try { UnityIconDecoder.ReleaseAll(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: icon release: " + e.Message); }
        // C5: the Works across versions glyph's texture, after the rows that drew it.
        ReleaseLoopGlyph();
        // P6: the resolved panel views hold references to vanilla windows; drop them with the generation.
        ClearPanelViews();

        return n;
    }

    public void SetCapture(bool capture) => BootstrapLink.SetUiCapture(capture);

    // ---- C5: the loop-arrows glyph (CellSkin.LoopGlyph) ---------------------------------------------

    private static Texture2D? _loopGlyphTexture;
    private static Sprite? _loopGlyphSprite;

    /// <summary>
    /// The Works across versions glyph (<see cref="LoopGlyph"/>), white on transparent for the Image to tint. Made once per
    /// Core generation and shared by every row; destroyed on Core Uninstall (<see cref="ReleaseLoopGlyph"/>). Null when it
    /// cannot be made (the cell then draws nothing).
    /// </summary>
    internal static Sprite? LoopGlyphSprite()
    {
        if (Alive(_loopGlyphSprite) && Alive(_loopGlyphTexture)) return _loopGlyphSprite;
        ReleaseLoopGlyph();
        try
        {
            int w = LoopGlyph.TextureWidth, h = LoopGlyph.TextureHeight;
            var pixels = LoopGlyph.Pixels();
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "progenitor-loop-glyph",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _loopGlyphTexture = texture;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, pixels[y * w + x]));
            texture.Apply(false, false);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, w, h), new Vector2(0.5f, 0.5f), 100f);
            sprite.name = "progenitor-loop-glyph";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _loopGlyphSprite = sprite;
            return sprite;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: loop glyph: " + e.Message);
            ReleaseLoopGlyph();
            return null;
        }
    }

    /// <summary>Destroy the glyph's sprite and texture (Core Uninstall, or a half-made glyph).</summary>
    internal static void ReleaseLoopGlyph()
    {
        try { if (Alive(_loopGlyphSprite)) UnityEngine.Object.Destroy(_loopGlyphSprite); } catch { /* gone */ }
        try { if (Alive(_loopGlyphTexture)) UnityEngine.Object.Destroy(_loopGlyphTexture); } catch { /* gone */ }
        _loopGlyphSprite = null;
        _loopGlyphTexture = null;
    }

    // Job FUI36. The vanilla-attach half lives in VanillaSiteRuntime, one file per concern: this
    // class is the mod overlay and that one is the graft onto a live vanilla instance. They share
    // nothing but these three lines.
    public IDisposable? WatchSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame) =>
        VanillaSiteRuntime.Watch(site, onFrame);

    public IRuntimeWidget? BuildAttachContainer(
        VanillaUiSite site, string containerName, VanillaAttachParent parent, bool ignoreLayout) =>
        VanillaSiteRuntime.BuildContainer(site, containerName, parent, ignoreLayout);

    public IRuntimeWidget? CloneDonorRow(VanillaUiSite site, IRuntimeWidget container, string text) =>
        VanillaUiSites.IsPanel(site)
            ? ClonePanelRow(site, container, text)
            : VanillaSiteRuntime.CloneRow(site, container, text);

    /// <summary>
    /// The panel frame's rect, transformed out of the overlay canvas's units into screen pixels
    /// (job FUI19).
    ///
    /// <para>
    /// <b>Why the transform, and why screen pixels.</b> Since evidence 165 the overlay copies the
    /// game's own <c>CanvasScaler</c>, so the canvas is laid out in the game's design space and one
    /// canvas unit is <i>not</i> one device pixel — the scaler puts the scale factor on the canvas
    /// <c>RectTransform</c>'s own <c>localScale</c>, which is exactly what
    /// <c>RectTransform.TransformPoint</c> applies. The mouse device answers in screen pixels and
    /// nothing can change that, so the rect is moved to the mouse's ruler rather than the other way
    /// round: comparing <c>rt.rect</c> to the mouse directly (or dividing the mouse by a scale
    /// factor guessed here) is correct only at the scaler's reference resolution, i.e. right on the
    /// developer's screen and progressively wrong on the player's.
    /// </para>
    ///
    /// <para>
    /// <b>Why this and not <c>RectTransformUtility.RectangleContainsScreenPoint</c>.</b> It is the
    /// same resolution — with a <c>null</c> camera that helper treats the screen point as the world
    /// point, which is exact for a <c>ScreenSpaceOverlay</c> canvas because such a canvas's world
    /// space <i>is</i> the pixel grid — but it answers one bool for one point and keeps the
    /// arithmetic inside the game. Handing back the rect instead puts the containment, the
    /// overlapping-panel rule and the edge-crossing transition in <see cref="HoverRouter"/>, which
    /// is game-free and has tests, and leaves this method with the one thing only the game can
    /// answer. <c>WorldToScreenPoint(null, ...)</c> is still what converts each corner, so the
    /// space is the helper's own and not a re-derivation of it.
    /// </para>
    ///
    /// <para>
    /// Two corners rather than <c>GetWorldCorners</c>: our frames are axis-aligned and unrotated,
    /// the four-corner call allocates an IL2CPP array per panel per frame, and
    /// <see cref="ScreenRect.FromCorners"/> normalizes the pair so a mirrored or negatively scaled
    /// canvas still yields min/max in the right order.
    /// </para>
    /// </summary>
    public ScreenRect PanelRect(IRuntimePanel panel)
    {
        try
        {
            if (panel is not UnityPanel unity) return ScreenRect.Unknown;
            var go = unity.GameObject;
            // A hidden panel has no hover region. Ui detaches on Hide, so this only catches the
            // panel deactivated behind our back (a destroyed overlay, a Core swap mid-frame).
            if (go == null || !go.activeInHierarchy) return ScreenRect.Unknown;

            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return ScreenRect.Unknown;

            return ScreenRectOf(rt);
        }
        catch
        {
            // Contracted never to throw. Unknown is the fail-closed answer: the panel keeps
            // blocking rather than becoming a panel the player can see and cannot scroll.
            return ScreenRect.Unknown;
        }
    }

    /// <summary>
    /// One <c>RectTransform</c>'s two opposite corners in screen pixels. Extracted from
    /// <see cref="PanelRect"/> by job FUI31 so the interaction probe measures a vanilla canvas's
    /// raycast targets in exactly the space it measures our panel in — two rects compared across
    /// two different conversions is the defect evidence 165 already paid for once.
    /// </summary>
    private static ScreenRect ScreenRectOf(RectTransform rt)
    {
        Rect local = rt.rect;
        Vector3 worldMin = rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f));
        Vector3 worldMax = rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f));
        Vector2 a = RectTransformUtility.WorldToScreenPoint(null, worldMin);
        Vector2 b = RectTransformUtility.WorldToScreenPoint(null, worldMax);
        return ScreenRect.FromCorners(a.x, a.y, b.x, b.y);
    }

    /// <summary>
    /// The mouse in screen pixels, through the Input System's device layer — the same read
    /// <c>UnityKeyInputSource</c> uses for the keyboard, and for the same reason
    /// (<see cref="IKeyInputSource"/>: this build ships with the Input System as the active input
    /// handler, where legacy <c>UnityEngine.Input</c> throws on first read).
    /// <c>Mouse.position</c> is a <c>Vector2Control</c> in pixels with the origin at the bottom
    /// left, which is the space <see cref="ScreenRect"/> is in.
    ///
    /// <para>
    /// The device is re-fetched every pass and never cached: <c>Mouse.current</c> is replaced when
    /// the player unplugs a mouse or the Steam overlay re-initializes input, the same hazard
    /// <c>KeyEdgeTracker.NoteDevice</c> exists for.
    /// </para>
    ///
    /// <para>
    /// <b>Off the window is not the same as unreadable.</b> A pointer past the window edge, or a
    /// window that does not have focus, is a pointer that is definitely not over a mod panel, so it
    /// releases the block; a pointer that cannot be read at all keeps it. The focus test matters
    /// most on a second monitor, where the coordinates stay inside <c>Screen</c> bounds while the
    /// player is clicking in another application entirely.
    /// </para>
    /// </summary>
    public PointerSample ReadPointer()
    {
        // Job FUI30: the frame-driven half of the interaction probe. This is the one method of the
        // runtime that HoverCapture calls every frame, so it is where a probe armed by Show is
        // spent — a frame later, with the canvas sized and every row's OnEnable run. It costs a
        // null check per frame and never throws into the pointer read below.
        try { FireArmedProbe("a show"); } catch { /* */ }

        return SamplePointer();
    }

    /// <summary>
    /// KEYS8: whether the top UI raycast hit under the pointer is ours (<see cref="UiOwnership"/>, walking up the
    /// parents as the click diagnostics do). The top hit is the object that takes the click; a Progenitor object
    /// under a game window does not. Called on a click chord's press, never per frame.
    /// </summary>
    public bool PointerOverOwnedUi()
    {
        try
        {
            var sample = SamplePointer(ignoreFocus: true);
            if (sample.Kind != PointerKind.Inside) return false;
            var es = EventSystem.current;
            if (es == null) return false;
            var ev = new PointerEventData(es) { position = new Vector2(sample.X, sample.Y) };
            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ev, results);
            if (results == null || results.Count == 0) return false;
            return IsOurs(results[0].gameObject);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// MAPTP1: the pointer's in-window screen position for <see cref="WorldMap.TryReadPointer"/>, from the same
    /// sample the hover gate uses. False when unreadable or outside the window.
    /// </summary>
    internal static bool TrySamplePointerScreen(out float x, out float y)
    {
        var sample = SamplePointer();
        x = sample.X;
        y = sample.Y;
        return sample.Kind == PointerKind.Inside;
    }

    /// <summary>
    /// Pointer sample only. Wheel and typing used to run here, which meant they only ran while
    /// the hover gate was armed — and a WhileShown workspace disarms that gate.
    /// </summary>
    /// <summary>
    /// CHAT5: the pointer's in-window position whatever the window focus, for the chat box's own clicks and hover. A
    /// window in the background gets no presses anyway (the Input System disables the mouse there), so focus adds
    /// nothing for the chat box; and the dev bridge's focus-free mouse op (<c>background:true</c>) can then test it.
    /// </summary>
    internal static bool TrySamplePointerScreenAnyFocus(out float x, out float y)
    {
        var sample = SamplePointer(ignoreFocus: true);
        x = sample.X;
        y = sample.Y;
        return sample.Kind == PointerKind.Inside;
    }

    private static PointerSample SamplePointer(bool ignoreFocus = false)
    {
        // BRIDGEUI1: the bridge's synthetic pointer replaces the device while its lease is held. It needs no window
        // focus: it is the framework's own reading, not a device's.
        if (UiSyntheticInput.Active)
        {
            if (!UiSyntheticInput.HasPointer) return PointerSample.Outside;
            return SyntheticPointerSample(UiSyntheticInput.X, UiSyntheticInput.Y);
        }

        try
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return PointerSample.Unreadable;

            var position = mouse.position;
            if (position == null) return PointerSample.Unreadable;

            Vector2 p = position.ReadValue();

            try
            {
                if (!ignoreFocus && !Application.isFocused) return PointerSample.Outside;
            }
            catch
            {
                // An unreadable focus flag is not evidence the pointer left; fall through to the
                // bounds test, which is the answer we can actually defend.
            }

            float w, h;
            try
            {
                w = Screen.width;
                h = Screen.height;
            }
            catch
            {
                return PointerSample.At(p.x, p.y);
            }

            if (w > 0f && h > 0f && (p.x < 0f || p.y < 0f || p.x > w || p.y > h))
                return PointerSample.Outside;

            return PointerSample.At(p.x, p.y);
        }
        catch
        {
            return PointerSample.Unreadable;
        }
    }

    /// <summary>
    /// Per-frame typing and wheel while any panel is shown. Subscribed from
    /// <see cref="NoteShown"/>, not from the hover gate. The pump is the wheel mover:
    /// one <see cref="MenuPanelLayout.WheelRowsPerNotch"/>-row step per new notch, written
    /// to <c>content.anchoredPosition</c>. uGUI is inert on these panels
    /// (<c>scrollSensitivity = 0</c>), so EventSystem order cannot double-apply.
    /// </summary>
    private static void PumpShownInput()
    {
        long start = Stopwatch.GetTimestamp();
        bool dirtyBefore = FocusedTextDirty();
        try { PumpShownInputTick(); }
        finally
        {
            UiFrameCost.Note("pump", start);
            NoteFocusedTextDirty(dirtyBefore, FocusedTextDirty());
        }
    }

    private static void PumpShownInputTick()
    {
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = 0; }
        if (frame != 0 && frame == _lastShownInputFrame) return;
        _lastShownInputFrame = frame;

        EnsureLayoutBatchBound();
        UiLayoutBatch.Begin();
        try
        {
            // NAV1 review: what owned Enter and Space before this frame's typed keys ran (a field, a dialog, a capture).
            // The body may close or blur it on this very press, and the same press must not act again through UiNav.
            NoteNavKeyGates();
            PumpShownInputBody(frame);
            PumpNav();
        }
        finally { UiLayoutBatch.End(); }
    }

    // ---- NAV1: controller and keyboard navigation (UiNav) ---------------------------------------------------

    private static readonly long NavClockStart = Stopwatch.GetTimestamp();
    private static bool _navThrewLogged;

    /// <summary>Stick deflection that counts as a direction press.</summary>
    private const float NavStickPress = 0.5f;

    /// <summary>Mouse movement (pixels in a frame) that counts as the pointer being used.</summary>
    private const float NavPointerMove = 2f;

    /// <summary>Space was owned by a field or a capture when the frame began (<see cref="NoteNavKeyGates"/>).</summary>
    private static bool _navSpaceGated;

    /// <summary>Enter was owned by a field, a capture, a dialog or a screen's Enter when the frame began.</summary>
    private static bool _navEnterGated;

    /// <summary>A keyboard Space or Enter that began while owned stays out of navigation until it is released.</summary>
    private static bool _navSpaceLatched;

    /// <summary>The pointer's position at the last navigation sample (NaN before the first).</summary>
    private static float _navPointerX = float.NaN;

    private static float _navPointerY = float.NaN;

    private static bool _navEnterLatched;

    private static void NoteNavKeyGates()
    {
        bool field = false, capture = false, dialog = false;
        try { field = FocusedArmedField() != null; } catch { /* */ }
        try { capture = Keybinds.Capturing; } catch { /* */ }
        try { dialog = UiDialog.Current is { IsOpen: true } || UiDialog.EnterFallback != null; } catch { /* */ }
        _navSpaceGated = field || capture;
        _navEnterGated = field || capture || dialog;
        _navFieldGated = field;
    }

    private static bool _navFieldGated;
    private static bool _navDownLatched;
    private static bool _navSyntheticDownLatched;

    /// <summary>NAV1: read the navigation inputs and run <see cref="UiNav.Tick"/>. Once a frame while a panel is shown.</summary>
    private static void PumpNav()
    {
        NavSample sample;
        try { sample = SampleNav(); }
        catch { return; }
        double now = (Stopwatch.GetTimestamp() - NavClockStart) / (double)Stopwatch.Frequency;
        try { UiNav.Tick(sample, now); }
        catch (Exception e)
        {
            if (_navThrewLogged) return;
            _navThrewLogged = true;
            Plugin.Logger?.LogWarning("Ui: navigation threw (logged once): " + e.Message);
        }
    }

    /// <summary>
    /// NAV1: the controller (the current gamepad: D-pad or left stick, A, B, X, the triggers, the right stick) and the
    /// keyboard's arrows, Space and Enter (Enter stays a dialog's own answer while a dialog is open; Escape stays
    /// Bootstrap's). The framework's input layer: devices are read here and nowhere else.
    /// </summary>
    private static NavSample SampleNav()
    {
        var s = new NavSample();
        try { s.TextFocused = FocusedArmedField() != null; } catch { s.TextFocused = false; }
        try { s.Capturing = Keybinds.Capturing; } catch { s.Capturing = false; }

        var pad = Gamepad.current;
        if (pad != null)
        {
            try
            {
                Vector2 stick = pad.leftStick.ReadValue();
                s.Up = pad.dpad.up.isPressed || stick.y > NavStickPress;
                s.Down = pad.dpad.down.isPressed || stick.y < -NavStickPress;
                s.Left = pad.dpad.left.isPressed || stick.x < -NavStickPress;
                s.Right = pad.dpad.right.isPressed || stick.x > NavStickPress;
                s.Submit = pad.buttonSouth.isPressed;
                s.Cancel = pad.buttonEast.isPressed;
                s.Reset = pad.buttonWest.isPressed;
                s.TabPrev = pad.leftTrigger.isPressed;
                s.TabNext = pad.rightTrigger.isPressed;
                s.Scroll = pad.rightStick.ReadValue().y;
            }
            catch { /* a pad read that fails this frame is no input */ }
        }

        var keyboard = Keyboard.current;
        if (keyboard != null && !s.TextFocused)
        {
            try
            {
                s.Up |= keyboard.upArrowKey.isPressed;
                // FORMDIALOG1: a Down that left a dialog's field (the typed-key pump) does not step again until released.
                s.Down |= UiNavMath.KeyCounts(keyboard.downArrowKey.isPressed, _navFieldGated, ref _navDownLatched);
                s.Left |= keyboard.leftArrowKey.isPressed;
                s.Right |= keyboard.rightArrowKey.isPressed;
                s.Submit |= UiNavMath.KeyCounts(keyboard.spaceKey.isPressed, _navSpaceGated, ref _navSpaceLatched);
                bool enter = UiNavMath.KeyCounts(keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed, _navEnterGated, ref _navEnterLatched);
                s.Submit |= enter;
                s.Enter |= enter;
            }
            catch { /* */ }
        }

        var mouse = Mouse.current;
        if (mouse != null)
        {
            try
            {
                Vector2 delta = mouse.delta.ReadValue();
                // The position too: a device that reports where the pointer is without a delta (live 2026-10-10: the
                // bridge's mouse op) still moved it.
                Vector2 at = mouse.position.ReadValue();
                float dx = float.IsNaN(_navPointerX) ? 0f : at.x - _navPointerX;
                float dy = float.IsNaN(_navPointerY) ? 0f : at.y - _navPointerY;
                _navPointerX = at.x;
                _navPointerY = at.y;
                s.PointerActive = delta.x * delta.x + delta.y * delta.y > NavPointerMove * NavPointerMove
                    || dx * dx + dy * dy > NavPointerMove * NavPointerMove
                    || mouse.leftButton.isPressed || mouse.rightButton.isPressed;
            }
            catch { /* */ }
        }

        ApplySyntheticNav(ref s);
        return s;
    }

    private static int _navSyntheticSerial = -1;

    /// <summary>
    /// BRIDGEUI1: the bridge's synthetic navigation (held buttons, the right stick, UI-layer arrow, Space and Enter taps)
    /// joins the device read, and the synthetic pointer moving or clicking counts as the pointer in use.
    /// </summary>
    private static void ApplySyntheticNav(ref NavSample s)
    {
        var nav = UiSyntheticInput.NavHeld;
        if (nav != UiSyntheticNav.None)
        {
            s.Up |= (nav & UiSyntheticNav.Up) != 0;
            s.Down |= (nav & UiSyntheticNav.Down) != 0;
            s.Left |= (nav & UiSyntheticNav.Left) != 0;
            s.Right |= (nav & UiSyntheticNav.Right) != 0;
            s.Submit |= (nav & UiSyntheticNav.Submit) != 0;
            s.Cancel |= (nav & UiSyntheticNav.Cancel) != 0;
            s.Reset |= (nav & UiSyntheticNav.Reset) != 0;
            s.TabPrev |= (nav & UiSyntheticNav.TabPrev) != 0;
            s.TabNext |= (nav & UiSyntheticNav.TabNext) != 0;
        }

        float stick = UiSyntheticInput.StickHeld;
        if (stick != 0f && Math.Abs(stick) > Math.Abs(s.Scroll)) s.Scroll = stick;
        if (!UiSyntheticInput.Active) return;
        int frame;
        try { frame = Time.frameCount; }
        catch { frame = 0; }
        if (!s.TextFocused)
        {
            s.Up |= UiSyntheticInput.KeyHeld(UiKey.UpArrow, frame);
            s.Down |= UiNavMath.KeyCounts(UiSyntheticInput.KeyHeld(UiKey.DownArrow, frame), _navFieldGated, ref _navSyntheticDownLatched);
            s.Left |= UiSyntheticInput.KeyHeld(UiKey.LeftArrow, frame);
            s.Right |= UiSyntheticInput.KeyHeld(UiKey.RightArrow, frame);
            s.Submit |= UiNavMath.KeyCounts(UiSyntheticInput.KeyHeld(UiKey.Space, frame), _navSpaceGated, ref _navSyntheticSpaceLatched);
            bool enter = UiNavMath.KeyCounts(UiSyntheticInput.KeyHeld(UiKey.Enter, frame), _navEnterGated, ref _navSyntheticEnterLatched);
            s.Submit |= enter;
            s.Enter |= enter;
        }

        int serial = UiSyntheticInput.PointerSerial;
        if (serial != _navSyntheticSerial)
        {
            if (_navSyntheticSerial >= 0) s.PointerActive = true;
            _navSyntheticSerial = serial;
        }
    }

    private static bool _navSyntheticSpaceLatched;
    private static bool _navSyntheticEnterLatched;

    /// <summary>
    /// P7b: the Shift and Ctrl keys held now, for a click's multi-select meaning (Ctrl adds a row, Shift extends a
    /// range). Part of the UI kit's pointer sample, like <see cref="PrimaryPointerHeld"/>; it binds no action to a key.
    /// </summary>
    internal static UiClickModifiers HeldClickModifiers()
    {
        var held = UiClickModifiers.None;
        // BRIDGEUI1: the synthetic modifiers held with the synthetic pointer's click.
        if (UiSyntheticInput.Active) return UiSyntheticInput.Modifiers;
        try
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return held;
            if ((keyboard.leftShiftKey != null && keyboard.leftShiftKey.isPressed)
                || (keyboard.rightShiftKey != null && keyboard.rightShiftKey.isPressed)) held |= UiClickModifiers.Shift;
            if ((keyboard.leftCtrlKey != null && keyboard.leftCtrlKey.isPressed)
                || (keyboard.rightCtrlKey != null && keyboard.rightCtrlKey.isPressed)) held |= UiClickModifiers.Ctrl;
        }
        catch { /* a keyboard mid-teardown: no modifiers */ }

        return held;
    }

    /// <summary>
    /// The primary (left) mouse button's held state right now, for <see cref="Ui.WatchPointer"/>:
    /// the EventTrigger trampoline carries no event payload, so the watch samples the button the
    /// moment its PointerDown dispatches. False with no mouse or on any read failure.
    /// </summary>
    internal static bool PrimaryPointerHeld()
    {
        if (UiSyntheticInput.Active) return (UiSyntheticInput.Buttons & UiSyntheticButtons.Left) != 0;
        try
        {
            var mouse = Mouse.current;
            return mouse != null && mouse.leftButton != null && mouse.leftButton.isPressed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// BRIDGEUI1: the left button for the shown-input pump: the synthetic pointer's while its lease is held, else the
    /// mouse's. False with neither.
    /// </summary>
    private static bool LeftButtonHeld(Mouse? mouse)
    {
        if (UiSyntheticInput.Active) return (UiSyntheticInput.Buttons & UiSyntheticButtons.Left) != 0;
        try { return mouse != null && mouse.leftButton != null && mouse.leftButton.isPressed; }
        catch { return false; }
    }

    /// <summary>
    /// BRIDGEUI1: the wheel reading for one pump sample: the synthetic notches while the lease is held (one notch a
    /// sample with a zero between, the edge a real notch makes), else the mouse's scroll.
    /// </summary>
    private static float WheelDeltaY(Mouse? mouse)
    {
        // One reading a frame, shared by every reader (the panel pump and the chat box's wheel): a second reader in the
        // same frame must not dequeue the next synthetic notch, or each would see only half of them.
        int frame = -1;
        try { frame = Time.frameCount; } catch { /* no frame clock: read fresh */ }
        if (frame >= 0 && frame == _wheelSampleFrame) return _wheelSampleValue;
        float value;
        if (UiSyntheticInput.Active) value = UiSyntheticInput.SampleWheel();
        else
        {
            try { value = mouse == null ? 0f : mouse.scroll.ReadValue().y; }
            catch { value = 0f; }
        }

        _wheelSampleFrame = frame;
        _wheelSampleValue = value;
        return value;
    }

    private static int _wheelSampleFrame = -1;
    private static float _wheelSampleValue;

    /// <summary>
    /// This frame's wheel reading for the chat box, whatever the window focus (a background window gets no wheel
    /// anyway). The same reading the panel pump uses, including the dev bridge's synthetic notches.
    /// </summary>
    internal static float SampleWheelDeltaY()
    {
        Mouse? mouse = null;
        try { mouse = Mouse.current; } catch { /* */ }
        return WheelDeltaY(mouse);
    }

    /// <summary>
    /// BRIDGEUI1: whether the player used the real mouse or keyboard since the last call (a button or key held, or the
    /// pointer moved more than a few pixels), so the bridge's synthetic input yields to them. Reads the devices: part of
    /// the framework's input layer. Binds no action.
    /// </summary>
    internal static bool PhysicalInputUsed(ref float lastX, ref float lastY, ref bool havePointer)
    {
        bool used = false;
        try
        {
            var mouse = Mouse.current;
            if (mouse != null)
            {
                if ((mouse.leftButton != null && mouse.leftButton.isPressed)
                    || (mouse.rightButton != null && mouse.rightButton.isPressed)
                    || (mouse.middleButton != null && mouse.middleButton.isPressed)) used = true;
                var p = mouse.position.ReadValue();
                if (havePointer && (Math.Abs(p.x - lastX) > 3f || Math.Abs(p.y - lastY) > 3f)) used = true;
                lastX = p.x;
                lastY = p.y;
                havePointer = true;
            }

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.anyKey != null && keyboard.anyKey.isPressed) used = true;
        }
        catch { /* a device mid-teardown is no activity */ }

        return used;
    }

    /// <summary>BRIDGEUI1: a key the synthetic input holds for the typed-key pump (a tap, or a held modifier).</summary>
    private static bool SyntheticKeyHeld(UiKey ui, int frame)
    {
        if (!UiSyntheticInput.Active) return false;
        if (UiSyntheticInput.KeyHeld(ui, frame)) return true;
        var m = UiSyntheticInput.Modifiers;
        return ((m & UiClickModifiers.Shift) != 0 && ui == UiKey.LeftShift)
            || ((m & UiClickModifiers.Ctrl) != 0 && ui == UiKey.LeftCtrl);
    }

    private static void PumpShownInputBody(int frame)
    {
        var pointer = SamplePointer();
        var mouse = Mouse.current;
        float deltaY = 0f;
        string hitName = "none";
        float pos = 1f;
        float contentH = 0f;
        float viewH = 0f;
        float? nextPos = null;
        bool pumpWrote = false;
        int keys = 0;
        char typed = '\0';
        bool focused = false;
        string selected = "none";
        bool click = false;
        bool activated = false;
        bool dragging = false;
        try
        {
            // BRIDGEUI1: the synthetic pointer stands in for the mouse (it needs no device).
            if (mouse != null || UiSyntheticInput.Active)
            {
                dragging = LeftButtonHeld(mouse);
                ObservePanelWheel(pointer, mouse, out deltaY, out hitName, out pos, out contentH, out viewH);
            }
        }
        catch { /* a missed sample is not a pointer failure */ }

        float posBefore = pos;
        bool sampled = contentH > 0.5f;
        bool posMoved = sampled && !float.IsNaN(_lastObservedPos)
            && (posBefore - _lastObservedPos > 0.001f || posBefore - _lastObservedPos < -0.001f);
        bool hadDelta = UiShownInput.HasWheelDelta(deltaY);
        bool notch = UiShownInput.IsNewNotch(deltaY, _lastWheelDelta, _lastWheelHadDelta);
        _lastWheelDelta = deltaY;
        _lastWheelHadDelta = hadDelta;

        if (notch && !dragging)
        {
            try
            {
                UnityPanel? hit = pointer.Kind == PointerKind.Inside ? ResolveHoveredPanel(pointer) : null;
                var scroll = hit?.Scroll;
                // TEXTEXPAND1: a text area or the expanded editor under the pointer takes the notch
                // while its text can move that way; at its end the notch scrolls the list.
                // P7b: a widget that scrolls itself (the data table's body) takes the notch over its area
                // while it can move that way (UiWheelClaims); at its end the notch goes on as below.
                if (pointer.Kind == PointerKind.Inside && TryWheelClaim(pointer, deltaY, hit))
                {
                    pumpWrote = true;
                    scroll = null;
                }
                else if (TryScrollTextField(pointer, deltaY, hit))
                {
                    pumpWrote = true;
                    scroll = null;
                }
                // INSTALLSIDES1: a scroll block under the pointer takes the notch while it can move.
                else if (scroll != null && TryScrollInner(pointer, deltaY, hit))
                {
                    pumpWrote = true;
                    scroll = null;
                }

                if (scroll != null)
                {
                    pumpWrote = true;
                    if (ApplyScrollOffset(scroll, deltaY, contentH, viewH, posBefore, out float written))
                    {
                        nextPos = written;
                        pos = written;
                    }
                    else
                    {
                        nextPos = MenuPanelLayout.NextScrollPosition(
                            posBefore, deltaY, contentH, viewH, MenuPanelLayout.RowStep);
                    }

                    // INSTALLUX1 item 7: the list moved, so a pane whose icon requests follow the
                    // scroll window asks for the new one. Idempotent per URL, so this is cheap.
                    UiReveal.RaiseScrolled();
                }
            }
            catch { /* a missed write is applied=none on this line */ }
        }

        string applied = UiShownInput.AppliedToken(notch, posMoved, pumpWrote);
        if (sampled) _lastObservedPos = pos;

        // TEXTEXPAND1: the expanded editor's scroll bar (a drag moves the text; an edit, the wheel or the
        // caret moved the text, so the bar follows). Nothing to do with no editor open.
        if (EditorBars.Count > 0)
        {
            try { TickEditorBars(dragging); }
            catch { /* the bar is cosmetic; the text still scrolls */ }
        }

        try
        {
            if (mouse != null || UiSyntheticInput.Active)
                PumpTextField(pointer, mouse, frame, out keys, out typed, out focused, out selected, out click, out activated);
        }
        catch { /* typing is best-effort */ }

        try { SyncOwnedCarets(); }
        catch { /* a missed blink frame is better than a throw on the pump */ }

        bool interesting = notch || keys > 0 || click || (posMoved && !pumpWrote);
        // BUILDSEARCH1: with no panel shown the pump runs only for an external field (the build-menu
        // search, the whole time the build menu is open); its once-a-second heartbeat would fill the
        // log during ordinary building.
        // A click or a wheel notch on the game's own screen is not news either: only typing and a click
        // that focused the box are logged with no panel shown.
        if (ShownPanels.Count == 0 && keys == 0 && !activated) return;
        long now = Environment.TickCount64;
        if (!UiShownInput.ShouldLog(now, _lastShownInputLogMs, interesting)) return;
        _lastShownInputLogMs = now;
        bool hoverPump = false;
        try
        {
            string describe = UiHoverGate.Describe();
            hoverPump = describe.IndexOf("pump=True", StringComparison.Ordinal) >= 0;
        }
        catch { /* */ }
        if (ReferenceEquals(selected, SelectedUnread)) selected = DescribeSelected();
        try
        {
            Plugin.Logger?.LogDiag(UiShownInput.Format(
                ShownPanels.Count, hoverPump, deltaY, notch, hitName, posBefore, nextPos, contentH, viewH,
                applied, MenuPanelLayout.WheelMover,
                keys, typed, focused, selected, click, activated));
        }
        catch { /* */ }
    }

    /// <summary>
    /// Sample the wheel and the hovered list position. Prefer the viewport rect so a
    /// pointer over the search field is not treated as a list hit, and sizeDelta when
    /// rect.height is still the pre-canvas-update zero. The pump writes separately
    /// through <see cref="ApplyScrollOffset"/>.
    /// </summary>
    private static void ObservePanelWheel(
        PointerSample pointer,
        Mouse? mouse,
        out float deltaY,
        out string hitName,
        out float pos,
        out float contentH,
        out float viewH)
    {
        deltaY = 0f;
        hitName = "none";
        pos = 1f;
        contentH = 0f;
        viewH = 0f;
        deltaY = WheelDeltaY(mouse);

        if (pointer.Kind != PointerKind.Inside) return;
        UnityPanel? hit = ResolveHoveredPanel(pointer);
        if (hit != null)
        {
            try { hitName = hit.Text ?? "panel"; }
            catch { hitName = "panel"; }
        }

        if (hit == null) return;
        var scroll = hit.Scroll;
        if (scroll == null) return;
        try { scroll.scrollSensitivity = MenuPanelLayout.UguiScrollSensitivity; }
        catch { /* */ }
        RectTransform? content = null;
        RectTransform? viewport = null;
        try { content = scroll.content; } catch { /* */ }
        try { viewport = scroll.viewport; } catch { /* */ }
        if (content == null || viewport == null) return;
        contentH = ContentHeightOf(content);
        viewH = ViewportHeightOf(viewport);
        pos = MenuPanelLayout.NormalizedFromOffset(content.anchoredPosition.y, contentH, viewH);
    }

    /// <summary>
    /// Write one notch onto the hovered list. Returns false when the write did not
    /// change position (content fits, or already at the end).
    /// </summary>
    private static bool ApplyScrollOffset(
        ScrollRect scroll,
        float deltaY,
        float contentH,
        float viewH,
        float pos,
        out float nextPos)
    {
        nextPos = pos;
        if (!MenuPanelLayout.CanApplyNormalized(contentH, viewH)) return false;
        nextPos = MenuPanelLayout.NextScrollPosition(
            pos, deltaY, contentH, viewH, MenuPanelLayout.RowStep);
        if (nextPos - pos < 0.0001f && pos - nextPos < 0.0001f) return false;
        RectTransform? content = null;
        try { content = scroll.content; } catch { /* */ }
        if (content == null) return false;
        float y = MenuPanelLayout.ContentOffsetY(nextPos, contentH, viewH);
        try
        {
            content.anchoredPosition = new Vector2(content.anchoredPosition.x, y);
            // The pump owns the offset from this notch. A leftover row-select preserve
            // must not snap the list back on the next settle or Relayout.
            ClearSidebarScrollPreserve();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// P7b: offer a wheel notch to the widget claims (<see cref="UiWheelClaims"/>) of the topmost shown panel under the
    /// pointer only, and only inside that panel's visible viewport: a table under a dialog or another panel, and rows
    /// clipped out of the viewport, never take the wheel.
    /// </summary>
    private static bool TryWheelClaim(PointerSample pointer, float deltaY, UnityPanel? hovered)
    {
        if (UiWheelClaims.Count == 0) return false;
        // WINDOWS1 review: the panel the wheel is over is the one ResolveHoveredPanel picked (draw order first), never
        // the last-shown panel whose frame happens to contain the pointer (a table behind a window).
        var top = hovered;
        if (top == null) return false;
        return UiWheelClaims.TryTake(pointer.X, pointer.Y, deltaY, top, top.ViewportScreenRect);
    }

    private static UnityPanel? ResolveHoveredPanel(PointerSample pointer)
    {
        // WINDOWS1 live fix: a window or dialog (drawn above the ordinary panels and the workspace) whose frame is under the
        // pointer takes the wheel, the topmost by canvas order. Over a window's info column (outside its viewport) the
        // wheel went on to the gallery page behind the window.
        UnityPanel? above = null;
        int aboveOrder = int.MinValue;
        for (int i = ShownPanels.Count - 1; i >= 0; i--)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive || !panel.Visible || panel.WheelPassThrough) continue;
            int order = panel.CanvasOrder;
            if (order <= UiOwnership.OverlaySorting || order <= aboveOrder) continue;
            var rect = ScreenRectOfPanel(panel);
            if (!rect.IsKnown || !rect.Contains(pointer.X, pointer.Y)) continue;
            above = panel;
            aboveOrder = order;
        }

        if (above != null) return above;

        UnityPanel? viewportHit = null;
        UnityPanel? frameHit = null;
        for (int i = ShownPanels.Count - 1; i >= 0; i--)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive || !panel.Visible || panel.WheelPassThrough) continue;
            var view = panel.ViewportScreenRect;
            if (view.IsKnown && view.Contains(pointer.X, pointer.Y))
            {
                viewportHit = panel;
                break;
            }

            var rect = ScreenRectOfPanel(panel);
            if (rect.IsKnown && rect.Contains(pointer.X, pointer.Y) && frameHit == null)
                frameHit = panel;
        }

        return viewportHit ?? frameHit;
    }

    private static float ContentHeightOf(RectTransform content)
    {
        float h = 0f;
        try { h = content.sizeDelta.y; } catch { /* */ }
        if (h > 0.5f) return h;
        try { h = content.rect.height; } catch { /* */ }
        return h;
    }

    /// <summary>
    /// Job LIST4 item 3. Measure the band from <paramref name="first"/>'s top to
    /// <paramref name="last"/>'s bottom in the panel's content pixels, ask
    /// <see cref="MenuPanelLayout.RevealOffsetY"/> where the viewport should sit, and write it.
    /// The written offset is reported back as <c>viewportTop</c>, so a readback shows where the
    /// list ended up rather than where it was.
    /// </summary>
    internal static bool TryRevealBlock(
        IRuntimePanel panel,
        IRuntimeWidget first,
        IRuntimeWidget last,
        bool toTop,
        out float rowTop,
        out float viewportTop,
        out float viewportHeight)
    {
        rowTop = 0f;
        viewportTop = 0f;
        viewportHeight = 0f;
        if (panel is not UnityPanel host || !host.Alive) return false;
        var scroll = host.Scroll;
        if (scroll == null) return false;
        RectTransform? content = null, viewport = null;
        try { content = scroll.content; } catch { /* */ }
        try { viewport = scroll.viewport; } catch { /* */ }
        if (content == null || viewport == null) return false;

        var firstRt = RectTransformOfWidget(first);
        if (firstRt == null) return false;
        var lastRt = RectTransformOfWidget(last) ?? firstRt;

        // The list viewport's own rect, not the row parent's: the pinned toolbar, filters and
        // header sit above it in the same parent, and measuring the row against that parent's top
        // is what scrolled the opened row off the top of the list (LIST4 item 3, second pass).
        var viewRect = host.ViewportScreenRect;
        if (!viewRect.IsKnown) viewRect = ScreenRectOf(viewport);
        var rowRect = ScreenRectOf(firstRt);
        if (!viewRect.IsKnown || !rowRect.IsKnown) return false;
        var lastRect = ScreenRectOf(lastRt);

        float contentH = ContentHeightOf(content);
        viewportHeight = viewRect.MaxY - viewRect.MinY;
        if (viewportHeight <= 0.5f) viewportHeight = ViewportHeightOf(viewport);
        if (viewportHeight <= 0.5f) return false;

        try
        {
            float was = content.anchoredPosition.y;
            float blockBottomY = lastRect.IsKnown && lastRect.MinY < rowRect.MinY ? lastRect.MinY : rowRect.MinY;
            float wanted = MenuPanelLayout.RevealOffsetFromScreen(
                rowRect.MaxY, blockBottomY, viewRect.MaxY, viewportHeight, was, contentH, toTop, out rowTop);
            if (wanted - was > 0.5f || was - wanted > 0.5f)
            {
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, wanted);
                // The reveal owns the offset now; a stale row-select preserve must not snap
                // the list back on the next settle (the wheel pump's rule).
                ClearSidebarScrollPreserve();
            }

            // Report the offset the list actually holds, read back off the ScrollRect rather than
            // the number we asked for, so a value Unity clamped or refused is not reported as
            // written. rowTop stays the distance from the same read that produced this offset —
            // the two must describe one layout, or the readback describes neither.
            viewportTop = wanted;
            try { viewportTop = content.anchoredPosition.y; }
            catch { /* keep the asked-for offset */ }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// INSTALLSIDES2: the row-anchor hook (<see cref="UiReveal.AnchorHook"/>). Measures from the
    /// rows' live screen rects against the list viewport's own rect, like the reveal, and writes the
    /// content offset only when asked.
    /// </summary>
    internal static bool TryAnchorRow(
        IRuntimePanel panel, IRuntimeWidget row, bool write, float wantedY, out float rowY, out float offset)
    {
        rowY = 0f;
        offset = 0f;
        if (panel is not UnityPanel host || !host.Alive) return false;
        var scroll = host.Scroll;
        if (scroll == null) return false;
        RectTransform? content = null, viewport = null;
        try { content = scroll.content; } catch { /* */ }
        try { viewport = scroll.viewport; } catch { /* */ }
        if (content == null || viewport == null) return false;
        var rowRt = RectTransformOfWidget(row);
        if (rowRt == null) return false;
        var viewRect = host.ViewportScreenRect;
        if (!viewRect.IsKnown) viewRect = ScreenRectOf(viewport);
        var rowRect = ScreenRectOf(rowRt);
        if (!viewRect.IsKnown || !rowRect.IsKnown) return false;
        float viewH = viewRect.MaxY - viewRect.MinY;
        if (viewH <= 0.5f) viewH = ViewportHeightOf(viewport);
        try
        {
            // Screen y grows upward: the row's top edge is rowRect.MaxY, the viewport's viewRect.MaxY.
            rowY = viewRect.MaxY - rowRect.MaxY;
            float was = content.anchoredPosition.y;
            offset = was;
            if (!write) return true;
            float next = MenuPanelLayout.AnchorOffset(was, rowY, wantedY, ContentHeightOf(content), viewH);
            if (Math.Abs(next - was) > 0.5f)
            {
                content.anchoredPosition = new Vector2(content.anchoredPosition.x, next);
                // The anchor owns the offset now; a stale preserve must not snap the list back.
                ClearSidebarScrollPreserve();
            }
            try { offset = content.anchoredPosition.y; } catch { offset = next; }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static RectTransform? RectTransformOfWidget(IRuntimeWidget? widget)
    {
        GameObject? go = widget switch
        {
            UnityCellRow row => row.GameObject,
            UnityWidget w => w.GameObject,
            _ => null,
        };
        if (go == null) return null;
        try { return go.GetComponent<RectTransform>(); }
        catch { return null; }
    }

    private static float ViewportHeightOf(RectTransform viewport)
    {
        try { return viewport.rect.height; }
        catch { return 0f; }
    }

    private static ScreenRect ScreenRectOfPanel(UnityPanel panel)
    {
        try
        {
            var go = panel.GameObject;
            if (go == null || !go.activeInHierarchy) return ScreenRect.Unknown;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return ScreenRect.Unknown;
            return ScreenRectOf(rt);
        }
        catch
        {
            return ScreenRect.Unknown;
        }
    }

    private static void PumpTextField(
        PointerSample pointer,
        Mouse? mouse,
        int frame,
        out int keys,
        out char typed,
        out bool focused,
        out string selected,
        out bool click,
        out bool activated)
    {
        keys = 0;
        typed = '\0';
        focused = false;
        selected = "none";
        click = false;
        activated = false;

        bool held = LeftButtonHeld(mouse);
        bool pressed = held && !_leftHeld;
        _leftHeld = held;
        if (pressed)
        {
            if (pointer.Kind == PointerKind.Inside)
            {
                click = true;
                activated = FocusFieldAt(pointer.X, pointer.Y);
                // Strip first, and never as a sidebar row index. FindShownScroll
                // skips the strip for the wheel; clicks must not copy that skip.
                if (!activated) activated = ClickStripButtonAt(pointer.X, pointer.Y);
            }

            if (!activated) BlurArmedFieldOnClickElsewhere();
        }

        // SEARCHCLEAR1: the clear buttons brighten under the pointer and darken while pressed.
        try { SyncSearchClearTints(pointer, held); } catch { /* the tint is cosmetic */ }

        try { PumpTypedKeys(frame, out keys, out typed); } catch { /* */ }
        PruneArmedFields();

        var field = FocusedArmedField();
        focused = field != null;
        // Job KEY2. The pump's own focus answer is the one the menu-hotkey block follows, so
        // every way a field can stop being focused - Escape, a click elsewhere, a rebuild that
        // destroyed it, the tab changing - releases the block here without knowing it exists.
        SyncMenuHotkeyGuard(field);
        if (field != null)
        {
            try { selected = FieldCaption(field); }
            catch { selected = "field"; }
        }
        else
        {
            // Read only if the shown-input line is actually written: Object.name builds a new
            // IL2CPP string, and this ran every frame the build menu was open.
            selected = SelectedUnread;
        }
    }

    /// <summary>Placeholder for "the event system's selection, not read yet"; see <see cref="DescribeSelected"/>.</summary>
    private const string SelectedUnread = "(selection not read)";

    private static string DescribeSelected()
    {
        try
        {
            var go = EventSystem.current?.currentSelectedGameObject;
            return go == null ? "none" : (go.name ?? "object");
        }
        catch { return "none"; }
    }

    private static bool FocusFieldAt(float x, float y)
    {
        PruneArmedFields();
        // TEXTEXPAND1: with a dialog open only its fields take a click (the page behind it is covered
        // by the backdrop); a press on a field's own scroll bar or expand button is that control's,
        // and must not refocus the field and pull the view back to the caret.
        var dialogRoot = OpenDialogRoot();
        if (OverFieldChrome(x, y, dialogRoot)) return true;
        // SEARCHCLEAR1: a search box's clear button, before the box it sits in.
        if (TryPressSearchClearAt(x, y, dialogRoot)) return true;
        TMP_InputField? found = null;
        string why = ModsMenuTabDiag.WhyNoHit;
        float best = float.MaxValue;
        void Consider(TMP_InputField? field)
        {
            if (field == null || found != null && best == 0f) return;
            try
            {
                if (dialogRoot != null && !InsideDialog(field.gameObject, dialogRoot)) return;
                // P7b: only a field on screen takes a click: not one under a hidden tab or panel (inactive), nor one
                // scrolled out of its viewport whose rect still lies under the pointer.
                if (!field.gameObject.activeInHierarchy) return;
                var rt = field.GetComponent<RectTransform>();
                if (rt == null) return;
                var rect = ScreenRectOf(rt);
                if (!rect.IsKnown || !rect.Contains(x, y)) return;
                if (!InsideClipViewport(field.transform.parent, x, y)) return;
                float cx = (rect.MinX + rect.MaxX) * 0.5f;
                float cy = (rect.MinY + rect.MaxY) * 0.5f;
                float dx = x - cx;
                float dy = y - cy;
                float d = dx * dx + dy * dy;
                if (d < best)
                {
                    best = d;
                    found = field;
                }
            }
            catch { /* */ }
        }

        // The control the player clicks (and <c>mods_ui rects</c> reports), not
        // the row GameObject: Install search's field rect can sit where the row
        // RT does not, so a row-only test left the pump with no focused field.
        for (int i = 0; i < _armedFields.Count; i++)
            Consider(_armedFields[i].Field);
        if (found != null) why = ModsMenuTabDiag.WhyArmedRect;

        if (found == null)
        {
            for (int i = 0; i < ShownPanels.Count; i++)
            {
                var panel = ShownPanels[i];
                if (panel == null || !panel.Alive) continue;
                panel.ForEachRow((go, kind, _) =>
                {
                    if (kind != "input" || go == null) return;
                    try
                    {
                        Consider(go.GetComponentInChildren<TMP_InputField>(true));
                    }
                    catch { /* */ }
                });
            }
            if (found != null) why = ModsMenuTabDiag.WhyRowInput;
        }

        if (found == null)
        {
            // BUILDSEARCH1: with no panel shown every click on the game's own screen is a miss (the
            // build menu is open and the player is building); not worth a line each.
            if (ShownPanels.Count > 0)
                try { Plugin.Logger?.LogDiag(ModsMenuTabDiag.FormatFocusField(x, y, null, why)); }
            catch { /* */ }
            return false;
        }
        FocusOwnedField(found);
        try
        {
            string id = IsSearchInput(found) ? "search" : FieldCaption(found);
            Plugin.Logger?.LogDiag(ModsMenuTabDiag.FormatFocusField(x, y, id, why));
        }
        catch { /* */ }
        return true;
    }

    /// <summary>
    /// Focus an armed field exactly as a click on it does: select it in the event system, activate
    /// it, and make it the pump's owned focus. DIALOGS1 uses it to give a Prompt's field the keyboard
    /// when the dialog appears. A field the framework has not armed is left alone.
    /// </summary>
    internal static bool FocusOwnedField(TMP_InputField field)
    {
        if (field == null) return false;
        PruneArmedFields();
        if (FindArmed(field) == null) return false;
        try
        {
            var system = EventSystem.current;
            if (system != null) system.SetSelectedGameObject(field.gameObject);
        }
        catch { /* */ }
        try { field.Select(); } catch { /* */ }
        try { field.ActivateInputField(); } catch { /* */ }
        bool wasFocused = ReferenceEquals(_ownedFocusField, field);
        NoteOwnedFocus(field);
        // P7b: a data table's value box selects its whole value when it takes the keyboard, so typing replaces it.
        if (!wasFocused && SelectsAllOnFocus(field)) QueueSelectAll(field);
        return true;
    }

    /// <summary>
    /// Invoke the strip button for the slot under the point. The strip frame
    /// (union of button rows, else the panel) is split with
    /// <see cref="ModsTabStrip.HitIndex"/> — last-matching <c>ScreenRectOf</c>
    /// on overlapping row transforms selected the neighbor to the right
    /// (Mods center → Install). Never Close, never a sidebar row.
    /// </summary>
    private static bool ClickStripButtonAt(float x, float y)
    {
        for (int i = ShownPanels.Count - 1; i >= 0; i--)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive || !panel.Visible || !panel.IsStrip) continue;
            if (!TryStripHitIndex(panel, x, y, out int index)) continue;
            GameObject? hit = null;
            int n = 0;
            panel.ForEachRow((go, kind, _) =>
            {
                if (hit != null || go == null) return;
                if (!string.Equals(kind, "button", StringComparison.Ordinal)) return;
                if (n == index) hit = go;
                n++;
            });
            if (hit == null) continue;
            try { InvokeRowClick(hit); }
            catch { /* */ }
            try { Plugin.Logger?.LogDiag(ModsMenuTabDiag.FormatClickStrip(x, y, index, RowCaption(hit))); }
            catch { /* */ }
            return true;
        }

        try { Plugin.Logger?.LogDiag(ModsMenuTabDiag.FormatClickStrip(x, y, -1, null)); }
        catch { /* */ }
        return false;
    }

    private static bool TryStripHitIndex(UnityPanel panel, float x, float y, out int index)
    {
        index = -1;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        int buttons = 0;
        panel.ForEachRow((go, kind, _) =>
        {
            if (go == null || !string.Equals(kind, "button", StringComparison.Ordinal)) return;
            try
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return;
                var rect = ScreenRectOf(rt);
                if (!rect.IsKnown) return;
                buttons++;
                if (rect.MinX < minX) minX = rect.MinX;
                if (rect.MinY < minY) minY = rect.MinY;
                if (rect.MaxX > maxX) maxX = rect.MaxX;
                if (rect.MaxY > maxY) maxY = rect.MaxY;
            }
            catch { /* */ }
        });
        if (buttons == 0)
        {
            var panelRect = ScreenRectOfPanel(panel);
            if (!panelRect.IsKnown) return false;
            minX = panelRect.MinX;
            minY = panelRect.MinY;
            maxX = panelRect.MaxX;
            maxY = panelRect.MaxY;
        }

        var strip = ModsTabStrip.GeometryFromLeftOrigin(minX, minY, maxX - minX, maxY - minY);
        index = ModsTabStrip.HitIndex(strip, x, y);
        return index >= 0;
    }

    private static void InvokeRowClick(GameObject go)
    {
        try
        {
            var stun = go.GetComponent<SimpleStunButton>();
            if (stun != null)
            {
                stun.onClick?.Invoke();
                return;
            }
        }
        catch { /* */ }
        try
        {
            var button = go.GetComponent<Button>();
            if (button != null) button.onClick?.Invoke();
        }
        catch { /* */ }
    }

    /// <summary>
    /// This build's Keyboard interop exposes <c>add_onTextInput</c> as an
    /// <c>Il2CppSystem.Action&lt;char&gt;</c>, not a C# event a collectible Core can
    /// subscribe. Printable keys are sampled from <c>Keyboard.current</c> through
    /// <see cref="KeyEdgeTracker"/> — <c>wasPressedThisFrame</c> alone drops presses
    /// whose input update step <c>Plugin.Frame</c> never observed (evidence 161).
    /// BlockAllKeys subtracts ProjectM actions, not the Input System device.
    ///
    /// <para>
    /// Every sampled key is observed first so a Ctrl+A that we do not type still
    /// updates held state; otherwise releasing Ctrl while A is held would look like
    /// a fresh <c>a</c>. Mapping is <see cref="UiTypedKeys"/> (named keys, never
    /// <c>Key.Digit0 + i</c>). Printable insertion goes through
    /// <see cref="ApplyTypedChar"/>, the Unity adapter over <see cref="UiTextEdit"/>.
    /// Armed fields are <c>readOnly</c>, so TMP cannot also insert. Held keys
    /// auto-repeat through <see cref="UiKeyRepeat"/>. Escape is sampled here so a
    /// key-up releases the search-clear latch; the hide sweep still runs first.
    /// </para>
    /// </summary>
    private static void PumpTypedKeys(int frame, out int keys, out char typed)
    {
        keys = 0;
        typed = '\0';
        var field = FocusedArmedField();
        ApplyPendingSelectAll(field); // P7b: after this frame's click and caret placement, before any key
        var keyboard = Keyboard.current;
        // BRIDGEUI1: the synthetic keys need no keyboard device; the key helpers below read them with a null keyboard.
        if (keyboard == null && !UiSyntheticInput.Active)
        {
            // TEXTTEST1: bridge-injected input does not need a keyboard device.
            if (field != null) PumpInjected(field, ref keys, ref typed);
            return;
        }

        if (keyboard != null)
        {
            try { TypeEdges.NoteDevice(keyboard.deviceId); } catch { /* */ }
        }

        SampleTypeEdges(keyboard, frame);
        // P7b: a widget's value box released the keyboard mid-typing; its burst ends when every typing key is up.
        if (_keyBurstHold && !AnyTypingKeyHeld(keyboard)) _keyBurstHold = false;
        long nowMs = Environment.TickCount64;
        bool escapeHeld = KeyHeld(keyboard, Key.Escape);
        bool escapeEdge = TypeKeyDown(keyboard, UiKey.Escape, frame);
        // KEYS3 polish (live P2): while a key capture or a framework dialog owns Escape, the 498 guard
        // also takes Back/Menu_Close so the game's FullscreenMenu does not close under the workspace.
        try
        {
            // NAV1: also while B on a workspace page means back to the list (UiNav.HoldsGameClose).
            MenuHotkeyGuard.SetCloseHeld(MenuHotkeyGuard.NextCloseHold(
                Keybinds.EscapeBelongsToCapture, UiDialog.Current is { IsOpen: true } || UiChoiceHandle.AnyOpen || UiNav.HoldsGameClose,
                escapeHeld, MenuHotkeyGuard.CloseHeld));
        }
        catch { MenuHotkeyGuard.SetCloseHeld(false); }
        if (escapeEdge)
        {
            ModsMenu.TryClearSearchOnEscape();
            // BUILDSEARCH1: an external field (the build-menu search) gives up focus on Escape.
            NotifyExternalEscape(field);
            field = FocusedArmedField();
        }
        else if (!escapeHeld) ModsMenu.NoteEscapeReleased();

        // DIALOGS1: Enter (or keypad Enter) answers the open dialog with its main action — except
        // while a multi-line field has focus, where Enter is a new line. Bootstrap only forwards
        // Escape, so the edge is read here, from the same held-state tracker the typing uses.
        bool enterEdge = TypeKeyDown(keyboard, UiKey.Enter, frame) || TypeKeyDown(keyboard, UiKey.NumpadEnter, frame);
        // TEXTEXPAND1: in a read-only editor Enter has no line to start, so it is the dialog's (Close).
        // BUILDSEARCH1: Enter in an external single-line field commits and gives up focus.
        if (enterEdge && field != null && EnterLeavesField(field))
        {
            // P7b: Enter in a data table's value box commits it: the box gives up the keyboard now.
            ReleaseWidgetField(field);
            field = FocusedArmedField();
        }
        else if (enterEdge && field != null && ExternalGuardId(field) != null)
        {
            NotifyExternalSubmit(field);
            field = FocusedArmedField();
        }
        else if (enterEdge && !(field != null && IsMultiline(field) && FindArmed(field) is not { ReadOnly: true }))
        {
            try { UiDialog.NotifyEnter(TypingOutsideDialog(field)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog enter threw: " + e.Message); }
            field = FocusedArmedField();
        }

        // FORMDIALOG1: in an open dialog, Tab and Shift+Tab walk its one-line field, its check boxes and its buttons, and
        // Down leaves the one-line field for the first of them (Up from the first comes back: UiNav). Not while a field
        // outside the dialog has the keyboard (the game's chat), nor in a multi-line field, where the keys are the text's,
        // nor while an input method composes text in the field (its candidate list takes Tab and Down). The edges were
        // sampled for this frame already (SampleTypeEdges); these reads return them.
        if (UiDialog.Current is { IsOpen: true } && !TypingOutsideDialog(field) && !(field != null && IsMultiline(field)))
        {
            bool tabEdge = TypeKeyDown(keyboard, UiKey.Tab, frame);
            bool downEdge = field != null && TypeKeyDown(keyboard, UiKey.DownArrow, frame);
            if ((tabEdge || downEdge) && !(field != null && ImeComposing()))
            {
                bool back = tabEdge && (KeyHeld(keyboard, Key.LeftShift) || KeyHeld(keyboard, Key.RightShift));
                try { UiNav.TabStep(back ? -1 : 1, fromField: field != null); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog tab threw: " + e.Message); }
                field = FocusedArmedField();
            }
        }

        if (!ReferenceEquals(field, _repeatField))
        {
            TypeRepeats.Reset();
            // TEXTUNDO1: the field the pump typed into last lost focus (also through the isFocused or
            // event-system fallback, which NoteOwnedFocus does not see): its history ends.
            ClearUndo(_repeatField, "blur");
            _repeatField = field;
        }

        // TEXTINPUT1: drain every frame, focused field or not, so text typed elsewhere is
        // discarded instead of arriving when a field next takes focus.
        var drain = BootstrapLink.TextInputDrain;
        int queued = DrainTypedText(drain, frame);

        if (field == null) return;

        bool suppressPhysicalInserts = TextMerge.SuppressPhysicalInserts(drain != null, queued);
        for (int q = 0; q < queued; q++)
        {
            char c = TypedTextBuffer[q];
            ApplyTypedChar(field, c);
            keys++;
            typed = c;
        }

        // TEXTTEST1: bridge-injected OS text, after the real queue and through the same path. It
        // was not counted above, so it can never prove the OS queue.
        PumpInjected(field, ref keys, ref typed);

        bool ctrl = KeyHeld(keyboard, Key.LeftCtrl) || KeyHeld(keyboard, Key.RightCtrl);
        bool alt = KeyHeld(keyboard, Key.LeftAlt) || KeyHeld(keyboard, Key.RightAlt);
        bool meta = KeyHeld(keyboard, Key.LeftMeta) || KeyHeld(keyboard, Key.RightMeta);
        bool shift = KeyHeld(keyboard, Key.LeftShift) || KeyHeld(keyboard, Key.RightShift);

        for (int i = 0; i < TypedSampleKeys.Length; i++)
        {
            var ui = TypedSampleKeys[i];
            if (ui == UiKey.Escape) continue;

            bool edge = TypeKeyDown(keyboard, ui, frame);
            bool held = KeyHeld(keyboard, ui);
            var command = UiTypedKeys.Decide(ui, ctrl, alt, meta, shift);
            bool fire = edge;
            // TEXTUNDO1: a held Ctrl+Z / Ctrl+Y repeats like a held Backspace. The repeat belongs to the
            // command the key was pressed as: a modifier released while the key is held (Ctrl+Z becoming
            // z, Ctrl+Shift+Z becoming Ctrl+Z) stops the repeat until the next press.
            bool undoKey = command.Kind == UiTypedCommandKind.Shortcut
                && UiTextUndoHistory.ShortcutFor(command.Char, shift) != UiUndoShortcut.None;
            int decided = ((int)command.Kind << 16) | command.Char
                | (undoKey && UiTextUndoHistory.ShortcutFor(command.Char, shift) == UiUndoShortcut.Redo ? 1 << 30 : 0);
            if (!edge && RepeatCommands.TryGetValue(ui, out int was) && was != decided) TypeRepeats.Reset(ui);
            RepeatCommands[ui] = decided;
            if (UiKeyRepeat.IsRepeatable(command.Kind) || undoKey)
            {
                if (TypeRepeats.Observe(ui, held, edge, nowMs))
                    fire = true;
            }
            else
            {
                TypeRepeats.Reset(ui);
            }

            if (!fire) continue;
            FireKey(field, ui, command, ctrl, alt, shift, suppressPhysicalInserts, ref keys, ref typed);
        }
    }

    /// <summary>
    /// One decided key press on <paramref name="field"/>: Enter's line break in a text area, an
    /// insert (unless the OS queue types this frame's text), a Ctrl shortcut, or an editing command.
    /// The physical pump and the bridge's injected keys share it.
    /// </summary>
    private static void FireKey(
        TMP_InputField field, UiKey ui, UiTypedCommand command, bool ctrl, bool alt, bool shift,
        bool suppressInserts, ref int keys, ref char typed)
    {
        if (UiTextEdit.EnterStartsLine(ui, ctrl, alt, IsMultiline(field)))
        {
            ApplyFieldCommand(field, s => UiTextEdit.Newline(s, FieldMaxLength(field)), UiTextEditKind.Typing);
            keys++;
            return;
        }

        if (command.Kind == UiTypedCommandKind.None) return;
        if (command.Kind == UiTypedCommandKind.Modifier)
        {
            keys++;
            return;
        }

        if (command.Kind == UiTypedCommandKind.Insert)
        {
            // The OS queue typed this frame's text (or has proven it does): the physical
            // US map must not type the same keystroke a second time.
            if (suppressInserts) return;
            ApplyTypedChar(field, command.Char);
            keys++;
            typed = command.Char;
            return;
        }

        if (command.Kind == UiTypedCommandKind.Shortcut)
        {
            // TEXTUNDO1: Ctrl+Z undoes; Ctrl+Y and Ctrl+Shift+Z redo. Ctrl combinations come from the
            // physical keys only: the OS text queue's matching control characters (0x1A, 0x19) are
            // dropped at the drain (UiTextInputMerge.IsTypedText), so nothing is typed twice.
            var undo = UiTextUndoHistory.ShortcutFor(command.Char, shift);
            if (undo != UiUndoShortcut.None) ApplyUndo(field, undo == UiUndoShortcut.Redo);
            else ApplyShortcut(field, command.Char);
            keys++;
            return;
        }

        var decided = command;
        var kind = UiTextUndoHistory.KindFor(decided, ReadFieldState(field));
        ApplyFieldCommand(field, s => UiTypedKeys.Execute(s, decided, shift, clipboard: null, FieldMaxLength(field)), kind);
        keys++;
    }

    /// <summary>TEXTUNDO1: undo / redo stats for the bridge readback (process totals).</summary>
    internal static long UndoApplied;
    internal static long RedoApplied;
    internal static long UndoIgnoredReadOnly;

    /// <summary>
    /// TEXTUNDO1: one Ctrl+Z (or, with <paramref name="redo"/>, Ctrl+Y / Ctrl+Shift+Z) on
    /// <paramref name="field"/>. A read-only field ignores it; a restored text longer than the field's
    /// current limit is refused and the step kept.
    /// </summary>
    private static void ApplyUndo(TMP_InputField field, bool redo)
    {
        try
        {
            var armed = FindArmed(field);
            if (armed == null) return;
            if (armed.ReadOnly)
            {
                UndoIgnoredReadOnly++;
                return;
            }

            var before = ReadFieldState(field);
            int max = FieldMaxLength(field);
            Func<string, bool> accepts = text => UiTextUndoHistory.FieldAccepts(text, max);
            UiTextEditState next;
            bool done = redo
                ? armed.Undo.TryRedo(before, accepts, out next)
                : armed.Undo.TryUndo(before, accepts, out next);
            if (!done) return;
            if (redo) RedoApplied++;
            else UndoApplied++;
            WriteFieldState(field, before, next);
        }
        catch { /* an undo that cannot run changes nothing */ }
    }

    /// <summary>
    /// TEXTTEST1: up to <see cref="UiTextInjection.PerFrame"/> bridge-injected items this frame:
    /// text through <see cref="ApplyTypedChar"/> (control characters dropped, as the OS queue's are),
    /// keys through <see cref="UiTypedKeys.Decide"/> and <see cref="FireKey"/> (the US map, inserts
    /// always allowed). An Enter injected as a key into a single-line field submits it, as TMP does.
    /// </summary>
    private static void PumpInjected(TMP_InputField field, ref int keys, ref char typed)
    {
        if (UiTextInjection.Pending == 0) return;
        int budget = Math.Max(1, UiTextInjection.PerFrame);
        while (budget-- > 0 && UiTextInjection.Pending > 0)
        {
            if (UiTextInjection.Text.Count > 0)
            {
                char c = UiTextInjection.Text.Dequeue();
                if (!UiTextInputMerge.IsTypedText(c))
                {
                    InjectedControlDropped++;
                    continue;
                }
                ApplyTypedChar(field, c);
                keys++;
                typed = c;
                continue;
            }

            var chord = UiTextInjection.Keys.Dequeue();
            // BUILDSEARCH1: an external field takes Escape and Enter through its owner's callbacks, as
            // the physical keys do (PumpTypedKeys).
            if (ExternalGuardId(field) != null && !chord.Ctrl && !chord.Alt
                && (chord.Key == UiKey.Escape || chord.Key == UiKey.Enter || chord.Key == UiKey.NumpadEnter))
            {
                if (chord.Key == UiKey.Escape) NotifyExternalEscape(field);
                else NotifyExternalSubmit(field);
                keys++;
                if (FocusedArmedField() == null)
                {
                    // The box gave up the keyboard: what is still queued must not land in the next field.
                    UiTextInjection.Text.Clear();
                    UiTextInjection.Keys.Clear();
                    return;
                }
                continue;
            }
            if ((chord.Key == UiKey.Enter || chord.Key == UiKey.NumpadEnter) && !IsMultiline(field) && !chord.Ctrl && !chord.Alt)
            {
                SubmitField(field);
                keys++;
                continue;
            }
            var command = UiTypedKeys.Decide(chord.Key, chord.Ctrl, chord.Alt, chord.Meta, chord.Shift);
            FireKey(field, chord.Key, command, chord.Ctrl, chord.Alt, chord.Shift, suppressInserts: false, ref keys, ref typed);
        }
    }

    /// <summary>TEXTTEST1: injected control characters the text path dropped (Enter, Tab, …).</summary>
    internal static long InjectedControlDropped;

    private static readonly UiTextInputMerge TextMerge = new();
    private static readonly char[] TypedTextBuffer = new char[256];
    private static readonly char[] TypedTextRaw = new char[256];
    private static int _lastTextDrainFrame = int.MinValue;

    /// <summary>
    /// TEXTINPUT1. Drain Bootstrap's typed-character queue into <see cref="TypedTextBuffer"/>,
    /// keeping only text a field may insert. Returns the count; 0 with no Bootstrap queue.
    /// </summary>
    private static int DrainTypedText(Func<char[], int, int>? drain, int frame)
    {
        if (drain == null) return 0;
        int minFrame = UiTextInputMerge.MinFrame(frame, _lastTextDrainFrame);
        _lastTextDrainFrame = frame;
        int raw;
        try { raw = drain(TypedTextRaw, minFrame); }
        catch { return 0; }
        int n = 0;
        for (int i = 0; i < raw && i < TypedTextRaw.Length; i++)
        {
            char c = TypedTextRaw[i];
            if (UiTextInputMerge.IsTypedText(c)) TypedTextBuffer[n++] = c;
        }
        return n;
    }

    /// <summary>TEXTINPUT1 readback for <c>mods_ui status</c> (<c>focus.textInput</c>).</summary>
    internal static Dictionary<string, object?> DescribeTextInput()
    {
        var (state, received) = BootstrapLink.TextInputStatus;
        int capability = BootstrapLink.TextInputCapability;
        return new Dictionary<string, object?>
        {
            ["capability"] = capability,
            ["state"] = state,
            ["received"] = received,
            ["proven"] = TextMerge.Proven,
            ["source"] = capability >= 1 && TextMerge.Proven ? "os-text" : "physical-us-map",
        };
    }

    /// <summary>
    /// DIALOGS1: true when Enter belongs to a text field that is not the open dialog's: an armed
    /// field on another panel, any other focused field the event system has selected, or the game's
    /// chat box. Then Enter answers no dialog.
    /// </summary>
    private static bool TypingOutsideDialog(TMP_InputField? armed)
    {
        var dialogRoot = OpenDialogRoot();
        if (armed != null && !InsideDialog(armed.gameObject, dialogRoot)) return true;
        try
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected != null && !InsideDialog(selected, dialogRoot))
            {
                var tmp = selected.GetComponent<TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                var legacy = selected.GetComponent<UnityEngine.UI.InputField>();
                if (legacy != null && legacy.isFocused) return true;
            }
        }
        catch { /* an unreadable selection is not typing */ }
        return Keybinds.ChatIsFocused;
    }

    private static Transform? OpenDialogRoot()
    {
        try
        {
            return UiDialog.Current?.Panel is UiPanelHandle { RuntimePanel: UnityPanel host } && host.GameObject != null
                ? host.GameObject.transform
                : null;
        }
        catch { return null; }
    }

    private static bool InsideDialog(GameObject go, Transform? dialogRoot)
    {
        if (go == null || dialogRoot == null) return false;
        try { return go.transform.IsChildOf(dialogRoot); }
        catch { return false; }
    }

    private static bool IsMultiline(TMP_InputField field)
    {
        try { return field != null && field.lineType == TMP_InputField.LineType.MultiLineNewline; }
        catch { return false; }
    }

    private static bool KeyHeld(Keyboard? keyboard, UiKey ui)
    {
        if (!Enum.TryParse(ui.ToString(), out Key key)) return false;
        return KeyHeld(keyboard, key);
    }

    private static void SampleTypeEdges(Keyboard? keyboard, int frame)
    {
        for (int i = 0; i < TypedSampleKeys.Length; i++)
            TypeKeyDown(keyboard, TypedSampleKeys[i], frame);
        // FORMDIALOG1: the dialog walk's keys are sampled every frame too (real and synthetic), never dispatched here, so
        // a press is an edge whether or not a dialog was open on the frames before it.
        for (int i = 0; i < DialogWalkKeys.Length; i++)
            TypeKeyDown(keyboard, DialogWalkKeys[i], frame);
    }

    /// <summary>FORMDIALOG1: Tab and Down, read by the dialog walk in <see cref="PumpTypedKeys"/>.</summary>
    private static readonly UiKey[] DialogWalkKeys = { UiKey.Tab, UiKey.DownArrow };

    /// <summary>
    /// FORMDIALOG1: an input method is composing text (a candidate string is open), so Tab and Down belong to it, not to
    /// the dialog walk. Read from the text composition string, not a key; a runtime where it cannot be read reads as none.
    /// </summary>
    private static bool ImeComposing()
    {
        if (_imeUnreadable) return false;
        try { return !string.IsNullOrEmpty(UnityEngine.Input.compositionString); }
        catch
        {
            _imeUnreadable = true;
            return false;
        }
    }

    private static bool _imeUnreadable;

    private static bool TypeKeyDown(Keyboard? keyboard, UiKey ui, int frame)
    {
        if (!Enum.TryParse(ui.ToString(), out Key key)) return false;
        return TypeKeyDown(keyboard, key, ui, frame);
    }

    private static bool TypeKeyDown(Keyboard? keyboard, Key key, UiKey ui, int frame)
    {
        try
        {
            // BRIDGEUI1: with no keyboard device only the synthetic keys are read.
            var control = keyboard?[key];
            bool held = (control != null && control.isPressed) || SyntheticKeyHeld(ui, frame);
            bool raw = control != null && control.wasPressedThisFrame;
            if (control == null && keyboard != null) return false;
            return TypeEdges.Observe(ui, frame, held, raw);
        }
        catch
        {
            return false;
        }
    }

    private static bool KeyHeld(Keyboard? keyboard, Key key)
    {
        try
        {
            var control = keyboard?[key];
            if (control != null && control.isPressed) return true;
            // BRIDGEUI1: a key the synthetic input holds (an Escape or Enter tap, a held Shift or Ctrl).
            return UiSyntheticInput.Active && Enum.TryParse(key.ToString(), out UiKey ui) && SyntheticKeyHeld(ui, Time.frameCount);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unity adapter over <see cref="UiTextEdit"/>. Backspace and printable insertion
    /// honor caret and selection; the owned notify path raises
    /// <c>onValueChanged</c> at most once.
    /// </summary>
    private static void ApplyTypedChar(TMP_InputField field, char c)
    {
        try
        {
            // TEXTEXPAND1: a read-only editor takes no character.
            if (FindArmed(field) is { ReadOnly: true })
            {
                TypedStats.RejectedReadOnly++;
                return;
            }

            // TEXTFILTER1: the field's character filter first, on every path that types.
            var filter = FieldFilter(field);
            if (filter != UiCharFilter.None)
            {
                var now = ReadFieldState(field);
                if (!UiCharFilters.Accepts(filter, now.Text, now.SelStart, now.SelEnd, c))
                {
                    TypedStats.RejectedFilter++;
                    return;
                }
            }

            int before = SafeLength(field);
            bool hadSelection = ReadFieldState(field).HasSelection;
            ApplyFieldCommand(field, s => UiTextEdit.TypeChar(s, c, FieldMaxLength(field)), UiTextEditKind.Typing);
            if (SafeLength(field) > before || hadSelection) TypedStats.Accepted++;
            else TypedStats.RejectedLimit++;
        }
        catch { /* a dropped character is better than a throw on the input path */ }
    }

    private static void ApplyShortcut(TMP_InputField field, char letter)
    {
        try
        {
            // Only Ctrl+V needs the clipboard; reading it for Ctrl+A/C/X marshals the whole buffer
            // (a 1 MB clipboard on every Select All).
            bool paste = char.ToLowerInvariant(letter) == 'v';
            // TEXTEXPAND1: nothing is pasted into (or cut from) a read-only editor; Ctrl+A and Ctrl+C work.
            if (FindArmed(field) is { ReadOnly: true } && (paste || char.ToLowerInvariant(letter) == 'x'))
            {
                if (paste) TypedStats.RejectedReadOnly += Math.Max(1, ReadClipboard().Length);
                return;
            }

            string clip = paste ? ReadClipboard() : "";
            var filter = FieldFilter(field);
            ApplyFieldCommand(field, s =>
            {
                string offered = clip;
                if (paste)
                {
                    offered = UiTextEdit.CleanPaste(clip, IsMultiline(field));
                    if (filter != UiCharFilter.None)
                    {
                        offered = UiCharFilters.Filter(filter, s.Text, s.SelStart, s.SelEnd, offered, out int dropped);
                        TypedStats.RejectedFilter += dropped;
                    }
                }

                var result = UiTextEdit.ApplyShortcut(s, letter, offered, FieldMaxLength(field), IsMultiline(field));
                if (paste)
                {
                    int inserted = result.State.Text.Length - (s.Text.Length - (s.SelEnd - s.SelStart));
                    if (inserted < 0) inserted = 0;
                    TypedStats.Accepted += inserted;
                    TypedStats.RejectedLimit += Math.Max(0, offered.Length - inserted);
                }
                if (result.ClipboardSet != null)
                    WriteClipboard(result.ClipboardSet);
                return result;
            }, paste ? UiTextEditKind.Paste : char.ToLowerInvariant(letter) == 'x' ? UiTextEditKind.Cut : UiTextEditKind.Other);
        }
        catch { /* */ }
    }

    /// <inheritdoc cref="IUiRuntime.InsertText"/>
    public bool InsertText(IRuntimeWidget widget, string text)
    {
        if (widget is not UnityWidget target || !target.Alive || target.GameObject == null || string.IsNullOrEmpty(text)) return false;
        TMP_InputField? field;
        try { field = FindIn<TMP_InputField>(target.GameObject); }
        catch { field = null; }
        if (field == null) return false;
        try
        {
            var armed = FindArmed(field);
            if (armed is { ReadOnly: true }) return false;
            string token = text;
            ApplyFieldCommand(field, s =>
            {
                var next = UiFieldInsert.Token(s, token, FieldMaxLength(field));
                return new UiTextEditResult(next, !string.Equals(next.Text, s.Text, StringComparison.Ordinal), consumed: true);
            }, UiTextEditKind.Paste);
            // The keyboard stays (or goes back) in the field, with the caret after the token.
            FocusOwnedField(field);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: insert into a field threw: " + e.Message);
            return false;
        }
    }

    private static int SafeLength(TMP_InputField field)
    {
        try { return (field.text ?? "").Length; }
        catch { return 0; }
    }

    /// <summary>TEXTFILTER1: the armed field's character filter (<see cref="UiCharFilter.None"/> when not set).</summary>
    private static UiCharFilter FieldFilter(TMP_InputField field)
    {
        var armed = FindArmed(field);
        return armed == null ? UiCharFilter.None : armed.Filter;
    }

    /// <summary>TEXTFILTER1: give an armed field its character filter (after <c>ArmTextField</c>).</summary>
    private static void SetFieldFilter(TMP_InputField? field, UiCharFilter filter)
    {
        if (field == null) return;
        var armed = FindArmed(field);
        if (armed != null) armed.Filter = filter;
    }

    /// <summary>TEXTTEST1: what the typing paths did with each character (the bridge's type/paste readback).</summary>
    internal sealed class TypedCounters
    {
        public long Accepted;
        public long RejectedFilter;
        public long RejectedLimit;

        // TEXTEXPAND1: characters (and edits) a read-only editor refused.
        public long RejectedReadOnly;
    }

    internal static readonly TypedCounters TypedStats = new();

    private static void ApplyFieldCommand(
        TMP_InputField field, Func<UiTextEditState, UiTextEditResult> command, UiTextEditKind kind = UiTextEditKind.Other)
    {
        if (field == null || command == null) return;
        var before = ReadFieldState(field);
        UiTextEditResult result;
        try { result = command(before); }
        catch { return; }
        if (!result.Consumed) return;
        var armed = FindArmed(field);
        // TEXTEXPAND1: in a read-only editor only the caret and the selection move (Backspace,
        // Delete, Enter and the like change nothing).
        if (armed is { ReadOnly: true } && !string.Equals(result.State.Text, before.Text, StringComparison.Ordinal))
        {
            TypedStats.RejectedReadOnly++;
            return;
        }
        // TEXTUNDO1: every owned edit is a step (or extends the open one); a caret move or a selection
        // ends the open step. An outside change since the last step clears the history first.
        try { armed?.Undo.Record(before, result.State, kind, Environment.TickCount64); }
        catch { /* the history never blocks an edit */ }
        WriteFieldState(field, before, result.State);
    }

    private static UiTextEditState ReadFieldState(TMP_InputField field)
    {
        string text = "";
        try { text = field.text ?? ""; }
        catch { text = ""; }
        var armed = FindArmed(field);
        UiTextEditState? owned = armed is { HasOwned: true } ? armed.Owned : null;
        // Owned caret, not TMP. field.text resets stringPosition to 0; caretPosition
        // reads back from that, which is the live "doolb" / dead-Backspace path.
        return UiFieldCaret.Read(text, owned);
    }

    private static void WriteFieldState(TMP_InputField field, UiTextEditState previous, UiTextEditState next)
    {
        next = UiTextEdit.Normalize(next);
        int raises = 0;
        UiTextChange.Apply(
            previous.Text,
            next.Text,
            setText: value =>
            {
                try
                {
                    string now = field.text ?? "";
                    field.text = value;
                    string after = field.text ?? "";
                    // TMP's setter raises onValueChanged when the string changes. Count that as
                    // the one notification; a second Invoke would be a duplicate, not a throw.
                    if (now != after) raises++;
                }
                catch { /* */ }
            },
            invokeChanged: value =>
            {
                try { field.onValueChanged?.Invoke(value); }
                catch { /* */ }
                raises++;
            },
            raiseCount: () => raises);

        var armed = FindArmed(field);
        if (armed != null)
        {
            armed.Owned = next;
            armed.HasOwned = true;
        }

        if (IsSearchInput(field))
        {
            _searchOwnedCaret = next.Caret;
            _searchOwnedAnchor = next.Anchor;
            _searchHasOwned = true;
        }

        PushTmpCaret(field, next);
        SyncSearchHint(field);
        if (armed != null) NoteOwnedCaretEdit(armed);
        try { SyncOwnedCarets(); } catch { /* */ }
    }

    /// <summary>
    /// Visual push only. <see cref="UiFieldCaret"/> is what the next key reads.
    /// <c>stringPosition</c> is the string index; <c>caretPosition</c> is derived
    /// from it on this build and is not enough on its own after a text assign.
    /// </summary>
    internal static void PushTmpCaret(TMP_InputField field, UiTextEditState state)
    {
        if (field == null) return;
        state = UiTextEdit.Normalize(state);
        try { field.stringPosition = state.Caret; } catch { /* */ }
        try { field.selectionStringAnchorPosition = state.Anchor; } catch { /* */ }
        try { field.selectionStringFocusPosition = state.Caret; } catch { /* */ }
        try { field.caretPosition = state.Caret; } catch { /* */ }
        try { field.selectionAnchorPosition = state.Anchor; } catch { /* */ }
        try { field.selectionFocusPosition = state.Caret; } catch { /* */ }
    }

    /// <summary>
    /// After a rebuild the new field has no session. Prefer the last owned search
    /// caret so a mid-string insert is not jumped to the end; otherwise <paramref name="caret"/>.
    /// </summary>
    internal static void SeedOwnedCaret(TMP_InputField field, int caret)
    {
        if (field == null) return;
        string text = "";
        try { text = field.text ?? ""; } catch { text = ""; }
        int useCaret = caret;
        int useAnchor = caret;
        if (_searchHasOwned)
        {
            useCaret = _searchOwnedCaret;
            useAnchor = _searchOwnedAnchor;
        }

        var state = UiTextEdit.Normalize(new UiTextEditState(text, useCaret, useAnchor));
        var armed = FindArmed(field);
        if (armed != null)
        {
            armed.Owned = state;
            armed.HasOwned = true;
        }

        PushTmpCaret(field, state);
        if (armed != null) NoteOwnedCaretEdit(armed);
        try { SyncOwnedCarets(); } catch { /* */ }
    }

    private static int FieldMaxLength(TMP_InputField field)
    {
        try
        {
            int n = field.characterLimit;
            return n > 0 ? n : int.MaxValue;
        }
        catch
        {
            return int.MaxValue;
        }
    }

    private static string ReadClipboard()
    {
        // TEXTTEST1: the bridge's test clipboard, so a test never reads the player's.
        if (UiTextInjection.Clipboard != null) return UiTextInjection.Clipboard;
        try { return GUIUtility.systemCopyBuffer ?? ""; }
        catch { return ""; }
    }

    private static void WriteClipboard(string value)
    {
        if (UiTextInjection.Clipboard != null)
        {
            UiTextInjection.Clipboard = value ?? "";
            return;
        }
        try { GUIUtility.systemCopyBuffer = value ?? ""; }
        catch { /* */ }
    }

    /// <summary>SETTINGS1 stage 3: the Mods page's problem report (the same clipboard the text fields paste from).</summary>
    public bool TryCopyToClipboard(string text)
    {
        try
        {
            GUIUtility.systemCopyBuffer = text ?? "";
            return string.Equals(GUIUtility.systemCopyBuffer, text ?? "", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public bool CanCopyToClipboard => true;

    private static string FieldCaption(TMP_InputField field)
    {
        try
        {
            var row = field.transform.parent;
            if (row == null) return "field";
            var labels = row.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (labels == null) return "field";
            for (int i = 0; i < labels.Length; i++)
            {
                var label = labels[i];
                if (label == null || string.IsNullOrEmpty(label.text)) continue;
                try { if (field.textComponent != null && label == field.textComponent) continue; }
                catch { /* */ }
                return label.text;
            }
        }
        catch { /* */ }
        return "field";
    }

    private static TMP_InputField? FocusedArmedField()
    {
        PruneArmedFields();
        // Owned click-to-focus is the pump's writer. readOnly fields may never
        // set TMP isFocused, which is how Install search saw keys with no field.
        if (_ownedFocusField != null && FindArmed(_ownedFocusField) != null)
            return _ownedFocusField;
        // BUILDSEARCH1: an external field (one hosted in a game screen) has focus only when Progenitor
        // gave it focus. The game's own menu navigation selects it in the event system on its own
        // (live 2026-10-04, the build menu), and that is not the player choosing to type.
        for (int i = 0; i < _armedFields.Count; i++)
        {
            var field = _armedFields[i].Field;
            if (field == null || IsExternal(field)) continue;
            try { if (field.isFocused) return field; } catch { /* */ }
        }

        try
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null) return null;
            var tmp = selected.GetComponent<TMP_InputField>();
            if (tmp != null && !IsExternal(tmp) && FindArmed(tmp) != null) return tmp;
        }
        catch { /* */ }

        return null;
    }

    private static ArmedField? FindArmed(TMP_InputField field)
    {
        if (field == null) return null;
        for (int i = 0; i < _armedFields.Count; i++)
        {
            if (ReferenceEquals(_armedFields[i].Field, field))
                return _armedFields[i];
        }

        return null;
    }

    private static bool IsSearchInput(TMP_InputField field)
    {
        if (field == null) return false;
        try
        {
            var row = field.transform.parent;
            if (row != null && string.Equals(row.name, MenuRowStyle.SearchRowName, StringComparison.Ordinal))
                return true;
        }
        catch { /* */ }
        return false;
    }

    /// <summary>
    /// Job KEY2. Which of the two search fields <paramref name="field"/> is, by the parent the
    /// field builder was handed: the sidebar search row (<c>MenuRowStyle.SearchRowName</c>, the
    /// same test <see cref="IsSearchInput"/> makes) or the Install toolbar's
    /// <c>CellKind.Input</c> host. Both are armed through the one
    /// <see cref="ArmTextField"/> call site, so the pump already treats them identically; this
    /// only names them for the readback.
    ///
    /// <para>
    /// An armed field that is neither - a settings text row, say - is reported as the Install id
    /// rather than as "none": the question this answers is "is a Progenitor field taking text",
    /// and answering "none" for a field that is would put the menu hotkey back under the player's
    /// typing. The <c>none</c> answer belongs to <c>field == null</c> alone.
    /// </para>
    /// </summary>
    private static string FocusedFieldId(TMP_InputField? field)
    {
        if (field == null) return MenuHotkeyGuard.NoField;
        var external = ExternalGuardId(field);
        if (external != null) return external;
        return IsSearchInput(field) ? MenuHotkeyGuard.ModsSearch : MenuHotkeyGuard.InstallSearch;
    }

    /// <summary>
    /// Push the pump's focus answer at the menu-hotkey block. The only writer that runs every
    /// frame; the ones on <see cref="NoteOwnedFocus"/> and the teardown paths exist so a release
    /// is immediate rather than one frame late.
    /// </summary>
    private static void SyncMenuHotkeyGuard(TMP_InputField? field)
    {
        try { MenuHotkeyGuard.SetFocusedField(FocusedFieldId(field)); }
        catch { /* */ }
        // P7b: the guard stays up while the keys of a burst typed into a released widget box are still held.
        if (field == null && _keyBurstHold)
        {
            try { MenuHotkeyGuard.SetFocusedField(MenuHotkeyGuard.InstallSearch); }
            catch { /* */ }
        }
    }

    /// <summary>P7b: whether any typing key (the pump's sampled set, Enter included) is held now.</summary>
    private static bool AnyTypingKeyHeld(Keyboard? keyboard)
    {
        for (int i = 0; i < TypedSampleKeys.Length; i++)
            if (KeyHeld(keyboard, TypedSampleKeys[i])) return true;
        return false;
    }

    /// <summary>
    /// P7b: (<paramref name="x"/>, <paramref name="y"/>) is inside the nearest clipping viewport above
    /// <paramref name="from"/> (a <c>RectMask2D</c>), or there is none.
    /// </summary>
    private static bool InsideClipViewport(Transform? from, float x, float y)
    {
        try
        {
            for (var t = from; t != null; t = t.parent)
            {
                var mask = t.GetComponent<RectMask2D>();
                if (mask == null) continue;
                var rt = t.GetComponent<RectTransform>();
                if (rt == null) return true;
                var clip = ScreenRectOf(rt);
                return !clip.IsKnown || clip.Contains(x, y);
            }
        }
        catch { /* */ }
        return true;
    }

    /// <summary>
    /// The id of the field that currently has owned focus, for <c>mods_ui status.focus</c>.
    /// Recomputed rather than read back off the guard so the readback reports the runtime's own
    /// state and can disagree with the guard if a writer is ever missed.
    /// </summary>
    internal static string DescribeFocusedFieldId()
    {
        try { return FocusedFieldId(FocusedArmedField()); }
        catch { return MenuHotkeyGuard.NoField; }
    }

    public bool TryReadSearchField(out string text, out bool focused)
    {
        text = "";
        focused = false;
        PruneArmedFields();
        TMP_InputField? search = null;
        // SEARCHCLEAR1: the focused workspace search box, the Mods search or the Install search, is the one
        // Escape clears and then leaves; unfocused, the Mods search row answers as before.
        if (_ownedFocusField != null && IsWorkspaceSearch(_ownedFocusField) && FindArmed(_ownedFocusField) != null)
            search = _ownedFocusField;
        for (int i = 0; i < _armedFields.Count && search == null; i++)
        {
            var field = _armedFields[i].Field;
            if (field == null) continue;
            if (!IsSearchInput(field)) continue;
            search = field;
        }

        if (search == null) return false;
        try { text = search.text ?? ""; } catch { text = ""; }
        focused = ReferenceEquals(_ownedFocusField, search);
        if (!focused)
        {
            var armedFocus = FocusedArmedField();
            focused = armedFocus != null && ReferenceEquals(armedFocus, search);
        }

        if (!focused)
        {
            try { focused = search.isFocused; } catch { /* */ }
        }

        return true;
    }

    /// <summary>
    /// A click that did not land on a field (or the tab strip) leaves the field. Clearing the owned focus alone was the
    /// bug (user report 2026-10-10: click into Search settings, click elsewhere, and Mouse Back was dead): the click had
    /// selected the field in the event system and activated it (<see cref="FocusOwnedField"/>), and on this build a click
    /// on a non-selectable spot does not deselect it, so the field stayed <c>isFocused</c>. <see cref="FocusedArmedField"/>
    /// then found it again through that selection and kept the menu-hotkey guard, and the key source's
    /// <c>TextFieldFocused</c> probe (the event system's selection) held every bind back. Now the field is deactivated
    /// and, when it is the selection, deselected, the way an external field's release does.
    /// </summary>
    private static void BlurArmedFieldOnClickElsewhere()
    {
        TMP_InputField? field = _ownedFocusField;
        if (field == null)
        {
            try { field = FocusedArmedField(); }
            catch { field = null; }
        }
        NoteOwnedFocus(null);
        if (field == null || IsExternal(field)) return;
        try { field.DeactivateInputField(); } catch { /* */ }
        try
        {
            var system = EventSystem.current;
            if (system != null && system.currentSelectedGameObject != null && field.gameObject != null
                && system.currentSelectedGameObject.Pointer == field.gameObject.Pointer)
                system.SetSelectedGameObject(null);
        }
        catch { /* */ }
        _lastBlur = "click elsewhere: " + SafeFieldName(field);
        SyncMenuHotkeyGuard(FocusedArmedField());
    }

    /// <summary>What the last click-elsewhere blur released (<c>mods_ui status.focus.lastBlur</c>).</summary>
    private static string _lastBlur = "";
    internal static string LastBlur => _lastBlur;

    private static string SafeFieldName(TMP_InputField field)
    {
        try { return IsSearchInput(field) ? "search" : FieldCaption(field); }
        catch { return "field"; }
    }

    internal static void NoteOwnedFocus(TMP_InputField? field)
    {
        // TEXTUNDO1: history ends with the edit. A field that loses focus (a click elsewhere, Enter's
        // submit, Tab to another field, the dialog closing) forgets its steps, committed or not.
        if (!ReferenceEquals(_ownedFocusField, field)) ClearUndo(_ownedFocusField, "blur");
        _ownedFocusField = field;
        // Job KEY2. The pump would reach the same answer on its next frame; taking it here means a
        // click that focuses a field cannot be followed by one menu-hotkey press slipping through,
        // and a click that clears focus gives the hotkeys straight back.
        SyncMenuHotkeyGuard(field == null ? null : FocusedArmedField());
        if (field != null)
        {
            var armed = FindArmed(field);
            if (armed != null) NoteOwnedCaretEdit(armed);
        }

        try { SyncOwnedCarets(); } catch { /* */ }
    }

    /// <summary>TEXTUNDO1: forget <paramref name="field"/>'s undo history (focus left it, or it closed).</summary>
    private static void ClearUndo(TMP_InputField? field, string reason)
    {
        if (field == null) return;
        try { FindArmed(field)?.Undo.Clear(reason); }
        catch { /* */ }
    }

    /// <summary>TEXTUNDO1 readback: the focused field's undo history (<c>field.undo</c>).</summary>
    internal static Dictionary<string, object?>? DescribeUndo(TMP_InputField? field)
    {
        if (field == null) return null;
        var armed = FindArmed(field);
        if (armed == null) return null;
        var h = armed.Undo;
        string text = "";
        try { text = field.text ?? ""; } catch { /* */ }
        // An outside change (a sync, a reset, a commit's trimming) is noticed on the next edit; the
        // readback applies the same rule so it reports what the next Ctrl+Z would see.
        h.Observe(text);
        return new Dictionary<string, object?>
        {
            ["depth"] = h.UndoDepth,
            ["redoDepth"] = h.RedoDepth,
            ["totalChars"] = h.TotalChars,
            ["lastKind"] = h.LastKind,
            ["maxEntries"] = h.MaxEntries,
            ["maxChars"] = h.MaxChars,
            ["evicted"] = h.Evicted,
            ["refused"] = h.Refused,
            ["lastClear"] = h.LastClear,
            ["readOnly"] = armed.ReadOnly,
            ["undoApplied"] = UndoApplied,
            ["redoApplied"] = RedoApplied,
            ["ignoredReadOnly"] = UndoIgnoredReadOnly,
        };
    }

    /// <summary>
    /// Owned insertion index and whether the framework caret is currently lit.
    /// Used by <c>mods_ui rects</c>; TMP's <c>stringPosition</c> is not this value.
    /// </summary>
    internal static bool TryDescribeOwnedCaret(
        TMP_InputField field, out int caret, out bool focused, out bool blinkOn)
    {
        caret = 0;
        focused = false;
        blinkOn = false;
        if (field == null) return false;
        var state = ReadFieldState(field);
        caret = state.Caret;
        focused = ReferenceEquals(CaretFocusField(), field);
        var armed = FindArmed(field);
        long origin = armed == null ? 0 : armed.BlinkOriginMs;
        blinkOn = UiOwnedCaret.IsLit(focused, Environment.TickCount64, origin, armed is { ReadOnly: true });
        return true;
    }

    /// <summary>
    /// TEXTAREA1 readback (<c>mods_ui status</c> → <c>focus.caret</c>). The focused armed field's
    /// caret against TMP's own rendering: our layout (<c>line</c>, <c>column</c>, <c>lineCount</c>)
    /// next to TMP's <c>textInfo.lineCount</c>, and the caret bar's client rect (y-down pixels)
    /// next to the rendered text's bounds (<c>textBounds</c>). With the caret at the end of the
    /// text, <c>checks.endOnLastLine</c> and <c>checks.endAtRightEdge</c> compare the bar with the
    /// last rendered line; a stale rendering on the frame of an edit can disagree once.
    /// </summary>
    internal static Dictionary<string, object?> DescribeFocusedCaret()
    {
        var result = new Dictionary<string, object?>();
        var field = CaretFocusField();
        if (field == null)
        {
            result["focused"] = false;
            return result;
        }

        var armed = FindArmed(field);
        var state = ReadFieldState(field);
        result["focused"] = true;
        result["caption"] = SafeCaption(field);
        result["multiline"] = IsMultiline(field);
        result["textLength"] = state.Text.Length;
        result["caret"] = state.Caret;
        // TEXTUNDO1 polish: a read-only field never lights its caret.
        result["caretHidden"] = armed is { ReadOnly: true };
        try
        {
            if (armed?.CaretGraphic != null) result["caretAlpha"] = (float)Math.Round(armed.CaretGraphic.color.a, 2);
        }
        catch { /* */ }
        result["characterLimit"] = FieldMaxLength(field) == int.MaxValue ? 0 : FieldMaxLength(field);
        TMP_Text? tmp = null;
        try { tmp = field.textComponent; } catch { tmp = null; }
        if (armed != null && tmp != null)
        {
            // Measure now (cached when nothing moved) so the readback is this frame's answer.
            MeasureOwnedCaret(armed, tmp, state.Text, state.Caret, out _, out _, out _);
            var spot = armed.MeasuredSpot;
            result["line"] = spot.Line;
            result["column"] = spot.Column;
            result["lineCount"] = spot.LineCount;
            result["beyondView"] = spot.BeyondView;
        }

        if (tmp != null)
        {
            try { result["wrap"] = tmp.enableWordWrapping; } catch { /* */ }
            try { result["hAlign"] = tmp.horizontalAlignment.ToString(); } catch { /* */ }
            try { result["vAlign"] = tmp.verticalAlignment.ToString(); } catch { /* */ }
            try { result["richText"] = tmp.richText; } catch { /* */ }
            try { result["tmpLineCount"] = tmp.textInfo != null ? tmp.textInfo.lineCount : -1; } catch { /* */ }
        }

        float screenH = 0f;
        try { screenH = UnityPanel.ScreenHeightForHit(); } catch { /* */ }
        var caretRect = ClientRectOf(armed?.Caret?.GetComponent<RectTransform>(), screenH);
        var fieldRect = ClientRectOf(field.GetComponent<RectTransform>(), screenH);
        Dictionary<string, object?>? rendered = null;
        if (tmp != null)
        {
            try
            {
                var b = tmp.textBounds;
                var rt = tmp.rectTransform;
                if (b.size.x > 0f || b.size.y > 0f)
                {
                    Vector2 a = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(b.min.x, b.min.y, 0f)));
                    Vector2 c = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(b.max.x, b.max.y, 0f)));
                    rendered = ClientRect(Math.Min(a.x, c.x), Math.Max(a.y, c.y), Math.Abs(c.x - a.x), Math.Abs(c.y - a.y), screenH);
                }
            }
            catch { rendered = null; }
        }

        result["caretRect"] = caretRect;
        result["fieldRect"] = fieldRect;
        result["textBounds"] = rendered;

        var checks = new Dictionary<string, object?>();
        if (caretRect != null && fieldRect != null)
        {
            float ct = (float)caretRect["y"]!, ch = (float)caretRect["h"]!;
            float ft = (float)fieldRect["y"]!, fh = (float)fieldRect["h"]!;
            checks["insideField"] = UiOwnedCaret.ContainedVertically(ct, ch, ft, fh);
        }

        bool atEnd = state.Caret == state.Text.Length && state.Text.Length > 0 && !state.Text.EndsWith("\n", StringComparison.Ordinal);
        if (atEnd && caretRect != null && rendered != null)
        {
            float cx = (float)caretRect["x"]! + (float)caretRect["w"]! * 0.5f;
            float cy = (float)caretRect["y"]! + (float)caretRect["h"]! * 0.5f;
            float rBottom = (float)rendered["y"]! + (float)rendered["h"]!;
            float rRight = (float)rendered["x"]! + (float)rendered["w"]!;
            float lineH = Math.Max(UiOwnedCaret.HeightPx, (float)caretRect["h"]!);
            checks["endOnLastLine"] = cy <= rBottom + UiOwnedCaret.LineCenterTolerancePx && cy >= rBottom - lineH - UiOwnedCaret.LineCenterTolerancePx;
            // Only meaningful when the last line is also the widest (always true for one line).
            checks["endAtRightEdge"] = Math.Abs(cx - rRight) <= 4f;
        }

        result["checks"] = checks;
        return result;
    }

    private static string SafeCaption(TMP_InputField field)
    {
        try { return FieldCaption(field); }
        catch { return ""; }
    }

    private static Dictionary<string, object?>? ClientRectOf(RectTransform? rt, float screenH)
    {
        if (rt == null) return null;
        try
        {
            Rect local = rt.rect;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(local.xMin, local.yMin, 0f)));
            Vector2 b = RectTransformUtility.WorldToScreenPoint(null, rt.TransformPoint(new Vector3(local.xMax, local.yMax, 0f)));
            return ClientRect(Math.Min(a.x, b.x), Math.Max(a.y, b.y), Math.Abs(b.x - a.x), Math.Abs(b.y - a.y), screenH);
        }
        catch { return null; }
    }

    /// <summary>Screen pixels (y-up, <paramref name="topY"/> the upper edge) to a y-down client rect.</summary>
    private static Dictionary<string, object?> ClientRect(float x, float topY, float w, float h, float screenH) => new()
    {
        ["x"] = (float)Math.Round(x, 1),
        ["y"] = (float)Math.Round(screenH > 0f ? screenH - topY : -topY, 1),
        ["w"] = (float)Math.Round(w, 1),
        ["h"] = (float)Math.Round(h, 1),
    };

    private static void PruneArmedFields()
    {
        for (int i = _armedFields.Count - 1; i >= 0; i--)
        {
            var field = _armedFields[i].Field;
            if (field == null)
            {
                _armedFields.RemoveAt(i);
                continue;
            }

            bool dead;
            try
            {
                var go = field.gameObject;
                dead = go == null;
            }
            catch { dead = true; }

            if (dead) _armedFields.RemoveAt(i);
        }
    }

    internal static void ReleaseInputPumps()
    {
        HideAllOwnedCarets();
        // BUILDSEARCH1: external fields belong to their own owner (ReleaseExternalField), not to the
        // workspace whose release this is.
        PruneExternalFields();
        _armedFields.RemoveAll(a => a == null || !IsExternal(a.Field));
        _searchHasOwned = false;
        _searchOwnedCaret = 0;
        _searchOwnedAnchor = 0;
        _ownedFocusField = null;
        // Job KEY2: released with the armed list itself - workspace close, Core unload, Dispose.
        MenuHotkeyGuard.Release();
        // CONTROLS1 round 2: no skin retry (or its frame subscription) outlives the workspace.
        ReleaseSkinRetries();
        ReleasePendingGameButtons();
        TypeRepeats.Reset();
        _repeatField = null;
        ShownPanels.Clear();
        // TABCAPS1: no strip reference or frame subscription outlives this runtime.
        _lastStrip = null;
        _stripRecheck = null;
        try { Plugin.Frame -= StripRecheckPump; } catch { /* */ }
        WorkspaceTabCaptions.Reset();
        InnerScrolls.Clear();
        try { ModsWorkspaceChrome.Release(); } catch { /* */ }
        ClearSidebarScrollPreserve();
        _lastObservedPos = float.NaN;
        _lastWheelDelta = 0f;
        _lastWheelHadDelta = false;
        SyncShownInputPump();
    }

    // ---- clone -------------------------------------------------------------------------------

    /// <summary>
    /// Build a button row and hand back the GameObject carrying its caption, so the row can be
    /// measured like every other row type (R43 finding 3).
    ///
    /// <para>
    /// The caption is the awkward one: for a cloned vanilla <c>SimpleStunButton</c> it is a TMP
    /// somewhere in the donor's own subtree, not a label this code created, which is why
    /// <paramref name="caption"/> comes back from <see cref="SetButtonText"/> rather than from
    /// <see cref="MakeLabel"/>. The clone is placed provisionally here and placed for real by
    /// <see cref="UnityPanel.AddRow"/>, because the caption cannot be measured until it is
    /// parented and the row cannot be placed until the caption has been measured.
    /// </para>
    /// </summary>
    private static GameObject? CloneOrCreateButton(
        GameObject parent, string text, out GameObject? caption, out ControlSource source, bool left = false, bool workspaceTab = false)
    {
        caption = null;
        // Scene-wide button donors can be HUD icons with unrelated artwork and target refs.
        // Use our composed row here; named native entry sites qualify their own donor.
        var attempt = ControlBuildPlan.Resolve(
            () => workspaceTab ? CloneWorkspaceTab(parent) : null,
            () => BuildButton(parent),
            ButtonOperable,
            (go, wasClone) => DropUnoperableControl("button", go, wasClone));

        LogControlSource(ref _buttonSourceLogged, "panel button", "button", attempt.Source);
        source = attempt.Source;
        var built = attempt.Control;
        if (built == null) return null;

        // After the resolve and before the activation, exactly like the other four: the caption is
        // part of what the row is, and a clone's OnEnable must not run over a half-written one.
        caption = SetButtonText(built, text);
        var label = caption?.GetComponent<TextMeshProUGUI>();
        if (label != null)
        {
            // A donor's auto-size/alignment belongs to its old layout, not this full-width row.
            label.enableAutoSizing = false;
            label.fontSize = 22f;
            bool multiline = !string.IsNullOrEmpty(text) && text.IndexOf('\n') >= 0;
            ApplyHorizontalAlign(label, left || multiline);
            try { label.enableWordWrapping = multiline; } catch { /* */ }
            label.transform.localScale = Vector3.one;
        }
        Activate(built);
        return built;
    }

    /// <summary>
    /// TABS2 tab donor ladder (<see cref="WorkspaceTabLadder"/>). Rung 1 is the in-game inner-tab
    /// prefab this method has always cloned; it needs the FullscreenMenu, which the title screen does
    /// not have. Rungs 2 and 3 carry the same native prefab without it: the template taken while the
    /// in-game menu was up this generation, then the prefab found resident. A null return is the flat
    /// composed button, exactly the look before TABS2. The order comes from
    /// <see cref="WorkspaceTabLadder.Next"/>, asked once per rung.
    /// </summary>
    private static GameObject? CloneWorkspaceTab(GameObject parent)
    {
        bool fullscreenOut = false;
        bool templateOut = false;
        bool residentOut = false;
        for (int rung = 0; rung < 4; rung++)
        {
            switch (WorkspaceTabLadder.Next(!fullscreenOut, !templateOut, !residentOut))
            {
                case WorkspaceTabSource.FullscreenInner:
                {
                    fullscreenOut = true;
                    var clone = TryCloneFullscreenInnerTab(parent);
                    if (clone == null) break;
                    // Cached from the silenced, undecorated clone, not the raw donor.
                    try { if (WorkspaceTabDonors.TryBeginCapture()) WorkspaceTabDonors.CaptureTemplate(clone); }
                    catch { /* */ }
                    WorkspaceTabDonors.Note(WorkspaceTabSource.FullscreenInner);
                    return clone;
                }
                case WorkspaceTabSource.CachedTemplate:
                {
                    templateOut = true;
                    var template = WorkspaceTabDonors.UsableTemplate();
                    if (template == null) break;
                    var clone = CloneButton(template, parent);
                    if (clone == null) break;
                    WorkspaceTabDonors.Note(WorkspaceTabSource.CachedTemplate);
                    return clone;
                }
                case WorkspaceTabSource.ResidentInnerTab:
                {
                    residentOut = true;
                    var resident = WorkspaceTabDonors.ResidentDonor();
                    if (resident == null) break;
                    var clone = CloneButton(resident, parent);
                    if (clone == null) break;
                    WorkspaceTabDonors.ResidentDecorations(out var icon, out var newTag);
                    HideDonorDecorations(clone, icon, newTag);
                    WorkspaceTabDonors.Note(WorkspaceTabSource.ResidentInnerTab);
                    return clone;
                }
                default:
                    WorkspaceTabDonors.Note(WorkspaceTabSource.Flat);
                    return null;
            }
        }

        WorkspaceTabDonors.Note(WorkspaceTabSource.Flat);
        return null;
    }

    /// <summary>Rung 1: the in-game inner-tab prefab, callbacks silenced and decorations hidden.</summary>
    private static GameObject? TryCloneFullscreenInnerTab(GameObject parent)
    {
        if (!ModsWorkspaceChrome.TryGetFullscreenMenu(out var menu, out _) || menu == null) return null;
        return TryCloneInnerTabOf(menu, parent);
    }

    private static GameObject? TryCloneInnerTabOf(FullscreenMenu menu, GameObject parent)
    {
        var entry = menu.InnerTabGroup?.EntryPrefab;
        if (entry == null || entry.Button == null) return null;
        var clone = CloneButton(entry.Button.gameObject, parent);
        if (clone == null) return null;
        // Copy only the qualified button subtree. Never register it with the native
        // selection group or retain its gameplay callbacks.
        HideDonorDecorations(
            clone,
            entry.IconImage != null ? entry.IconImage.sprite : null,
            entry.NewTagImage != null ? entry.NewTagImage.sprite : null);
        return clone;
    }

    /// <summary>Switch off the inner-tab entry's school icon and new-tag graphics on a clone.</summary>
    private static void HideDonorDecorations(GameObject clone, Sprite? icon, Sprite? newTag)
    {
        if (icon == null && newTag == null) return;
        try
        {
            var images = clone.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image == null || image.sprite == null) continue;
                if (image.sprite == icon || image.sprite == newTag)
                    image.gameObject.SetActive(false);
            }
        }
        catch { /* a visible decoration is not fatal */ }
    }

    /// <summary>
    /// TABS2: take the cross-site tab template the first time the in-game FullscreenMenu is seen this
    /// generation, so a title strip later in the session gets the native framing even when Mods was
    /// never opened in game. Same clone path as a strip tab, under an inactive owned holder that is
    /// destroyed straight after. Bounded by <see cref="WorkspaceTabLadder.MaxTemplateCaptures"/>.
    /// </summary>
    internal static void PrimeWorkspaceTabTemplate(FullscreenMenu? menu)
    {
        if (menu == null) return;
        // An attempt is spent only when there is a donor to clone, so the first frames of a menu
        // whose tab group is not filled in yet cannot burn the generation's budget.
        try
        {
            var entry = menu.InnerTabGroup?.EntryPrefab;
            if (entry == null || entry.Button == null) return;
        }
        catch { return; }
        if (!WorkspaceTabDonors.TryBeginCapture()) return;
        GameObject? holder = null;
        try
        {
            holder = new GameObject(WorkspaceTabDonors.PrimeHolderName);
            holder.SetActive(false);
            var clone = TryCloneInnerTabOf(menu, holder);
            if (clone != null) WorkspaceTabDonors.CaptureTemplate(clone);
        }
        catch { /* a failed prime leaves the ladder's lower rungs */ }
        finally
        {
            TryDestroyImmediate(holder);
        }
    }

    private static bool _stripTargetsLogged;

    /// <summary>
    /// On this clone only (never the donor): each color-tint target's highlighted color moves part of the
    /// way back to its normal color, so hovering an unselected tab is lighter than the selected tab.
    /// Sprite-swap targets are reported once per Core generation with their highlighted sprite.
    /// </summary>
    private static void DimCloneHover(SimpleStunButton button)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            var targets = button.TargetSettings;
            int n = targets == null ? 0 : targets.Length;
            for (int i = 0; i < n; i++)
            {
                var target = targets![i];
                if (target == null || !target.Enabled) continue;
                int transition = (int)target.Transition;
                if (transition == 1 /* ColorTint */)
                {
                    var colors = target.Colors;
                    var n0 = colors.normalColor;
                    var h0 = colors.highlightedColor;
                    var h = WorkspaceTabHover.HoverColor((n0.r, n0.g, n0.b, n0.a), (h0.r, h0.g, h0.b, h0.a));
                    colors.highlightedColor = new Color(h.R, h.G, h.B, h.A);
                    target.Colors = colors;
                    report.Append(" [").Append(i).Append(" tint normalA=").Append(n0.a.ToString("0.##", CultureInfo.InvariantCulture));
                    try
                    {
                        var graphics = target.GraphicTargets;
                        int texts = 0;
                        if (graphics != null)
                            for (int g = 0; g < graphics.Length; g++)
                                if (graphics[g] != null && graphics[g].TryCast<TMP_Text>() != null) texts++;
                        report.Append(" texts=").Append(texts);
                    }
                    catch { /* */ }
                    report.Append(']');
                }
                else
                {
                    string sprite = "";
                    try { sprite = target.SpriteState.highlightedSprite != null ? target.SpriteState.highlightedSprite.name : ""; } catch { /* */ }
                    report.Append(" [").Append(i).Append(" transition=").Append(transition).Append(" highlighted=").Append(sprite).Append(']');
                }
            }
        }
        catch (Exception e) { report.Append(" threw:").Append(e.Message); }

        if (_stripTargetsLogged) return;
        _stripTargetsLogged = true;
        Plugin.Logger?.LogDiag("Mods workspace: strip tab targets (unselected hover dimmed on tint targets):" + report);
    }

    private static void PaintWorkspaceTab(GameObject go, MenuRowTone tone)
    {
        var native = go.GetComponent<SimpleStunButton>();
        if (native != null)
        {
            native.ForceSelect = false;
            bool selected = tone == MenuRowTone.Selected;
            // Hover is the same Highlighted state as selected on the native tab: make an unselected
            // tab's hover lighter on this clone (WorkspaceTabHover) before the state is applied.
            if (WorkspaceTabHover.DimsHover(selected)) DimCloneHover(native);
            // FullscreenMenu_InnerTabEntry.Refresh uses ForceHighlight for IsSelected.
            native.ForceHighlight = selected;
            return;
        }
        var image = go.GetComponent<Image>();
        if (image != null) image.color = tone == MenuRowTone.Selected
            ? new Color(.42f, .035f, .06f, 1f) : new Color(.035f, .065f, .09f, 1f);
        PaintChromeRim(go, MenuRowTone.Chrome);
    }

    /// <summary>
    /// Selected sidebar rows use the gold-brown fill from the panel palette (slider fill /
    /// toggle mark family). Hover stays a lighter gray so the two states cannot be confused.
    /// Chrome (Overview / Close) uses the darker inset fill, a rim, and never the selected gold.
    /// </summary>
    private static void PaintSidebarRow(GameObject go, MenuRowTone tone)
    {
        if (go == null) return;
        Image? image = null;
        Button? button = null;
        try { image = go.GetComponent<Image>(); } catch { /* */ }
        try { button = go.GetComponent<Button>(); } catch { /* */ }
        Color fill = tone switch
        {
            MenuRowTone.Selected => new Color(MenuRowStyle.SelectedR, MenuRowStyle.SelectedG, MenuRowStyle.SelectedB, MenuRowStyle.SelectedA),
            MenuRowTone.Chrome => new Color(MenuRowStyle.ChromeR, MenuRowStyle.ChromeG, MenuRowStyle.ChromeB, MenuRowStyle.ChromeA),
            _ => new Color(MenuRowStyle.NormalR, MenuRowStyle.NormalG, MenuRowStyle.NormalB, MenuRowStyle.NormalA),
        };
        if (image != null)
        {
            try { image.color = fill; } catch { /* */ }
            try { image.maskable = true; } catch { /* */ }
            try { image.raycastTarget = true; } catch { /* */ }
        }

        try
        {
            var label = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                label.fontSize = tone == MenuRowTone.Chrome
                    ? MenuRowStyle.ChromeFontSize
                    : MenuRowStyle.CatalogFontSize;
                if (tone == MenuRowTone.Chrome)
                    label.color = new Color(
                        MenuRowStyle.ChromeLabelR, MenuRowStyle.ChromeLabelG,
                        MenuRowStyle.ChromeLabelB, MenuRowStyle.ChromeLabelA);
            }
        }
        catch { /* */ }

        PaintChromeRim(go, tone);

        if (button == null) return;
        try
        {
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = tone switch
            {
                MenuRowTone.Selected => new Color(MenuRowStyle.SelectedHoverR, MenuRowStyle.SelectedHoverG, MenuRowStyle.SelectedHoverB, MenuRowStyle.SelectedHoverA),
                MenuRowTone.Chrome => new Color(MenuRowStyle.ChromeHoverR, MenuRowStyle.ChromeHoverG, MenuRowStyle.ChromeHoverB, MenuRowStyle.ChromeHoverA),
                _ => new Color(MenuRowStyle.HoverR, MenuRowStyle.HoverG, MenuRowStyle.HoverB, MenuRowStyle.HoverA),
            };
            colors.pressedColor = tone switch
            {
                MenuRowTone.Selected => new Color(MenuRowStyle.SelectedR, MenuRowStyle.SelectedG, MenuRowStyle.SelectedB, 1f),
                MenuRowTone.Chrome => new Color(MenuRowStyle.ChromeR, MenuRowStyle.ChromeG, MenuRowStyle.ChromeB, 1f),
                _ => new Color(MenuRowStyle.NormalR, MenuRowStyle.NormalG, MenuRowStyle.NormalB, 1f),
            };
            colors.selectedColor = colors.normalColor;
            colors.colorMultiplier = 1f;
            button.colors = colors;
        }
        catch { /* */ }
    }

    /// <summary>
    /// Footer actions get a 1px rim so they read as controls, not as extra catalog rows.
    /// Catalog and selected rows do not.
    /// </summary>
    private static void PaintChromeRim(GameObject go, MenuRowTone tone)
    {
        Outline? outline = null;
        try { outline = go.GetComponent<Outline>(); } catch { /* */ }
        if (tone != MenuRowTone.Chrome)
        {
            if (outline != null)
            {
                try { outline.enabled = false; } catch { /* */ }
            }

            return;
        }

        if (outline == null)
        {
            try { outline = go.AddComponent<Outline>(); }
            catch { outline = null; }
        }

        if (outline == null) return;
        try { outline.enabled = true; } catch { /* */ }
        try
        {
            outline.effectColor = new Color(
                MenuRowStyle.ChromeBorderR, MenuRowStyle.ChromeBorderG,
                MenuRowStyle.ChromeBorderB, MenuRowStyle.ChromeBorderA);
        }
        catch { /* */ }
        try { outline.effectDistance = new Vector2(1f, -1f); } catch { /* */ }
        try { outline.useGraphicAlpha = false; } catch { /* */ }
    }

    /// <summary>
    /// Search field fill and rim. Distinct from catalog rows, selected gold, and footer chrome.
    /// </summary>
    private static void PaintSearchField(GameObject go, TMP_InputField field)
    {
        if (go == null) return;
        Image? image = null;
        try { image = go.GetComponent<Image>(); } catch { /* */ }
        if (image != null)
        {
            try { image.sprite = null; } catch { /* */ }
            try
            {
                image.color = new Color(
                    MenuRowStyle.SearchR, MenuRowStyle.SearchG, MenuRowStyle.SearchB, MenuRowStyle.SearchA);
            }
            catch { /* */ }
            try { image.raycastTarget = true; } catch { /* */ }
        }

        Outline? outline = null;
        try { outline = go.GetComponent<Outline>(); } catch { /* */ }
        if (outline == null)
        {
            try { outline = go.AddComponent<Outline>(); }
            catch { outline = null; }
        }

        if (outline != null)
        {
            try { outline.enabled = true; } catch { /* */ }
            try
            {
                outline.effectColor = new Color(
                    MenuRowStyle.SearchBorderR, MenuRowStyle.SearchBorderG,
                    MenuRowStyle.SearchBorderB, MenuRowStyle.SearchBorderA);
            }
            catch { /* */ }
            try { outline.effectDistance = new Vector2(1f, -1f); } catch { /* */ }
            try { outline.useGraphicAlpha = false; } catch { /* */ }
        }

        _ = field;
    }

    /// <summary>
    /// Instantiate the vanilla button donor into a row, inactive.
    ///
    /// <para>
    /// <b>Inactive, and that is the whole of job FUI30's first fix.</b> The clone was previously
    /// left at whatever <c>activeSelf</c> the donor had and was never activated, while the donor
    /// sweep runs through <c>FindObjectsOfType(type, includeInactive: true)</c> — so a screen whose
    /// first <c>SimpleStunButton</c> belongs to a view that is not currently displayed handed this
    /// method an inactive donor and got back an inactive row. That row is still parented, still
    /// measured (<see cref="SetButtonText"/> and <see cref="MeasuredRowHeight"/> both read through
    /// <c>includeInactive: true</c>) and still counted by the layout line — and it draws nothing and
    /// receives nothing. <see cref="ButtonDonor"/> now refuses an unshown donor and this activates
    /// deliberately at the end, which is the same build-order rule evidence 176 put on the other
    /// four row types and which the fifth never got.
    /// </para>
    ///
    /// <para>
    /// The deactivation is not redundant with the donor filter either: <c>Instantiate</c> of an
    /// active donor has already run <c>Awake</c> and <c>OnEnable</c>, and
    /// <c>SimpleStunSelectable</c> applies its target colors in <c>Selectable.OnEnable</c>
    /// (<c>DoStateTransition</c> / <c>StartColorTween</c>). Going inactive and back
    /// makes that transition run once more, after <see cref="SilenceClone"/> and the placement, so
    /// the row is colored for the state it is actually in.
    /// </para>
    /// </summary>
    private static GameObject? CloneButton(GameObject? donor, GameObject parent)
    {
        if (!Alive(donor) || donor == null) return null;
        if (!QualifiedButtonTargets(donor)) return null;
        GameObject? clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(donor);
            clone.name = "button";
            clone.transform.SetParent(parent.transform, false);
            clone.SetActive(false);
            // Evidence 176's second trap, which this clone had too and had had for longer: a
            // vanilla button arrives with the game's inspector-wired onClick calls still
            // pointing at the game's own objects, because Instantiate remaps only what is
            // inside the copied subtree. Pressing a mod panel's button would have fired
            // whatever the donor was wired to as well as our handler.
            SilenceClone(clone);
            if (!QualifiedButtonTargets(clone))
            {
                TryDestroyImmediate(clone);
                return null;
            }
            PlaceTop(clone, 0f, RowWidth, RowHeight);
            return clone;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Instantiate SimpleStunButton threw: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    private static bool QualifiedButtonTargets(GameObject root)
    {
        try
        {
            var buttons = root.GetComponentsInChildren<SimpleStunButton>(true);
            if (buttons == null || buttons.Length == 0) return false;
            foreach (var button in buttons)
            {
                // Derived game controls can carry additional external hover/selection targets.
                if (button.GetIl2CppType().FullName != "ProjectM.UI.SimpleStunButton") return false;
                if (!InternalGraphic(button.targetGraphic)) return false;
                var targets = button.TargetSettings;
                if (targets == null) continue;
                foreach (var target in targets)
                {
                    if (target == null || target.GraphicTargets == null) continue;
                    foreach (var graphic in target.GraphicTargets)
                        if (!InternalGraphic(graphic)) return false;
                }
            }
            return true;
        }
        catch { return false; }

        bool InternalGraphic(Graphic? graphic) => graphic == null
            || graphic.transform.Pointer == root.transform.Pointer
            || graphic.transform.IsChildOf(root.transform);
    }

    /// <summary>
    /// The button built from parts: a framed, raycastable background carrying a <c>Button</c> whose
    /// <c>targetGraphic</c> is that background. The caption is added afterwards by
    /// <see cref="SetButtonText"/>, which finds no TMP here and makes one.
    ///
    /// <para>
    /// Two things this did not do before. It never set <c>targetGraphic</c>, so the built button had
    /// no state transition at all — no highlight on hover, no press feedback — while
    /// <c>Selectable</c> reported itself perfectly healthy. And it took <c>Image</c>'s default
    /// white, so a built row was a solid white bar rather than something that looks like the panel
    /// it is in; <see cref="ApplyFrame"/> is the decision the toggle box and the text field already
    /// make, and it is the same decision here.
    /// </para>
    ///
    /// <para>
    /// That white default is also the evidence that the deny panel's Close button did <b>not</b>
    /// take this path: <c>Image</c> with no sprite draws its color through
    /// <c>Graphic.OnPopulateMesh</c>, so a built button cannot produce an empty band. Read from the
    /// uGUI contract, not measured on this build.
    /// </para>
    /// </summary>
    private static GameObject? BuildButton(GameObject parent)
    {
        GameObject? go = null;
        try
        {
            go = new GameObject("button");
            // Before the parenting and before any AddComponent, for NewControl's reason.
            go.SetActive(false);
            go.transform.SetParent(parent.transform, false);
            PlaceTop(go, 0f, RowWidth, RowHeight);

            var background = TryAdd<Image>(go);
            if (background == null) { TryDestroyImmediate(go); return null; }
            ApplyFrame(background, new Color(0.18f, 0.18f, 0.24f, 0.95f));
            try { background.raycastTarget = true; } catch { /* */ }

            var button = TryAdd<Button>(go);
            if (button == null) { TryDestroyImmediate(go); return null; }
            try { button.targetGraphic = background; } catch { /* */ }
            try { if (background != null) background.maskable = true; } catch { /* */ }
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a button threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>
    /// The selector: one framed container the width of the control column (so the row's relayout
    /// moves it as one control), holding a previous arrow, the value text and a next arrow. The
    /// container is itself a button (a click on the value advances); each arrow is a button of its
    /// own drawn over it.
    /// </summary>
    private static GameObject? BuildChoiceControl(
        GameObject row, in RowColumns columns, UiChoiceState state, out ControlSource source,
        out object? valueClick, out GameObject? previous, out GameObject? next)
    {
        source = ControlSource.None;
        valueClick = null;
        previous = null;
        next = null;
        GameObject? go = null;
        try
        {
            float width = columns.ControlWidth;
            go = NewControl(row, "choice", columns, width, RowHeight);
            var background = TryAdd<Image>(go);
            if (background == null) { TryDestroyImmediate(go); return null; }
            ApplyFrame(background, new Color(0.16f, 0.16f, 0.20f, 0.95f));
            try { background.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button != null)
            {
                try { button.targetGraphic = background; } catch { /* */ }
                valueClick = button.onClick;
            }

            // Three parts: the previous arrow, the value's own area between them, the next arrow. The value is fitted inside
            // its area only (user report 2026-10-09: "< ilobal (Automatic >", a centered caption wider than the gap ran under
            // both arrows).
            const float pad = UiControlDraw.ChoiceValuePad;
            const float arrowW = UiControlDraw.ChoiceArrowWidth;
            var valueGo = new GameObject("value-text");
            valueGo.transform.SetParent(go.transform, false);
            AnchorInside(valueGo, 0f, 1f, arrowW + pad, -(arrowW + pad), 0f);
            var tmp = TryAdd<TextMeshProUGUI>(valueGo);
            if (tmp == null) { TryDestroyImmediate(go); return null; }
            ApplyControlTmp(tmp, state.DisplayLabel, UiControlDraw.ChoiceValueFontSize);
            FitInsideArea(tmp, UiControlDraw.ChoiceValueFontSize, UiControlDraw.ChoiceValueMinFontSize);
            ApplyCachedFont(tmp);

            previous = BuildChoiceArrow(go, "choice-prev", left: true, UiControlDraw.ChoicePrevious);
            next = BuildChoiceArrow(go, "choice-next", left: false, UiControlDraw.ChoiceNext);

            Activate(go);
            source = ControlSource.Built;
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a choice threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>One arrow of the selector, a child of its container. Returns the arrow's own object.</summary>
    private static GameObject? BuildChoiceArrow(GameObject parent, string name, bool left, string caption)
    {
        try
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            float w = UiControlDraw.ChoiceArrowWidth;
            if (left) AnchorInside(go, 0f, 0f, 0f, w);
            else AnchorInside(go, 1f, 1f, -w, 0f);
            var image = TryAdd<Image>(go);
            if (image == null) return null;
            ApplyFrame(image, new Color(0.20f, 0.20f, 0.26f, 0.95f));
            try { image.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button == null) return null;
            try { button.targetGraphic = image; } catch { /* */ }
            var textGo = new GameObject("caption");
            textGo.transform.SetParent(go.transform, false);
            Stretch(textGo);
            var tmp = TryAdd<TextMeshProUGUI>(textGo);
            if (tmp != null)
            {
                ApplyControlTmp(tmp, caption, 16f);
                ApplyCachedFont(tmp);
            }

            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a choice arrow threw: " + e.Message);
            return null;
        }
    }

    private static GameObject? BuildNumericControl(
        GameObject row, in RowColumns columns, UiNumericState state,
        out TMP_InputField? field, out ControlSource source, out GameObject? minusGo, out GameObject? plusGo)
    {
        field = null;
        source = ControlSource.None;
        minusGo = null;
        plusGo = null;
        const float stepW = 28f;
        float fieldW = Math.Max(48f, columns.ControlWidth - stepW * 2f - 8f);
        var minusCols = new RowColumns(
            columns.LabelWidth, columns.LabelX, stepW, columns.ControlLeft + stepW * 0.5f);
        var fieldCols = new RowColumns(
            columns.LabelWidth, columns.LabelX, fieldW, columns.ControlLeft + stepW + 4f + fieldW * 0.5f);
        var plusCols = new RowColumns(
            columns.LabelWidth, columns.LabelX, stepW, columns.ControlLeft + columns.ControlWidth - stepW * 0.5f);

        // SETTINGS1: one container spans the control column and is the row's control, so the menu
        // pane's relayout stretches it and not the field alone (which would cover the steppers).
        var container = NewControl(row, "numeric", columns, columns.ControlWidth, RowHeight);
        var minus = BuildStepperButton(container, minusCols, UiControlDraw.StepperMinus);
        var input = MakeInput(container, fieldCols, state.Draft, out field, out source, composed: true);
        var plus = BuildStepperButton(container, plusCols, UiControlDraw.StepperPlus);
        if (minus != null) { AnchorInside(minus, 0f, 0f, 0f, stepW); minusGo = minus; }
        if (plus != null) { AnchorInside(plus, 1f, 1f, -stepW, 0f); plusGo = plus; }
        if (input != null) AnchorInside(input, 0f, 1f, stepW + 4f, -(stepW + 4f));
        Activate(container);
        if (input == null && minus == null && plus == null)
        {
            TryDestroyImmediate(container);
            return null;
        }

        return container;
    }

    private static GameObject? BuildStepperButton(GameObject row, in RowColumns columns, string caption)
    {
        GameObject? go = null;
        try
        {
            go = NewControl(row, "stepper", columns, columns.ControlWidth, RowHeight);
            var background = TryAdd<Image>(go);
            if (background == null) { TryDestroyImmediate(go); return null; }
            ApplyFrame(background, new Color(0.18f, 0.18f, 0.24f, 0.95f));
            try { background.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button == null) { TryDestroyImmediate(go); return null; }
            try { button.targetGraphic = background; } catch { /* */ }

            var textGo = new GameObject("caption");
            textGo.transform.SetParent(go.transform, false);
            Stretch(textGo);
            var tmp = TryAdd<TextMeshProUGUI>(textGo);
            if (tmp != null)
            {
                var font = FindFont();
                string draw = FontHasString(font, caption) ? caption : "";
                ApplyControlTmp(tmp, draw, 18f);
                ApplyCachedFont(tmp);
            }

            Activate(go);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a stepper threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>A color row's swatch in <paramref name="hex"/>'s color (opaque: the swatch shows the hue, the hex box the alpha).</summary>
    private static void PaintSwatch(GameObject row, string hex)
    {
        try
        {
            if (!UiColor.TryParseHex(hex, true, out var c) && !UiColor.TryParseHex(hex, false, out c)) return;
            var swatch = row.transform.Find("swatch");
            var image = swatch != null ? swatch.GetComponent<Image>() : null;
            if (image != null) image.color = new Color(c.R, c.G, c.B, 1f);
        }
        catch { /* the swatch keeps its color */ }
    }

    private static GameObject? BuildColorControl(
        GameObject row, in RowColumns columns, UiColorState state,
        out TMP_InputField? field, out ControlSource source)
    {
        field = null;
        source = ControlSource.None;
        const float swatchW = 28f;
        float fieldW = Math.Max(48f, columns.ControlWidth - swatchW - 8f);
        var swatchCols = new RowColumns(
            columns.LabelWidth, columns.LabelX, swatchW, columns.ControlLeft + swatchW * 0.5f);
        var hexCols = new RowColumns(
            columns.LabelWidth, columns.LabelX, fieldW, columns.ControlLeft + swatchW + 8f + fieldW * 0.5f);

        GameObject? swatch = null;
        try
        {
            swatch = NewControl(row, "swatch", swatchCols, swatchW, 24f);
            var image = TryAdd<Image>(swatch);
            if (image == null) { TryDestroyImmediate(swatch); swatch = null; }
            else
            {
                try { image.sprite = null; } catch { /* */ }
                try
                {
                    image.color = new Color(state.Value.R, state.Value.G, state.Value.B, 1f);
                }
                catch { /* */ }
                try { image.raycastTarget = false; } catch { /* */ }
                Activate(swatch);
            }
        }
        catch
        {
            TryDestroyImmediate(swatch);
            swatch = null;
        }

        var hex = MakeInput(row, hexCols, state.HexCommitted, out field, out source, composed: true);
        return hex ?? swatch;
    }

    private static GameObject? BuildKeybindControl(
        GameObject row, in RowColumns columns, UiKeybindPickerState state, out ControlSource source)
    {
        source = ControlSource.None;
        GameObject? go = null;
        try
        {
            go = NewControl(row, "keybind", columns, columns.ControlWidth, RowHeight);
            var background = TryAdd<Image>(go);
            if (background == null) { TryDestroyImmediate(go); return null; }
            ApplyFrame(background, new Color(0.16f, 0.16f, 0.20f, 0.95f));
            try { background.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button == null) { TryDestroyImmediate(go); return null; }
            try { button.targetGraphic = background; } catch { /* */ }

            var textGo = new GameObject("value-text");
            textGo.transform.SetParent(go.transform, false);
            Stretch(textGo);
            var tmp = TryAdd<TextMeshProUGUI>(textGo);
            if (tmp == null) { TryDestroyImmediate(go); return null; }
            ApplyControlTmp(tmp, state.DisplayText, 16f);
            ApplyCachedFont(tmp);

            Activate(go);
            source = ControlSource.Built;
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a keybind picker threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>
    /// Keep a one-line caption inside its own rect: no wrapping, shrink from <paramref name="max"/> down to
    /// <paramref name="min"/> when it is wider, and an ellipsis past that, never drawing outside the rect.
    /// </summary>
    private static void FitInsideArea(TextMeshProUGUI tmp, float max, float min)
    {
        try { tmp.enableWordWrapping = false; } catch { /* */ }
        try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }
        try
        {
            tmp.fontSizeMax = max;
            tmp.fontSizeMin = Math.Min(min, max);
            tmp.enableAutoSizing = true;
        }
        catch { /* the ellipsis still keeps it inside */ }
    }

    private static void ApplyControlTmp(TextMeshProUGUI tmp, string text, float fontSize)
    {
        try { tmp.text = text ?? ""; } catch { /* */ }
        try { tmp.fontSize = fontSize; } catch { /* */ }
        ApplyHorizontalAlign(tmp, left: false);
        try { tmp.color = Color.white; } catch { /* */ }
        try { tmp.richText = false; } catch { /* */ }
        try { tmp.raycastTarget = false; } catch { /* */ }
        // SETTINGS1 stage 2: a control's caption sits in the middle of its box (live, a keybind's
        // chord and a segment's label sat at the top).
        try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
    }

    /// <summary>
    /// What makes a button a button, re-checked on whatever was produced: something that can raise
    /// a click, something a pointer can hit, and something a player can see.
    ///
    /// <para>
    /// The third clause is the one the other four predicates do not need. A toggle, a slider and a
    /// field are judged by references that are null or not; a button's whole defect on the deny
    /// screen was a subtree that had every reference it needed and painted nothing, so "at least one
    /// enabled graphic above <see cref="UiInteractionReport.VisibleAlpha"/>" is part of being
    /// operable here rather than a cosmetic extra. It is checked after <c>Instantiate</c> has run
    /// the donor's own <c>OnEnable</c> and before our re-activation, so a color a vanilla button
    /// only takes on once it is enabled has already been applied; a donor still transparent then is
    /// rejected, because the fallback is a built button that is certainly visible.
    /// </para>
    /// </summary>
    private static bool ButtonOperable(GameObject go)
    {
        try
        {
            if (go == null) return false;
            if (FindIn<SimpleStunButton>(go) == null && FindIn<Button>(go) == null) return false;

            bool visible = false;
            bool hittable = false;
            var graphics = go.GetComponentsInChildren<Graphic>(true);
            if (graphics == null) return false;
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                bool on;
                try { on = g.enabled; }
                catch { continue; }
                if (!on) continue;
                try { if (g.color.a > UiInteractionReport.VisibleAlpha) visible = true; }
                catch { /* an unreadable color is not evidence of a visible one */ }
                try { if (g.raycastTarget) hittable = true; }
                catch { /* */ }
            }

            return visible && hittable;
        }
        catch
        {
            return false;
        }
    }

    private static object? FindClickEvent(GameObject go)
    {
        try
        {
            var stun = go.GetComponent<SimpleStunButton>();
            if (stun != null) return stun.onClick;
        }
        catch { /* */ }
        try
        {
            var button = go.GetComponent<Button>();
            if (button != null) return button.onClick;
        }
        catch { /* */ }
        return null;
    }

    /// <summary>
    /// Write a button's caption and answer the GameObject that holds it — the vanilla clone's own
    /// TMP when it brought one, otherwise a label made here. The row measures whatever comes back.
    /// </summary>
    private static GameObject? SetButtonText(GameObject go, string text)
    {
        try
        {
            // TABCAPS1: the label the clone shows, not merely the first TMP in it. A donor can carry
            // inactive TMPs (a counter, a second caption a state shows); writing the caption into one
            // of those leaves the visible button blank.
            var tmp = PrimaryLabel(go, out _);
            if (tmp != null)
            {
                tmp.text = text ?? "";
                return tmp.gameObject;
            }
        }
        catch { /* */ }
        return MakeLabel(go, text, Vector2.zero, 22f);
    }

    /// <summary>
    /// TABCAPS1: a button's caption TMP — the first one that is enabled and whose own object and
    /// parents up to <paramref name="root"/> are active (the clone itself may be inactive while it is
    /// built); the first TMP at all when none is. <paramref name="count"/> is how many TMPs it holds.
    /// </summary>
    private static TextMeshProUGUI? PrimaryLabel(GameObject root, out int count)
    {
        count = 0;
        TextMeshProUGUI? first = null;
        try
        {
            var all = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (all == null) return null;
            count = all.Length;
            for (int i = 0; i < all.Length; i++)
            {
                var tmp = all[i];
                if (tmp == null) continue;
                first ??= tmp;
                bool enabled = false;
                try { enabled = tmp.enabled; } catch { enabled = false; }
                if (enabled && SubtreeActive(tmp.gameObject, root)) return tmp;
            }
        }
        catch { /* */ }
        return first;
    }

    /// <summary>The object and every parent up to (and including) <paramref name="root"/> are activeSelf.</summary>
    private static bool SubtreeActive(GameObject go, GameObject root)
    {
        try
        {
            var t = go.transform;
            var stop = root.transform;
            for (int guard = 0; t != null && guard < 64; guard++)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t == stop) return true;
                t = t.parent;
            }
        }
        catch { return false; }
        return true;
    }

    // ---- TABCAPS1: verify and repair the workspace strip's tab captions ------------------------

    /// <summary>The buttons of a strip panel in row order (the tabs, in <see cref="ModsTabStrip.Order"/>).</summary>
    private static List<GameObject> StripButtons(UnityPanel panel)
    {
        var buttons = new List<GameObject>();
        panel.ForEachRow((go, kind, _) =>
        {
            if (go != null && string.Equals(kind, "button", StringComparison.Ordinal)) buttons.Add(go);
        });
        return buttons;
    }

    /// <summary>One tab button's caption as it would draw now.</summary>
    private static WorkspaceTabCaptions.Observation ObserveTabCaption(GameObject button, string tab, string intended)
    {
        var tmp = PrimaryLabel(button, out int count);
        if (tmp == null) return new WorkspaceTabCaptions.Observation(tab, intended, false, false, false, "", false, false, 0f, "", count);
        bool active = SubtreeActive(tmp.gameObject, button);
        bool enabled = false;
        string text = "";
        string fontName = "";
        bool fontAlive = false;
        bool glyphs = false;
        float alpha = 0f;
        try { enabled = tmp.enabled; } catch { /* */ }
        try { text = tmp.text ?? ""; } catch { /* */ }
        try
        {
            var font = tmp.font;
            fontAlive = Alive(font) && font != null;
            if (fontAlive)
            {
                fontName = font!.name ?? "";
                var shared = tmp.fontSharedMaterial;
                if (shared != null && !Alive(shared)) fontAlive = false;
            }
            if (fontAlive)
            {
                glyphs = true;
                foreach (char c in intended)
                {
                    if (char.IsWhiteSpace(c)) continue;
                    bool has = false;
                    try { has = font!.HasCharacter(c, true, true); } catch { has = false; }
                    if (!has) { glyphs = false; break; }
                }
            }
        }
        catch { fontAlive = false; }
        try
        {
            var renderer = tmp.canvasRenderer;
            float rendererAlpha = renderer != null ? renderer.GetAlpha() * renderer.GetColor().a : 1f;
            alpha = tmp.color.a * rendererAlpha;
        }
        catch { alpha = float.NaN; }
        return new WorkspaceTabCaptions.Observation(tab, intended, true, active, enabled, text, fontAlive, glyphs, alpha, fontName, count);
    }

    /// <summary>
    /// Observe every tab of a strip panel; with <paramref name="repair"/>, put a faulty caption back
    /// (activate, enable, rewrite the words, the kit font when the donor's is gone or lacks a glyph,
    /// full alpha) and log <c>Ui: tab caption repaired …</c>. Returns the observations after any repair.
    /// </summary>
    private static List<WorkspaceTabCaptions.Observation> CheckStripCaptions(UnityPanel panel, bool repair)
    {
        var result = new List<WorkspaceTabCaptions.Observation>();
        var buttons = StripButtons(panel);
        var intended = ModsTabStrip.CaptionsFor(buttons.Count);
        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            string tab = i < ModsTabStrip.Order.Length && buttons.Count == ModsTabStrip.Order.Length
                ? ModsTabStrip.Order[i].ToString() : "button" + i;
            string want = i < intended.Length ? intended[i] : "";
            var seen = ObserveTabCaption(button, tab, want);
            var faults = WorkspaceTabCaptions.Faults(seen);
            if (repair && faults.Count > 0)
            {
                RepairTabCaption(button, seen, faults);
                string line = WorkspaceTabCaptions.RepairLine(seen, faults);
                WorkspaceTabCaptions.NoteRepair(line);
                Plugin.Logger?.LogWarning(line);
                seen = ObserveTabCaption(button, tab, want);
            }
            result.Add(seen);
        }
        return result;
    }

    private static void RepairTabCaption(GameObject button, in WorkspaceTabCaptions.Observation seen, List<string> faults)
    {
        try
        {
            var tmp = PrimaryLabel(button, out _);
            if (tmp == null)
            {
                var made = MakeLabel(button, seen.Intended, Vector2.zero, 22f);
                PrepareStripButtonCaption(made);
                return;
            }
            // Activate the label's own chain inside the button.
            var t = tmp.transform;
            for (int guard = 0; t != null && guard < 64; guard++)
            {
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                if (t == button.transform) break;
                t = t.parent;
            }
            if (!tmp.enabled) tmp.enabled = true;
            if (!string.Equals(tmp.text, seen.Intended, StringComparison.Ordinal)) tmp.text = seen.Intended;
            if (faults.Contains(WorkspaceTabCaptions.FaultFont) || faults.Contains(WorkspaceTabCaptions.FaultGlyphs))
                ApplyCachedFont(tmp);
            if (faults.Contains(WorkspaceTabCaptions.FaultTransparent))
            {
                var c = tmp.color;
                tmp.color = new Color(c.r, c.g, c.b, 1f);
                try { tmp.canvasRenderer.SetAlpha(1f); } catch { /* */ }
            }
            try { tmp.SetAllDirty(); } catch { /* */ }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: tab caption repair threw: " + e.Message);
        }
    }

    /// <summary>
    /// TABCAPS1 readback (<c>mods_ui tabs.stripCaptions</c>): the shown (else the last) strip's tabs,
    /// intended against rendered text, without repairing anything.
    /// </summary>
    internal static Dictionary<string, object?> DescribeStripCaptions()
    {
        UnityPanel? strip = null;
        for (int i = ShownPanels.Count - 1; i >= 0; i--)
        {
            var panel = ShownPanels[i];
            if (panel == null || !panel.Alive || !panel.IsStrip) continue;
            strip = panel;
            break;
        }
        strip ??= _lastStrip != null && _lastStrip.Alive ? _lastStrip : null;
        var tabs = new List<Dictionary<string, object?>>();
        bool visible = false;
        if (strip != null)
        {
            try { visible = strip.Visible; } catch { /* */ }
            foreach (var seen in CheckStripCaptions(strip, repair: false)) tabs.Add(WorkspaceTabCaptions.Readback(seen));
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["strip"] = strip != null,
            ["visible"] = visible,
            ["tabs"] = tabs,
            ["repairs"] = WorkspaceTabCaptions.Repairs,
            ["lastRepair"] = WorkspaceTabCaptions.LastRepair,
            ["residentDonor"] = WorkspaceTabDonors.ResidentKind,
        };
    }

    /// <summary>The strip panel shown last (the readback reads it while hidden too); dropped on release.</summary>
    private static UnityPanel? _lastStrip;

    /// <summary>A strip shown this frame is checked again on the next one, after the clone's state transitions have run.</summary>
    private static UnityPanel? _stripRecheck;

    private static void StripRecheckPump()
    {
        try { Plugin.Frame -= StripRecheckPump; } catch { /* */ }
        var panel = _stripRecheck;
        _stripRecheck = null;
        if (panel == null || !panel.Alive || !panel.Visible) return;
        try { CheckStripCaptions(panel, repair: true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tab caption recheck threw: " + e.Message); }
    }

    private static void VerifyStripOnShow(UnityPanel host)
    {
        _lastStrip = host;
        try { CheckStripCaptions(host, repair: true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tab caption check threw: " + e.Message); }
        if (_stripRecheck == null)
        {
            try { Plugin.Frame += StripRecheckPump; } catch { return; }
        }
        _stripRecheck = host;
    }

    private static void EnsurePointerEvents(GameObject go, out object? enter, out object? exit)
    {
        enter = null;
        exit = null;
        try
        {
            var trigger = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
            if (trigger == null) return;
            var enterEntry = NewTrigger(EventTriggerType.PointerEnter);
            var exitEntry = NewTrigger(EventTriggerType.PointerExit);
            if (enterEntry != null)
            {
                try { trigger.triggers.Add(enterEntry); } catch { /* */ }
                enter = enterEntry.callback;
            }

            if (exitEntry != null)
            {
                try { trigger.triggers.Add(exitEntry); } catch { /* */ }
                exit = exitEntry.callback;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: EventTrigger: " + e.Message);
        }
    }

    private static EventTrigger.Entry? NewTrigger(EventTriggerType type)
    {
        try
        {
            var entry = new EventTrigger.Entry();
            entry.eventID = type;
            return entry;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Editor controls (evidence 176).
    //
    // A widget is not the component; it is the component plus the objects it draws through. Every
    // check this module had was a check on the component — does it exist, does it hold a value,
    // does its event fire — and all of them passed on three controls that did not exist as far as
    // the player was concerned. What follows builds the objects, and then asserts the references
    // that make the control operable before the row is allowed to keep it.
    // ---------------------------------------------------------------------------------------

    /// <summary>Side of the checkbox: the click target, and the only part of a toggle on screen.</summary>
    private const float ToggleBoxSize = 22f;

    /// <summary>Side of the mark inside the box, whose alpha <c>Toggle.PlayEffect</c> drives.</summary>
    private const float ToggleMarkSize = 12f;

    /// <summary>Thickness of a slider's track and of its fill.</summary>
    private const float SliderTrackHeight = 6f;

    /// <summary>Side of a slider's handle, and therefore twice the inset its travel needs.</summary>
    private const float SliderHandleSize = 16f;

    /// <summary>Height of a text field inside its row's first line.</summary>
    private const float InputHeight = 28f;

    /// <summary>Inset of a field's text viewport inside the field's own frame.</summary>
    private const float InputPadX = 8f;

    /// <summary>Vertical inset of a field's text viewport inside the field's own frame.</summary>
    private const float InputPadY = 4f;

    /// <summary>
    /// The most transforms a vanilla donor may carry before it stops being a widget and starts
    /// being somebody else's whole settings row. See <see cref="IsWidgetSized"/>.
    /// </summary>
    private const int MaxDonorTransforms = 12;

    /// <summary>
    /// The most transforms a vanilla <b>button</b> donor may carry. Larger than
    /// <see cref="MaxDonorTransforms"/> because a button row is the whole row rather than a 180 px
    /// column, so a donor with a frame, a glow, an icon and a caption is still a button; small
    /// enough that somebody's whole view cannot be cloned into one row.
    /// </summary>
    private const int MaxButtonDonorTransforms = 24;

    private static Toggle? _toggleDonor;
    private static Slider? _sliderDonor;
    private static TMP_InputField? _inputDonor;
    private static SimpleStunButton? _buttonDonor;
    private static bool _toggleDonorSearched;
    private static bool _sliderDonorSearched;
    private static bool _inputDonorSearched;
    private static bool _buttonDonorSearched;
    private static Sprite? _frameSprite;
    private static bool _frameSpriteSearched;
    /// <summary>
    /// Whether the once-per-panel-build <c>source=</c> line has already been said for this control
    /// type. Re-armed by <see cref="ResetSceneCaches"/> with the donor caches whose answer it
    /// reports — see the note there (R45 finding 27).
    /// </summary>
    private static bool _toggleSourceLogged;
    private static bool _sliderSourceLogged;
    private static bool _inputSourceLogged;
    private static bool _buttonSourceLogged;

    /// <summary>
    /// Forget every scene lookup this class caches. Called at the top of
    /// <see cref="CreatePanel"/>, which is the one moment the runtime knows a fresh sweep is worth
    /// paying for: the alternative is a <c>FindObjectsOfType</c> per control per row, and the
    /// settings panel has 47 of them. The cached objects are re-validated with <see cref="Alive"/>
    /// anyway, so this is a freshness reset and not a correctness one — except that a donor that
    /// only exists on certain screens is looked for again each time a panel is built.
    ///
    /// <para>
    /// <b>The three source latches are reset here too (R45 finding 27).</b> They were not, and that
    /// made the one line that answers the donor question a fact about the <i>first</i> panel built
    /// in the process rather than about this one. Since the donor caches above are exactly what
    /// that line reports, a reset that cleared them and left the latches set could only produce a
    /// stale answer: a panel built during loading or on the main menu, where the vanilla settings
    /// widgets do not exist, would record <c>source=built</c> for the life of the Core generation
    /// and every later panel's real answer would be silent. It also matters in the other direction
    /// now that the clone rejection has a fallback — a latch that does not re-arm hides the
    /// <c>built-after-clone-rejected</c> line that says a donor on this screen is bad.
    /// </para>
    /// </summary>
    private static void ResetSceneCaches()
    {
        _toggleDonor = null;
        _sliderDonor = null;
        _inputDonor = null;
        _buttonDonor = null;
        _toggleDonorSearched = false;
        _sliderDonorSearched = false;
        _inputDonorSearched = false;
        _buttonDonorSearched = false;
        // A found frame sprite is a game asset and stays valid (FrameSprite re-checks Alive); a
        // miss is looked for again, since the screen that has it may be the next one.
        if (!Alive(_frameSprite))
        {
            _frameSprite = null;
            _frameSpriteSearched = false;
        }
        _toggleSourceLogged = false;
        _sliderSourceLogged = false;
        _inputSourceLogged = false;
        _buttonSourceLogged = false;
    }

    /// <summary>
    /// The three references that make a <c>Toggle</c> operable, per evidence 176's table: the
    /// raycastable click target it does not own, and the graphic whose alpha shows its state.
    /// </summary>
    private static bool ToggleOperable(Toggle? t)
    {
        try { return t != null && t.targetGraphic != null && t.graphic != null; }
        catch { return false; }
    }

    /// <summary>
    /// The rects <c>Slider.UpdateVisuals</c> drives, plus something to click. Without all three a
    /// slider holds a value and draws nothing to grab.
    /// </summary>
    private static bool SliderOperable(Slider? s)
    {
        try { return s != null && s.fillRect != null && s.handleRect != null && s.targetGraphic != null; }
        catch { return false; }
    }

    /// <summary>
    /// Where accepted characters are written, what the field clips to and parents its caret under,
    /// and the background that lets a click select the field in the first place.
    /// </summary>
    private static bool InputOperable(TMP_InputField? f)
    {
        try { return f != null && f.textComponent != null && f.textViewport != null && f.targetGraphic != null; }
        catch { return false; }
    }

    /// <summary>
    /// Build the checkbox for a toggle row: a cloned vanilla one if an operable widget-sized donor
    /// exists, otherwise one made from the game's frame sprite and the panel's palette — and, when
    /// the donor exists but its clone comes out inert, the built one anyway.
    ///
    /// <para>
    /// The control is built <b>inactive</b> and activated once at the end. A component added to an
    /// already-active <c>GameObject</c> runs <c>OnEnable</c> immediately, and <c>Toggle</c> applies
    /// its checkmark's alpha there — with <c>graphic</c> still null. That build-order fact is worth
    /// as much as the references themselves (evidence 176), and it holds on the fallback path for
    /// free: <see cref="BuildToggle"/> starts from <see cref="NewControl"/>, which is inactive, and
    /// the rejected clone is destroyed without ever being activated.
    /// </para>
    /// </summary>
    private static GameObject? MakeToggle(
        GameObject row, in RowColumns columns, bool initial, out Toggle? toggle, out ControlSource source,
        bool composed = false)
    {
        toggle = null;
        source = ControlSource.None;
        var donor = composed ? null : ToggleDonor()?.gameObject;
        // Copied out of the `in` parameter because a closure cannot capture one. The column
        // geometry is a value and both attempts must be placed into the same one.
        var cols = columns;
        var attempt = ControlBuildPlan.Resolve(
            () => CloneDonor(donor, row, "toggle", cols, RowHeight),
            () => BuildToggle(row, cols),
            go => ToggleOperable(FindIn<Toggle>(go)),
            (go, wasClone) => DropUnoperableControl("toggle", go, wasClone));

        LogControlSource(ref _toggleSourceLogged, "settings toggle", "toggle", attempt.Source);
        source = attempt.Source;
        var built = attempt.Control;
        if (built == null) return null;

        var found = FindIn<Toggle>(built);
        try { found!.toggleTransition = Toggle.ToggleTransition.None; } catch { /* */ }
        try { found!.isOn = initial; } catch { /* */ }
        Activate(built);
        toggle = found;
        return built;
    }

    private static GameObject? BuildToggle(GameObject row, in RowColumns columns)
    {
        GameObject? go = null;
        try
        {
            go = NewControl(row, "toggle", columns, columns.ControlWidth, RowHeight);

            // The box is the click target. A Toggle owns no graphic, so this is the whole reason
            // a pointer can reach it; the Toggle component itself sits on the parent and receives
            // the click that the box's raycast bubbles up to it, which is the vanilla shape.
            var box = new GameObject("box");
            box.transform.SetParent(go.transform, false);
            PlaceRect(box, new Vector2(-(columns.ControlWidth - ToggleBoxSize) * 0.5f, 0f), ToggleBoxSize, ToggleBoxSize);
            var boxImage = TryAdd<Image>(box);
            if (boxImage == null) { TryDestroyImmediate(go); return null; }
            ApplyFrame(boxImage, new Color(0.16f, 0.16f, 0.20f, 0.95f));
            try { boxImage.raycastTarget = true; } catch { /* */ }

            var mark = new GameObject("mark");
            mark.transform.SetParent(box.transform, false);
            PlaceRect(mark, Vector2.zero, ToggleMarkSize, ToggleMarkSize);
            var markImage = TryAdd<Image>(mark);
            if (markImage == null) { TryDestroyImmediate(go); return null; }
            try { markImage.sprite = null; } catch { /* */ }
            try { markImage.color = new Color(0.93f, 0.86f, 0.62f, 1f); } catch { /* */ }
            // The mark must not eat the click meant for the box it sits on top of.
            try { markImage.raycastTarget = false; } catch { /* */ }

            var toggle = TryAdd<Toggle>(go);
            if (toggle == null) { TryDestroyImmediate(go); return null; }
            try { toggle.targetGraphic = boxImage; } catch { /* */ }
            try { toggle.graphic = markImage; } catch { /* */ }
            try { toggle.group = null; } catch { /* */ }
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a toggle threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>
    /// Build the bar for a slider row. Clone-first like the others; the built one is a raycastable
    /// track with a fill and a handle, each in a parent inset by half a handle so the travel stops
    /// where the track does instead of hanging half off each end.
    /// </summary>
    private static GameObject? MakeSlider(
        GameObject row, in RowColumns columns, float min, float max, float initial, out Slider? slider,
        out ControlSource source, bool composed = false)
    {
        slider = null;
        source = ControlSource.None;
        var donor = composed ? null : SliderDonor()?.gameObject;
        var cols = columns;
        var attempt = ControlBuildPlan.Resolve(
            () => CloneDonor(donor, row, "slider", cols, RowHeight),
            () => BuildSlider(row, cols),
            go => SliderOperable(FindIn<Slider>(go)),
            (go, wasClone) => DropUnoperableControl("slider", go, wasClone));

        LogControlSource(ref _sliderSourceLogged, "settings slider", "slider", attempt.Source);
        source = attempt.Source;
        var built = attempt.Control;
        if (built == null) return null;

        var found = FindIn<Slider>(built);
        // Explicit, never inherited. A cloned donor may have been authored bottom-to-top, and a
        // vertical settings slider in a horizontal column is not a cosmetic difference — the
        // player would be dragging along an axis the control does not read (evidence 176).
        try { found!.direction = Slider.Direction.LeftToRight; } catch { /* */ }
        try { found!.wholeNumbers = false; } catch { /* */ }
        try { found!.minValue = min; } catch { /* */ }
        try { found!.maxValue = max; } catch { /* */ }
        try { found!.value = initial; } catch { /* */ }
        Activate(built);
        slider = found;
        return built;
    }

    private static GameObject? BuildSlider(GameObject row, in RowColumns columns)
    {
        GameObject? go = null;
        try
        {
            go = NewControl(row, "slider", columns, columns.ControlWidth, RowHeight);
            float inset = SliderHandleSize * 0.5f;

            // The track, not the handle, is the raycast target: a click anywhere along the bar
            // should move the value, which is what a player expects and what a handle-only
            // raycast would refuse.
            var track = new GameObject("track");
            track.transform.SetParent(go.transform, false);
            StretchWide(track, 0f, SliderTrackHeight);
            var trackImage = TryAdd<Image>(track);
            if (trackImage == null) { TryDestroyImmediate(go); return null; }
            try { trackImage.sprite = null; } catch { /* */ }
            try { trackImage.color = new Color(1f, 1f, 1f, 0.16f); } catch { /* */ }
            try { trackImage.raycastTarget = true; } catch { /* */ }

            var fillArea = new GameObject("fill-area");
            fillArea.transform.SetParent(go.transform, false);
            StretchWide(fillArea, inset, SliderTrackHeight);
            var fill = new GameObject("fill");
            fill.transform.SetParent(fillArea.transform, false);
            // Slider.UpdateVisuals overwrites this rect's anchors every frame it changes; what it
            // needs from us is a rect with a parent it can measure against, which is the only
            // reason the fill and the handle each have a container of their own.
            DrivenRect(fill, Vector2.zero);
            var fillImage = TryAdd<Image>(fill);
            if (fillImage == null) { TryDestroyImmediate(go); return null; }
            try { fillImage.sprite = null; } catch { /* */ }
            try { fillImage.color = new Color(0.80f, 0.70f, 0.45f, 0.85f); } catch { /* */ }
            try { fillImage.raycastTarget = false; } catch { /* */ }

            var handleArea = new GameObject("handle-area");
            handleArea.transform.SetParent(go.transform, false);
            StretchWide(handleArea, inset, SliderHandleSize);
            var handle = new GameObject("handle");
            handle.transform.SetParent(handleArea.transform, false);
            DrivenRect(handle, new Vector2(SliderHandleSize, 0f));
            var handleImage = TryAdd<Image>(handle);
            if (handleImage == null) { TryDestroyImmediate(go); return null; }
            ApplyFrame(handleImage, new Color(0.86f, 0.82f, 0.70f, 1f));
            try { handleImage.raycastTarget = true; } catch { /* */ }

            var slider = TryAdd<Slider>(go);
            if (slider == null) { TryDestroyImmediate(go); return null; }
            try { slider.direction = Slider.Direction.LeftToRight; } catch { /* */ }
            try { slider.fillRect = fill.GetComponent<RectTransform>(); } catch { /* */ }
            try { slider.handleRect = handle.GetComponent<RectTransform>(); } catch { /* */ }
            try { slider.targetGraphic = handleImage; } catch { /* */ }
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a slider threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>
    /// Build the text field for an input row. Clone-first; the built one is a framed background
    /// that is a raycast target, a <c>RectMask2D</c> viewport, and a TMP child in the game's font.
    ///
    /// <para>
    /// The inactive build matters most here: <c>TMP_InputField</c> creates its caret and its
    /// selection mesh in <c>OnEnable</c>, and an <c>OnEnable</c> that runs before
    /// <c>textComponent</c> and <c>textViewport</c> are set produces a field with no caret that
    /// silently swallows every keystroke.
    /// </para>
    /// </summary>
    private static GameObject? MakeInput(
        GameObject row, in RowColumns columns, string initial, out TMP_InputField? field,
        out ControlSource source, bool composed = false, string? placeholder = null, bool searchField = false)
    {
        field = null;
        source = ControlSource.None;
        var donor = composed ? null : InputDonor()?.gameObject;
        var cols = columns;
        var attempt = ControlBuildPlan.Resolve(
            () => CloneDonor(donor, row, "input", cols, InputHeight),
            () => BuildInput(row, cols, initial, placeholder, searchField),
            go => InputOperable(FindIn<TMP_InputField>(go)),
            (go, wasClone) => DropUnoperableControl("input", go, wasClone));

        LogControlSource(ref _inputSourceLogged, "settings input", "input", attempt.Source);
        source = attempt.Source;
        var built = attempt.Control;
        if (built == null) return null;

        var found = FindIn<TMP_InputField>(built);
        try { found!.lineType = TMP_InputField.LineType.SingleLine; } catch { /* */ }
        // TEXTAREA1: a cloned vanilla field keeps its donor's settings. A typed value is data, so
        // rich text is off here as on the built path, and the donor's character limit is not ours.
        try { found!.richText = false; } catch { /* */ }
        try { if (found!.textComponent != null) found.textComponent.richText = false; } catch { /* */ }
        try { found!.characterLimit = 0; } catch { /* */ }
        try { found!.text = initial ?? ""; } catch { /* */ }
        Activate(built);
        // Written once more after the activation that builds the caret and the mesh, because the
        // whole point of activating last is that OnEnable rebuilds what it finds.
        try { if (found!.text != (initial ?? "")) found!.text = initial ?? ""; } catch { /* */ }
        field = found;
        return built;
    }

    /// <summary>
    /// Remember the field so a later click can Select + ActivateInputField. Keystrokes are
    /// sampled from <c>Keyboard.current</c> through <see cref="KeyEdgeTracker"/> on the
    /// shown-input pump; this build has no C# <c>Keyboard.onTextInput</c> and no
    /// <c>shouldActivateOnSelect</c>.
    ///
    /// <para>
    /// <c>readOnly = true</c> leaves TMP's native key processing inert so the pump is
    /// the only writer — the same sole-writer rule as
    /// <see cref="MenuPanelLayout.UguiScrollSensitivity"/> = 0 for the wheel.
    /// <c>onFocusSelectAll = false</c> so a rebuild that restores focus cannot replace
    /// the whole field with the next character (live <c>bb</c> then <c>ll</c>).
    /// </para>
    /// </summary>
    private static void ArmTextField(GameObject row, TMP_InputField field)
    {
        if (row == null || field == null) return;
        try { field.readOnly = true; } catch { /* */ }
        try { field.onFocusSelectAll = false; } catch { /* */ }
        var armed = new ArmedField { Field = field };
        // SEARCHCLEAR1: the clear button is attached before the field is armed.
        try
        {
            var clear = field.transform.Find(MenuRowStyle.SearchClearName);
            if (clear != null)
            {
                armed.Clear = clear.gameObject;
                armed.ClearText = clear.GetComponent<TextMeshProUGUI>();
            }
        }
        catch { /* a field without a clear button */ }
        _armedFields.Add(armed);
        AttachOwnedCaret(armed);
        SyncSearchHint(field);
    }

    private static GameObject? BuildInput(
        GameObject row, in RowColumns columns, string initial, string? placeholder = null, bool searchField = false)
    {
        GameObject? go = null;
        try
        {
            go = NewControl(row, "input", columns, columns.ControlWidth, InputHeight);

            // Without this the field is not a raycast target, so it can never become the
            // EventSystem's selected object and can never be focused — the defect underneath the
            // defect (evidence 176).
            var background = TryAdd<Image>(go);
            if (background == null) { TryDestroyImmediate(go); return null; }
            if (searchField)
            {
                try { background.sprite = null; } catch { /* */ }
                try
                {
                    background.color = new Color(
                        MenuRowStyle.SearchR, MenuRowStyle.SearchG, MenuRowStyle.SearchB, MenuRowStyle.SearchA);
                }
                catch { /* */ }
            }
            else
            {
                ApplyFrame(background, new Color(0.12f, 0.12f, 0.16f, 0.95f));
            }
            try { background.raycastTarget = true; } catch { /* */ }
            // Field-level clip so an owned caret cannot paint past the frame even
            // if its height or Y is wrong. The viewport keeps its own mask for text.
            TryAdd<RectMask2D>(go);

            var viewport = new GameObject("viewport");
            viewport.transform.SetParent(go.transform, false);
            InsetStretch(viewport, InputPadX, InputPadY);
            TryAdd<RectMask2D>(viewport);

            var textGo = new GameObject("text");
            textGo.transform.SetParent(viewport.transform, false);
            Stretch(textGo);
            var tmp = TryAdd<TextMeshProUGUI>(textGo);
            if (tmp == null) { TryDestroyImmediate(go); return null; }
            try { tmp.text = initial ?? ""; } catch { /* */ }
            try { tmp.fontSize = 18f; } catch { /* */ }
            ApplyHorizontalAlign(tmp, left: true);
            try { tmp.color = Color.white; } catch { /* */ }
            // A config value is data, not markup: "<b>" typed into a field must stay four
            // characters and not silently become a formatting tag that cannot be read back.
            try { tmp.richText = false; } catch { /* */ }
            // The RectMask2D does the clipping; TMP must not also ellipsize what the player typed.
            try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
            try { tmp.raycastTarget = false; } catch { /* */ }
            ApplyCachedFont(tmp);

            var field = TryAdd<TMP_InputField>(go);
            if (field == null) { TryDestroyImmediate(go); return null; }
            try { field.textViewport = viewport.GetComponent<RectTransform>(); } catch { /* */ }
            try { field.textComponent = tmp; } catch { /* */ }
            try { field.targetGraphic = background; } catch { /* */ }
            try { field.lineType = TMP_InputField.LineType.SingleLine; } catch { /* */ }
            try { field.richText = false; } catch { /* */ }
            try { field.customCaretColor = true; } catch { /* */ }
            try { field.caretColor = Color.white; } catch { /* */ }
            // Width 0 so TMP's own GenerateCaret quad cannot double-draw. The
            // visible bar is the framework Image at UiOwnedCaret.Name.
            try { field.caretWidth = 0; } catch { /* */ }
            try { field.text = initial ?? ""; } catch { /* */ }
            if (!string.IsNullOrEmpty(placeholder))
                AttachPlaceholder(go, field, placeholder!);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: building a text field threw: " + e.Message);
            TryDestroyImmediate(go);
            return null;
        }
    }

    /// <summary>
    /// Empty-field hint: the word on the left and a small magnifying-glass mark on the
    /// right. These are ordinary child graphics, not TMP's <c>placeholder</c> slot —
    /// assigning that Graphic lets <c>UpdateLabel</c> disable it, which is why the
    /// previous empty box drew nothing. Shown while <c>field.text</c> is empty and
    /// hidden from the typed-key pump once the player types. No extra UnityAction:
    /// a Core listener on <c>onValueChanged</c> would pin the collectible ALC.
    /// </summary>
    private static void AttachSearchHints(GameObject fieldGo, TMP_InputField field, string initial, string? placeholder = null)
    {
        if (fieldGo == null || field == null) return;
        GameObject? root = null;
        try
        {
            root = new GameObject(MenuRowStyle.SearchHintName);
            root.transform.SetParent(fieldGo.transform, false);
            Stretch(root);

            AttachPlaceholder(root, field, placeholder ?? MenuRowStyle.SearchPlaceholder, MenuRowStyle.SearchGlyphWidth);
            AttachSearchGlyph(root, field);
            bool show = MenuRowStyle.SearchHintVisible(initial);
            try { root.SetActive(show); } catch { /* */ }
            try { root.transform.SetAsLastSibling(); } catch { /* */ }
        }
        catch
        {
            TryDestroyImmediate(root);
        }
    }

    private static void AttachPlaceholder(GameObject parent, TMP_InputField field, string text, float rightInset = 0f)
    {
        GameObject? go = null;
        try
        {
            go = new GameObject(rightInset > 0f ? MenuRowStyle.SearchHintWordName : "placeholder");
            go.transform.SetParent(parent.transform, false);
            InsetStretch(go, InputPadX, InputPadY);
            if (rightInset > 0f)
            {
                try
                {
                    var rt = go.GetComponent<RectTransform>();
                    if (rt != null)
                        rt.offsetMax = new Vector2(-(InputPadX + rightInset), -InputPadY);
                }
                catch { /* */ }
            }
            var tmp = TryAdd<TextMeshProUGUI>(go);
            if (tmp == null)
            {
                TryDestroyImmediate(go);
                return;
            }
            try { tmp.text = text; } catch { /* */ }
            try { tmp.fontSize = 18f; } catch { /* */ }
            ApplyHorizontalAlign(tmp, left: true);
            try
            {
                tmp.color = new Color(
                    MenuRowStyle.SearchPlaceholderR, MenuRowStyle.SearchPlaceholderG,
                    MenuRowStyle.SearchPlaceholderB, MenuRowStyle.SearchPlaceholderA);
            }
            catch { /* */ }
            try { tmp.richText = false; } catch { /* */ }
            // Overflow, not Ellipsis: the word TMP is InsetStretch'd into InputHeight
            // (28) minus 2*InputPadY (8) = 20 px at fontSize 18. TMP Ellipsis drops a
            // line that does not fit, so text stays "Search" and the mesh is empty —
            // which is why mods_ui reported renderedPlaceholder while the crop was
            // blank. The typed-text TMP in this same inset already uses Overflow.
            try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
            try { tmp.enableWordWrapping = false; } catch { /* */ }
            try { tmp.raycastTarget = false; } catch { /* */ }
            try { tmp.enableAutoSizing = false; } catch { /* */ }
            try { tmp.fontStyle = FontStyles.Normal; } catch { /* */ }
            try
            {
                var placeholderRt = tmp.rectTransform;
                if (placeholderRt != null) placeholderRt.localScale = Vector3.one;
            }
            catch { /* */ }
            ApplyCachedFont(tmp);
            try { tmp.ForceMeshUpdate(); } catch { /* */ }
            // Not assigned to field.placeholder: TMP OnEnable/UpdateLabel would set
            // Graphic.enabled from m_Text and can leave this drawing nothing.
            _ = field;
        }
        catch
        {
            TryDestroyImmediate(go);
        }
    }

    private static void AttachSearchGlyph(GameObject parent, TMP_InputField field)
    {
        GameObject? go = null;
        try
        {
            go = new GameObject(MenuRowStyle.SearchHintGlyphName);
            go.transform.SetParent(parent.transform, false);
            PlaceSearchGlyphRect(go);
            var font = FindFont();
            string glyph = MenuRowStyle.PickSearchGlyph(s => FontHasString(font, s));
            if (!string.IsNullOrEmpty(glyph))
            {
                var tmp = TryAdd<TextMeshProUGUI>(go);
                if (tmp == null)
                {
                    TryDestroyImmediate(go);
                    return;
                }
                try { tmp.text = glyph; } catch { /* */ }
                try { tmp.fontSize = 16f; } catch { /* */ }
                try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Right; } catch { /* */ }
                try
                {
                    tmp.color = new Color(
                        MenuRowStyle.SearchPlaceholderR, MenuRowStyle.SearchPlaceholderG,
                        MenuRowStyle.SearchPlaceholderB, MenuRowStyle.SearchPlaceholderA);
                }
                catch { /* */ }
                try { tmp.richText = false; } catch { /* */ }
                try { tmp.enableWordWrapping = false; } catch { /* */ }
                try { tmp.raycastTarget = false; } catch { /* */ }
                ApplyCachedFont(tmp);
                try { tmp.ForceMeshUpdate(); } catch { /* */ }
            }
            else
            {
                // No magnifier in the font and none among the game's sprites (scan of 3090 loaded
                // sprites, 2026-10-04): draw nothing. The placeholder word already says Search, and a
                // mark built from quads read as a box with a tail (user review).
                TryDestroyImmediate(go);
                return;
            }
            _ = field;
        }
        catch
        {
            TryDestroyImmediate(go);
        }
    }

    private static void PlaceSearchGlyphRect(GameObject go)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = new Vector2(-InputPadX, 0f);
            rt.sizeDelta = new Vector2(MenuRowStyle.SearchGlyphWidth, 0f);
        }
        catch { /* */ }
    }

    private static bool FontHasString(TMP_FontAsset? font, string s)
    {
        if (font == null || string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            try
            {
                if (!font.HasCharacter(s[i])) return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static void SyncSearchHint(TMP_InputField field)
    {
        if (field == null) return;
        // SEARCHCLEAR1: the clear button is the hint's opposite: shown while the box has text.
        try { SyncSearchClear(field, field.text); } catch { /* */ }
        GameObject? hint = null;
        try
        {
            var t = field.transform.Find(MenuRowStyle.SearchHintName);
            hint = t == null ? null : t.gameObject;
        }
        catch { hint = null; }
        if (hint == null) return;
        bool show = false;
        try { show = MenuRowStyle.SearchHintVisible(field.text); } catch { show = false; }
        try { hint.SetActive(show); } catch { /* */ }
        if (show)
        {
            try { hint.transform.SetAsLastSibling(); } catch { /* */ }
        }
    }

    /// <summary>
    /// Parent Image draws first; later siblings draw on top. The hint root must be
    /// the last child so it is not covered by the viewport or any later caret.
    /// </summary>
    private static void BringSearchHintToFront(GameObject fieldGo)
    {
        if (fieldGo == null) return;
        try
        {
            var t = fieldGo.transform.Find(MenuRowStyle.SearchHintName);
            t?.SetAsLastSibling();
        }
        catch { /* */ }
    }

    private static void NoteOwnedCaretEdit(ArmedField armed)
    {
        if (armed == null) return;
        if (UiOwnedCaret.RestartBlinkOnEdit)
            armed.BlinkOriginMs = Environment.TickCount64;
        armed.LastPlacedCaret = int.MinValue;
        // TEXTEXPAND1: an edit or a caret move can move the text; the editor's bar follows next frames.
        MarkEditorBarDirty(armed);
    }

    private static void AttachOwnedCaret(ArmedField armed)
    {
        if (armed == null || armed.Field == null) return;
        GameObject? go = null;
        try
        {
            var fieldGo = armed.Field.gameObject;
            if (fieldGo == null) return;
            RectTransform? viewport = null;
            try { viewport = armed.Field.textViewport; } catch { viewport = null; }
            // Same inset as the placeholder / typed TMP: the text viewport, not the
            // field root. PlaceControl's field pivot is top-center; the viewport's
            // is center, matching a center-anchored caret.
            Transform parent = viewport != null ? viewport : fieldGo.transform;
            Transform? existing = null;
            try { existing = parent.Find(UiOwnedCaret.Name); } catch { existing = null; }
            if (existing == null)
            {
                try { existing = fieldGo.transform.Find(UiOwnedCaret.Name); } catch { existing = null; }
            }
            go = existing == null ? new GameObject(UiOwnedCaret.Name) : existing.gameObject;
            go.transform.SetParent(parent, false);
            try { go.transform.SetAsLastSibling(); } catch { /* */ }
            try
            {
                if (fieldGo.GetComponent<RectMask2D>() == null)
                    TryAdd<RectMask2D>(fieldGo);
            }
            catch { /* */ }
            try
            {
                var clipHost = parent.gameObject;
                if (clipHost != null && clipHost.GetComponent<RectMask2D>() == null)
                    TryAdd<RectMask2D>(clipHost);
            }
            catch { /* */ }
            var image = go.GetComponent<Image>() ?? TryAdd<Image>(go);
            if (image == null)
            {
                if (existing == null) TryDestroyImmediate(go);
                return;
            }
            try { image.raycastTarget = false; } catch { /* */ }
            try { image.maskable = true; } catch { /* */ }
            try { image.sprite = null; } catch { /* */ }
            try { image.color = Color.white; } catch { /* */ }
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(UiOwnedCaret.WidthPx, UiOwnedCaret.HeightPx);
            try { go.SetActive(false); } catch { /* */ }
            armed.Caret = go;
            armed.CaretGraphic = image;
            armed.Placed = false;
            BringSearchHintToFront(fieldGo);
        }
        catch
        {
            if (go != null && armed.Caret == null) TryDestroyImmediate(go);
        }
    }

    private static void SyncOwnedCarets()
    {
        PruneArmedFields();
        var focused = CaretFocusField();
        long now = Environment.TickCount64;
        for (int i = 0; i < _armedFields.Count; i++)
        {
            var armed = _armedFields[i];
            if (armed == null) continue;
            if (focused == null || !ReferenceEquals(armed.Field, focused))
            {
                HideOwnedCaret(armed);
                continue;
            }

            ShowOwnedCaret(armed, now);
        }
    }

    private static TMP_InputField? CaretFocusField()
    {
        // Owned activation, not TMP isFocused. Cancel on Escape drops isFocused
        // while the box is still awaiting input; a click-outside clears owned
        // focus and must hide the caret even if TMP has not deselected yet.
        if (_ownedFocusField == null) return null;
        return FindArmed(_ownedFocusField) == null ? null : _ownedFocusField;
    }

    private static void ShowOwnedCaret(ArmedField armed, long nowMs)
    {
        if (armed.Caret == null || armed.CaretGraphic == null)
            AttachOwnedCaret(armed);
        if (armed.Caret == null || armed.CaretGraphic == null) return;

        var state = ReadFieldState(armed.Field);
        // TEXTEXPAND1: a held view (wheel, scroll bar) lasts until the caret or the text moves.
        if (armed.ViewHeld && (state.Caret != armed.LastPlacedCaret || !string.Equals(state.Text, armed.LastPlacedText, StringComparison.Ordinal)))
            armed.ViewHeld = false;
        // Layout can move the field without changing its text or insertion index.
        PlaceOwnedCaret(armed, state);
        armed.LastPlacedCaret = state.Caret;
        armed.LastPlacedText = state.Text;

        // TEXTUNDO1 polish: a read-only field (the read-only editor) draws no caret; it is still placed,
        // so the view follows the arrow keys and Ctrl+A / Ctrl+C keep working.
        bool lit = UiOwnedCaret.IsLit(true, nowMs, armed.BlinkOriginMs, armed.ReadOnly) && !armed.CaretOutOfView;
        try { armed.Caret.SetActive(true); } catch { /* */ }
        try { armed.CaretGraphic.enabled = true; } catch { /* */ }
        try
        {
            var c = armed.CaretGraphic.color;
            c.a = lit ? 1f : 0f;
            armed.CaretGraphic.color = c;
        }
        catch { /* */ }
    }

    private static void PlaceOwnedCaret(ArmedField armed, UiTextEditState state)
    {
        var field = armed.Field;
        var caretGo = armed.Caret;
        if (field == null || caretGo == null) return;
        TMP_Text? tmp = null;
        try { tmp = field.textComponent; } catch { tmp = null; }
        MeasureOwnedCaret(armed, tmp, state.Text, state.Caret, out float localX, out float midY, out float height);

        RectTransform? textRt = null;
        try { textRt = tmp != null ? tmp.rectTransform : field.textViewport; } catch { textRt = null; }

        Vector3 world;
        try
        {
            world = textRt != null
                ? textRt.TransformPoint(new Vector3(localX, midY, 0f))
                : field.transform.TransformPoint(new Vector3(InputPadX, -InputHeight * 0.5f, 0f));
        }
        catch
        {
            world = field.transform.TransformPoint(new Vector3(InputPadX, -InputHeight * 0.5f, 0f));
        }

        var parentRt = caretGo.transform.parent?.GetComponent<RectTransform>()
            ?? field.GetComponent<RectTransform>();
        if (parentRt == null) return;

        Vector3 parentLocal;
        try { parentLocal = parentRt.InverseTransformPoint(world); }
        catch { parentLocal = new Vector3(localX, midY, 0f); }

        // TEXTAREA2: keep the caret's line inside a multi-line field's viewport. TMP_InputField
        // scrolls by its own caret (which PushTmpCaret keeps equal to ours); this is the second
        // line for when it has not: move the text by the overflow, so the caret is drawn on its
        // line and never pinned to an edge away from the text it belongs to.
        armed.CaretOutOfView = false;
        if (textRt != null && tmp != null && IsMultiline(field))
        {
            try
            {
                Rect view = parentRt.rect;
                float half = Math.Min(height, view.height) * 0.5f;
                float shift = UiCaretLayout.ScrollToShow(parentLocal.y - half, parentLocal.y + half, view.yMin, view.yMax);
                if (armed.ViewHeld)
                {
                    // TEXTEXPAND1: the player scrolled away from the caret: leave the text where the
                    // wheel or the bar put it, and hide the caret while its line is out of view.
                    armed.CaretOutOfView = Math.Abs(shift) > 0.5f;
                }
                else if (Math.Abs(shift) > 0.5f)
                {
                    var pos = textRt.anchoredPosition;
                    textRt.anchoredPosition = new Vector2(pos.x, pos.y + shift);
                    TextFieldScrollWrites++;
                    MarkEditorBarDirty(armed);
                    world = textRt.TransformPoint(new Vector3(localX, midY, 0f));
                    parentLocal = parentRt.InverseTransformPoint(world);
                }
            }
            catch { /* the clamp below still keeps the bar inside */ }
        }

        try
        {
            var rt = caretGo.GetComponent<RectTransform>() ?? caretGo.AddComponent<RectTransform>();
            if (!armed.Placed)
            {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
            }
            Rect prect = parentRt.rect;
            float clipH = prect.height;
            try
            {
                var vp = field.textViewport;
                if (vp != null)
                {
                    float vh = vp.rect.height;
                    if (vh > 1f && vh < clipH) clipH = vh;
                }
            }
            catch { /* */ }
            height = UiOwnedCaret.ClampHeight(height, clipH);
            float cy = UiOwnedCaret.ClampCenter(parentLocal.y, height, prect.yMin, prect.yMax);
            // InverseTransformPoint is parent-pivot local; anchoredPosition is
            // offset from the child's anchors. PlaceControl's field is top-pivoted;
            // writing local.y straight into anchoredPosition dropped the bar by
            // half the field height (live: hanging off the bottom edge).
            UiOwnedCaret.AnchoredFromParentLocal(
                parentLocal.x, cy,
                prect.xMin, prect.yMin, prect.xMax, prect.yMax,
                0.5f, 0.5f,
                out float ax, out float ay);
            // TEXTAREA2: an idle focused field writes nothing: the bar's transform and its sibling
            // order are touched only when they would change.
            var anchored = new Vector2(ax, ay);
            var size = new Vector2(UiOwnedCaret.WidthPx, height);
            if (!armed.Placed || (armed.PlacedAnchored - anchored).sqrMagnitude > 0.0001f)
            {
                rt.anchoredPosition = anchored;
                armed.PlacedAnchored = anchored;
                CaretTransformWrites++;
            }
            if (!armed.Placed || (armed.PlacedSize - size).sqrMagnitude > 0.0001f)
            {
                rt.sizeDelta = size;
                armed.PlacedSize = size;
            }
            armed.Placed = true;
            try
            {
                var t = caretGo.transform;
                var p = t.parent;
                if (p != null && t.GetSiblingIndex() != p.childCount - 1) t.SetAsLastSibling();
            }
            catch { /* */ }
        }
        catch { /* */ }
    }

    /// <summary>TEXTAREA2: perf readback counters (process lifetime of this Core generation).</summary>
    internal static long CaretTransformWrites;
    internal static long TextFieldScrollWrites;

    /// <summary>
    /// TEXTAREA1. The caret's point in the text component's local space, from
    /// <see cref="UiCaretLayout"/>: the visual line that holds the caret (line breaks and word
    /// wrap), x from that line's own prefix and the text's horizontal alignment, y from the line
    /// index, the line advance and the vertical alignment, all inside the text rect minus the
    /// component's <c>margin</c> (the settings skin gives free text a left inset and a text area
    /// a top inset). Before this, y was always the rect's center and x the whole prefix's width
    /// from the rect's left edge: right only for a single left-aligned line without margins.
    ///
    /// <para>
    /// Widths are measured, not read from <c>textInfo</c>: the input callback precedes TMP's
    /// glyph refresh, so <c>characterInfo</c> can describe the previous string. A width is the
    /// difference of two <c>GetPreferredValues</c> calls with a visible end mark, so trailing
    /// spaces count and margins cancel. The result is cached until text, caret or rect change.
    /// </para>
    /// </summary>
    private static void MeasureOwnedCaret(
        ArmedField armed, TMP_Text? tmp, string text, int caret, out float localX, out float midY, out float height)
    {
        localX = 0f;
        midY = 0f;
        height = UiOwnedCaret.HeightPx;
        if (tmp == null) return;

        Rect rect;
        Vector4 margin = Vector4.zero;
        bool wrap = false;
        var h = UiTextHAlign.Left;
        var v = UiTextVAlign.Middle;
        try
        {
            rect = tmp.rectTransform.rect;
            localX = rect.xMin;
            midY = UiOwnedCaret.LineCenterY(rect.yMin, rect.yMax);
        }
        catch { return; }
        try { margin = tmp.margin; } catch { margin = Vector4.zero; }
        try { wrap = tmp.enableWordWrapping; } catch { wrap = false; }
        try { h = CaretHAlign(tmp.horizontalAlignment); } catch { /* left */ }
        try { v = CaretVAlign(tmp.verticalAlignment); } catch { /* middle */ }

        float innerLeft = rect.xMin + margin.x;
        float innerTop = rect.yMax - margin.y;
        float innerW = Math.Max(0f, rect.width - margin.x - margin.z);
        float innerH = Math.Max(0f, rect.height - margin.y - margin.w);
        // One float that changes when anything but the text and caret moves the answer.
        float key = innerLeft * 3f + innerTop * 5f + innerW * 7f + innerH * 11f
            + (wrap ? 13f : 0f) + (int)h * 17f + (int)v * 19f;
        if (armed != null && armed.MeasuredCaret == caret && armed.MeasuredKey.Equals(key)
            && string.Equals(armed.MeasuredText, text, StringComparison.Ordinal))
        {
            localX = armed.MeasuredX;
            midY = armed.MeasuredY;
            height = armed.MeasuredHeight;
            return;
        }

        var measurer = armed != null ? EnsureMeasurer(armed, tmp) : null;
        TMP_Text ruler = measurer != null ? measurer : tmp;

        // TEXTAREA2: TMP's own lines first (left-aligned text: every text field and text area).
        // They already include TMP's wrapping and the scroll is in the rect's position, so the
        // caret lands on the line TMP drew. Stale lines (the frame of an edit, before TMP has laid
        // the new text out) keep the previous placement and are not cached, so the next frame
        // reads TMP's fresh layout.
        if (h == UiTextHAlign.Left)
        {
            var lines = TryCaretFromTmpLines(tmp, ruler, text, caret, innerLeft, innerTop,
                out float lx, out float ly, out float lh, out var lineSpot);
            if (lines == TmpLinesResult.Fresh)
            {
                localX = lx;
                midY = ly;
                height = lh;
                if (armed != null) StoreMeasured(armed, text, caret, key, lx, ly, lh, lineSpot, "tmp-lines");
                return;
            }

            if (lines == TmpLinesResult.Stale && armed != null && armed.MeasuredCaret != int.MinValue)
            {
                localX = armed.MeasuredX;
                midY = armed.MeasuredY;
                height = armed.MeasuredHeight;
                armed.MeasuredSource = "stale";
                return;
            }
        }

        try
        {
            Func<string, float> measure = s => MeasureWidth(ruler, s);
            float advance = LineAdvance(ruler);
            var spot = UiCaretLayout.Locate(text, caret, wrap, innerW, innerH, advance, h, v, measure);
            localX = innerLeft + spot.X;
            midY = innerTop - spot.CenterYDown;
            height = UiCaretLayout.BarHeight(advance);
            if (armed != null) StoreMeasured(armed, text, caret, key, localX, midY, height, spot, "measured");
        }
        catch { /* keep the rect-left, rect-center fallback */ }
    }

    private static void StoreMeasured(
        ArmedField armed, string text, int caret, float key, float x, float y, float height, UiCaretSpot spot, string source)
    {
        armed.MeasuredSpot = spot;
        armed.MeasuredText = text;
        armed.MeasuredCaret = caret;
        armed.MeasuredKey = key;
        armed.MeasuredX = x;
        armed.MeasuredY = y;
        armed.MeasuredHeight = height;
        armed.MeasuredSource = source;
    }

    private enum TmpLinesResult
    {
        NotApplicable = 0,
        Fresh = 1,
        Stale = 2,
    }

    /// <summary>
    /// TEXTAREA2. The caret from TMP's last layout of the field: <c>textInfo.lineInfo</c> gives each
    /// laid-out line's first character and its ascender/descender in the text component's local
    /// space; x is the left inset plus the measured width of the caret's line up to the caret.
    /// <c>TMP_LineInfo</c> is read by index from the interop <c>Il2CppStructArray</c>: an explicit
    /// layout of ints and floats whose offsets match the client metadata (<c>firstCharacterIndex</c>
    /// 0x14, <c>ascender</c> 0x2C, <c>descender</c> 0x34, <c>lineExtents</c>
    /// 0x4C, 0x5C bytes), so the stride is right. <c>characterInfo</c> is not read: it is a boxed
    /// bool-bearing value type in this interop.
    /// </summary>
    private static TmpLinesResult TryCaretFromTmpLines(
        TMP_Text tmp, TMP_Text ruler, string text, int caret, float innerLeft, float innerTop,
        out float x, out float y, out float height, out UiCaretSpot spot)
    {
        x = innerLeft;
        y = innerTop;
        height = UiOwnedCaret.HeightPx;
        spot = default;
        try
        {
            var info = tmp.textInfo;
            if (info == null) return TmpLinesResult.NotApplicable;
            for (int i = 0; i < text.Length; i++)
                if (char.IsSurrogate(text[i])) return TmpLinesResult.NotApplicable;
            int count = info.characterCount;
            int lineCount = info.lineCount;
            if (!UiCaretLayout.TmpLayoutMatches(text, count, lineCount)) return TmpLinesResult.Stale;
            var lines = info.lineInfo;
            if (lines == null || lineCount > lines.Length) return TmpLinesResult.Stale;

            int c = Math.Clamp(caret, 0, text.Length);
            int index = UiCaretLayout.TmpLineOf(c, lineCount, i => lines[i].firstCharacterIndex);
            if (index < 0) return TmpLinesResult.Stale;
            var line = lines[index];
            float asc = line.ascender;
            float desc = line.descender;
            float lineHeight = line.lineHeight > 1f ? line.lineHeight : asc - desc;
            if (!(asc > desc))
            {
                asc = line.baseline + lineHeight * 0.75f;
                desc = line.baseline - lineHeight * 0.25f;
            }

            int first = Math.Clamp(line.firstCharacterIndex, 0, text.Length);
            int visualLine = index;
            string prefix;
            if (c == text.Length && text.Length > 0 && text[text.Length - 1] == '\n' && line.lastCharacterIndex < c)
            {
                // A caret after a final line break that TMP gave no line of its own: the next line.
                float step = lineHeight > 1f ? lineHeight : asc - desc;
                asc -= step;
                desc -= step;
                prefix = "";
                visualLine = lineCount;
            }
            else
            {
                prefix = c > first ? text.Substring(first, c - first).Replace("\n", "") : "";
            }

            x = innerLeft + MeasureWidth(ruler, prefix);
            y = (asc + desc) * 0.5f;
            height = UiCaretLayout.BarHeight(asc - desc);
            spot = new UiCaretSpot(visualLine, prefix.Length, Math.Max(lineCount, visualLine + 1), x - innerLeft, innerTop - y);
            return TmpLinesResult.Fresh;
        }
        catch
        {
            return TmpLinesResult.NotApplicable;
        }
    }

    /// <summary>TEXTAREA2: prefix measurements since the process started (the perf readback).</summary>
    internal static long CaretMeasureCalls;

    /// <summary>
    /// Width of <paramref name="s"/> as <paramref name="ruler"/> sets it: the difference against a
    /// visible end mark, so trailing spaces count and margins cancel.
    /// </summary>
    private static float MeasureWidth(TMP_Text ruler, string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0f;
        CaretMeasureCalls += 2;
        return ruler.GetPreferredValues(s + CaretMeasureMark).x - ruler.GetPreferredValues(CaretMeasureMark).x;
    }

    private static float LineAdvance(TMP_Text ruler)
    {
        CaretMeasureCalls += 2;
        float advance = ruler.GetPreferredValues("A\nA").y - ruler.GetPreferredValues("A").y;
        return advance > 1f ? advance : UiOwnedCaret.HeightPx + 2f;
    }

    /// <summary>
    /// TEXTAREA2: the field's hidden measuring label (created once, empty, never drawn). It copies
    /// the font, size, style and spacing of the field's text, so its widths are the field's, and
    /// measuring on it never re-parses the field's own (possibly 4,096-character) text.
    /// </summary>
    private static TextMeshProUGUI? EnsureMeasurer(ArmedField armed, TMP_Text source)
    {
        TextMeshProUGUI? m = armed.Measurer;
        bool alive;
        try { alive = m != null && m.gameObject != null; }
        catch { alive = false; }
        if (!alive)
        {
            m = null;
            armed.Measurer = null;
            GameObject? go = null;
            try
            {
                var host = armed.Field.gameObject;
                go = new GameObject("caret-measure");
                go.transform.SetParent(host.transform, false);
                var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
                rt.sizeDelta = Vector2.zero;
                m = TryAdd<TextMeshProUGUI>(go);
                if (m == null) { TryDestroyImmediate(go); return null; }
                m.text = "";
                m.raycastTarget = false;
                m.richText = false;
                m.enableWordWrapping = false;
                m.color = new Color(1f, 1f, 1f, 0f);
                armed.Measurer = m;
            }
            catch
            {
                TryDestroyImmediate(go);
                return null;
            }
        }

        try
        {
            if (m!.font != source.font) m.font = source.font;
            if (m.fontSize != source.fontSize) m.fontSize = source.fontSize;
            if (m.fontStyle != source.fontStyle) m.fontStyle = source.fontStyle;
            if (m.characterSpacing != source.characterSpacing) m.characterSpacing = source.characterSpacing;
            if (m.wordSpacing != source.wordSpacing) m.wordSpacing = source.wordSpacing;
            if (m.lineSpacing != source.lineSpacing) m.lineSpacing = source.lineSpacing;
        }
        catch { /* measures with what it has */ }
        return m;
    }

    /// <summary>A visible end mark for width differences (a trailing space alone may not advance).</summary>
    private const string CaretMeasureMark = "|";

    private static UiTextHAlign CaretHAlign(HorizontalAlignmentOptions align) => align switch
    {
        HorizontalAlignmentOptions.Center => UiTextHAlign.Center,
        HorizontalAlignmentOptions.Geometry => UiTextHAlign.Center,
        HorizontalAlignmentOptions.Right => UiTextHAlign.Right,
        _ => UiTextHAlign.Left,
    };

    private static UiTextVAlign CaretVAlign(VerticalAlignmentOptions align) => align switch
    {
        VerticalAlignmentOptions.Top => UiTextVAlign.Top,
        VerticalAlignmentOptions.Bottom => UiTextVAlign.Bottom,
        _ => UiTextVAlign.Middle,
    };

    private static void HideOwnedCaret(ArmedField armed)
    {
        if (armed == null) return;
        try { armed.Caret?.SetActive(false); } catch { /* */ }
        armed.LastPlacedCaret = int.MinValue;
    }

    private static void HideAllOwnedCarets()
    {
        for (int i = 0; i < _armedFields.Count; i++)
            HideOwnedCaret(_armedFields[i]);
    }

    /// <summary>
    /// A control's own inactive root, placed in the row's control column and pinned to the row's
    /// first line. Every built control starts here so the inactive-until-wired rule is in one place.
    /// </summary>
    private static GameObject NewControl(GameObject row, string name, in RowColumns columns, float width, float height)
    {
        var go = new GameObject(name);
        // Before the parenting and before any AddComponent: this is the whole trap. An
        // AddComponent on an active object runs OnEnable with every reference still null.
        go.SetActive(false);
        go.transform.SetParent(row.transform, false);
        PlaceControl(go, columns, width, height);
        return go;
    }

    /// <summary>
    /// SETTINGS1: place a part of a composite control inside its container by anchors, so a later
    /// relayout that resizes the container keeps the part where it belongs. The part spans the
    /// container's height less <paramref name="inset"/> top and bottom, and horizontally from
    /// <paramref name="anchorMinX"/> + <paramref name="left"/> to <paramref name="anchorMaxX"/> +
    /// <paramref name="right"/> (fractions of the container width plus pixel offsets).
    /// </summary>
    private static void AnchorInside(
        GameObject go, float anchorMinX, float anchorMaxX, float left, float right, float inset = 2f)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(anchorMinX, 0f);
            rt.anchorMax = new Vector2(anchorMaxX, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, inset);
            rt.offsetMax = new Vector2(right, -inset);
        }
        catch { /* the part keeps its build-time placement */ }
    }

    private static void Activate(GameObject go)
    {
        try { go.SetActive(true); } catch { /* */ }
    }

    /// <summary>
    /// The component a control is built around, wherever in its little hierarchy it sits. A clone
    /// carries the donor's shape and a built control carries ours, so the search is the same one
    /// in both cases and is done once per candidate.
    /// </summary>
    private static T? FindIn<T>(GameObject go) where T : Component
    {
        try { return go.GetComponentInChildren<T>(true); }
        catch { return null; }
    }

    /// <summary>
    /// Destroy a candidate that failed its operability check. Evidence 176's rule: a control that
    /// cannot be operated is not left on the panel as an ornament.
    ///
    /// <para>
    /// This no longer says what happens to the row, because it no longer knows: a rejected clone is
    /// followed by a build attempt (R45 finding 2) and the row usually survives it. The row's fate
    /// is stated once per panel build by <see cref="LogControlSource"/>, which is the only place
    /// that has seen the whole sequence. Kept at debug level for the same reason — a settings panel
    /// with 47 rows would otherwise emit 47 warnings about a recovery that worked.
    /// </para>
    /// </summary>
    private static void DropUnoperableControl(string kind, GameObject go, bool cloned)
    {
        // "a panel <kind>", not "a settings <kind>" (job FUI30): the button row that joined this
        // path lives on a disconnect screen as often as in the settings list.
        Plugin.Logger?.LogDebug(
            "Ui: a panel " + kind + " " + (cloned ? "cloned from a vanilla donor" : "built from parts")
            + " came out without the references that make it operable; it is destroyed.");
        TryDestroyImmediate(go);
    }

    /// <summary>
    /// Say once per panel build which path this control type took — including the two paths that
    /// only exist because a clone was rejected.
    ///
    /// <para>
    /// This exists because evidence 176 could not answer, offline, whether the settings screens'
    /// <c>SettingsEntry_*</c> widgets are instantiated in the scene while the player is in the
    /// world — <c>OptionsMenu</c> is a view, and evidence 88's live sweep found nine
    /// <c>SimpleStunButton</c>s and no <c>MenuButton</c>s. So the runtime does not assume: it
    /// looks, and it reports what it found, and the next live run answers the donor question from
    /// a log line instead of from a guess.
    /// </para>
    ///
    /// <para>
    /// R45 finding 2: it is called on <b>every</b> outcome, before the caller can return null, and
    /// it is called with the outcome rather than with a <c>cloned</c> bool. Before this, the call
    /// sat past the gate that gave up, so in the one state the line was written to catch — a live
    /// donor whose clone comes out inert — it was never printed at all, and the log said a great
    /// deal about rows falling back to labels while answering the wrong question.
    /// </para>
    /// </summary>
    private static void LogControlSource(ref bool logged, string kind, string noun, ControlSource source)
    {
        if (logged) return;
        logged = true;
        var text = ControlBuildPlan.Describe(kind, noun, source);
        if (ControlBuildPlan.IsWarning(source)) Plugin.Logger?.LogWarning(text);
        else Plugin.Logger?.LogDiag(text);
    }

    /// <summary>
    /// Instantiate a vanilla donor into a row, or answer null when there is nothing to clone.
    ///
    /// <para>
    /// <b>Instantiate remaps only what is inside the copied hierarchy</b> (evidence 176), which
    /// makes every clone three separate hazards. Its <i>persistent</i>, inspector-wired
    /// <c>UnityEvent</c> calls still point at the game's own objects and survive
    /// <c>RemoveAllListeners</c>, so a cloned checkbox would drive the vanilla setting it came
    /// from; they are switched off one at a time. A cloned toggle arrives still enrolled in the
    /// vanilla <c>ToggleGroup</c> its reference points at, and <c>Instantiate</c> has already run
    /// <c>OnEnable</c> and registered it there, so it is un-enrolled first and while it is still
    /// active. And references that pointed outside the donor's subtree are simply gone, which is
    /// why the caller re-runs the operability predicate on the clone rather than trusting the
    /// donor's.
    /// </para>
    /// </summary>
    private static GameObject? CloneDonor(
        GameObject? donor, GameObject row, string name, in RowColumns columns, float height)
    {
        if (!Alive(donor) || donor == null) return null;
        GameObject? clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(donor);
            clone.name = name;
            clone.transform.SetParent(row.transform, false);
            UnEnrolToggleGroups(clone);
            clone.SetActive(false);
            SilenceClone(clone);
            // An editor-control donor is meant to be a checkbox, not a caption. IsWidgetSized
            // makes a donor carrying somebody else's label unlikely rather than impossible, so any
            // text that came along is blanked: the row already has a caption, and a stray vanilla
            // word beside it would read as ours. The button clone is the deliberate exception —
            // SetButtonText writes into exactly this TMP — which is why this is here and not in
            // SilenceClone.
            ForEachIn<TextMeshProUGUI>(clone, tmp =>
            {
                try { tmp.text = ""; } catch { /* */ }
            });
            // A donor keeps its own size where that fits the column. Stretching a 24 px vanilla
            // checkbox to fill a 180 px column would hand back a smeared control, which is the
            // same mistake as slicing a border-less sprite (evidence 164) one layer along: the
            // point of borrowing a vanilla piece is that it already looks right.
            PlaceControl(clone, columns, DonorWidth(clone, columns.ControlWidth), DonorHeight(clone, height));
            return clone;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: cloning a vanilla " + name + " threw: " + e.Message);
            TryDestroyImmediate(clone);
            return null;
        }
    }

    /// <summary>The donor's own width, clamped to the column; its own size is why it was borrowed.</summary>
    private static float DonorWidth(GameObject clone, float max) => DonorSide(clone, max, horizontal: true);

    /// <summary>The donor's own height, clamped to the row's first line.</summary>
    private static float DonorHeight(GameObject clone, float max) => DonorSide(clone, max, horizontal: false);

    private static float DonorSide(GameObject clone, float max, bool horizontal)
    {
        try
        {
            var rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                var size = rt.rect.size;
                float own = horizontal ? size.x : size.y;
                if (!float.IsNaN(own) && own > 1f && own < max) return own;
            }
        }
        catch { /* an unreadable donor takes the column, which is never wrong, only generous */ }

        return max;
    }

    /// <summary>
    /// Cut every inherited event wire in a clone. Applied to editor-control clones and to the
    /// <see cref="CloneOrCreateButton"/> button clone alike, which had the same defect and had had
    /// it for longer.
    /// </summary>
    private static void SilenceClone(GameObject clone)
    {
        DisableForeignBehaviours(clone);
        ForEachIn<Toggle>(clone, t => Silence(t.onValueChanged));
        ForEachIn<Slider>(clone, s => Silence(s.onValueChanged));
        ForEachIn<Scrollbar>(clone, s => Silence(s.onValueChanged));
        ForEachIn<ScrollRect>(clone, s => Silence(s.onValueChanged));
        ForEachIn<Button>(clone, b => Silence(b.onClick));
        ForEachIn<SimpleStunButton>(clone, b =>
        {
            Silence(b.onClick);
            Silence(b.onDoubleClick);
            b.OnBeginDragAction = null;
            b.OnEndDragAction = null;
            b.OnDropAction = null;
            b.m_DisableTooltip = true;
            b.m_NoDisabledTooltip = true;
        });
        ForEachIn<TMP_InputField>(clone, f =>
        {
            Silence(f.onEndEdit);
            Silence(f.onValueChanged);
            Silence(f.onSubmit);
            Silence(f.onSelect);
            Silence(f.onDeselect);
        });
        ForEachIn<EventTrigger>(clone, t =>
        {
            var entries = t.triggers;
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null) Silence(entry.callback);
            }
        });
    }

    /// <summary>
    /// Switch off a <c>UnityEvent</c>'s persistent calls and drop its runtime ones. Persistent
    /// listeners are the inspector-wired ones; they are not in the runtime list and
    /// <c>RemoveAllListeners</c> does not touch them, which is exactly why a clone kept driving
    /// the game's own objects.
    /// </summary>
    private static void Silence(UnityEventBase? e)
    {
        if (e == null) return;
        try
        {
            int n = e.GetPersistentEventCount();
            for (int i = 0; i < n; i++)
            {
                try { e.SetPersistentListenerState(i, UnityEventCallState.Off); } catch { /* */ }
            }
        }
        catch { /* */ }

        // Safe here and only here: this runs on a fresh clone, before TryWire attaches anything
        // of ours to it.
        try { e.RemoveAllListeners(); } catch { /* */ }
    }

    private static void UnEnrolToggleGroups(GameObject clone)
    {
        ForEachIn<Toggle>(clone, t =>
        {
            try
            {
                if (t.group != null) t.group = null;
            }
            catch { /* */ }
        });
    }

    /// <summary>
    /// True when a candidate donor is a live object that is actually on screen.
    ///
    /// <para>
    /// <b>Job FUI30.</b> Every donor sweep goes through <see cref="ForEach{T}"/>, which is
    /// <c>FindObjectsOfType(type, includeInactive: <b>true</b>)</c> — so the candidates include
    /// every widget belonging to a view the player is not looking at, and the game swaps views by
    /// activating and deactivating GameObjects (<c>ViewLocationSystem</c>). An inactive donor is
    /// the wrong thing to borrow twice over: <c>Instantiate</c> copies its <c>activeSelf</c>, and
    /// its rects and colors are whatever was serialized rather than whatever the game does to them
    /// on enable. This gate can only turn a donor into a non-donor, i.e. it can only move a row from
    /// the clone path to the build path — and the build path is the one this build is already
    /// live-verified on (<c>Ui: settings toggle source=built</c>, <c>input source=built</c>).
    /// </para>
    /// </summary>
    private static bool IsShownDonor(GameObject? go)
    {
        try { return Alive(go) && go != null && go.activeInHierarchy; }
        catch { return false; }
    }

    /// <summary>
    /// The vanilla <c>SimpleStunButton</c> a button row borrows, or null.
    ///
    /// <para>
    /// Deliberately <b>not</b> filtered by <see cref="IsWidgetSized"/>. That gate exists so a
    /// donor carrying somebody else's caption and geometry cannot be smeared into a 180 px control
    /// column; a button row is the full row, so the reason does not transfer and applying it anyway
    /// would reject nearly every vanilla button on width alone. What does transfer is the transform
    /// count — a button is a handful of objects and a view is not — so that half is kept, at
    /// <see cref="MaxButtonDonorTransforms"/>, and the rest of the judgment is
    /// <see cref="ButtonOperable"/> re-run on the clone.
    /// </para>
    /// </summary>
    private static SimpleStunButton? ButtonDonor()
    {
        if (_buttonDonorSearched) return Alive(_buttonDonor) ? _buttonDonor : null;
        _buttonDonorSearched = true;
        SimpleStunButton? found = null;
        ForEach<SimpleStunButton>(b =>
        {
            if (found != null) return;
            if (IsOurs(b.gameObject)) return;
            if (!IsShownDonor(b.gameObject)) return;
            if (!IsUnderTransformCap(b.gameObject, MaxButtonDonorTransforms)) return;
            found = b;
        });
        _buttonDonor = found;
        return found;
    }

    private static Toggle? ToggleDonor()
    {
        if (_toggleDonorSearched) return Alive(_toggleDonor) ? _toggleDonor : null;
        _toggleDonorSearched = true;
        Toggle? found = null;
        ForEach<Toggle>(t =>
        {
            if (found != null) return;
            if (IsOurs(t.gameObject)) return;
            if (!IsShownDonor(t.gameObject)) return;
            if (!ToggleOperable(t)) return;
            if (!IsWidgetSized(t.gameObject, PanelLayout.ControlColumnWidth, RowHeight)) return;
            found = t;
        });
        _toggleDonor = found;
        return found;
    }

    private static Slider? SliderDonor()
    {
        if (_sliderDonorSearched) return Alive(_sliderDonor) ? _sliderDonor : null;
        _sliderDonorSearched = true;
        Slider? found = null;
        ForEach<Slider>(s =>
        {
            if (found != null) return;
            if (IsOurs(s.gameObject)) return;
            if (!IsShownDonor(s.gameObject)) return;
            if (!SliderOperable(s)) return;
            if (!IsWidgetSized(s.gameObject, PanelLayout.ControlColumnWidth, RowHeight)) return;
            found = s;
        });
        _sliderDonor = found;
        return found;
    }

    private static TMP_InputField? InputDonor()
    {
        if (_inputDonorSearched) return Alive(_inputDonor) ? _inputDonor : null;
        _inputDonorSearched = true;
        TMP_InputField? found = null;
        ForEach<TMP_InputField>(f =>
        {
            if (found != null) return;
            if (IsOurs(f.gameObject)) return;
            if (!IsShownDonor(f.gameObject)) return;
            if (!InputOperable(f)) return;
            if (!IsWidgetSized(f.gameObject, PanelLayout.ControlColumnWidth, RowHeight)) return;
            found = f;
        });
        _inputDonor = found;
        return found;
    }

    /// <summary>
    /// True when a candidate donor is a widget rather than somebody else's whole settings row.
    ///
    /// <para>
    /// The obvious donors named in evidence 176 — <c>SettingsEntry_Checkbox</c>,
    /// <c>SettingsEntry_Slider</c>, <c>SettingsEntry_TextInput</c> — are <i>entries</i>: each
    /// carries its own caption and its own layout, and cloning one would drop a second label and a
    /// stranger's geometry into a column 180 px wide. Two cheap facts rule that out: it has to fit
    /// the control column, and it has to be a handful of transforms rather than a subtree.
    /// </para>
    /// </summary>
    private static bool IsWidgetSized(GameObject go, float maxWidth, float maxHeight)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return false;
            var size = rt.rect.size;
            if (float.IsNaN(size.x) || float.IsNaN(size.y)) return false;
            if (size.x <= 1f || size.y <= 1f) return false;
            if (size.x > maxWidth || size.y > maxHeight) return false;
            return IsUnderTransformCap(go, MaxDonorTransforms);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when a candidate is a handful of transforms rather than a subtree. Split out of
    /// <see cref="IsWidgetSized"/> so a button donor can take this half of the rule without the
    /// column-width half, which does not apply to a row-wide control.
    /// </summary>
    private static bool IsUnderTransformCap(GameObject go, int cap)
    {
        try
        {
            var all = go.GetComponentsInChildren<Transform>(true);
            return all != null && all.Length <= cap;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="go"/> lives anywhere under one of this module's roots.
    ///
    /// <para>
    /// <see cref="UiOwnership.IsOwned"/> tests one name, and only our <i>roots</i> carry the
    /// prefix — a row is called "toggle-row", a checkbox "toggle", a caption "label". Asking it
    /// about the object itself therefore answers "not ours" about every object this module has
    /// ever made below a root, which is how a donor sweep would happily clone the checkbox our own
    /// last panel built, and how the button clone could clone a clone. The parents are walked
    /// instead, with a depth guard because a malformed hierarchy must not hang the sweep.
    /// </para>
    /// </summary>
    private static bool IsOurs(GameObject? go)
    {
        try
        {
            var t = go == null ? null : go.transform;
            int guard = 0;
            while (t != null && guard++ < 64)
            {
                if (UiOwnership.IsOwned(SafeName(t.gameObject))) return true;
                t = t.parent;
            }
        }
        catch { /* */ }

        return false;
    }

    /// <summary>
    /// The game's frame sprite when it can slice, otherwise a flat ground. Same rule as the panel
    /// frame (evidence 164): <c>Image.Type.Sliced</c> on a border-less sprite is not an error and
    /// logs nothing, it just stretches, which is how a 22 px checkbox would wear a smeared 640 px
    /// frame. Cached per panel build; the sweep behind it is a <c>FindObjectsOfType&lt;Image&gt;</c>
    /// and there are 47 rows.
    /// </summary>
    private static void ApplyFrame(Image image, Color flat)
    {
        var sprite = FrameSprite();
        if (sprite != null && HasSliceBorder(sprite))
        {
            try { image.sprite = sprite; } catch { /* */ }
            try { image.type = Image.Type.Sliced; } catch { /* */ }
            try { image.color = Color.white; } catch { /* */ }
            return;
        }

        try { image.sprite = null; } catch { /* */ }
        try { image.color = flat; } catch { /* */ }
    }

    private static Sprite? FrameSprite()
    {
        if (_frameSpriteSearched && Alive(_frameSprite)) return _frameSprite;
        if (_frameSpriteSearched && _frameSprite == null) return null;
        _frameSpriteSearched = true;
        _frameSprite = FindSprite("PassiveFrame_Normal") ?? FindSprite("IconFrame");
        return _frameSprite;
    }

    /// <summary>
    /// Pin a control to the first line of its row's control column.
    ///
    /// <para>
    /// Top-anchored inside the row, not centered in it. A caption here is an author-written config
    /// description that wraps to ten lines, and the row grows to hold it; a control centered in
    /// that row would float halfway down the panel, away from the first line of the text it
    /// belongs to. Top-anchoring also means the control never has to be placed again when the row
    /// is re-measured.
    /// </para>
    ///
    /// <para>
    /// Left-aligned <i>within</i> the column rather than centered in it, so that a control narrower
    /// than the column — a cloned vanilla checkbox that is 24 px wide — sits at the column's edge
    /// and lines up with every other row's control instead of floating in the middle of its own
    /// row. A control the full width of the column lands in exactly the same place either way.
    /// </para>
    /// </summary>
    private static void PlaceControl(GameObject go, in RowColumns columns, float width, float height)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(columns.ControlLeft + (width * 0.5f), 0f);
            rt.sizeDelta = new Vector2(width, height);
        }
        catch { /* */ }
    }

    /// <summary>
    /// KIT1: a fixed square at the <b>right</b> edge of the row's control column, vertically centered
    /// in an ordinary row. <see cref="PlaceControl"/> places from the column's left edge, which is
    /// right for an editor that fills the column and wrong for an icon.
    ///
    /// <para>POLISH3 item C: the square's right edge is the control column's less
    /// <see cref="MenuPanelLayout.IconLinkCentreX"/>'s inset, so the mod page's <c>»</c> ends at the
    /// same x as the head block's status word and action button instead of 12 px further right,
    /// under the pane's scrollbar. Only square controls (the icon link is the sole caller of
    /// <c>AttachRightControl</c>) move; <see cref="PlaceControl"/> is untouched.</para>
    /// </summary>
    private static void PlaceSquareControl(GameObject go, in RowColumns columns, float size)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(
                MenuPanelLayout.IconLinkCentreX(columns, size),
                -Math.Max(0f, (UnityUiRuntimeMetrics.RowHeight - size) * 0.5f));
            rt.sizeDelta = new Vector2(size, size);
        }
        catch { /* */ }
    }

    /// <summary>
    /// A rect stretched across its parent's width, inset by <paramref name="inset"/> at each end,
    /// <paramref name="height"/> tall and vertically centered. The shape a slider's track, fill area
    /// and handle area all want.
    /// </summary>
    private static void StretchWide(GameObject go, float inset, float height)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(1f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, -height * 0.5f);
            rt.offsetMax = new Vector2(-inset, height * 0.5f);
        }
        catch { /* */ }
    }

    /// <summary>Stretched to the parent and inset on both axes. A text field's clipped viewport.</summary>
    private static void InsetStretch(GameObject go, float x, float y)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(x, y);
            rt.offsetMax = new Vector2(-x, -y);
        }
        catch { /* */ }
    }

    /// <summary>
    /// A rect whose anchors <c>Slider.UpdateVisuals</c> owns. Only the pivot and the size survive
    /// what the slider writes, so those are all this sets; a zero <paramref name="sizeDelta"/>
    /// axis means "fill the container on that axis", which is what a fill wants on both and a
    /// handle wants vertically.
    /// </summary>
    private static void DrivenRect(GameObject go, Vector2 sizeDelta)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = sizeDelta;
        }
        catch { /* */ }
    }

    private static void ForEachIn<T>(GameObject root, Action<T> body) where T : Component
    {
        try
        {
            var comps = root.GetComponentsInChildren<T>(true);
            if (comps == null) return;
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                try { body(c); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ForEachIn " + typeof(T).Name + ": " + e.Message); }
            }
        }
        catch { /* */ }
    }

    private static GameObject? MakeLabel(
        GameObject parent, string text, Vector2 anchored, float fontSize, bool left = false, bool muted = false)
    {
        var go = new GameObject("label");
        go.transform.SetParent(parent.transform, false);
        PlaceRect(go, anchored, RowWidth, RowHeight);
        var tmp = TryAdd<TextMeshProUGUI>(go);
        if (tmp != null)
        {
            try { tmp.text = text ?? ""; } catch { /* */ }
            try { tmp.fontSize = fontSize; } catch { /* */ }
            try
            {
                var align = left ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
                tmp.horizontalAlignment = (HorizontalAlignmentOptions)((int)align & LabelTmpWrite.HorizontalMask);
            }
            catch { /* */ }
            // Evidence 167. TMP defaults to TextOverflowModes.Overflow, which draws the whole
            // string outside the rect when it does not fit. Every row reserves its measured
            // height, but that height is clamped (PanelLayout.MaxRowHeight), so a description
            // longer than the clamp kept rendering straight over the rows beneath it — the
            // overlap of evidence 163 and 164, with the reserved heights correct this time.
            // Ellipsis makes the rect mean something: text stops at the row it was given.
            try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }
            try
            {
                tmp.color = muted
                    ? new Color(0.62f, 0.62f, 0.68f, 1f)
                    : Color.white;
            }
            catch { /* */ }
            // Fresh TMP defaults: tracking 0, Normal, auto-size off, scale 1.
            // Do not write characterSpacing / wordSpacing (ALIGN2). The font
            // is the name-ranked body asset, not the tracked MainMenu variant.
            try { tmp.enableAutoSizing = false; } catch { /* */ }
            try { tmp.fontStyle = FontStyles.Normal; } catch { /* */ }
            try
            {
                var labelRt = tmp.rectTransform;
                if (labelRt != null) labelRt.localScale = Vector3.one;
            }
            catch { /* */ }
            ApplyCachedFont(tmp);
        }

        return go;
    }

    /// <summary>The kit's body font on a TMP made outside a panel (the title tile's result line).</summary>
    internal static void ApplyBodyFont(TextMeshProUGUI tmp) => ApplyCachedFont(tmp);

    private static TMP_FontAsset? FindFont() =>
        UiFontLookup.Resolve(SweepFont, static font => Alive(font));

    /// <summary>
    /// Stamp the cached body font (and that asset's own material) onto a TMP. Cached;
    /// the sweep behind <see cref="FindFont"/> is not repeated per widget.
    /// </summary>
    private static void ApplyCachedFont(TextMeshProUGUI tmp)
    {
        if (tmp == null) return;
        var font = FindFont();
        if (font == null) return;
        try { tmp.font = font; } catch { /* */ }
        try
        {
            var mat = UiFontLookup.HeldMaterial<Material>();
            if (mat != null) tmp.fontSharedMaterial = mat;
        }
        catch { /* */ }
    }

    /// <summary>
    /// One on-demand font pick. Callers must go through <see cref="FindFont"/>: repeating this
    /// per sidebar row is a main-thread stall. Loaded <c>TMP_FontAsset</c>s are ranked by name
    /// first (body Regular SDF, prefix-agnostic); the ALIGN3 scene-TMP walk is the last
    /// fallback, with <see cref="UiFontDonor.KindMenuButtonPrefab"/> below the options label.
    /// </summary>
    private static TMP_FontAsset? SweepFont()
    {
        var loaded = PickFromLoadedFontAssets();
        if (loaded != null)
            return loaded;

        var live = new List<LiveFontCandidate>(16);
        var names = new List<UiFontDonor.Candidate>(16);

        void Consider(TMP_Text? tmp, string kind)
        {
            if (tmp == null) return;
            try
            {
                if (IsOurs(tmp.gameObject)) return;
                var font = tmp.font;
                if (font == null) return;
                string fontName = font.name ?? "";
                if (fontName.Length == 0) return;
                Material? material = null;
                try { material = tmp.fontSharedMaterial; } catch { material = null; }
                live.Add(new LiveFontCandidate(font, material, fontName, ObjectPath(tmp.gameObject), kind));
                names.Add(new UiFontDonor.Candidate(fontName, kind));
            }
            catch { /* skip a dead TMP */ }
        }

        ForEach<MainMenuNewView>(view =>
        {
            try { Consider(view.MenuCollection?.MenuButtonPrefab?.ButtonText, UiFontDonor.KindMenuButtonPrefab); }
            catch { /* */ }
        });
        ForEach<EscapeMenuView>(view =>
        {
            try { Consider(view.MenuCollection?.MenuButtonPrefab?.ButtonText, UiFontDonor.KindMenuButtonPrefab); }
            catch { /* */ }
        });
        ForEach<MenuButtonSettings>(settings =>
        {
            try
            {
                var font = settings.FontStandard;
                if (font == null) return;
                string fontName = font.name ?? "";
                if (fontName.Length == 0) return;
                live.Add(new LiveFontCandidate(font, null, fontName, ObjectPath(settings.gameObject) + "/FontStandard", UiFontDonor.KindMenuButtonPrefab));
                names.Add(new UiFontDonor.Candidate(fontName, UiFontDonor.KindMenuButtonPrefab));
            }
            catch { /* */ }
        });
        ForEach<OptionsMenu>(menu =>
        {
            try { Consider(menu.DetailsHeader, UiFontDonor.KindOptionsLabel); }
            catch { /* */ }
        });
        ForEach<SettingsEntry_Label>(entry =>
        {
            try
            {
                var header = entry.HeaderText;
                Consider(header != null ? header.Text : null, UiFontDonor.KindOptionsLabel);
            }
            catch { /* */ }
        });

        int preferred = UiFontDonor.PickIndex(names);
        if (preferred >= 0 && UiFontDonor.Rank(names[preferred].FontName, names[preferred].Kind) >= UiFontDonor.RankOptionsLabel)
            return RememberSweep(live[preferred]);

        ForEach<TextMeshProUGUI>(tmp => Consider(tmp, UiFontDonor.KindScene));
        int pick = UiFontDonor.PickIndex(names);
        if (pick < 0) return null;
        return RememberSweep(live[pick]);
    }

    /// <summary>
    /// One <c>Resources.FindObjectsOfTypeAll(typeof(TMP_FontAsset))</c> — the Type
    /// overload; the generic icall has no native address (-1). Never per frame: <see cref="FindFont"/>
    /// caches the pick until Invalidate. Uses the asset's own <c>material</c>.
    /// </summary>
    private static TMP_FontAsset? PickFromLoadedFontAssets()
    {
        var live = new List<LiveFontCandidate>(16);
        var names = new List<string>(16);
        ForEachLoadedFontAsset(font =>
        {
            string fontName;
            try { fontName = font.name ?? ""; }
            catch { return; }
            if (fontName.Length == 0) return;
            Material? material = null;
            try { material = font.material; } catch { material = null; }
            live.Add(new LiveFontCandidate(font, material, fontName, "", UiFontDonor.KindAssetList));
            names.Add(fontName);
        });
        int pick = UiFontDonor.PickLoadedIndex(names);
        if (pick < 0) return null;
        return RememberSweep(live[pick]);
    }

    private static void ForEachLoadedFontAsset(Action<TMP_FontAsset> body)
    {
        try
        {
            var type = Il2CppType.Of<TMP_FontAsset>();
            if (type == null) return;
            var arr = Resources.FindObjectsOfTypeAll(type);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                TMP_FontAsset? font = null;
                try { font = arr[i] == null ? null : arr[i].TryCast<TMP_FontAsset>(); }
                catch { continue; }
                if (font == null || !Alive(font)) continue;
                try { body(font); }
                catch { /* one unreadable asset must not end the sweep */ }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: Resources.FindObjectsOfTypeAll<TMP_FontAsset> threw (" + e.Message + ").");
        }
    }

    /// <summary>
    /// HOSTS1: the title band's caption in the font the game's outer tab captions use
    /// (<see cref="WorkspaceHostLayout.BandCaptionFont"/>, read from the in-game probe), found among the
    /// loaded font assets once per band; the kit's body font when that asset is not resident.
    /// </summary>
    internal static void ApplyNativeCaptionFont(TextMeshProUGUI tmp)
    {
        if (tmp == null) return;
        TMP_FontAsset? exact = null, family = null;
        ForEachLoadedFontAsset(font =>
        {
            string name = font.name ?? "";
            if (exact == null && string.Equals(name, WorkspaceHostLayout.BandCaptionFont, StringComparison.Ordinal)) exact = font;
            else if (family == null && name.IndexOf(WorkspaceHostLayout.BandCaptionFamily, StringComparison.Ordinal) >= 0) family = font;
        });
        var pick = exact ?? family;
        if (pick == null)
        {
            ApplyCachedFont(tmp);
            return;
        }

        try { tmp.font = pick; } catch { /* */ }
        try { if (pick.material != null) tmp.fontSharedMaterial = pick.material; } catch { /* */ }
    }

    /// <summary>
    /// On-demand <c>mods_ui status.fontAssets</c> (<c>args.fonts=true</c> only).
    /// A separate enumeration from <see cref="FindFont"/>'s cached pick.
    /// </summary>
    internal static object DescribeLoadedFontAssets()
    {
        var rows = new List<object>(16);
        ForEachLoadedFontAsset(font => rows.Add(DescribeOneFontAsset(font)));
        return rows;
    }

    private static Dictionary<string, object?> DescribeOneFontAsset(TMP_FontAsset font)
    {
        string name = "";
        try { name = font.name ?? ""; } catch { name = ""; }
        object? pointSize = null;
        object? scale = null;
        object? atlasPadding = null;
        try
        {
            var face = font.faceInfo;
            pointSize = face.pointSize;
            scale = face.scale;
        }
        catch { /* faceInfo unreachable */ }
        try { atlasPadding = font.atlasPadding; } catch { /* */ }
        float? advanceN = TryGlyphAdvance(font, 'n', out string? whyN);
        float? advanceI = TryGlyphAdvance(font, 'i', out string? whyI);
        string? why = null;
        if (advanceN == null || advanceI == null)
            why = whyN ?? whyI ?? FontAssetReadback.WhyLookupUnreachable;
        return FontAssetReadback.Shape(name, pointSize, scale, atlasPadding, advanceN, advanceI, why);
    }

    private static float? TryGlyphAdvance(TMP_FontAsset font, char ch, out string? why)
    {
        uint code = ch;
        try
        {
            var lookup = font.characterLookupTable;
            if (lookup != null)
            {
                TMP_Character? found = null;
                try
                {
                    if (lookup.ContainsKey(code))
                        found = lookup[code];
                }
                catch { found = null; }
                if (found != null)
                {
                    float? advance = AdvanceFromCharacter(font, found, out why);
                    if (advance != null) return advance;
                    if (why != null) return null;
                }
            }
        }
        catch { /* lookup unreachable; try the table */ }

        try
        {
            var table = font.characterTable;
            if (table != null)
            {
                int n = table.Count;
                for (int i = 0; i < n; i++)
                {
                    TMP_Character? c = null;
                    try { c = table[i]; } catch { continue; }
                    if (c == null) continue;
                    uint unicode;
                    try { unicode = c.unicode; } catch { continue; }
                    if (unicode != code) continue;
                    return AdvanceFromCharacter(font, c, out why);
                }
            }
        }
        catch { /* */ }

        why = FontAssetReadback.WhyLookupUnreachable;
        return null;
    }

    private static float? AdvanceFromCharacter(TMP_FontAsset font, TMP_Character character, out string? why)
    {
        try
        {
            var glyph = character.glyph;
            if (glyph != null)
            {
                why = null;
                return glyph.metrics.horizontalAdvance;
            }
        }
        catch { /* fall through to glyphTable */ }

        try
        {
            uint glyphIndex = character.glyphIndex;
            var glyphs = font.glyphTable;
            if (glyphs != null)
            {
                int n = glyphs.Count;
                for (int i = 0; i < n; i++)
                {
                    var g = glyphs[i];
                    if (g == null) continue;
                    uint index;
                    try { index = g.index; } catch { continue; }
                    if (index != glyphIndex) continue;
                    why = null;
                    return g.metrics.horizontalAdvance;
                }
            }
        }
        catch { /* */ }

        why = FontAssetReadback.WhyGlyphMissing;
        return null;
    }

    private static TMP_FontAsset? RememberSweep(in LiveFontCandidate chosen)
    {
        object? material = chosen.Material;
        try
        {
            if (chosen.Font != null && chosen.Font.material != null)
                material = chosen.Font.material;
        }
        catch { /* keep the listed material */ }
        UiFontLookup.RememberDonor(
            chosen.FontName, chosen.Path, material, UiFontDonor.SourceForKind(chosen.Kind));
        return chosen.Font;
    }

    private readonly struct LiveFontCandidate
    {
        public readonly TMP_FontAsset Font;
        public readonly Material? Material;
        public readonly string FontName;
        public readonly string Path;
        public readonly string Kind;

        public LiveFontCandidate(TMP_FontAsset font, Material? material, string fontName, string path, string kind)
        {
            Font = font;
            Material = material;
            FontName = fontName;
            Path = path;
            Kind = kind;
        }
    }

    private static string ObjectPath(GameObject? go)
    {
        if (go == null) return "";
        try
        {
            var parts = new List<string>(8);
            var t = go.transform;
            int guard = 0;
            while (t != null && guard++ < 32)
            {
                parts.Add(t.name ?? "");
                t = t.parent;
            }

            if (parts.Count == 0) return "";
            parts.Reverse();
            return string.Join("/", parts);
        }
        catch
        {
            try { return go.name ?? ""; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// On-demand TMP snapshot for <c>mods_ui widget_read</c> / <c>rects</c>.
    /// <c>GetComponentInChildren</c> here is the op, not a tick.
    /// </summary>
    internal static object? ReadTmpSnapshot(GameObject? go)
    {
        if (go == null) return null;
        TextMeshProUGUI? tmp = null;
        try { tmp = go.GetComponentInChildren<TextMeshProUGUI>(true); }
        catch { tmp = null; }
        if (tmp == null) return null;
        return SnapshotTmp(tmp);
    }

    internal static object? ReadWidgetTmp(IRuntimeWidget? runtime)
    {
        if (runtime is not UnityWidget widget) return null;
        return ReadTmpSnapshot(widget.GameObject);
    }

    private static object SnapshotTmp(TextMeshProUGUI tmp)
    {
        string font = "";
        string material = "";
        float fontSize = 0f;
        float fontScale = 1f;
        float characterSpacing = 0f;
        float wordSpacing = 0f;
        int alignment = 0;
        int horizontalAlignment = 0;
        int fontStyle = 0;
        bool enableAutoSizing = false;
        bool wordWrapping = false;
        int overflow = 0;
        float lossyX = 1f;
        float lossyY = 1f;
        float rectW = 0f;
        float rectH = 0f;
        bool richText = false;
        float preferredWidth = 0f;
        try { if (tmp.font != null) font = tmp.font.name ?? ""; } catch { /* */ }
        try { if (tmp.fontSharedMaterial != null) material = tmp.fontSharedMaterial.name ?? ""; } catch { /* */ }
        try { fontSize = tmp.fontSize; } catch { /* */ }
        try
        {
            if (tmp.font != null)
            {
                var face = tmp.font.faceInfo;
                float point = face.pointSize;
                float faceScale = face.scale;
                fontScale = point > 0.01f ? fontSize / point : (faceScale != 0f ? faceScale : 1f);
            }
        }
        catch { fontScale = 1f; }
        try { characterSpacing = tmp.characterSpacing; } catch { /* */ }
        try { wordSpacing = tmp.wordSpacing; } catch { /* */ }
        try { alignment = (int)tmp.alignment; } catch { /* */ }
        try { horizontalAlignment = (int)tmp.horizontalAlignment; } catch { /* */ }
        try { fontStyle = (int)tmp.fontStyle; } catch { /* */ }
        try { enableAutoSizing = tmp.enableAutoSizing; } catch { /* */ }
        try { wordWrapping = tmp.enableWordWrapping; } catch { /* */ }
        try { overflow = (int)tmp.overflowMode; } catch { /* */ }
        try
        {
            var ls = tmp.rectTransform != null ? tmp.rectTransform.lossyScale : Vector3.one;
            lossyX = ls.x;
            lossyY = ls.y;
        }
        catch { /* */ }
        try
        {
            var rect = tmp.rectTransform != null ? tmp.rectTransform.rect : default;
            rectW = rect.width;
            rectH = rect.height;
        }
        catch { /* */ }
        try { richText = tmp.richText; } catch { /* */ }
        try { preferredWidth = tmp.preferredWidth; } catch { /* */ }
        return TmpReadback.Shape(
            font, material, fontSize, fontScale, characterSpacing, wordSpacing,
            alignment, horizontalAlignment, fontStyle, enableAutoSizing, wordWrapping, overflow,
            lossyX, lossyY, rectW, rectH, richText, preferredWidth);
    }

    private static Sprite? FindSprite(string name)
    {
        Sprite? exact = null;
        Sprite? contains = null;
        ForEach<Image>(img =>
        {
            if (exact != null) return;
            if (IsOurs(img.gameObject)) return;
            try
            {
                var sp = img.sprite;
                if (sp == null) return;
                string n = sp.name ?? "";
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) exact = sp;
                else if (contains == null && n.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) contains = sp;
            }
            catch { /* */ }
        });
        return exact ?? contains;
    }

    /// <summary>
    /// Plugin roots by name. Finding one is a sweep of every <c>Transform</c> in the scene — in a
    /// loaded world that was 20–90 ms per panel, two or three panels a Mods tab switch. A remembered
    /// root is used only while it is alive, still top-level and still carries the name.
    /// </summary>
    private static readonly Dictionary<string, GameObject> Roots = new(StringComparer.OrdinalIgnoreCase);

    private static GameObject? EnsureRoot(string rootName)
    {
        if (Roots.TryGetValue(rootName, out var known) && IsRoot(known, rootName)) return known;
        Roots.Remove(rootName);
        var root = FindOrCreateRoot(rootName);
        if (root != null) Roots[rootName] = root;
        return root;
    }

    private static bool IsRoot(GameObject? go, string rootName)
    {
        if (!Alive(go)) return false;
        try { return go!.transform.parent == null && string.Equals(go.name, rootName, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static GameObject? FindOrCreateRoot(string rootName)
    {
        GameObject? found = null;
        ForEach<Transform>(t =>
        {
            if (found != null) return;
            try
            {
                if ((t.gameObject.name ?? "").Equals(rootName, StringComparison.OrdinalIgnoreCase)
                    && t.parent == null)
                    found = t.gameObject;
            }
            catch { /* */ }
        });
        if (Alive(found)) return found;
        try
        {
            var go = new GameObject(rootName);
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ui: EnsureRoot '" + rootName + "': " + e);
            return null;
        }
    }

    private GameObject? CreateOverlay(GameObject root)
    {
        int n = Interlocked.Increment(ref _overlaySerial);
        var canvasGo = new GameObject("overlay-" + n.ToString(CultureInfo.InvariantCulture));
        canvasGo.transform.SetParent(root.transform, false);
        var canvas = TryAdd<Canvas>(canvasGo);
        if (canvas == null)
        {
            TryDestroyImmediate(canvasGo);
            return null;
        }

        try { canvas.renderMode = RenderMode.ScreenSpaceOverlay; } catch { /* */ }
        try { canvas.sortingOrder = UiOwnership.OverlaySorting; } catch { /* */ }
        try { canvas.overrideSorting = true; } catch { /* */ }
        try { canvas.pixelPerfect = true; } catch { /* */ }
        var scaler = TryAdd<CanvasScaler>(canvasGo);
        if (scaler != null) AdoptGameScaler(scaler);

        TryAdd<GraphicRaycaster>(canvasGo);
        Stretch(canvasGo);
        return canvasGo;
    }

    /// <summary>
    /// Scale a mod overlay the way the game scales its own UI, by copying a vanilla
    /// <c>CanvasScaler</c>'s settings rather than inventing our own (evidence 165).
    ///
    /// <para>
    /// <b>Why.</b> The overlay used <c>ConstantPixelSize</c> at <c>scaleFactor = 1</c>, so mod
    /// panels were measured in raw device pixels while every vanilla screen — the escape menu,
    /// the options screens — is laid out in the game's design space and scales with the player's
    /// resolution and UI-scale setting. A panel sized against <c>Screen.height</c> is therefore
    /// sized against the wrong ruler: correct at the reference resolution and wrong everywhere
    /// else, and never matching the menu sitting next to it. Copying the scaler puts our panels
    /// in the same coordinate space the game already agreed with the player.
    /// </para>
    ///
    /// <para>
    /// The donor is any <c>CanvasScaler</c> the framework does not own — the same "read the
    /// game's own asset" approach <see cref="FindFont"/> uses for the TMP font. If none can be
    /// found (the main menu before the HUD exists, a stripped scene) the old constant-pixel
    /// behavior is kept, which is wrong in the same way it always was rather than newly broken.
    /// </para>
    /// </summary>
    internal static void AdoptGameScaler(CanvasScaler scaler) => AdoptGameScaler(scaler, null);

    /// <summary>
    /// HOSTS1: the one scaler donor every Mods workspace canvas copies (the panes, the backdrop and
    /// artwork canvas, the curtain), so all of them are laid out on the same canvas at both hosts.
    /// <paramref name="preferred"/> is a caller's own native scaler (the curtain's parent menu) and wins
    /// when it qualifies as a donor.
    /// </summary>
    internal static void AdoptGameScaler(CanvasScaler scaler, CanvasScaler? preferred)
    {
        // The game's scaler outlives our panels; re-sweeping the scene for it per panel cost
        // ~15 ms in a loaded world. A remembered one is re-checked, so a scene change finds anew.
        CanvasScaler? donor = ScalerDonor(preferred) ? preferred
            : ScalerDonor(_gameScaler) ? _gameScaler : null;
        if (donor == null)
        {
            ForEach<CanvasScaler>(candidate =>
            {
                if (donor != null) return;
                if (ScalerDonor(candidate)) donor = candidate;
            });
            _gameScaler = donor;
        }

        if (donor == null)
        {
            try { scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize; } catch { /* */ }
            try { scaler.scaleFactor = 1f; } catch { /* */ }
            if (!_scalerFallbackLogged)
            {
                _scalerFallbackLogged = true;
                Plugin.Logger?.LogInfo(
                    "Ui: no vanilla CanvasScaler to copy; mod overlays stay at constant pixel size, "
                    + "so panels do not follow the game's UI scale. Logged once.");
            }

            return;
        }

        try { scaler.uiScaleMode = donor.uiScaleMode; } catch { /* */ }
        try { scaler.referenceResolution = donor.referenceResolution; } catch { /* */ }
        try { scaler.screenMatchMode = donor.screenMatchMode; } catch { /* */ }
        try { scaler.matchWidthOrHeight = donor.matchWidthOrHeight; } catch { /* */ }
        try { scaler.referencePixelsPerUnit = donor.referencePixelsPerUnit; } catch { /* */ }

        if (!_scalerAdoptedLogged)
        {
            _scalerAdoptedLogged = true;
            try
            {
                Plugin.Logger?.LogDiag(
                    "Ui: overlay scaling copied from the game's own canvas ("
                    + SafeName(donor.gameObject) + "): mode=" + donor.uiScaleMode
                    + " reference=" + donor.referenceResolution.x + "x" + donor.referenceResolution.y
                    + " match=" + donor.matchWidthOrHeight
                    + ". Panel sizes are in the game's design space, not device pixels.");
            }
            catch { /* */ }
        }
    }

    private static CanvasScaler? _gameScaler;

    private static bool ScalerDonor(CanvasScaler? candidate)
    {
        if (!Alive(candidate)) return false;
        try
        {
            if (UiOwnership.IsOwned(SafeName(candidate!.gameObject))) return false;
            return candidate.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize
                && candidate.referenceResolution.y > 0f;
        }
        catch { return false; }
    }

    private static bool _scalerAdoptedLogged;
    private static bool _scalerFallbackLogged;

    private static void DisableForeignBehaviours(GameObject go)
    {
        try
        {
            var comps = go.GetComponentsInChildren<Behaviour>(true);
            if (comps == null) return;
            int n = comps.Length;
            for (int i = 0; i < n; i++)
            {
                var c = comps[i];
                if (c == null) continue;
                // GetComponentsInChildren<Behaviour> returns base managed wrappers. C# `is`
                // tests the wrapper, not its IL2CPP class, and disables valid graphics too.
                if (c.TryCast<Image>() != null || c.TryCast<Button>() != null
                    || c.TryCast<Toggle>() != null || c.TryCast<Slider>() != null
                    || c.TryCast<TextMeshProUGUI>() != null || c.TryCast<TMP_InputField>() != null
                    || c.TryCast<SimpleStunButton>() != null || c.TryCast<GraphicRaycaster>() != null
                    || c.TryCast<Canvas>() != null || c.TryCast<CanvasScaler>() != null
                    || c.TryCast<EventTrigger>() != null || c.TryCast<ScrollRect>() != null
                    || c.TryCast<RectMask2D>() != null || c.TryCast<Scrollbar>() != null
                    || c.TryCast<Outline>() != null)
                    continue;
                try { c.enabled = false; } catch { /* */ }
            }
        }
        catch { /* */ }
    }

    /// <summary>
    /// The clipped window rows are seen through. Stretched to the panel on both axes and inset by
    /// the header at the top, so it tracks the panel's height without any per-frame work when
    /// <see cref="UnityPanel.ApplyLayout"/> resizes the frame.
    ///
    /// <para>
    /// <c>RectMask2D</c> rather than <c>Mask</c>: it clips to the rect with no stencil buffer and
    /// no material instancing, which matters because every panel gets its own overlay canvas
    /// (R24 finding 3) and a stencil mask per canvas is a real cost for a rectangular clip.
    /// </para>
    /// </summary>
    private static GameObject? MakeViewport(GameObject panel)
    {
        try
        {
            var go = new GameObject("viewport");
            go.transform.SetParent(panel.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 1f);
            // Bottom inset 0, not PadBottom (evidence 175). The content rect already ends with
            // PanelLayout.PadBottom, so insetting the viewport by it as well made the real
            // viewport PadBottom shorter than PanelLayout.ViewportHeightFor says it is — the
            // arithmetic called a panel "fits" while the ScrollRect, measuring the actual rects,
            // found it overflowing by exactly that padding and drew a scrollbar. The gap under
            // the last row comes from the content's own padding; the viewport itself reaches the
            // frame.
            rt.offsetMin = new Vector2(PanelLayout.PadSide, 0f);
            rt.offsetMax = new Vector2(-PanelLayout.PadSide, -PanelLayout.HeaderHeight);
            // Image supplies the CanvasRenderer RectMask2D needs to clip both draw and
            // raycast. A fully transparent graphic is skipped by some raycast paths, so
            // the fill is a hair above zero; it is still invisible on the dark panel.
            var image = TryAdd<Image>(go);
            if (image != null)
            {
                try { image.color = new Color(0f, 0f, 0f, 0.02f); } catch { /* */ }
                try { image.raycastTarget = true; } catch { /* */ }
                try { image.maskable = true; } catch { /* */ }
            }
            TryAdd<RectMask2D>(go);
            return go;
        }
        catch (Exception e)
        {
            // No viewport means no clipping and no scrolling, but rows still land on the panel:
            // the panel degrades to what it was before evidence 163 rather than disappearing.
            Plugin.Logger?.LogWarning("Ui: panel viewport threw: " + e.Message + "; rows will not clip or scroll.");
            return null;
        }
    }

    /// <summary>
    /// The rect rows are parented to. Top-anchored and top-pivoted so its height can grow
    /// downward as rows are added without moving the rows already placed, which is the whole
    /// reason row positions are measured from the top rather than from the panel's center.
    /// </summary>
    private static GameObject? MakeContent(GameObject viewport)
    {
        try
        {
            var go = new GameObject("content");
            go.transform.SetParent(viewport.transform, false);
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, PanelLayout.PadTop + PanelLayout.PadBottom);
            return go;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: panel content threw: " + e.Message + "; rows will not scroll.");
            return null;
        }
    }

    /// <summary>
    /// Vertical scrolling over the viewport. Clamped and inertia-free on purpose: this is a
    /// settings list, not a touch surface, and a list that keeps gliding after the wheel stops
    /// makes a row hard to click.
    /// </summary>
    private static GameObject? AddScroll(GameObject panel, GameObject viewport, GameObject content)
    {
        try
        {
            var scroll = TryAdd<ScrollRect>(panel);
            if (scroll == null) return null;
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = content.GetComponent<RectTransform>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.inertia = false;
            // Zero: the shown-input pump is the only writer. OnScroll then adds nothing
            // even if the EventSystem delivers the same notch after Plugin.Frame.
            scroll.scrollSensitivity = MenuPanelLayout.UguiScrollSensitivity;

            var bar = MakeScrollbar(panel);
            if (bar != null)
            {
                var sb = bar.GetComponent<Scrollbar>();
                if (sb != null)
                {
                    scroll.verticalScrollbar = sb;
                    // AutoHide, not Permanent (evidence 175). Permanent does not mean "we manage
                    // visibility ourselves" — ScrollRect force-activates the scrollbar's
                    // GameObject on every layout rebuild, so ApplyLayout's SetActive(false) was
                    // undone a frame later and a panel that fits still wore a bar. AutoHide is the
                    // middle mode I skipped: it hides the bar when the content fits and, unlike
                    // AutoHideAndExpandViewport, never re-lays out the viewport — which is the
                    // only reason Permanent was chosen in the first place.
                    //
                    // This line is the ONLY owner of the bar's visibility (R45 finding 22).
                    // ApplyLayout's hand-written SetActive is gone: evidence 175's rule is to
                    // prefer the mode that already does what you want over a stronger mode plus a
                    // workaround, and c9ee05d adopted the mode while leaving the workaround in
                    // place — two owners of one GameObject, driven by two different measurements
                    // of the same quantity, which is exactly the pair that drifted apart in the
                    // first place. What ApplyLayout does instead is read this owner's answer back
                    // and print it next to its own arithmetic.
                    scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                }
            }

            return bar;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: panel scroll threw: " + e.Message + "; the list will clip without scrolling.");
            return null;
        }
    }

    /// <summary>
    /// The slim bar down the panel's right inset. It is the only thing on screen that says the
    /// list continues: the settings panel holds 5,615 px of rows in a 747 px window on this
    /// build, so without it 87% of the list is below the fold with no indication it exists
    /// (evidence 165). Built in the <see cref="PanelLayout.PadSide"/> inset so it never sits on
    /// top of a row.
    /// </summary>
    private static GameObject? MakeScrollbar(GameObject panel)
    {
        try
        {
            var bar = new GameObject("scrollbar");
            bar.transform.SetParent(panel.transform, false);
            var rt = bar.GetComponent<RectTransform>() ?? bar.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.offsetMin = new Vector2(
                -(PanelLayout.ScrollbarInset + PanelLayout.ScrollbarWidth), PanelLayout.PadBottom);
            rt.offsetMax = new Vector2(-PanelLayout.ScrollbarInset, -PanelLayout.HeaderHeight);

            var track = TryAdd<Image>(bar);
            if (track != null)
            {
                try { track.color = new Color(1f, 1f, 1f, 0.10f); } catch { /* */ }
            }

            // Unity's Scrollbar drives handleRect's anchors itself, so the handle needs its own
            // stretched parent to be driven inside. That is the vanilla scrollbar shape.
            var area = new GameObject("sliding-area");
            area.transform.SetParent(bar.transform, false);
            Stretch(area);

            var handle = new GameObject("handle");
            handle.transform.SetParent(area.transform, false);
            Stretch(handle);
            var handleImage = TryAdd<Image>(handle);
            if (handleImage != null)
            {
                try { handleImage.color = new Color(1f, 1f, 1f, 0.45f); } catch { /* */ }
            }

            var scrollbar = TryAdd<Scrollbar>(bar);
            if (scrollbar == null) return bar;
            try { scrollbar.direction = Scrollbar.Direction.BottomToTop; } catch { /* */ }
            try { scrollbar.handleRect = handle.GetComponent<RectTransform>(); } catch { /* */ }
            try { scrollbar.targetGraphic = handleImage; } catch { /* */ }
            return bar;
        }
        catch (Exception e)
        {
            // No bar means no affordance, but the wheel still scrolls: strictly better than
            // failing the panel.
            Plugin.Logger?.LogWarning("Ui: panel scrollbar threw: " + e.Message + "; the list scrolls with no indicator.");
            return null;
        }
    }

    /// <summary>
    /// Place a rect against the top edge of its parent, <paramref name="yFromTop"/> pixels down.
    /// The counterpart of <see cref="PlaceRect"/> for anything whose parent's height changes:
    /// the panel frame (resized to its content) and the row content rect (grows per row).
    /// </summary>
    private static void PlaceTop(GameObject go, float yFromTop, float width, float height) =>
        PlaceTop(go, 0f, yFromTop, width, height);

    /// <summary>A rect <see cref="PlaceTop(GameObject, float, float, float, float)"/> placed, re-pivoted at its center without moving.</summary>
    private static void CenterPivot(GameObject go, float height)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null || Math.Abs(rt.pivot.y - 0.5f) < 0.001f) return;
            var at = rt.anchoredPosition;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(at.x, at.y - height * 0.5f);
        }
        catch { /* the glyph keeps its top pivot */ }
    }

    private static void PlaceTop(GameObject go, float x, float yFromTop, float width, float height)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
            rt.sizeDelta = new Vector2(width, height);
        }
        catch { /* */ }
    }

    /// <summary>
    /// Measure a label's wrapped text, grow the label's own rect to fit it, and answer the
    /// height the row containing it needs. The rect is set first because TMP wraps to the rect it
    /// is in, then <c>ForceMeshUpdate</c> makes <c>preferredHeight</c> reflect this text rather
    /// than whatever was measured last. A measurement that cannot be taken answers one ordinary
    /// row's height, which is what every row used to get unconditionally.
    ///
    /// <para>
    /// All <b>five</b> row types go through this, which is the correction evidence 164 started
    /// and R43 finding 3 finished: the first cut measured only standalone labels, so a toggle, a
    /// slider or a text field still reserved one constant row while its own caption — an
    /// author-written config description, four lines of it for <c>EditOtherModsSettings</c> —
    /// wrapped straight over the row beneath. The second cut fixed four of the five and left
    /// <see cref="AddButton"/> on the constant, which made the same mistake one layer up: the
    /// rule was re-scoped from one widget type to four widget types when it belongs to the
    /// <i>text</i>. Which control a row carries has nothing to do with how long somebody else's
    /// description is, and <c>IUiPanel.Button</c> takes author-written text like the rest.
    /// </para>
    ///
    /// <para>
    /// Called on every row on every layout pass, not once at build time: see
    /// <see cref="UnityPanel.RunLayout"/>.
    /// </para>
    /// </summary>
    private static float FitLabel(GameObject? label, in RowColumns columns, bool left = false)
    {
        ClampCaptionText(label);
        ApplyCaptionAlign(label, left);
        float height = MeasuredRowHeight(label, columns.LabelWidth);
        if (label != null)
        {
            try
            {
                var rt = label.GetComponent<RectTransform>();
                if (rt != null)
                {
                    // The label's rect has to hold the text too, or TMP clips the lines the
                    // row now has room for.
                    rt.sizeDelta = new Vector2(columns.LabelWidth, height);
                    // Evidence 176. The caption's x comes from the same call that gives the
                    // control its x, and it is re-applied on every layout pass, so a row cannot
                    // end up indented for a control it does not have. That disagreement is what
                    // the player saw as ragged alignment: three builders each shifted their
                    // caption sideways to leave room for a control none of them built.
                    rt.anchoredPosition = new Vector2(columns.LabelX, rt.anchoredPosition.y);
                }
            }
            catch { /* the row is still the right height; only the text may clip */ }
        }

        return height;
    }

    private static void ApplyCaptionAlign(GameObject? label, bool left)
    {
        if (label == null) return;
        try
        {
            var tmp = label.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return;
            ApplyHorizontalAlign(tmp, left);
        }
        catch { /* the row is still placed; only the caption may stay centered */ }
    }

    /// <summary>
    /// Horizontal anchor only. ALIGN1 wrote <c>tmp.alignment =
    /// TextAlignmentOptions.Left/Center</c> (513/514) on every caption; those
    /// combined values can land unmasked in TMP's line justification and
    /// flush-space the glyphs (gen 17 letter-spacing). Mask to
    /// <see cref="LabelTmpWrite.HorizontalMask"/> (Left=1, Center=2). Never
    /// writes <c>characterSpacing</c>, <c>wordSpacing</c>, Justified or Flush.
    /// </summary>
    private static void ApplyHorizontalAlign(TextMeshProUGUI tmp, bool left)
    {
        var align = left ? TextAlignmentOptions.Left : TextAlignmentOptions.Center;
        tmp.horizontalAlignment = (HorizontalAlignmentOptions)((int)align & LabelTmpWrite.HorizontalMask);
    }

    /// <summary>
    /// The pane chrome already shows this title; a body row repeating it is
    /// the redundant "Configuration" under the Configuration pane title.
    /// </summary>
    private static bool IsRedundantPaneTitle(UnityPanel host, string text)
    {
        if (!host.IsMenuPane || host.IsSidebar || host.IsStrip) return false;
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var title = host.Title;
            if (title == null) return false;
            var tmp = title.GetComponent<TextMeshProUGUI>();
            string titleText = tmp == null ? "" : (tmp.text ?? "");
            return string.Equals(text, titleText, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static IRuntimeWidget SkipRedundantPaneTitle(UnityPanel host)
    {
        var go = new GameObject("skipped-pane-title");
        go.transform.SetParent(host.RowParent.transform, false);
        try { go.SetActive(false); } catch { /* */ }
        return new UnityWidget(host, go, null, null, null, null, null, null);
    }

    /// <summary>
    /// Make the row's height clamp a boundary for this caption, whoever built it.
    ///
    /// <para>
    /// <b>Why here and not in the factory</b> (job FUI25, R45 finding 10). Evidence 167's defect is
    /// that <see cref="PanelLayout.MaxRowHeight"/> is a reservation and not a boundary: TMP
    /// defaults to <c>TextOverflowModes.Overflow</c> and paints the whole string outside the
    /// clamped rect, over every row beneath it. <c>debd05d</c> fixed that by writing
    /// <c>Ellipsis</c> in <see cref="MakeLabel"/> — the caption factory the four label-owning row
    /// types use — and <c>e50bf94</c> then added a fifth caption path after it: a button row's
    /// caption is the TMP the cloned vanilla <c>SimpleStunButton</c> brought with it, handed back
    /// by <c>SetButtonText</c>, which writes no overflow mode at all. So the one row type whose
    /// caption this code did not create was the one row type the rule did not reach, and it is
    /// reachable only when the vanilla clone succeeds, i.e. on the live client and in no headless
    /// path. Asserting the rule at the seam every measured caption goes through — <see
    /// cref="FitLabel"/>, called on every row on every layout pass — is what makes "every row type"
    /// true by construction rather than by remembering to write it in the next factory.
    /// </para>
    ///
    /// <para>
    /// This deliberately does not reach a <c>TMP_InputField</c>'s own text component, which is
    /// clipped by a <c>RectMask2D</c> and must keep <c>Overflow</c> so the player's typing is not
    /// ellipsized: that component is never a row's caption.
    /// </para>
    /// </summary>
    private static void ClampCaptionText(GameObject? label)
    {
        if (label == null) return;
        try
        {
            var tmp = label.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
        }
        catch { /* the row is still clamped; only the text may overflow it */ }
    }

    private static float MeasuredRowHeight(GameObject? go, float width)
    {
        if (go == null) return RowHeight;
        try
        {
            var tmp = go.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return RowHeight;

            NormaliseLabelRect(go);
            var rt = go.GetComponent<RectTransform>();
            if (rt != null) rt.sizeDelta = new Vector2(width, RowHeight);
            tmp.ForceMeshUpdate();
            return PanelLayout.ClampRowHeight(tmp.preferredHeight);
        }
        catch
        {
            return RowHeight;
        }
    }

    /// <summary>
    /// Put a caption's rect into the one shape the measurement understands — center-anchored,
    /// where <c>sizeDelta</c> <i>is</i> the rect's size.
    ///
    /// <para>
    /// Labels this code makes are already like that (<see cref="MakeLabel"/> goes through
    /// <see cref="PlaceRect"/>). A caption inside a cloned vanilla button is not: it is typically
    /// stretched to its parent, and on a stretched rect <c>sizeDelta</c> is an inset from the
    /// anchors, so writing a 400 into it makes the rect 400 px <i>wider than the button</i>
    /// instead of 400 px wide. TMP would then wrap against a rect nothing on screen has, and the
    /// height measured back would describe a layout that never happens. Nothing is touched when
    /// the rect is already center-anchored, so the four label-owning row types are unaffected.
    /// </para>
    /// </summary>
    private static void NormaliseLabelRect(GameObject go)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            var min = rt.anchorMin;
            var max = rt.anchorMax;
            if (min.x == 0.5f && min.y == 0.5f && max.x == 0.5f && max.y == 0.5f) return;
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
        }
        catch { /* the measurement below still answers something usable */ }
    }

    /// <summary>
    /// Whether <paramref name="sprite"/> carries a 9-slice border. <c>Image.Type.Sliced</c>
    /// on a border-less sprite is not an error and logs nothing: it just stretches, which is
    /// how a 640x360 frame became a 640x819 oval once the panel started sizing itself to its
    /// rows.
    /// </summary>
    private static bool HasSliceBorder(Sprite sprite)
    {
        try
        {
            var border = sprite.border;
            return border.x > 0f || border.y > 0f || border.z > 0f || border.w > 0f;
        }
        catch
        {
            // Unreadable: assume it cannot slice, and take the flat ground.
            return false;
        }
    }

    private static void PlaceRect(GameObject go, Vector2 anchored, float width, float height)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchored;
            rt.sizeDelta = new Vector2(width, height);
        }
        catch { /* */ }
    }

    private static void Stretch(GameObject go)
    {
        try
        {
            var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
        catch { /* */ }
    }

    private static T? TryAdd<T>(GameObject go) where T : Component
    {
        try { return go.AddComponent<T>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: AddComponent<" + typeof(T).Name + ">: " + e.Message);
            return null;
        }
    }

    private static bool TryDestroyImmediate(GameObject? go) => TryDestroy(go, immediate: true);

    /// <summary>
    /// Destroy <paramref name="go"/> and say <b>which of three things</b> happened.
    /// <c>internal</c> rather than private because <see cref="VanillaSiteRuntime"/> destroys
    /// objects parented into the vanilla tree and must use this one rather than a second, quieter
    /// copy of it (R60 finding 1): the warn line and the deferred fallback are the whole
    /// difference between a failed destroy an operator can see and a container welded into the
    /// game's hierarchy.
    ///
    /// <para><b>Why three.</b> <c>DestroyImmediate</c> is the one Unity destroy that throws rather
    /// than deferring, and the fallback it throws into — a plain <c>Destroy</c> — is the branch
    /// most likely to work. Reporting that as a failure told the caller to keep the object, warn
    /// that the destroy "did not take", and try the same throwing call again every frame,
    /// queueing another <c>Destroy</c> each time (R61 finding 7). <see cref="UiDestroyVerdict.Deferred"/>
    /// says what is true instead: the object is Unity's now and is gone at the end of the frame,
    /// so the caller follows it rather than fighting it.</para>
    ///
    /// <para>An object that was already gone reports <see cref="UiDestroyVerdict.Failed"/>: there
    /// was no destroy, and the callers that count destroys must not count it.</para>
    /// </summary>
    internal static UiDestroyVerdict DestroyWithVerdict(GameObject? go, bool immediate)
    {
        if (!Alive(go) || go == null) return UiDestroyVerdict.Failed;
        try
        {
            if (!immediate)
            {
                UnityEngine.Object.Destroy(go);
                return UiDestroyVerdict.Deferred;
            }

            UnityEngine.Object.DestroyImmediate(go);
            return UiDestroyVerdict.Destroyed;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: " + (immediate ? "DestroyImmediate" : "Destroy") + ": " + e.Message);
            if (immediate)
            {
                try
                {
                    UnityEngine.Object.Destroy(go);
                    return UiDestroyVerdict.Deferred;
                }
                catch { /* */ }
            }

            return UiDestroyVerdict.Failed;
        }
    }

    /// <summary>
    /// The bool face of <see cref="DestroyWithVerdict"/>, for the callers that only want to know
    /// whether the object is going. A deferred destroy counts as one — it is queued and it lands
    /// this frame — which is the half of R61 finding 7 those callers were getting wrong too.
    /// </summary>
    internal static bool TryDestroy(GameObject? go, bool immediate) =>
        DestroyWithVerdict(go, immediate) != UiDestroyVerdict.Failed;

    internal static bool Alive(UnityEngine.Object? obj)
    {
        try { return UnityAlive.Of(obj); }
        catch { return false; }
    }

    private static string SafeName(GameObject? go)
    {
        try { return go != null ? (go.name ?? "") : ""; }
        catch { return ""; }
    }

    private static void ForEach<T>(Action<T> body) where T : Component
    {
        try
        {
            var type = Il2CppType.Of<T>();
            if (type == null) return;
            var arr = UnityEngine.Object.FindObjectsOfType(type, true);
            if (arr == null) return;
            for (int i = 0; i < arr.Length; i++)
            {
                T? c = null;
                try { c = arr[i] == null ? null : arr[i].TryCast<T>(); }
                catch { continue; }
                if (c == null) continue;
                try { body(c); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ForEach " + typeof(T).Name + ": " + e.Message); }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: FindObjectsOfType<" + typeof(T).Name + ">: " + e.Message);
        }
    }

    /// <summary>
    /// Every object of a type in the process, assets and unloaded prefabs included — the sweep the
    /// <c>EventSystem</c> question actually wants, because "there is an EventSystem prefab and no
    /// live instance" and "there is no EventSystem at all" are different answers with different
    /// fixes. Falls back to <see cref="ForEach{T}"/>'s scene sweep if the <c>Resources</c> overload
    /// cannot be bound, and says which one answered rather than leaving the caller to assume.
    /// </summary>
    private static string ForEachAll<T>(Action<T> body) where T : Component
    {
        try
        {
            var type = Il2CppType.Of<T>();
            if (type != null)
            {
                var arr = Resources.FindObjectsOfTypeAll(type);
                if (arr != null)
                {
                    for (int i = 0; i < arr.Length; i++)
                    {
                        T? c = null;
                        try { c = arr[i] == null ? null : arr[i].TryCast<T>(); }
                        catch { continue; }
                        if (c == null) continue;
                        try { body(c); }
                        catch { /* one unreadable instance must not end the sweep */ }
                    }

                    return "FindObjectsOfTypeAll";
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: Resources.FindObjectsOfTypeAll<" + typeof(T).Name + "> threw (" + e.Message
                + "); falling back to the scene sweep, which cannot see unloaded prefabs.");
        }

        ForEach(body);
        return "FindObjectsOfType(scene)";
    }

    // ---- the interaction probe (job FUI30) ----------------------------------------------------
    //
    // "Is uGUI interaction alive on this screen" had no answer before this, and the deny panel on
    // the ConnectingView disconnect screen is what made that expensive: nine correct rows, a Close
    // button that drew nothing and could not be clicked, a layout line that counted the row and a
    // hover line that fired — because the hover line is HoverCapture's own rect arithmetic and is
    // true whether or not an EventSystem exists. Every read is here; every judgment about what the
    // reads mean is in UiInteractionReport, which is game-free and has tests. These reads have
    // none, and cannot: nothing headless produces an EventSystem, a Canvas or a Graphic.

    /// <summary>
    /// Environment switch for the optional pointer read. Off by default and read once per Core
    /// generation. See <see cref="ProbePointerOverUi"/> for what it costs and why it is not free.
    /// </summary>
    private const string ProbePointerEnvVar = "PROGENITOR_UI_PROBE_POINTER";

    /// <summary>
    /// How many <c>Graphic</c>s of one vanilla canvas the overlap read will look at before it stops
    /// and answers <see cref="Reading.Unreadable"/> (job FUI31). The in-world sweep of evidence 88
    /// counted 1,939 <c>Image</c>s across 28 canvases, so an unbounded walk of a HUD canvas is a
    /// real cost on a probe that runs on every panel show. The cap answers "I stopped looking"
    /// rather than "there is nothing there", which is the whole point of <see cref="Reading"/>
    /// having three states.
    /// </summary>
    private const int MaxProbedGraphicsPerCanvas = 512;

    private static bool _probePointerChecked;
    private static bool _probePointerEnabled;

    /// <summary>
    /// The panel whose probe is owed, or null. Armed by <see cref="Show"/> and spent on the next
    /// <see cref="ReadPointer"/>, which is <see cref="HoverCapture"/>'s per-frame pass — so the
    /// readout is taken a frame <i>after</i> the panel was shown.
    ///
    /// <para>
    /// The deferral is the same rule as the scrollbar read-back, for the same reason and with the
    /// same evidence behind it: a canvas that has not had an update yet has a zero rect, which is
    /// exactly why the deny panel's own layout line said <c>ruler=constant</c>. A probe taken
    /// inside <c>Show</c> would report the canvas before Unity had sized it and the row states
    /// before <c>OnEnable</c> had run, and would look like a reading of the screen the player is
    /// looking at.
    /// </para>
    ///
    /// <para>
    /// One slot, deliberately: two shows inside one frame supersede each other and only the second
    /// is probed. This is a diagnostic and the readout is mostly about the <i>screen</i>, which both
    /// panels share; a queue would buy one more row block per frame at the cost of a structure that
    /// can leak a panel reference across a Core swap.
    /// </para>
    /// </summary>
    private UnityPanel? _probeOwed;

    /// <summary>
    /// The probe reads every <c>EventSystem</c> in the process (<c>Resources.FindObjectsOfTypeAll</c>)
    /// and logs a line per row: ~20 ms per show in a loaded world, and a Mods tab switch shows two
    /// or three panels. Its answer is about the screen, so a panel title is probed once per current
    /// <c>EventSystem</c>; a new screen (or a new EventSystem) probes it again.
    /// </summary>
    private void ArmProbe(UnityPanel panel)
    {
        string key = (panel.Text ?? "") + "|" + CurrentEventSystemId();
        if (!ProbedScreens.Add(key)) return;
        _probeOwed = panel;
    }

    private static readonly HashSet<string> ProbedScreens = new(StringComparer.Ordinal);

    private static string CurrentEventSystemId()
    {
        try
        {
            var current = EventSystem.current;
            return Alive(current) && current != null ? current.GetInstanceID().ToString(CultureInfo.InvariantCulture) : "none";
        }
        catch { return "unreadable"; }
    }

    // ---- the deferred re-measure (job FUI32) --------------------------------------------------
    //
    // FDOC5 owed one live check: the layout line had to read ruler=canvas. It never did, on any
    // panel, on any screen -- so every panel was sized from FallbackScreenHeight and only looked
    // right on a 1080-tall display. The canvas rect is genuinely zero at layout time and not by
    // accident: CreatePanel gives each panel a canvas of its own, SettingsPanel.Open fills and
    // shows it in the same Plugin.Frame tick, and a root ScreenSpaceOverlay canvas is sized by
    // Unity's canvas update in PostLateUpdate, after every Update has run. So the panel is laid
    // out with what is known and measured again a frame later, which is FUI30's precedent for its
    // interaction probe and for the same reason.
    //
    // Panels rather than one slot, unlike the probe: this is not a diagnostic about the screen,
    // it is the size of a specific panel, and two panels shown in one frame are two panels that
    // would otherwise keep the fallback forever.

    /// <summary>
    /// How <see cref="UiSettleGate.Reset"/> gives the frame pass back — a field rather than a
    /// lambda per arm, so the gate holds one delegate for the life of the process and an
    /// uninstall cannot leave a subscription behind a cleared latch.
    /// </summary>
    private static readonly Action SettleDisarm = () => Plugin.Frame -= SettlePump;

    /// <summary>
    /// Take the frame pass for <paramref name="panel"/> until its canvas answers. Idempotent: a
    /// panel already owed is not added twice, and the <c>Plugin.Frame</c> subscription is taken
    /// once for all of them.
    ///
    /// <para>The owed list itself is <see cref="UiSettleGate"/> (job FUI33): it is game-free, so
    /// the four transitions this pass has — armed, settled, re-armed by its own re-layout, given
    /// up on — are tested rather than pinned.</para>
    /// </summary>
    private static void ArmSettle(UnityPanel panel)
    {
        if (!UiSettleGate.Arm(panel, SettleDisarm)) return;
        try
        {
            // On its own line, and pinned there: a re-measure driven by anything but Plugin.Frame
            // -- ReadPointer, say, which is what FUI30's probe uses -- would never run for a panel
            // whose plugin did not register for the hover pass, which is every panel this job
            // measured.
            Plugin.Frame += SettlePump;
        }
        catch (Exception e)
        {
            UiSettleGate.SubscribeFailed();
            // The layout line above already said settle=armed. If the subscription failed, that
            // line is now a claim nothing will keep, so say so rather than leave it standing.
            Plugin.Logger?.LogWarning(
                "Ui: panel re-measure subscribe threw (" + e.Message
                + "); the panel keeps the fallback size its layout line reported.");
        }
    }

    /// <summary>Forget a panel that was hidden or released before its canvas ever answered.</summary>
    private static void DropSettle(UnityPanel panel) => UiSettleGate.Drop(panel);

    /// <summary>
    /// One frame of every owed panel's re-measure. Runs off <c>Plugin.Frame</c> and unsubscribes
    /// itself as soon as nothing is owed, so the common case — no panel open — costs nothing. The
    /// walk, and its refusal to drop a panel its own tick re-armed, are
    /// <see cref="UiSettleGate.Pump"/>'s (job FUI33).
    /// </summary>
    private static void SettlePump()
    {
        if (!UiSettleGate.Pump()) return;
        try { Plugin.Frame -= SettlePump; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel re-measure unsubscribe threw: " + e.Message); }
    }

    /// <summary>Spend an armed probe, if one is owed. Never throws; never runs twice for one show.</summary>
    private void FireArmedProbe(string when)
    {
        var owed = _probeOwed;
        if (owed == null) return;
        _probeOwed = null;
        FireProbe(owed, when);
    }

    private static void FireProbe(UnityPanel panel, string when)
    {
        try
        {
            var reading = ReadInteraction(panel, when);
            var verdict = UiInteractionReport.Classify(reading);
            string text = UiInteractionReport.Describe(reading);
            // A dead panel is a console warning; everything else the probe reads is a file-only diagnostic.
            if (UiInteractionReport.IsWarning(verdict)) Plugin.Logger?.LogWarning(text);
            else Plugin.Logger?.LogDiag(text);

            var overlayCanvas = OverlayCanvas(panel);
            panel.ForEachRow((go, kind, source) =>
            {
                // A label is not a thing the player operates, and a panel of thirty descriptions
                // would bury the one row that is.
                if (string.Equals(kind, "label", StringComparison.Ordinal)) return;
                var row = ReadRow(panel, overlayCanvas, go, kind, source);
                // Row verdicts (an inactive filtered row, a note with no click) are file-only: dozens per show.
                Plugin.Logger?.LogDiag(UiInteractionReport.DescribeRow(row));
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: interaction probe threw: " + e.Message);
        }
    }

    private static Canvas? OverlayCanvas(UnityPanel panel)
    {
        try { return panel.Overlay == null ? null : panel.Overlay.GetComponent<Canvas>(); }
        catch { return null; }
    }

    private static UiInteractionReading ReadInteraction(UnityPanel panel, string when)
    {
        int instances = 0;
        int active = 0;
        var detail = new System.Text.StringBuilder();
        var readable = Reading.Unreadable;
        bool currentExists = false;
        string currentName = "";
        string module = "";

        string via = "";
        try
        {
            via = ForEachAll<EventSystem>(es =>
            {
                instances++;
                string state;
                try
                {
                    bool on = es.isActiveAndEnabled;
                    if (on) active++;
                    state = on ? "on" : "off";
                }
                catch { state = "?"; }

                if (detail.Length > 0) detail.Append(' ');
                detail.Append(SafeName(es.gameObject)).Append(':').Append(state);
            });

            var current = EventSystem.current;
            if (Alive(current) && current != null)
            {
                currentExists = true;
                currentName = SafeName(current.gameObject);
                try
                {
                    var m = current.currentInputModule;
                    if (Alive(m) && m != null) module = SafeTypeName(m);
                }
                catch { /* an unreadable module reports as none, which the verdict names */ }
            }

            readable = Reading.Yes;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: interaction probe EventSystem read threw: " + e.Message);
        }

        var canvas = OverlayCanvas(panel);
        var raycaster = ReadRaycaster(panel, out var raycasterEnabled);

        FindCanvasAbove(
            canvas, PanelScreenRect(panel),
            out bool aboveExists, out string aboveName, out int aboveOrder, out Reading aboveOverlaps);

        return new UiInteractionReading
        {
            When = when,
            Panel = panel.Text,
            Readable = readable,
            Instances = instances,
            ActiveInstances = active,
            InstanceDetail = (via.Length == 0 ? "" : "via " + via + "; ") + detail,
            CurrentExists = currentExists,
            CurrentName = currentName,
            InputModule = module,
            PointerOverUi = ProbePointerOverUi(currentExists),
            CanvasName = SafeName(panel.Overlay),
            CanvasSortingOrder = ReadInt(() => canvas == null ? 0 : canvas.sortingOrder),
            CanvasOverrideSorting = ReadFlag(() => canvas != null && canvas.overrideSorting),
            CanvasRenderMode = canvas == null ? "" : ReadText(() => canvas.renderMode.ToString()),
            CanvasEnabled = ReadFlag(() => canvas != null && canvas.enabled),
            RaycasterPresent = canvas == null ? Reading.Unreadable : (raycaster ? Reading.Yes : Reading.No),
            RaycasterEnabled = raycasterEnabled,
            BlockingCanvasGroups = CountBlockingCanvasGroups(panel),
            CanvasAboveExists = aboveExists,
            CanvasAboveName = aboveName,
            CanvasAboveOrder = aboveOrder,
            CanvasAboveOverlaps = aboveOverlaps,
        };
    }

    private static bool ReadRaycaster(UnityPanel panel, out Reading enabled)
    {
        enabled = Reading.Unreadable;
        try
        {
            var caster = panel.Overlay == null ? null : panel.Overlay.GetComponent<GraphicRaycaster>();
            if (!Alive(caster) || caster == null) return false;
            enabled = ReadFlag(() => caster.enabled);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// <c>EventSystem.current.IsPointerOverGameObject()</c>, behind
    /// <see cref="ProbePointerEnvVar"/> and off by default.
    ///
    /// <para>
    /// It is the one read here that asks the input module to answer rather than reading a field,
    /// and it is the only line of the probe that could plausibly cost the process something on a
    /// screen where the module is half torn down. The rest of the readout already separates "no
    /// EventSystem" from "no module" from "our raycaster is off" without it, so it is opt-in:
    /// set <c>PROGENITOR_UI_PROBE_POINTER=1</c> in the client's environment before launching. It
    /// also answers only "a raycast landed on something", never "on us", which is why
    /// <see cref="UiInteractionReport.Classify"/> does not consult it.
    /// </para>
    /// </summary>
    private static Reading ProbePointerOverUi(bool currentExists)
    {
        if (!currentExists) return Reading.Unreadable;
        if (!_probePointerChecked)
        {
            _probePointerChecked = true;
            try
            {
                string? v = Environment.GetEnvironmentVariable(ProbePointerEnvVar);
                _probePointerEnabled = v != null && v.Trim() == "1";
            }
            catch { _probePointerEnabled = false; }
        }

        if (!_probePointerEnabled) return Reading.Unreadable;
        return ReadFlag(() => EventSystem.current.IsPointerOverGameObject());
    }

    /// <summary>
    /// A raycastable canvas that is not ours and sorts at or above ours — the one a click over the
    /// panel could go to instead — and, since job FUI31, <b>whether it actually can</b>.
    ///
    /// <para>
    /// <b>Why order alone was never the question.</b> Read from this build's own metadata:
    /// <c>GraphicRegistry</c> keys its raycastable set by canvas
    /// (<c>Dictionary&lt;Canvas, IndexedSet&lt;Graphic&gt;&gt; m_RaycastableGraphics</c>)
    /// and <c>GraphicRaycaster.Raycast</c> hands
    /// its private worker only that canvas's own registered graphics.
    /// Sorting order enters solely through <c>sortOrderPriority</c> / <c>renderOrderPriority</c>,
    /// which <c>EventSystem</c>'s <c>s_RaycastComparer</c>
    /// uses to <em>sort hits</em>. So a canvas above ours
    /// takes a click exactly where one of its own enabled raycast targets contains the pointer and
    /// <b>nowhere else</b>: order breaks ties between hits, it never produces one.
    /// </para>
    ///
    /// <para>
    /// <b>The pick prefers an overlapping canvas over a higher one</b>, so the single
    /// <c>overlapsPanel=no</c> on the line means <em>nothing at or above us overlaps</em> rather
    /// than "this particular one does not" — which is the statement worth printing. Where the
    /// overlap cannot be read the reading stays <see cref="Reading.Unreadable"/> and job FUI30's
    /// original sentence is kept: a canvas above ours is not a cause until the rects were looked at.
    /// </para>
    /// </summary>
    private static void FindCanvasAbove(
        Canvas? ours, ScreenRect panelRect,
        out bool exists, out string name, out int order, out Reading overlaps)
    {
        int oursOrder = ReadInt(() => ours == null ? UiOwnership.OverlaySorting : ours.sortingOrder);
        bool found = false;
        string bestName = "";
        int best = 0;
        bool hitFound = false;
        string hitName = "";
        int hitBest = 0;
        bool anyUnreadable = !panelRect.IsKnown;
        ForEach<Canvas>(c =>
        {
            try
            {
                if (ours != null && SamePointer(c, ours)) return;
                if (UiOwnership.IsOwned(SafeName(c.gameObject))) return;
                if (!c.isActiveAndEnabled) return;
                if (c.GetComponent<GraphicRaycaster>() == null) return;
                int o = c.sortingOrder;
                if (o < oursOrder) return;
                if (!found || o > best)
                {
                    found = true;
                    best = o;
                    bestName = SafeName(c.gameObject);
                }

                if (!panelRect.IsKnown) return;
                Reading hit = CanvasCoversPanel(c, panelRect);
                if (hit == Reading.Unreadable) anyUnreadable = true;
                if (hit != Reading.Yes) return;
                if (hitFound && o <= hitBest) return;
                hitFound = true;
                hitBest = o;
                hitName = SafeName(c.gameObject);
            }
            catch { anyUnreadable = true; }
        });

        exists = found;
        name = hitFound ? hitName : bestName;
        order = hitFound ? hitBest : best;
        overlaps = hitFound ? Reading.Yes
            : !found ? Reading.No
            : anyUnreadable ? Reading.Unreadable
            : Reading.No;
    }

    /// <summary>
    /// Highest native (non-owned) canvas sorting order strictly below consent.
    /// Used to place the Mods band and to report whether the title canvas occupies it.
    /// Modal canvases at or above OverlaySorting (e.g. 9000) are ignored.
    /// Owned overlay canvases are named <c>overlay-N</c>; a name-only prefix
    /// check reports our Panels order (4999) as native.
    /// </summary>
    internal static int NativeMaxSortingBelowConsent()
    {
        var orders = new List<int>();
        ForEach<Canvas>(c =>
        {
            try
            {
                if (c == null || !c.isActiveAndEnabled) return;
                if (IsOurs(c.gameObject)) return;
                if (!ModsCanvasBand.CountTowardNativeMax(SafeName(c.gameObject), AncestorNames(c.transform)))
                    return;
                orders.Add(c.sortingOrder);
            }
            catch { /* one unreadable canvas must not hide the rest */ }
        });
        return ModsCanvasBand.NativeMaxBelowOverlay(orders);
    }

    private static List<string>? AncestorNames(Transform? start)
    {
        if (start == null) return null;
        var names = new List<string>();
        var t = start.parent;
        int guard = 0;
        while (t != null && guard++ < 64)
        {
            names.Add(SafeName(t.gameObject));
            t = t.parent;
        }

        return names.Count == 0 ? null : names;
    }

    /// <summary>
    /// Does <paramref name="canvas"/> have an enabled raycast target of its own whose screen rect
    /// overlaps <paramref name="panelRect"/>? This is the read job FUI30 declared it was not
    /// taking, and it is the one that decides whether <c>canvasAbove=</c> is a hazard or a
    /// coincidence of geometry.
    ///
    /// <para>
    /// <b>"Of its own" is load-bearing.</b> A graphic under a nested <c>Canvas</c> registers to
    /// <em>that</em> canvas, so <c>canvas</c>'s raycaster never sees it — the same fact
    /// <see cref="UiRowVerdict.ForeignCanvas"/> exists for, read here in the other direction. The
    /// <c>g.canvas</c> test is therefore not a refinement: counting a nested canvas's graphics
    /// would report a canvas as covering us that cannot hit anything at all.
    /// </para>
    ///
    /// <para>
    /// Bounded by <see cref="MaxProbedGraphicsPerCanvas"/> because a HUD canvas can carry hundreds
    /// of graphics; hitting the cap reports <see cref="Reading.Unreadable"/> rather than
    /// <see cref="Reading.No"/>, since "I stopped looking" is not "there is nothing there".
    /// </para>
    ///
    /// <para>
    /// <b>Job FUI33 (R48 finding 1): the reads are here and the judgment is not.</b> Every value
    /// this method takes off a graphic goes into a
    /// <see cref="UiInteractionReport.CoveringGraphicReading"/> and
    /// <see cref="UiInteractionReport.TakesClickOver"/> decides — which is what gives the decision
    /// tests, and what made it obvious that the four reads FUI31 promoted to a verdict were not the
    /// filters <c>GraphicRaycaster</c> applies. The added ones are <c>CanvasGroup.blocksRaycasts</c>
    /// over the graphic (the same walk <see cref="CountBlockingCanvasGroups"/> does over our own
    /// panel, generalized to any transform) and the renderer's <c>cull</c>/<c>depth</c>. Alpha is
    /// read into the reading and judged by nothing: <c>GraphicRaycaster</c> never consults
    /// <c>Graphic.color.a</c>, and FUI33's alpha filter hid the canonical transparent click blocker
    /// (job FUI34, R49 finding 1).
    /// </para>
    ///
    /// <para>
    /// The one predicate this method applies before building the reading is the rect overlap, and
    /// it applies it by calling the same <see cref="UiInteractionReport.RectsOverlap"/> that
    /// <see cref="UiInteractionReport.TakesClickOver"/> ends on — a pre-pass over the shared
    /// predicate, not a second opinion. It exists so the parent-chain walk is paid for only by
    /// graphics that are actually over the panel (job FUI34, R49 finding 5).
    /// </para>
    ///
    /// <para>
    /// <c>ScreenRectOf</c> is exact for a <c>ScreenSpaceOverlay</c> canvas and meaningless for a
    /// camera- or world-space one; only canvases at or above <see cref="UiOwnership.OverlaySorting"/>
    /// reach here and the game's non-overlay canvases sort far below that, so the case is not
    /// reachable today — but it is the assumption this read rests on.
    /// </para>
    /// </summary>
    private static Reading CanvasCoversPanel(Canvas canvas, ScreenRect panelRect)
    {
        try
        {
            var graphics = canvas.gameObject.GetComponentsInChildren<Graphic>(false);
            if (graphics == null) return Reading.Unreadable;
            int examined = 0;
            for (int i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null) continue;
                if (++examined > MaxProbedGraphicsPerCanvas) return Reading.Unreadable;
                try
                {
                    // Cheap and decisive first, so a HUD canvas of hundreds of labels costs two
                    // reads each rather than a parent walk each. Job FUI34 (R49 finding 5) put the
                    // geometry back in front of the reading: the rect test is the most selective
                    // read available, and building the struct eagerly meant every raycast target
                    // on the canvas paid for a 64-level parent walk before anything asked whether
                    // it was even near the panel. The reading is therefore built in two steps —
                    // geometry, then the corroborators — so the walk is only reached by a graphic
                    // that is over the panel. Field order inside a literal is evaluation order.
                    if (!g.enabled || !g.raycastTarget) continue;
                    var own = g.canvas;
                    if (own == null || !SamePointer(own, canvas)) continue;
                    var go = g.gameObject;
                    var rt = go.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    var geometry = new UiInteractionReport.CoveringGraphicReading
                    {
                        Enabled = true,
                        RaycastTarget = true,
                        OwnCanvas = true,
                        Rect = ScreenRectOf(rt),
                    };

                    if (!UiInteractionReport.RectsOverlap(panelRect, geometry.Rect)) continue;

                    var reading = geometry with
                    {
                        Alpha = g.color.a,
                        Culled = GraphicCulled(g),
                        Depth = GraphicDepth(g),
                        NonBlockingCanvasGroups = CountBlockingCanvasGroups(go.transform),
                    };

                    if (UiInteractionReport.TakesClickOver(reading, panelRect)) return Reading.Yes;
                }
                catch { return Reading.Unreadable; }
            }

            return Reading.No;
        }
        catch
        {
            return Reading.Unreadable;
        }
    }

    /// <summary>The panel's own frame in screen pixels, for the probe. Never throws.</summary>
    private static ScreenRect PanelScreenRect(UnityPanel panel)
    {
        try
        {
            var go = panel.GameObject;
            if (go == null) return ScreenRect.Unknown;
            var rt = go.GetComponent<RectTransform>();
            return rt == null ? ScreenRect.Unknown : ScreenRectOf(rt);
        }
        catch
        {
            return ScreenRect.Unknown;
        }
    }

    /// <summary>
    /// <c>Graphic.canvasRenderer.cull</c>, defensively. A read that throws answers <c>false</c>
    /// rather than propagating: cull and depth corroborate the decision in
    /// <see cref="UiInteractionReport.TakesClickOver"/>, they do not carry it, and one unreadable
    /// corroborator must not turn a canvas whose primary reads all answered into
    /// <see cref="Reading.Unreadable"/>.
    /// </summary>
    private static bool GraphicCulled(Graphic g)
    {
        try
        {
            var cr = g.canvasRenderer;
            return cr != null && cr.cull;
        }
        catch { return false; }
    }

    /// <summary>
    /// <c>Graphic.depth</c>, defensively; <c>0</c> (drawn) when it cannot be read, for the reason
    /// given on <see cref="GraphicCulled"/>.
    /// </summary>
    private static int GraphicDepth(Graphic g)
    {
        try { return g.depth; }
        catch { return 0; }
    }

    /// <summary>
    /// <c>CanvasGroup</c>s over the panel with <c>blocksRaycasts</c> off. One is enough to make
    /// every graphic under it visible and untouchable, and it is invisible in a screenshot.
    /// </summary>
    private static int CountBlockingCanvasGroups(UnityPanel panel) =>
        CountBlockingCanvasGroups(panel.GameObject == null ? null : panel.GameObject.transform);

    /// <summary>
    /// The same walk over any transform (job FUI33, R48 finding 1). The vanilla graphic that a
    /// canvas above ours might take a click with is filtered by <c>blocksRaycasts</c> exactly as
    /// our own panel is, and the framework already knew how to count that — for one side only.
    ///
    /// <para><b>Job FUI34 (R49 finding 1) added the <c>ignoreParentGroups</c> stop, which is
    /// right, and an <c>enabled</c> test, which is wrong on this build; job FUI35 (R50 finding 1)
    /// removed the latter.</b> <c>Graphic.Raycast</c> stops consulting groups after the first one
    /// with <c>ignoreParentGroups</c>, and it reads that flag off the component itself, so a
    /// disabled group carrying it still ends the walk. But a disabled group is <b>not</b> skipped:
    /// <c>CanvasGroup.IsRaycastLocationValid</c> is <c>return blocksRaycasts;</c> — no
    /// <c>enabled</c> term, in every Unity version
    /// (<c>UnityCsReference</c>, <c>Modules/UI/ScriptBindings/CanvasGroup.bindings.cs</c>) — and
    /// the <c>enabled</c> skip lives only in Unity 6's rewritten <c>Graphic.Raycast</c>
    /// (<c>Unity-Technologies/uGUI</c>, branch <c>6000.0</c>, <c>Runtime/UI/Core/Graphic.cs</c>).</para>
    ///
    /// <para><b>Metadata-verified</b> (client metadata, job FUI35):
    /// <c>UnityEngine.CanvasGroup.IsRaycastLocationValid</c> and <c>get_blocksRaycasts</c> share
    /// one method address, i.e. the body <em>is</em> the getter;
    /// <c>UnityEngine.UI.Graphic</c> declares only <c>Raycast(Vector2, Camera)</c>, with no Unity 6
    /// <c>Raycast(Vector2, Camera, bool)</c> overload, and carries
    /// <c>private Vector4 m_RaycastPadding; // 0x3C</c>, the <c>com.unity.ugui</c> 1.0.0 layout.
    /// Those three facts are what settle it: on this build a disabled group with
    /// <c>blocksRaycasts</c> off still blocks, and it is counted.</para>
    ///
    /// <para><b>Inferred, not verified</b> (job FCFG5, R52 finding 8): that the pre-6000
    /// <c>Graphic.Raycast</c> fetches its filters with <c>GetComponents</c>, disabled components
    /// included, and calls <c>IsRaycastLocationValid</c> on each. That is the shape of the uGUI
    /// source for this version, and it is the reason the counted group is reached at all — but
    /// the native <c>Graphic.Raycast</c> has not been examined, so nothing here rests
    /// on it. The rule above stands on the shared method address alone.</para>
    ///
    /// <para>Still not modeled: <c>Graphic.Raycast</c> also ends the walk at a parent
    /// <c>Canvas</c> with <c>overrideSorting</c>. See
    /// <see cref="UiInteractionReport.TakesClickOver"/>'s direction list.</para>
    /// </summary>
    private static int CountBlockingCanvasGroups(Transform? from)
    {
        int n = 0;
        try
        {
            var t = from;
            int guard = 0;
            while (t != null && guard++ < 64)
            {
                bool stop = false;
                try
                {
                    var group = t.gameObject.GetComponent<CanvasGroup>();
                    if (Alive(group) && group != null)
                    {
                        // No `enabled` test: on this build IsRaycastLocationValid is
                        // `return blocksRaycasts;` (see the summary for the dump evidence), so a
                        // disabled group with blocksRaycasts off still blocks and still counts.
                        stop = group.ignoreParentGroups;
                        if (!group.blocksRaycasts) n++;
                    }
                }
                catch { /* */ }

                if (stop) break;
                t = t.parent;
            }
        }
        catch { /* */ }

        return n;
    }

    private static UiRowReading ReadRow(
        UnityPanel panel, Canvas? overlay, GameObject go, string kind, ControlSource source)
    {
        var readable = Reading.Unreadable;
        int graphics = 0;
        int visible = 0;
        int hittable = 0;
        int foreign = 0;
        int nested = 0;
        string click = "";

        try
        {
            var all = go.GetComponentsInChildren<Graphic>(true);
            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    var g = all[i];
                    if (g == null) continue;
                    graphics++;
                    bool on;
                    try { on = g.enabled; }
                    catch { continue; }
                    if (on)
                    {
                        try { if (g.color.a > UiInteractionReport.VisibleAlpha) visible++; }
                        catch { /* */ }
                        try { if (g.raycastTarget) hittable++; }
                        catch { /* */ }
                    }

                    try
                    {
                        var c = g.canvas;
                        if (overlay != null && (c == null || !SamePointer(c, overlay))) foreign++;
                    }
                    catch { /* */ }
                }
            }

            var canvases = go.GetComponentsInChildren<Canvas>(true);
            if (canvases != null) nested = canvases.Length;

            if (FindIn<SimpleStunButton>(go) != null) click = "SimpleStunButton";
            else if (FindIn<Button>(go) != null) click = "Button";
            else if (FindIn<Toggle>(go) != null) click = "Toggle";
            else if (FindIn<Slider>(go) != null) click = "Slider";
            else if (FindIn<TMP_InputField>(go) != null) click = "TMP_InputField";

            readable = Reading.Yes;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: interaction probe row read threw: " + e.Message);
        }

        return new UiRowReading
        {
            Panel = panel.Text,
            RowName = SafeName(go),
            Kind = kind,
            Source = source,
            Readable = readable,
            ActiveSelf = ReadFlag(() => go.activeSelf),
            ActiveInHierarchy = ReadFlag(() => go.activeInHierarchy),
            Graphics = graphics,
            VisibleGraphics = visible,
            RaycastGraphics = hittable,
            ForeignCanvasGraphics = foreign,
            NestedCanvases = nested,
            Click = click,
        };
    }

    private static Reading ReadFlag(Func<bool> read)
    {
        try { return read() ? Reading.Yes : Reading.No; }
        catch { return Reading.Unreadable; }
    }

    private static int ReadInt(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private static string ReadText(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return ""; }
    }

    private static string SafeTypeName(UnityEngine.Object obj)
    {
        try
        {
            var t = obj.GetIl2CppType();
            return t == null ? "" : (t.Name ?? "");
        }
        catch
        {
            try { return obj.GetType().Name; }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Identity for two interop wrappers. The native pointer, not <c>==</c>: two wrappers over one
    /// IL2CPP object are two managed objects, and Unity's own overload additionally lies about a
    /// destroyed one.
    /// </summary>
    private static bool SamePointer(UnityEngine.Object? a, UnityEngine.Object? b)
    {
        try
        {
            if (a == null || b == null) return false;
            return a.Pointer == b.Pointer;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// One row of a panel: the object that occupies the row, and the object carrying the text
    /// whose wrapped height decides how tall the row is. They are the same object for a plain
    /// label, a child for a toggle/slider/field, and a vanilla clone's own TMP for a button.
    /// </summary>
    private readonly struct PanelRow
    {
        public PanelRow(GameObject go, GameObject? label, bool hasControl, string kind, ControlSource source,
            GameObject? control, MenuRowPin pin, bool searchField = false, bool grid = false)
        {
            Go = go;
            Label = label;
            HasControl = hasControl;
            Kind = kind;
            Source = source;
            Control = control;
            Pin = pin;
            SearchField = searchField;
            Grid = grid;
        }

        /// <summary>
        /// SETTINGS1 stage 2: the row was added while the panel was in settings-grid mode, so every
        /// layout pass places it on <see cref="ModSettingsGrid"/>'s columns (fixed control column,
        /// reset gutter) rather than the kit's two-column split.
        /// </summary>
        public bool Grid { get; }

        public GameObject Go { get; }

        public GameObject? Label { get; }

        public GameObject? Control { get; }

        /// <summary>
        /// Which of the five row types this is: <c>label</c>, <c>button</c>, <c>toggle</c>,
        /// <c>slider</c>, <c>input</c>. Recorded for the interaction probe, which reports the rows
        /// a player is meant to be able to operate and stays quiet about the ones they are not.
        /// </summary>
        public string Kind { get; }

        /// <summary>
        /// Which path built the row's control. Carried on the row rather than in a per-type latch
        /// because the latch answers once per panel build and the probe asks about one row.
        /// </summary>
        public ControlSource Source { get; }

        /// <summary>
        /// Whether this row kept an operable editor control, and therefore the only input to
        /// <see cref="PanelLayout.Columns"/>. Recorded per row rather than inferred per pass so
        /// that a control which failed its operability check and was destroyed cannot leave the
        /// caption indented around the hole where it would have been (evidence 176).
        /// </summary>
        public bool HasControl { get; }

        public MenuRowPin Pin { get; }

        /// <summary>
        /// Full-width sidebar search. Layout must keep <see cref="MenuPanelLayout.SearchFieldColumns"/>
        /// rather than the two-column split used for settings editors.
        /// </summary>
        public bool SearchField { get; }
    }

    private sealed partial class UnityPanel : UnityWidget, IRuntimePanel, IHasOverlay, ISettlePanel
    {
        private readonly PanelLayout _layout = new();
        private bool _menuPane;
        private bool _sidebar;
        private bool _strip;
        private MenuPaneMode _mode = MenuPaneMode.Split;
        private string _contentAlignment = SettingsAlign.Centre;
        private bool _inline;
        private GameObject? _inlineHost;
        private readonly Dictionary<GameObject, List<GameObject>> _inlineButtons = new();
        // SEARCH2: inline items that are labels (the match count beside "Filters"), placed
        // right-aligned in their slot instead of stretched like a button caption.
        private MenuPaneGeometry _menuGeometry;
        private float _menuCanvasWidth;
        private float _menuCanvasHeight;

        public bool IsMenuPane => _menuPane;

        /// <summary>A description column beside a list (the Keybinds tab's); never the pane whose scroll is kept.</summary>
        public bool IsAside => _menuPane && _mode == MenuPaneMode.Aside;

        public bool IsSidebar => _sidebar;

        public bool IsStrip => _strip;

        /// <summary>The pane's <see cref="MenuPaneMode"/> (LIST3); Split for every pane but the Install tab's.</summary>
        public MenuPaneMode PaneMode => _mode;

        /// <summary>
        /// Height of the title band above the viewport: <see cref="PanelLayout.HeaderHeight"/>,
        /// or nothing on a <see cref="MenuPaneMode.FullWidth"/> pane, which hides its title
        /// (the tab strip already names the tab) and gives the band to the list (LIST3 item 8).
        /// </summary>
        /// <summary>KIT1 gen 1: this pane draws its own name, so it asked for the title band to go.</summary>
        private bool _hideTitle;
        private float _embeddedWidth, _embeddedHeight;
        internal void Embed(float width, float height)
        {
            bool changed = Math.Abs(width - _embeddedWidth) > .5f || Math.Abs(height - _embeddedHeight) > .5f;
            _embeddedWidth = width; _embeddedHeight = height; SelfPlaced = true;
            MakeGameWindow();
            HidePaneTitle();
            if (changed) Relayout();
        }

        private float TitleBand =>
            _embeddedWidth > 0 ? 8f : _dialog ? DialogLayout.FrameTopPad
            // P7b: a game window that draws its own title row (the tip card's dialog rows) keeps only the dialog's top pad.
            : _gameWindow && _hideTitle && !_menuPane ? DialogLayout.FrameTopPad
            : _menuPane && (MenuPaneModes.Titleless(_mode) || _hideTitle) ? 0f : PanelLayout.HeaderHeight;

        /// <summary>
        /// Drop the pane's centered title and give its band to the content. The same three writes
        /// <see cref="ConfigureMenu(bool, MenuPaneMode)"/> makes for a
        /// <see cref="MenuPaneMode.FullWidth"/> pane, without touching the geometry: this pane is
        /// still the sidebar/detail split. The flag is set first so <see cref="TitleBand"/> is already
        /// 0 when the scrollbar's top inset is written.
        /// </summary>
        public void HidePaneTitle()
        {
            if (Released || _hideTitle) return;
            _hideTitle = true;
            try
            {
                if (Title != null)
                {
                    Title.SetActive(false);
                    var tmp = Title.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.raycastTarget = false;
                }
            }
            catch { /* */ }
            try
            {
                var bar = _scrollbar == null ? null : _scrollbar.GetComponent<RectTransform>();
                if (bar != null) bar.offsetMax = new Vector2(-PanelLayout.ScrollbarInset, -TitleBand);
            }
            catch { /* */ }
            Relayout();
        }

        /// <summary>Whether this pane dropped its title band (the readback and the probe ask).</summary>
        public bool TitleHidden => _hideTitle || (_menuPane && MenuPaneModes.Titleless(_mode)) || _strip;

        public string ContentAlignment
        {
            get => _contentAlignment;
            set => _contentAlignment = SettingsAlign.Normalize(value);
        }

        public bool IsLeftAligned => SettingsAlign.IsLeft(_contentAlignment) && !_strip;

        public bool Inline => _inline;

        public GameObject InlineHostOrRowParent =>
            _inline && _inlineHost != null ? _inlineHost : RowParent;

        public bool CaptionWantsLeft(string? text) =>
            IsLeftAligned || (!string.IsNullOrEmpty(text) && text.IndexOf('\n') >= 0);

        public void BeginInline()
        {
            _inline = true;
            _inlineHost = null;
            _pageButtonRow = null;
            CloseCompactRow();
        }

        public void EndInline()
        {
            _inline = false;
            _pageButtonRow = null;
            CloseCompactRow();
            Relayout();
        }

        /// <summary>CSAMPLE1: page button rows and the buttons each one holds, in order.</summary>
        private readonly Dictionary<GameObject, List<(GameObject Button, string Caption)>> _pageButtons = new();

        /// <summary>CSAMPLE1: the page button row an inline block is filling, or null.</summary>
        private GameObject? _pageButtonRow;

        /// <summary>The row the next page button joins: the inline block's, or null for a row of its own.</summary>
        internal GameObject? OpenPageButtonRow() => _inline && _pageButtonRow != null && UnityUiRuntime.Alive(_pageButtonRow) ? _pageButtonRow : null;

        internal void AddPageButton(GameObject row, GameObject button, string caption)
        {
            if (!_pageButtons.TryGetValue(row, out var list))
            {
                list = new List<(GameObject, string)>();
                _pageButtons[row] = list;
            }

            list.Add((button, caption));
            if (_inline) _pageButtonRow = row;
        }

        /// <summary>A page button row is shown while one of its buttons is (a button's Visible hides it alone).</summary>
        private void SyncPageButtonRows()
        {
            // Review 2026-10-11: forget rows and buttons that were destroyed (a disposed button, a cleared page).
            List<GameObject>? dead = null;
            foreach (var pair in _pageButtons)
            {
                pair.Value.RemoveAll(b => !UnityUiRuntime.Alive(b.Button));
                if (!UnityUiRuntime.Alive(pair.Key) || pair.Value.Count == 0) (dead ??= new List<GameObject>()).Add(pair.Key);
            }

            if (dead != null)
                foreach (var key in dead)
                {
                    _pageButtons.Remove(key);
                    if (_pageButtonRow != null && key == _pageButtonRow) _pageButtonRow = null;
                }

            foreach (var pair in _pageButtons)
            {
                if (!UnityUiRuntime.Alive(pair.Key)) continue;
                bool any = false;
                foreach (var (button, _) in pair.Value)
                {
                    try { if (UnityUiRuntime.Alive(button) && button.activeSelf) any = true; }
                    catch { /* */ }
                }

                try { if (pair.Key.activeSelf != any) pair.Key.SetActive(any); }
                catch { /* */ }
            }
        }

        /// <summary>A page button's caption as drawn, or <paramref name="fallback"/> (the build-time caption).</summary>
        private static string ShownCaption(GameObject button, string fallback)
        {
            try
            {
                var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp != null && !string.IsNullOrEmpty(tmp.text)) return tmp.text;
            }
            catch { /* the build-time caption */ }
            return fallback;
        }

        /// <summary>The row's buttons side by side from its left edge, each sized to its caption, centered on the row.</summary>
        private void PlacePageButtons(in PanelRow row, float height)
        {
            if (row.Go == null || !_pageButtons.TryGetValue(row.Go, out var buttons) || buttons.Count == 0) return;
            var shown = new List<(GameObject Button, string Caption)>();
            foreach (var b in buttons)
            {
                try { if (UnityUiRuntime.Alive(b.Button) && b.Button.activeSelf) shown.Add(b); }
                catch { /* */ }
            }

            var widths = new List<float>(shown.Count);
            // The caption the button shows now (review 2026-10-11: a renamed button kept its first width and squeezed its text).
            // HUB1: a button with a key hint is wider by the cap's room; a centered row's group is centered on the row.
            foreach (var b in shown) widths.Add(Math.Min(ContentRowWidth, PageButtonLayout.Width(ShownCaption(b.Button, b.Caption), ContentRowWidth) + HintRoomOf(b.Button)));
            var lefts = PageButtonLayout.Lefts(widths, RowWidthOf(row.Go), IsCenteredPageRow(row.Go));
            for (int i = 0; i < shown.Count; i++)
            {
                try
                {
                    var rt = shown[i].Button.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    rt.anchorMin = new Vector2(0f, 0.5f);
                    rt.anchorMax = new Vector2(0f, 0.5f);
                    rt.pivot = new Vector2(0f, 0.5f);
                    rt.anchoredPosition = new Vector2(lefts[i], 0f);
                    rt.sizeDelta = new Vector2(widths[i], PageButtonLayout.Height);
                }
                catch { /* the button keeps its placement */ }
                FitInlineButtonCaption(shown[i].Button);
                KeepHintRoom(shown[i].Button);
            }
        }

        /// <summary>An inline block is open and has a field or toggle row its buttons attach to.</summary>
        internal bool HasInlineHost => _inline && _inlineHost != null && UnityUiRuntime.Alive(_inlineHost);

        public bool TryAttachInline(GameObject go)
        {
            if (!_inline || _inlineHost == null || go == null) return false;
            try { go.transform.SetParent(_inlineHost.transform, false); }
            catch { return false; }
            if (!_inlineButtons.TryGetValue(_inlineHost, out var list))
            {
                list = new List<GameObject>();
                _inlineButtons[_inlineHost] = list;
            }
            list.Add(go);
            return true;
        }

        /// <summary>
        /// An inline host row is as tall as the buttons it carries: a short caption
        /// measures under <see cref="RowHeight"/> and its chips would overlap the row beneath.
        /// </summary>
        private float InlineHostHeight(in PanelRow row, float measured) =>
            // HUB1: a row of compact dropdowns is as tall as its lines.
            CompactRowHeight(row, row.Go != null && _inlineButtons.TryGetValue(row.Go, out var items) && items.Count > 0
                ? Math.Max(measured, RowHeight)
                : measured);

        private MenuRowPin _nextPin;
        private MenuRowTone _nextTone;
        private GameObject? _footer;
        private GameObject? _footerRule;
        private GameObject? _headerBand;

        /// <summary>Non-scrolling band the pinned listing header rows sit on; null until a header pin has been laid out.</summary>
        internal GameObject? HeaderBand => _headerBand;

        /// <summary>The ScrollRect viewport the body scrolls in.</summary>
        internal GameObject? ViewportObject => _viewport;

        /// <summary>
        /// Sidebar panes bind text slots to TMP onValueChanged at creation (Mods search).
        /// Content and settings panes stay on onEndEdit.
        /// </summary>
        public bool UsesLiveText => _sidebar;

        public float ContentRowWidth => _narrowWidth > 0f ? _narrowWidth : FullRowWidth;

        /// <summary>The content's row width, whatever row is being placed (MENUKIT2: the description column's ruler).</summary>
        internal float FullRowWidth =>
            _embeddedWidth > 0 ? _embeddedWidth - 44 : _menuPane ? _menuGeometry.RowWidth : (_dialog || WideGameWindow) ? DialogLayout.ContentRowWidth(FrameWidth) : IsStandardWindow ? StandardRowWidth : RowWidth;

        /// <summary>
        /// MENUKIT2 round D: while a row below the first settings row is placed beside the description
        /// column, the row width that row sees; 0 otherwise.
        /// </summary>
        private float _narrowWidth;
        public RowColumns Columns(bool hasControl) =>
            _grid
                ? RowGrid.Columns(hasControl)
                : _menuPane || _embeddedWidth > 0
                    ? MenuPanelLayout.Columns(ContentRowWidth, hasControl)
                    : PanelLayout.Columns(hasControl);

        // ---- SETTINGS1 stage 2: the settings grid, help lines, the reset gutter, tall rows --------

        private bool _grid;

        /// <summary>Rows added while this is on use the settings grid (<see cref="ModSettingsGrid"/>).</summary>
        public bool Grid
        {
            get => _grid;
            set => _grid = value;
        }

        /// <summary>Help labels drawn under a row's caption, keyed by the row object.</summary>
        private readonly Dictionary<GameObject, GameObject> _rowHelp = new();

        /// <summary>Reset links sitting in a row's gutter, keyed by the row object.</summary>
        private readonly Dictionary<GameObject, GameObject> _gutterLinks = new();

        /// <summary>Rows whose control is taller than one line (a text area, an options list): their least height.</summary>
        private readonly Dictionary<GameObject, float> _minHeights = new();

        /// <summary>Rows with a fixed height (a divider).</summary>
        private readonly Dictionary<GameObject, float> _fixedHeights = new();

        internal void SetMinRowHeight(GameObject go, float height)
        {
            if (go != null) _minHeights[go] = height;
        }

        internal void SetFixedRowHeight(GameObject go, float height)
        {
            if (go != null) _fixedHeights[go] = height;
        }

        /// <summary>Help text under <paramref name="rowGo"/>'s caption; empty removes it. Re-lays the panel out.</summary>
        internal void SetRowHelp(GameObject rowGo, string help)
        {
            if (rowGo == null) return;
            _rowHelp.TryGetValue(rowGo, out var existing);
            if (string.IsNullOrEmpty(help))
            {
                if (existing == null) return;
                _rowHelp.Remove(rowGo);
                TryDestroy(existing, BootstrapLink.MayDestroyUiImmediate());
                Relayout();
                return;
            }

            if (existing == null || !UnityUiRuntime.Alive(existing))
            {
                existing = MakeLabel(rowGo, help, Vector2.zero, ModSettingsGrid.HelpFontSize, left: true, muted: true);
                if (existing == null) return;
                existing.name = "row-help";
                try
                {
                    var tmp = existing.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        tmp.enableWordWrapping = true;
                        tmp.raycastTarget = false;
                        tmp.verticalAlignment = VerticalAlignmentOptions.Top;
                    }
                }
                catch { /* the help still draws; it may not wrap */ }
                _rowHelp[rowGo] = existing;
            }
            else
            {
                try
                {
                    var tmp = existing.GetComponent<TextMeshProUGUI>();
                    if (tmp != null && tmp.text == help) return;
                    if (tmp != null) tmp.text = help;
                }
                catch { /* */ }
            }

            Relayout();
        }

        /// <summary>Put <paramref name="link"/> in <paramref name="rowGo"/>'s reset gutter. False when there is no such row.</summary>
        internal bool AttachGutter(GameObject rowGo, GameObject link, float size)
        {
            if (rowGo == null || link == null) return false;
            bool known = false;
            for (int i = 0; i < _rows.Count && !known; i++) known = ReferenceEquals(_rows[i].Go, rowGo);
            if (!known) return false;
            try { link.transform.SetParent(rowGo.transform, false); }
            catch { return false; }
            _gutterLinks[rowGo] = link;
            _squareControls[link] = size;
            Relayout();
            return true;
        }

        /// <summary>
        /// The height a row needs once its help line and a tall control are counted: the caption band
        /// (at least one line), then the help under it; never less than the control or a declared
        /// least height. A row with neither keeps the measured caption height.
        /// </summary>
        private float TallRowHeight(in PanelRow row, in RowColumns columns, float captionHeight, out float nameBand, out float helpHeight)
        {
            nameBand = captionHeight;
            helpHeight = 0f;
            if (row.Go == null) return captionHeight;
            if (_fixedHeights.TryGetValue(row.Go, out float fixedHeight)) return fixedHeight;
            if (row.Grid && !_cellRows.ContainsKey(row.Go) && (IsSettingRowKind(row.Kind) || row.Kind == "divider"))
                return GridRowHeight(row, columns, out nameBand, out helpHeight);
            float height = captionHeight;
            if (_rowHelp.TryGetValue(row.Go, out var help) && help != null)
            {
                nameBand = Math.Max(captionHeight, RowHeight);
                helpHeight = MeasureHelp(help, columns.LabelWidth);
                height = nameBand + helpHeight + HelpBottomPad;
            }

            if (_minHeights.TryGetValue(row.Go, out float least)) height = Math.Max(height, least);
            if (row.Grid && row.Control != null)
            {
                try
                {
                    var rt = row.Control.GetComponent<RectTransform>();
                    if (rt != null) height = Math.Max(height, rt.rect.height);
                }
                catch { /* the caption height stands */ }
            }

            return PanelLayout.ClampRowHeight(height);
        }

        private const float HelpBottomPad = 4f;

        /// <summary>
        /// SETTINGS1 stage 2: a settings-grid row's height from <see cref="ModSettingsGrid.RowHeight"/> —
        /// the name in its band, the help tight under it (capped by
        /// <see cref="ModSettingsGrid.HelpHeightFor"/>, so the row never passes the panel's row cap), the
        /// control at its drawn height, and the same bottom pad on every row.
        /// </summary>
        private float GridRowHeight(in PanelRow row, in RowColumns columns, out float nameBand, out float helpHeight)
        {
            // MENUKIT2: one line at the game's fixed pitch, whatever the help says (the help is in the
            // description column, or one ellipsized line under the name on a narrow pane). Only a tall
            // control (an options list, a text area) takes its own height.
            var shape = ModSettingsRowGrid.ShapeOfKind(row.Kind);
            float tall = 0f;
            if (shape == SettingsControlShape.Tall)
            {
                if (_minHeights.TryGetValue(row.Go, out float least)) tall = least;
                if (row.Control != null)
                {
                    try
                    {
                        var rt = row.Control.GetComponent<RectTransform>();
                        if (rt != null) tall = Math.Max(tall, rt.rect.height);
                    }
                    catch { /* the pitch stands */ }
                }
            }

            float height = ModSettingsRowGrid.DrawnRowHeight(shape, tall);
            nameBand = Math.Min(height, ModSettingsRowGrid.NameFontSize + 12f);
            helpHeight = GridHelpShown(row.Go) ? GridHelpLineHeight : 0f;
            return height;
        }

        /// <summary>A help label's wrapped height at <paramref name="width"/>, without the one-row floor captions get.</summary>
        private static float MeasureHelp(GameObject help, float width) => MeasureHelp(help, width, PanelLayout.MaxRowHeight);

        /// <summary>
        /// TOKENTIPS2: <see cref="MeasureHelp(GameObject, float)"/> with its own cap. The description column passes
        /// <see cref="PanelLayout.MaxFixedRowHeight"/>: capped at a row's 400 px, a long help was given less height than it
        /// drew and the notes under it were placed over its last lines (live 2026-10-06).
        /// </summary>
        private static float MeasureHelp(GameObject help, float width, float cap)
        {
            try
            {
                var tmp = help.GetComponent<TextMeshProUGUI>();
                if (tmp == null) return 0f;
                var rt = help.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(Math.Max(1f, width), RowHeight);
                }

                tmp.ForceMeshUpdate();
                float h = tmp.preferredHeight;
                return float.IsNaN(h) || h < 0f ? 0f : Math.Min(h + 2f, cap);
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Place a tall row's caption in its top band and the help under it, both in the caption
        /// column (labels are center-anchored, so offsets are from the row's center), and the gutter
        /// link at the gutter's center in the top band.
        /// </summary>
        private void PlaceTallRowParts(in PanelRow row, in RowColumns columns, float total, float nameBand, float helpHeight)
        {
            if (row.Go == null) return;
            bool grid = row.Grid && !_cellRows.ContainsKey(row.Go) && (IsSettingRowKind(row.Kind) || row.Kind == "divider");
            if (grid)
            {
                PlaceGridRowParts(row, columns, total);
                return;
            }

            bool tall = total > nameBand + 0.5f;
            if ((grid || tall || _rowHelp.ContainsKey(row.Go)) && row.Label != null && !ReferenceEquals(row.Label, row.Go))
            {
                try
                {
                    var rt = row.Label.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        if (grid) rt.sizeDelta = new Vector2(columns.LabelWidth, nameBand);
                        rt.anchoredPosition = new Vector2(columns.LabelX, total * 0.5f - nameBand * 0.5f);
                    }

                    if (grid)
                    {
                        // The name is centered in its band, a little heavier than the help under it.
                        var tmp = row.Label.GetComponent<TextMeshProUGUI>();
                        if (tmp != null)
                        {
                            tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                            tmp.fontStyle = FontStyles.Bold;
                        }
                    }
                }
                catch { /* */ }
            }

            if (_rowHelp.TryGetValue(row.Go, out var help) && help != null)
            {
                float helpTop = grid ? ModSettingsGrid.HelpTop(nameBand) : nameBand;
                try
                {
                    var rt = help.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        rt.sizeDelta = new Vector2(columns.LabelWidth, Math.Max(1f, helpHeight));
                        rt.anchoredPosition = new Vector2(columns.LabelX, total * 0.5f - helpTop - helpHeight * 0.5f);
                    }
                }
                catch { /* */ }
            }

            if (_gutterLinks.TryGetValue(row.Go, out var link) && link != null)
            {
                float size = _squareControls.TryGetValue(link, out float s) ? s : MenuPanelLayout.IconLinkSize;
                var gutterGrid = ModSettingsGrid.For(ContentRowWidth);
                try
                {
                    var rt = link.GetComponent<RectTransform>() ?? link.AddComponent<RectTransform>();
                    rt.anchorMin = new Vector2(0.5f, 1f);
                    rt.anchorMax = new Vector2(0.5f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.anchoredPosition = new Vector2(gutterGrid.GutterCentreX, -Math.Max(0f, (ModSettingsGrid.ControlHeight - size) * 0.5f));
                    rt.sizeDelta = new Vector2(size, size);
                }
                catch { /* */ }
            }

            if (string.Equals(row.Kind, "divider", StringComparison.Ordinal))
            {
                try
                {
                    var rule = row.Go.transform.Find("divider-rule");
                    if (rule != null) PlaceTop(rule.gameObject, 0f, total * 0.5f, ContentRowWidth - HeadBlockLayout.RightColumnInset, 1f);
                }
                catch { /* */ }
            }
        }

        public void ConfigureMenu(bool sidebar) => ConfigureMenu(sidebar, MenuPaneMode.Split);

        public void ConfigureMenu(bool sidebar, MenuPaneMode mode)
        {
            if (Released) return;
            if (!_menuPane) Plugin.Frame += MenuResizeTick;
            _menuPane = true;
            _sidebar = sidebar;
            _strip = false;
            _mode = sidebar ? MenuPaneMode.Split : mode;
            _nextPin = MenuRowPin.Content;
            _nextTone = MenuRowTone.Catalog;
            try
            {
                var canvas = Overlay == null ? null : Overlay.GetComponent<Canvas>();
                if (canvas != null)
                {
                    canvas.sortingOrder = ModsCanvasBand.Panels;
                    canvas.overrideSorting = true;
                }
            }
            catch { /* panels still show at OverlaySorting if this write fails */ }
            if (MenuPaneModes.Titleless(_mode))
            {
                // No pane title: the strip reads "Install mods" already (LIST3 item 8). The
                // viewport and scrollbar were built under the title band; FitChromeInsets
                // re-insets the viewport every layout pass (the Install pane always pins
                // top chrome), so only the scrollbar's top needs the band removed here.
                try
                {
                    if (Title != null)
                    {
                        Title.SetActive(false);
                        var tmp = Title.GetComponent<TextMeshProUGUI>();
                        if (tmp != null) tmp.raycastTarget = false;
                    }
                }
                catch { /* */ }
                try
                {
                    var bar = _scrollbar == null ? null : _scrollbar.GetComponent<RectTransform>();
                    if (bar != null) bar.offsetMax = new Vector2(-PanelLayout.ScrollbarInset, -TitleBand);
                }
                catch { /* */ }
            }
            ApplyMenuPaneFill();
            UpdateMenuGeometry(AvailableWidth(), AvailableHeight());
            Relayout();
        }

        public void ConfigureStrip()
        {
            if (Released) return;
            if (!_menuPane) Plugin.Frame += MenuResizeTick;
            _menuPane = true;
            _sidebar = false;
            _strip = true;
            _mode = MenuPaneMode.Split;
            _nextPin = MenuRowPin.Content;
            _nextTone = MenuRowTone.Catalog;
            try { GameObject.name = ModsMenu.StripRootName; } catch { /* */ }
            try
            {
                var canvas = Overlay == null ? null : Overlay.GetComponent<Canvas>();
                if (canvas != null)
                {
                    canvas.sortingOrder = ModsCanvasBand.Panels;
                    canvas.overrideSorting = true;
                }
            }
            catch { /* panels still show at OverlaySorting if this write fails */ }
            try
            {
                if (Title != null)
                {
                    Title.SetActive(false);
                    var tmp = Title.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.raycastTarget = false;
                }
            }
            catch { /* */ }
            try { if (_viewport != null) _viewport.SetActive(false); } catch { /* */ }
            try { if (_scrollbar != null) _scrollbar.SetActive(false); } catch { /* */ }
            try { if (_content != null) _content.SetActive(false); } catch { /* */ }
            ApplyMenuPaneFill();
            UpdateMenuGeometry(AvailableWidth(), AvailableHeight());
            Relayout();
        }

        public void SetNextPin(MenuRowPin pin) => _nextPin = pin;

        public void SetNextTone(MenuRowTone tone) => _nextTone = tone;

        public MenuRowTone ResolvedTone() =>
            _nextPin == MenuRowPin.Bottom ? MenuRowTone.Chrome : _nextTone;

        private void UpdateMenuGeometry(float width, float height)
        {
            _menuCanvasWidth = width;
            _menuCanvasHeight = height;
            NativeContentRect native = NativeContentRect.Missing("unresolved");
            NativeContentRect extent = NativeContentRect.Missing(ModsWorkspaceFrame.BackgroundWhyMissing);
            string site = "none";
            try { site = NativeModsEntry.DescribeOwned().Site; } catch { /* */ }
            try
            {
                var overlay = Overlay == null ? null : Overlay.GetComponent<RectTransform>();
                if (ModsWorkspaceChrome.Present)
                {
                    native = ModsWorkspaceChrome.Native;
                    extent = ModsWorkspaceChrome.NativeExtent;
                }
                else
                {
                    native = ModsWorkspaceChrome.ReadNative(overlay);
                    if (ModsWorkspaceFrame.LastOverhang.Measured && native.Found)
                        extent = ModsWorkspaceFrame.LastOverhang.Apply(native)
                            .WithWhy(ModsWorkspaceFrame.LastFrameExtentWhy);
                }
            }
            catch { /* */ }
            var frame = ModsWorkspaceFrame.Resolve(width, height, site, native, extent);
            _menuFrameX = frame.X;
            _menuGeometry = _strip
                ? MenuPanelLayout.ForStrip(frame)
                : MenuPanelLayout.ForFrame(frame, _sidebar, _mode);
            ApplyMenuPaneFill();
        }

        private void ApplyMenuPaneFill()
        {
            try
            {
                var image = GameObject.GetComponent<Image>();
                if (image == null) return;
                if (_strip)
                {
                    try { image.sprite = null; } catch { /* */ }
                    image.color = new Color(0f, 0f, 0f, 0f);
                    try { image.raycastTarget = false; } catch { /* */ }
                    return;
                }

                image.color = new Color(
                    ModsWorkspaceFrame.PaneFillR,
                    ModsWorkspaceFrame.PaneFillG,
                    ModsWorkspaceFrame.PaneFillB,
                    ModsWorkspaceFrame.PanelFillAlpha);
            }
            catch { /* a failed tint still leaves a readable pane */ }
        }

        private void MenuResizeTick()
        {
            if (Released || !Alive)
            {
                Plugin.Frame -= MenuResizeTick;
                return;
            }
            // A soft-hidden panel (the parked Mods list, a parked strip) keeps its objects active,
            // so Visible stays true; without this its canvas measure (a dozen IL2CPP-boxed
            // returns) ran every frame of play. Show re-runs the layout anyway.
            if (_canvasOff) return;
            if (!Visible) return;
            float width = AvailableWidth(), height = AvailableHeight();
            var frame = ModsWorkspaceFrame.Last;
            var expected = _strip
                ? MenuPanelLayout.ForStrip(frame)
                : MenuPanelLayout.ForFrame(frame, _sidebar, _mode);
            if (Math.Abs(width - _menuCanvasWidth) < .5f
                && Math.Abs(height - _menuCanvasHeight) < .5f
                && Math.Abs(expected.Width - _menuGeometry.Width) < .5f
                && Math.Abs(expected.Height - _menuGeometry.Height) < .5f
                && Math.Abs(expected.X - _menuGeometry.X) < .5f
                && Math.Abs(expected.Y - _menuGeometry.Y) < .5f)
                return;
            Relayout();
        }

        /// <summary>
        /// Every row, in the order it was created. This list is what makes the layout re-runnable
        /// (R43 finding 4): before it, the cursor ran once as rows were built and a row whose text
        /// changed afterwards kept a height measured for text it no longer showed.
        /// </summary>
        private readonly List<PanelRow> _rows = new();

        /// <summary>
        /// Guards against a layout pass re-entering itself. Nothing in <see cref="RunLayout"/>
        /// writes a caption today, but a text write during a layout would otherwise recurse
        /// through <see cref="UnityWidget.Text"/> forever, on the game's main thread.
        /// </summary>
        private bool _laying;

        /// <summary>True when <see cref="Relayout"/> ran inside <see cref="UiLayoutBatch"/>.</summary>
        private bool _layoutDirty;

        private readonly GameObject? _viewport;
        private readonly GameObject? _content;
        private readonly GameObject? _scrollbar;

        /// <summary>
        /// The last <c>(fits)</c>/<c>(scrolls)</c> this panel logged, and whether it is still owed
        /// a read-back of what <c>ScrollRect</c> actually did with the bar (R45 finding 22). The
        /// read is deferred to the next pass over the panel because <c>ScrollRect</c> decides in
        /// its own update: a read taken in the pass that set the content size would report the
        /// previous content's answer while looking like this one's.
        /// </summary>
        private bool _verdictScrolls;

        private bool _verdictPending;

        /// <summary>
        /// The frame the pending verdict was made in, and the last read-back line printed since the panel was
        /// shown. A read in that same frame, or with the ScrollRect not live, sees the bar of an older content
        /// (<see cref="ScrollbarVerdict.Decide"/>).
        /// </summary>
        private int _verdictFrame = int.MinValue;

        private string? _lastReadBackLine;

        /// <summary>
        /// Why the canvas rung answered or did not, on the last <see cref="MeasureHeight"/>. Job
        /// FUI32; printed as <c>canvas=</c> on the layout line so a reader can tell a canvas Unity
        /// has not sized yet (the one this panel waits for) from one that is structurally absent
        /// or that threw.
        /// </summary>
        private CanvasRead _canvasRead = CanvasRead.NoRect;

        /// <summary>The reading the last layout pass actually sized this panel with (job FUI32).</summary>
        private RulerReading _laidOutWith;

        /// <summary>Frames spent waiting for the canvas since the last <see cref="ApplyLayout"/>.</summary>
        private int _settleFrames;

        /// <summary>True while <see cref="SettleTick"/> is running the pass it armed.</summary>
        private bool _settling;

        /// <summary>True once the wait budget was spent without the canvas ever answering.</summary>
        private bool _settleExhausted;

        public GameObject? Overlay;
        public IRuntimeWidget? OverlayWidget { get; }

        IRuntimeWidget? IHasOverlay.Overlay => OverlayWidget;

        public UnityPanel(
            GameObject go,
            GameObject? title,
            GameObject? overlay,
            IRuntimeWidget? overlayWidget,
            GameObject? viewport = null,
            GameObject? content = null,
            GameObject? scrollbar = null)
            : base(null, go, null, null, null, null, null, null)
        {
            Title = title;
            Overlay = overlay;
            OverlayWidget = overlayWidget;
            _viewport = viewport;
            _content = content;
            _scrollbar = scrollbar;
        }

        /// <summary>
        /// What rows are parented to: the scrolling content rect when one was built, the panel
        /// frame itself when it could not be. The fallback is the pre-evidence-163 behavior, so a
        /// runtime that cannot make a viewport still shows the first few rows rather than none.
        /// </summary>
        public GameObject RowParent =>
            _strip
                ? GameObject
                : _nextPin == MenuRowPin.Content ? (_content ?? GameObject)
                : _nextPin == MenuRowPin.Side ? SideParent
                : GameObject;

        public ScrollRect? Scroll
        {
            get
            {
                try { return GameObject.GetComponent<ScrollRect>(); }
                catch { return null; }
            }
        }

        internal ScreenRect ViewportScreenRect
        {
            get
            {
                try
                {
                    // Strip has no list viewport (ConfigureStrip hid it). An inactive
                    // viewport can still report a stale rect that swallows sidebar
                    // clicks — do not treat it as a hit target.
                    if (_strip) return ScreenRect.Unknown;
                    if (_viewport == null) return ScreenRect.Unknown;
                    try { if (!_viewport.activeInHierarchy) return ScreenRect.Unknown; }
                    catch { return ScreenRect.Unknown; }
                    var rt = _viewport.GetComponent<RectTransform>();
                    if (rt == null) return ScreenRect.Unknown;
                    return ScreenRectOf(rt);
                }
                catch
                {
                    return ScreenRect.Unknown;
                }
            }
        }

        public GameObject? Title;

        /// <summary>
        /// Which of the shown panels this one is while it is shown (jobs FUI20, FUI21), or
        /// <see cref="PanelSpot.None"/>. Set by <see cref="UnityUiRuntime.PlacePanel"/> before the
        /// layout pass and read by <see cref="RunLayout"/> and <see cref="Reposition"/>; the only
        /// panel of a set of one, and a panel with no spot at all, both mean the center, which is
        /// where every panel was placed before this existed.
        /// </summary>
        public PanelSpot Spot = PanelSpot.None;

        /// <summary>
        /// CONSENT2: true when this panel writes its own frame <c>sizeDelta</c> and
        /// <c>anchoredPosition</c>, so <see cref="RunLayout"/> and <see cref="Reposition"/> must
        /// not. Rows, scroll, chrome insets and the settle re-measure are unaffected: only the two
        /// writes that decide where and how big the frame is are handed over. Default false, so
        /// every other panel keeps flowing exactly as before.
        /// </summary>
        public bool SelfPlaced;

        /// <summary>P7b: the panel has no scroll view (<see cref="IUiRuntime.NoScrollPanel"/>); the read-back skips it.</summary>
        public bool NoScroll;

        /// <summary>P7b: switch the ScrollRect and its bar off for good and mark the panel as not scrolling.</summary>
        internal void TurnOffScroll()
        {
            NoScroll = true;
            _verdictPending = false;
            try
            {
                var scroll = Scroll;
                if (scroll == null) return;
                scroll.enabled = false; // AutoHide lives in its update: off, it never shows the bar again
                var bar = scroll.verticalScrollbar;
                if (bar != null && bar.gameObject.activeSelf) bar.gameObject.SetActive(false);
            }
            catch { /* the read-back skips the panel all the same */ }

            try { if (_scrollbar != null && _scrollbar.activeSelf) _scrollbar.SetActive(false); }
            catch { /* */ }
        }

        /// <summary>
        /// INSTALLUX2 item 1: this panel is a modal dialog. It is centered rather than flowed (a
        /// dialog is never one of a ranked set), its frame is opaque over a raycast-blocking
        /// backdrop, and it stays at alpha 0 until the layout pass that sizes and places it has
        /// finished — the CONSENT2 rule, so no frame is ever drawn anywhere but the final rect.
        /// Content sizing and scrolling are the ordinary panel's (<see cref="PanelLayout"/>
        /// sizes to content up to <see cref="PanelLayout.ScreenFraction"/> and the bar auto-hides
        /// while the content fits), which is what "content-sized, no scrollbar when it fits" means.
        /// </summary>
        private bool _dialog;

        /// <summary>Name of the backdrop child this dialog owns on its own overlay canvas.</summary>
        internal const string DialogBackdropName = "dialog-backdrop";

        /// <summary>True while this panel is a modal dialog.</summary>
        public bool IsDialog => _dialog;

        /// <summary>The version footer's text (<see cref="IUiPanel.Footer"/>); empty when there is none.</summary>
        private string _footerText = "";

        /// <summary>The footer's TMP GameObject, a direct child of the frame (never of the scrolling rows).</summary>
        private GameObject? _versionFooter;

        /// <summary>The footer band the last layout pass inset the viewport by, so removing the footer gives it back.</summary>
        private float _footerInset;

        /// <summary>Center X of the Mods workspace frame, from the last menu geometry pass.</summary>
        private float _menuFrameX;

        public string FooterText => _footerText;

        /// <summary>
        /// The band this panel reserves for its footer in the runtime's own layout. The sidebar and the
        /// tab strip never draw one; a self-placed panel reserves its band itself (the consent prompt's
        /// action row does), so the runtime leaves its insets and height alone.
        /// </summary>
        private float FooterBand =>
            _strip || _sidebar || SelfPlaced ? 0f
            // WINDOWS1: a standard window's stamp sits UiWindowLayout.StampLift above the frame's edge, and its band with it.
            : IsStandardWindow || _dialog ? UiWindowLayout.StampBand(_footerText)
            : PanelFooterLayout.BandFor(_footerText);

        /// <summary>Set or clear the version footer and lay the panel out again.</summary>
        public void SetFooter(string text)
        {
            if (Released) return;
            string next = text?.Trim() ?? "";
            if (string.Equals(next, _footerText, StringComparison.Ordinal)
                && (_versionFooter != null || next.Length == 0))
                return;
            _footerText = next;
            try
            {
                if (_versionFooter == null && next.Length > 0 && GameObject != null)
                {
                    _versionFooter = MakeLabel(
                        GameObject, next, Vector2.zero, PanelFooterLayout.FontSize, left: false, muted: true);
                    if (_versionFooter != null)
                    {
                        _versionFooter.name = PanelFooterLayout.ObjectName;
                        var tmp = _versionFooter.GetComponent<TextMeshProUGUI>();
                        if (tmp != null)
                        {
                            try { tmp.raycastTarget = false; } catch { /* */ }
                            try { tmp.enableWordWrapping = false; } catch { /* */ }
                        }
                    }
                }
                else if (_versionFooter != null)
                {
                    var tmp = _versionFooter.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.text = next;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: panel footer threw: " + e.Message);
            }
            Relayout();
        }

        /// <summary>
        /// Anchor the footer at the bottom center of the frame, inside the band the viewport was inset
        /// above, and draw it last so the frame's own chrome cannot cover it. On a workspace detail pane
        /// it is shifted to the workspace frame's center (<see cref="PanelFooterLayout.CentredOffset"/>).
        /// </summary>
        private void PlaceVersionFooter()
        {
            if (_versionFooter == null) return;
            try
            {
                bool show = _footerText.Length > 0 && !_strip && !_sidebar;
                if (_versionFooter.activeSelf != show) _versionFooter.SetActive(show);
                if (!show) return;
                float frameWidth = FrameWidth;
                if (SelfPlaced)
                {
                    try
                    {
                        var frameRt = GameObject.GetComponent<RectTransform>();
                        if (frameRt != null && frameRt.rect.width > 1f) frameWidth = frameRt.rect.width;
                    }
                    catch { /* the default width still centers the line */ }
                }
                float x = _menuPane
                    ? PanelFooterLayout.CentredOffset(_menuFrameX, _menuGeometry.X, frameWidth)
                    : 0f;
                var rt = _versionFooter.GetComponent<RectTransform>() ?? _versionFooter.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(x, IsStandardWindow || _dialog ? UiWindowLayout.StampLift : 0f);
                rt.sizeDelta = new Vector2(PanelFooterLayout.TextWidth(frameWidth), PanelFooterLayout.BandHeight);
                var tmp = _versionFooter.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                    ApplyCachedFont(tmp);
                }
                _versionFooter.transform.SetAsLastSibling();
            }
            catch { /* a footer that cannot be placed is a missing caption, never a broken panel */ }
        }

        /// <summary>
        /// Turn this panel into a modal dialog: paint the chrome, and hide it until the next
        /// layout pass reveals it. Idempotent.
        /// </summary>
        private float _dialogFrameWidth;

        /// <summary>WIDEDIALOG1: the width a dialog asked for (0: the ordinary frame). Relayouts when it changes.</summary>
        public void SetDialogFrameWidth(float width)
        {
            float w = float.IsNaN(width) || width <= PanelWidth ? 0f : width;
            if (Math.Abs(w - _dialogFrameWidth) < 0.5f) return;
            _dialogFrameWidth = w;
            if ((_dialog || _gameWindow) && !Released) Relayout();
        }

        /// <summary>
        /// WIDEDIALOG1: this panel's frame width: a menu pane's geometry, a wide dialog's asked width capped to the
        /// canvas (<see cref="DialogLayout.FrameWidthFor"/>), else the constant <see cref="PanelWidth"/>.
        /// </summary>
        internal float FrameWidth => _embeddedWidth > 0 ? _embeddedWidth : _menuPane ? _menuGeometry.Width
            : _dialog && _dialogFrameWidth > PanelWidth ? DialogLayout.FrameWidthFor(_dialogFrameWidth, AvailableWidth())
            : WideGameWindow ? DialogLayout.FrameWidthFor(WideAsk, AvailableWidth())
            : IsStandardWindow ? StandardFrameWidth
            : PanelWidth;

        /// <summary>
        /// GAMEWINDOW1: the game's popup frame on this floating panel (a ctx.Dialogs dialog's art), without the dialog's
        /// backdrop, centering or title row: the panel keeps its title and its place among the shown panels.
        /// </summary>
        public void MakeGameWindow()
        {
            if (Released || _dialog || _gameWindow) return;
            _gameWindow = true;
            try
            {
                var image = GameObject == null ? null : GameObject.GetComponent<Image>();
                if (image != null)
                    PaintPart(image, GameArt.SettingsArtPart.PopupFrame, Color.white, ToColor(DialogFrameColors.Frame));
            }
            catch { /* an unpainted frame is still a usable window */ }
            Relayout();
        }

        private bool _gameWindow;

        /// <summary>
        /// P7b: sized like a dialog (its top pad, its content, its footer; no title band, no 200-unit floor): a dialog, or
        /// a game window that hid its title to draw a dialog's own rows (the tip card). Placement and capture stay its own.
        /// </summary>
        private bool DialogSized => _dialog || (_gameWindow && _hideTitle && !_menuPane);

        /// <summary>GAMEWINDOW1: drawn with the game's popup frame (readback).</summary>
        internal bool IsGameWindow => _gameWindow;

        public void MakeDialog()
        {
            if (Released || _dialog) return;
            _dialog = true;
            // WINDOWS1: every dialog draws above the standard windows (UiWindowLayout.DialogSorting).
            SetOverlaySorting(UiWindowLayout.DialogSorting);
            PaintDialogChrome();
            HideForDialogReveal();
            // DIALOGS1: the dialog draws its own title as its first row, so the pane's title band
            // goes (TitleBand is now DialogLayout.FrameTopPad); the next layout pass writes the
            // viewport and scrollbar insets from it (FitChromeInsets is forced for a dialog).
            try
            {
                if (Title != null)
                {
                    Title.SetActive(false);
                    var tmp = Title.GetComponent<TextMeshProUGUI>();
                    if (tmp != null) tmp.raycastTarget = false;
                }
            }
            catch { /* */ }
            Relayout();
        }

        /// <summary>Opaque frame and a dimming, raycast-blocking backdrop behind it.</summary>
        private void PaintDialogChrome()
        {
            try
            {
                var image = GameObject == null ? null : GameObject.GetComponent<Image>();
                if (image != null)
                {
                    // DIALOGS2: the game's popup panel (Window_Box_Background, 9-sliced, white) when its
                    // owned copy is ready; else the kit's flat ground (sprite = null first: a color
                    // written over a sliced frame is multiplied by that sprite's own alpha).
                    PaintPart(image, GameArt.SettingsArtPart.PopupFrame, Color.white,
                        ToColor(DialogFrameColors.Frame));
                    try { image.raycastTarget = true; } catch { /* */ }
                }
            }
            catch { /* an unpainted frame is still a usable dialog */ }

            try
            {
                if (Overlay == null) return;
                GameObject? backdrop = null;
                var existing = Overlay.transform.Find(DialogBackdropName);
                if (existing != null) backdrop = existing.gameObject;
                if (backdrop == null)
                {
                    backdrop = new GameObject(DialogBackdropName);
                    backdrop.transform.SetParent(Overlay.transform, false);
                }

                // First sibling: drawn before the frame and hit-tested after it, so the dialog's
                // own buttons stay the winners and nothing under the canvas is reachable.
                try { backdrop.transform.SetAsFirstSibling(); } catch { /* */ }
                var rt = backdrop.GetComponent<RectTransform>() ?? TryAdd<RectTransform>(backdrop);
                if (rt != null)
                {
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.offsetMin = Vector2.zero;
                    rt.offsetMax = Vector2.zero;
                }

                var fill = backdrop.GetComponent<Image>() ?? TryAdd<Image>(backdrop);
                if (fill == null) return;
                try { fill.sprite = null; } catch { /* */ }
                // DIALOGS2: the game's blocker (CommonPopup BlockerImage): black at 0.8 over the screen.
                try { fill.color = Rgba(GameArt.GameSettingsLook.PopupBlocker); } catch { /* */ }
                try { fill.raycastTarget = true; } catch { /* */ }
            }
            catch { /* without a backdrop the frame is still opaque */ }
        }

        /// <summary>Alpha 0 on this panel's overlay canvas, before <c>Show</c> activates it.</summary>
        private void HideForDialogReveal()
        {
            try
            {
                if (Overlay == null) return;
                var group = Overlay.GetComponent<CanvasGroup>() ?? TryAdd<CanvasGroup>(Overlay);
                if (group == null) return;
                group.alpha = 0f;
                group.blocksRaycasts = false;
                group.interactable = false;
            }
            catch { /* a dialog that cannot be hidden is drawn a frame early, never not at all */ }
        }

        /// <summary>Alpha 1, at the end of the layout pass that placed the frame.</summary>
        private void RevealDialog()
        {
            try
            {
                if (Overlay == null) return;
                var group = Overlay.GetComponent<CanvasGroup>();
                if (group == null) return;
                group.alpha = 1f;
                group.blocksRaycasts = true;
                group.interactable = true;
            }
            catch { /* */ }

            // DIALOGS1: a Prompt's field takes the keyboard when the dialog first appears, through
            // the same path a click on it takes (FocusOwnedField). Once per dialog.
            if (_dialogFocused) return;
            _dialogFocused = true;
            try
            {
                var field = GameObject == null ? null : GameObject.GetComponentInChildren<TMP_InputField>(true);
                if (field != null) FocusOwnedField(field);
            }
            catch { /* the player can still click the field */ }
        }

        private bool _dialogFocused;

        /// <summary>
        /// The body's cap for this pass: the frame cap less every other active content row (measured)
        /// and the gaps between them (<see cref="DialogLayout.BodyCap"/>).
        /// </summary>
        private float DialogBodyCap(float footerBand)
        {
            float others = 0f;
            int count = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Pin != MenuRowPin.Content) continue;
                bool active = true;
                try { if (row.Go != null) active = row.Go.activeSelf; } catch { active = true; }
                if (!active) continue;
                count++;
                if (string.Equals(row.Kind, DialogLayout.KindBody, StringComparison.Ordinal)) continue;
                others += MeasureRowHeight(row);
            }

            return DialogLayout.BodyCap(MeasureHeight().Value, footerBand, others, count);
        }

        public bool CanvasVisible
        {
            set
            {
                if (Overlay != null)
                {
                    try { Overlay.SetActive(value); } catch { /* */ }
                }
            }
        }

        /// <summary>HOSTS1 round 5: hide by switching the overlay canvas and raycaster off, objects kept active.</summary>
        internal bool SoftHide { get; set; }

        /// <summary>True while <see cref="SetCanvasEnabled"/> has the canvas off (a soft hide). Managed, so per-frame checks read it for free.</summary>
        private bool _canvasOff;

        internal void SetCanvasEnabled(bool enabled)
        {
            _canvasOff = !enabled;
            if (Overlay == null) return;
            var canvas = Overlay.GetComponent<Canvas>();
            if (canvas != null) canvas.enabled = enabled;
            var raycaster = Overlay.GetComponent<GraphicRaycaster>();
            if (raycaster != null) raycaster.enabled = enabled;
        }

        /// <summary>
        /// Register a row and place it at the current cursor. The single door every row type goes
        /// through: <paramref name="label"/> is measured here and nowhere else, so a new row type
        /// cannot be added without its caption being measured — which is exactly how
        /// <see cref="AddButton"/> spent a batch on a constant 40 px (R43 finding 3).
        ///
        /// <para>
        /// The row is remembered so <see cref="RunLayout"/> can place it again later. A panel is
        /// still built row by row, so this keeps the incremental placement it always had; what is
        /// new is that the placement is no longer the only one that will ever happen.
        /// </para>
        /// </summary>
        public void AddRow(
            GameObject go,
            GameObject? label,
            bool hasControl = false,
            string kind = "label",
            ControlSource source = ControlSource.None,
            GameObject? control = null,
            bool searchField = false)
        {
            var row = new PanelRow(go, label, hasControl, kind, source, control, _nextPin, searchField, _grid);
            if (_grid) PrepareGridRow(go, label, kind, control);
            _rows.Add(row);
            if (_inline && _inlineHost == null && (kind == "input" || kind == "toggle"))
                _inlineHost = go;
            PlaceRow(row);
            // WINDOWS1 live fix: a shown standard window grows to its new rows (on the next frame, once per batch).
            RequestWindowGrow();
        }

        /// <summary>
        /// KIT1: put <paramref name="control"/> in the <i>previous</i> row's right-hand column — the
        /// mock's exit link beside a section heading rather than under it. False when there is no row
        /// to attach to, when that row already owns a control, or when the row is pinned chrome; the
        /// caller then gives the control a row of its own.
        ///
        /// <para>The row is not re-placed here. Advancing the layout cursor a second time for the
        /// same row would put the next row on top of it, so the panel is asked for a whole
        /// <see cref="Relayout"/> instead, which is the one pass that resets the cursor and measures
        /// every row exactly once. <paramref name="size"/> is the square the control is given inside
        /// that column.</para>
        /// </summary>
        /// <summary>
        /// Controls that keep a fixed square inside their row's control column instead of stretching
        /// across it (KIT1's icon link), and the square each one asked for.
        /// </summary>
        private readonly Dictionary<GameObject, float> _squareControls = new();

        public bool AttachRightControl(GameObject control, float size)
        {
            if (control == null || _rows.Count == 0) return false;
            int last = _rows.Count - 1;
            var row = _rows[last];
            if (row.Go == null || row.HasControl || row.Control != null) return false;
            if (row.Pin != _nextPin) return false;
            try { control.transform.SetParent(row.Go.transform, false); }
            catch { return false; }
            try
            {
                var rt = control.GetComponent<RectTransform>() ?? control.AddComponent<RectTransform>();
                rt.sizeDelta = new Vector2(size, size);
            }
            catch { /* the relayout places it from the column model anyway */ }
            _squareControls[control] = size;
            _rows[last] = new PanelRow(
                row.Go, row.Label, hasControl: true, row.Kind, row.Source, control, row.Pin, row.SearchField, row.Grid);
            Relayout();
            return true;
        }

        /// <summary>Every row, for the interaction probe. Read only; the list itself is not exposed.</summary>
        /// <summary>TEXTTEST1: each row with its caption text (empty when it has none).</summary>
        internal void ForEachRowCaption(Action<GameObject, string, string> body)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                string caption = "";
                try
                {
                    var tmp = row.Label == null ? null : row.Label.GetComponent<TextMeshProUGUI>();
                    caption = tmp == null ? "" : tmp.text ?? "";
                }
                catch { caption = ""; }
                try { body(row.Go, row.Kind, caption); }
                catch { /* one unreadable row does not stop the search */ }
            }
        }

        public void ForEachRow(Action<GameObject, string, ControlSource> body)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                try { body(row.Go, row.Kind, row.Source); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: interaction probe row: " + e.Message); }
            }
        }

        /// <summary>
        /// Measure one row's caption, reserve that much of the cursor and put the row there.
        ///
        /// <para>
        /// Exception-safe by construction, because this runs on the game's main thread inside an
        /// IL2CPP process and a throw here would leave a panel half-built: a measurement that
        /// cannot be taken falls back to one ordinary row height, and the cursor is advanced
        /// <i>before</i> the placement so that a row that fails to move still cannot make the
        /// next row land on top of it.
        /// </para>
        /// </summary>
        private float MeasureRowHeight(in PanelRow row)
        {
            try
            {
                if (row.Go != null && _cellRows.TryGetValue(row.Go, out var cellRow)) return cellRow.Height;
                var cols = RowColumnsFor(row);
                if (IsDialogText(row.Kind)) return FitDialogText(row, cols);
                float height = InlineHostHeight(row, FitLabel(row.Label, cols, CaptionLeft(row)));
                height = TallRowHeight(row, cols, height, out _, out _);
                if (row.Kind == "heading") height += SettingsAlign.HeadingExtraTop;
                return height;
            }
            catch { return RowHeight; }
        }

        private bool CaptionLeft(in PanelRow row)
        {
            if (_strip) return false;
            // DIALOGS1: a dialog's title and body are centered; a grid row's caption is the name
            // column's, left-aligned on any pane (a custom page's controls use the grid).
            var rule = RowColumnRules.CaptionLeft(row.Kind, row.Grid);
            if (rule.HasValue) return rule.Value;
            if (IsLeftAligned) return true;
            return CaptionIsMultiline(row.Label);
        }

        private static bool CaptionIsMultiline(GameObject? label)
        {
            try
            {
                var tmp = label != null ? label.GetComponent<TextMeshProUGUI>() : null;
                string text = tmp == null ? "" : (tmp.text ?? "");
                return text.IndexOf('\n') >= 0;
            }
            catch { return false; }
        }

        private static bool IsDialogText(string? kind) =>
            string.Equals(kind, DialogLayout.KindTitle, StringComparison.Ordinal)
            || DialogLayout.IsBodyKind(kind);

        // DIALOGS1: one game-free decision (RowColumnRules), so the tests pin the geometry drawn here.
        private RowColumns RowColumnsFor(in PanelRow row) =>
            RowColumnRules.For(row.Kind, row.Grid, row.HasControl, row.SearchField, _menuPane, row.Grid ? GridWidth : ContentRowWidth,
                sizedToRow: IsStandardWindow);

        /// <summary>
        /// DIALOGS1: a dialog title or body row is its measured text's height (plus
        /// <see cref="DialogLayout.TextPad"/>), not the ordinary 40-unit row; the title is capped at
        /// three lines and the body at <see cref="_dialogBodyCap"/>, and TMP's ellipsis cuts the rest.
        /// </summary>
        private float FitDialogText(in PanelRow row, in RowColumns columns)
        {
            FitLabel(row.Label, columns, left: false);
            bool title = string.Equals(row.Kind, DialogLayout.KindTitle, StringComparison.Ordinal);
            float measured = 0f;
            try
            {
                var tmp = row.Label == null ? null : row.Label.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                {
                    var rt = row.Label!.GetComponent<RectTransform>();
                    if (rt != null) rt.sizeDelta = new Vector2(columns.LabelWidth, PanelLayout.MaxRowHeight);
                    tmp.ForceMeshUpdate();
                    measured = tmp.preferredHeight;
                }
            }
            catch { measured = 0f; }
            float height = DialogLayout.TextRowHeight(measured, title, title ? 0f : _dialogBodyCap);
            if (title) height += DialogLayout.TitleRuleBand;
            else if (string.Equals(row.Kind, DialogLayout.KindNote, StringComparison.Ordinal))
                height += DialogLayout.NoteTopMargin;
            try
            {
                var rt = row.Label == null ? null : row.Label.GetComponent<RectTransform>();
                if (rt != null) rt.sizeDelta = new Vector2(columns.LabelWidth, height);
            }
            catch { /* the reservation stands; only the text may clip */ }
            return height;
        }

        /// <summary>The body's cap for this pass (<see cref="DialogLayout.BodyCap"/>); 0 until a dialog pass computes it.</summary>
        private float _dialogBodyCap;

        private void PlaceRow(in PanelRow row, float shiftX)
        {
            // One call, both columns (evidence 176). The caption's width and x and the control's
            // x are the same answer, so "is this row indented?" and "does this row have a
            // control?" cannot be decided separately and disagree.
            var columns = RowColumnsFor(row);
            float height;
            try { height = FitLabel(row.Label, columns, CaptionLeft(row)); }
            catch { height = RowHeight; }
            height = InlineHostHeight(row, height);
            float nameBand = height, helpHeight = 0f;
            try { height = TallRowHeight(row, columns, height, out nameBand, out helpHeight); }
            catch { /* the caption height stands */ }
            // A cell row is a fixed band (RowHeight or the 53 px two-line variant): no caption
            // to measure, the cells are positioned inside it from the column model (LIST1).
            // INSTALLSIDES1 live fix: a fixed cell row reserves the height it is drawn at, even past
            // the measured-row cap (the drawer body), so the rows after it start below it.
            float maxHeight = PanelLayout.MaxRowHeight;
            // RADIOCLICK1: a compact cell row reserves exactly its height, and may set its own gap above.
            float minHeight = PanelLayout.MinRowHeight, gapAbove = -1f;
            if (row.Go != null && _cellRows.TryGetValue(row.Go, out var cellRowFixed))
            {
                height = cellRowFixed.Height;
                maxHeight = Math.Min(PanelLayout.MaxFixedRowHeight, Math.Max(PanelLayout.MaxRowHeight, height));
                minHeight = PanelLayout.CellRowFloor(cellRowFixed.Spec);
                gapAbove = cellRowFixed.Spec.GapAbove;
            }
            bool dialogText = IsDialogText(row.Kind);
            if (dialogText) height = FitDialogText(row, columns);
            float extraTop = 0f;
            if (string.Equals(row.Kind, "heading", StringComparison.Ordinal))
                extraTop = SettingsAlign.HeadingExtraTop;

            if (_strip)
            {
                // Re-place every strip button against the live count so the first
                // two are not left at the 1-wide / 2-wide slots they got when
                // added. Parent onto the panel (content is deactivated) and keep
                // the fill raycastable so EventSystem hits this row, not a native
                // tab underneath.
                PlaceStripRows();
                return;
            }

            bool active = true;
            try { if (row.Go != null) active = row.Go.activeSelf; }
            catch { active = true; }
            if (!active && row.Pin == MenuRowPin.Content)
                return;

            if (row.Pin == MenuRowPin.Header)
            {
                PlaceHeaderRows();
                return;
            }

            if (row.Pin == MenuRowPin.Top)
            {
                float top = TitleBand + PanelLayout.PadTop;
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i].Pin != MenuRowPin.Top) continue;
                    if (ReferenceEquals(_rows[i].Go, row.Go)) break;
                    if (!RowActive(_rows[i])) continue;
                    top += MeasureRowHeight(_rows[i]) + PanelLayout.RowGap;
                }

                try
                {
                    PlaceTop(row.Go, top + extraTop, ContentRowWidth, height);
                    PlaceRowControl(row, columns);
                    PlaceHeadingRule(row, height);
                    PlaceInlineRow(row);
                    PlacePageButtons(row, height);
                    PlaceCellRowCells(row);
                    PlaceTallRowParts(row, columns, height, nameBand, helpHeight);
                }
                catch { /* */ }
                return;
            }

            if (row.Pin == MenuRowPin.Bottom)
                return;

            float y = _layout.NextRow(height + extraTop, dialogText ? DialogLayout.MinTextRowHeight : minHeight, maxHeight, gapAbove);
            if (row.Grid)
            {
                // MENUKIT2: a settings-grid row is as wide as the grid (the description column takes the
                // rest), left-aligned with the content, with the game's row band behind it.
                try
                {
                    PlaceTop(row.Go, GridShiftX, y + extraTop, GridWidth, height);
                    PlaceRowControl(row, columns);
                    PlaceHeadingRule(row, height);
                    PlaceInlineRow(row);
                    PlacePageButtons(row, height);
                    PlaceCellRowCells(row);
                    PlaceTallRowParts(row, columns, height, nameBand, helpHeight);
                    PlaceGridChrome(row, height);
                    NoteGridRow(row, y + extraTop, height);
                }
                catch { /* the row keeps its reservation */ }
                return;
            }

            try
            {
                // A dialog text row keeps the width its text was measured and wrapped at.
                PlaceTop(row.Go, shiftX, y + extraTop, dialogText ? columns.LabelWidth : ContentRowWidth, height);
                PlaceRowControl(row, columns);
                PlaceHeadingRule(row, height);
                PlaceInlineRow(row);
                PlacePageButtons(row, height);
                PlaceCellRowCells(row);
                PlaceTallRowParts(row, columns, height, nameBand, helpHeight);
            }
            catch { /* the row keeps its reservation; only this one row is misplaced */ }
        }

        private void PlaceRow(in PanelRow row)
        {
            // MENUKIT2 round D: a plain row (a note, a label, an inline field) below the first settings
            // row shares the rows' width while the description column is shown, so nothing runs under it.
            // WINDOWS1: a Tabbed window's info-column row has its own column and cursor; its pinned toolbar and tab row
            // span the frame (the info column and the content both sit under them).
            if (PlaceSideRow(row)) return;
            _placingTop = IsStandardWindow && row.Pin == MenuRowPin.Top;
            try { PlaceRowBesideColumn(row); }
            finally { _placingTop = false; }
        }

        private void PlaceRowBesideColumn(in PanelRow row)
        {
            bool beside = BesideColumn(row);
            if (beside || row.Grid) _narrowWidth = GridWidth;
            try { PlaceRow(row, beside ? GridShiftX : 0f); }
            finally { _narrowWidth = 0f; }
        }

        /// <summary>KEYS3: whether a row's object is active (a hidden pinned row takes no chrome band).</summary>
        private static bool RowActive(in PanelRow row)
        {
            try { return row.Go == null || row.Go.activeSelf; }
            catch { return true; }
        }

        /// <summary>
        /// One line under a game tab row (<see cref="UiTabsLayout.TopLinesHidden"/>): the first shown row under the strip
        /// drops its own top line (a settings-grid row's top gradient, a cell row's separator), so the tabs stand on the
        /// strip's baseline alone. Run every layout pass, after a tab switch showed or hid rows.
        /// </summary>
        private void ApplyTabStripLines()
        {
            bool anyStrip = false;
            foreach (var cr in _cellRows.Values)
                if (cr.IsGameTabStrip) { anyStrip = true; break; }
            if (!anyStrip && !_stripLinesHidden) return;
            var rows = new List<UiTabsLayout.StripRow>(_rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                // A removed row can stay listed until the next sweep: a destroyed object is not shown, so the scan passes it.
                if (row.Go == null || !Alive(row.Go))
                {
                    rows.Add(new UiTabsLayout.StripRow(false, false, row.Pin, false));
                    continue;
                }

                _cellRows.TryGetValue(row.Go, out var cellRow);
                bool topLine = cellRow != null ? cellRow.HasTopLine : _gridChrome.TryGetValue(row.Go, out var c) && c.Top != null;
                rows.Add(new UiTabsLayout.StripRow(RowActive(row), cellRow != null && cellRow.IsGameTabStrip, row.Pin, topLine));
            }

            var hidden = UiTabsLayout.TopLinesHidden(rows);
            _stripLinesHidden = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                var go = _rows[i].Go;
                if (go == null || !Alive(go)) continue;
                if (hidden[i]) _stripLinesHidden = true;
                if (_cellRows.TryGetValue(go, out var cellRow)) cellRow.SetTopLineHidden(hidden[i]);
                else if (_gridChrome.TryGetValue(go, out var chrome) && chrome.Top != null)
                {
                    try { if (chrome.Top.enabled == hidden[i]) chrome.Top.enabled = !hidden[i]; } catch { /* */ }
                }
            }
        }

        /// <summary>Some row's top line is hidden under a game tab row, so a pass after the strip is gone restores it.</summary>
        private bool _stripLinesHidden;

        /// <summary>Cell rows registered on this panel (LIST1), keyed by the row object.</summary>
        private readonly Dictionary<GameObject, UnityCellRow> _cellRows = new();

        internal void RegisterCellRow(GameObject go, UnityCellRow cellRow, bool header)
        {
            _cellRows[go] = cellRow;
            AddRow(go, null, hasControl: false, kind: header ? "cellheader" : "cellrow", source: ControlSource.Built);
        }

        internal void ForgetCellRow(GameObject go)
        {
            if (go != null) _cellRows.Remove(go);
        }

        /// <summary>
        /// Job LIST4 item 2: every clickable cell of every live cell row on this panel, as
        /// (row key, cell key, cell object). This is the registry the pointer probe was missing —
        /// <see cref="ForEachRow"/> answers row objects and their kind, so a cell <i>inside</i> a
        /// row was invisible to it whether the row was built on the first pass or a later rebuild.
        /// </summary>
        internal void ForEachCellButton(Action<string, string, GameObject> body)
        {
            foreach (var pair in _cellRows)
            {
                var row = pair.Value;
                if (row == null || !row.Alive || !row.Visible) continue;
                try { row.ForEachClickableCell(body); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell probe row: " + e.Message); }
            }
        }

        /// <summary>Re-place a cell row's cells against the current row width; every layout pass runs this, so a resize re-resolves the columns.</summary>
        private void PlaceCellRowCells(in PanelRow row)
        {
            if (row.Go == null || !_cellRows.TryGetValue(row.Go, out var cellRow)) return;
            try { cellRow.PlaceCells(row.Grid ? GridWidth : ContentRowWidth); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell row placement: " + e.Message); }
        }

        private void PlaceHeadingRule(in PanelRow row, float height)
        {
            if (!string.Equals(row.Kind, "heading", StringComparison.Ordinal) || row.Go == null) return;
            Transform? rule = null;
            try { rule = row.Go.transform.Find("heading-rule"); }
            catch { return; }
            if (rule == null) return;
            try { PlaceTop(rule.gameObject, 0f, height - 2f, ContentRowWidth, 1f); }
            catch { /* */ }
        }

        private void PlaceInlineRow(in PanelRow row)
        {
            PlaceCompactChoices(row);
            if (row.Go == null || !_inlineButtons.TryGetValue(row.Go, out var buttons) || buttons.Count == 0)
                return;
            if (row.Grid)
            {
                // Settings organization review: a settings-grid row keeps its name column; the field and its buttons
                // share the control column (ModSettingsRowGrid.InlineSlots), centered on the row like every grid control.
                var grid = RowGrid;
                float barH = ModSettingsRowGrid.BarHeight;
                for (int i = 0; i < buttons.Count; i++)
                {
                    var slots = grid.InlineSlots(buttons.Count, i);
                    if (i == 0 && row.Control != null) PlaceGridCentered(row.Control, grid, slots.FieldLeft, slots.FieldWidth, barH);
                    PlaceGridCentered(buttons[i], grid, slots.ButtonLeft, slots.ButtonWidth, barH);
                    FitInlineButtonCaption(buttons[i]);
                    KeepHintRoom(buttons[i]);
                }

                return;
            }

            var geo = SettingsAlign.InlineRow(ContentRowWidth, buttons.Count);
            if (row.Control != null)
            {
                try { PlaceTop(row.Control, geo.FieldX, 0f, geo.FieldWidth, RowHeight); }
                catch { /* */ }
            }
            for (int i = 0; i < buttons.Count; i++)
            {
                try { PlaceTop(buttons[i], geo.ButtonX(i), 0f, geo.ButtonWidth, RowHeight); }
                catch { /* */ }
                FitInlineButtonCaption(buttons[i]);
                KeepHintRoom(buttons[i]);
            }
        }

        private static void PlaceGridCentered(GameObject go, in ModSettingsRowGrid grid, float left, float width, float height)
        {
            try
            {
                var rt = go.GetComponent<RectTransform>();
                if (rt == null) return;
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(grid.FromCenter(left + width * 0.5f), 0f);
                rt.sizeDelta = new Vector2(width, height);
            }
            catch { /* the object keeps its placement */ }
        }

        /// <summary>
        /// Fixed listing header: title and facts stacked on the left, action
        /// button(s) on the right of the same band. Viewport inset is
        /// <see cref="MenuPanelLayout.PinnedHeaderHeight"/>; this placement
        /// must stay inside that band.
        /// </summary>
        private void PlaceHeaderRows()
        {
            int buttons = HeaderButtonCount();
            MenuPanelLayout.HeaderColumns(
                ContentRowWidth, buttons, out float labelWidth, out float labelX, out float actionWidth, out float actionX);
            float top = TitleBand + PanelLayout.PadTop;
            float labelTop = top;
            float reasonTop = top + UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap;
            int buttonIndex = 0;

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Pin != MenuRowPin.Header) continue;
                try
                {
                    if (row.Go != null)
                    {
                        row.Go.transform.SetParent(GameObject.transform, false);
                        if (!row.Go.activeSelf) row.Go.SetActive(true);
                    }
                }
                catch { /* */ }

                bool isButton = string.Equals(row.Kind, "button", StringComparison.Ordinal);
                try
                {
                    if (row.Go == null) continue;
                    if (isButton)
                    {
                        // Buttons share the action column left to right (primary, then Remove).
                        // The caption was created at RowWidth: stretch it into this rect and
                        // keep it one line, or it draws as free text beside an empty box.
                        MenuPanelLayout.HeaderButton(ContentRowWidth, buttonIndex, buttons, out float x, out float width);
                        PlaceTop(row.Go, x, top, width, UnityUiRuntimeMetrics.RowHeight);
                        FitInlineButtonCaption(row.Go);
                        PrepareHeaderButtonCaption(row.Go);
                        buttonIndex++;
                    }
                    else if (buttonIndex > 0)
                    {
                        // The reason: under the buttons, right-aligned, wrapped to the action
                        // column and capped at HeaderReasonMaxLines (ellipsis past that).
                        float cap = MenuPanelLayout.HeaderReasonMaxLines * MenuPanelLayout.HeaderReasonLineHeight;
                        PrepareHeaderReason(row.Label);
                        float height;
                        try
                        {
                            var cols = new RowColumns(actionWidth, actionX, 0f, 0f);
                            height = FitLabel(row.Label, cols, left: false);
                        }
                        catch { height = MenuPanelLayout.HeaderReasonLineHeight; }
                        if (height > cap) height = cap;
                        PlaceTop(row.Go, actionX, reasonTop, actionWidth, height);
                        SizeHeaderLabel(row.Label, actionWidth, actionX, height, right: true);
                        reasonTop += height + PanelLayout.RowGap;
                    }
                    else
                    {
                        // Title and facts. A row that wraps its label (heading-row) is placed
                        // at x = 0 over the whole band and FitLabel offsets the label to the
                        // column; a self-labeled note row *is* the label, so it goes to the
                        // column directly. Gen 39 placed the heading-row at labelX and then
                        // FitLabel moved the label another labelX: the title started left of
                        // the pane.
                        bool isHeading = string.Equals(row.Kind, "heading", StringComparison.Ordinal);
                        float extraTop = isHeading ? SettingsAlign.HeadingExtraTop : 0f;
                        if (isHeading) PrepareHeaderTitle(row.Label);
                        float height;
                        try
                        {
                            var cols = new RowColumns(labelWidth, labelX, 0f, 0f);
                            height = FitLabel(row.Label, cols, CaptionLeft(row));
                        }
                        catch { height = UnityUiRuntimeMetrics.RowHeight; }
                        if (isHeading && height > UnityUiRuntimeMetrics.RowHeight) height = UnityUiRuntimeMetrics.RowHeight;
                        bool wrapsLabel = row.Label != null && !ReferenceEquals(row.Label, row.Go);
                        if (wrapsLabel)
                        {
                            PlaceTop(row.Go, 0f, labelTop + extraTop, ContentRowWidth, height);
                            SizeHeaderLabel(row.Label, labelWidth, labelX, height, right: false);
                            PlaceHeaderHeadingRule(row, height, labelWidth, labelX);
                        }
                        else
                        {
                            PlaceTop(row.Go, labelX, labelTop + extraTop, labelWidth, height);
                        }
                        labelTop += height + extraTop + PanelLayout.RowGap;
                    }
                }
                catch { /* */ }
            }
        }

        private int HeaderButtonCount()
        {
            int buttons = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Pin != MenuRowPin.Header) continue;
                if (string.Equals(_rows[i].Kind, "button", StringComparison.Ordinal)) buttons++;
            }
            return buttons;
        }

        /// <summary>
        /// Estimated wrapped lines of the header reason (the text row after the
        /// first header button), for the band height. Pure arithmetic on the
        /// caption text; the same estimate the layout tests use.
        /// </summary>
        internal int HeaderReasonLineCount()
        {
            int buttons = HeaderButtonCount();
            if (buttons == 0) return 0;
            MenuPanelLayout.HeaderColumns(ContentRowWidth, buttons, out _, out _, out float actionWidth, out _);
            bool sawButton = false;
            int lines = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Pin != MenuRowPin.Header) continue;
                if (string.Equals(row.Kind, "button", StringComparison.Ordinal))
                {
                    sawButton = true;
                    continue;
                }
                if (!sawButton) continue;
                string text = "";
                try
                {
                    var tmp = row.Label != null ? row.Label.GetComponent<TextMeshProUGUI>() : null;
                    text = tmp == null ? "" : (tmp.text ?? "");
                }
                catch { text = ""; }
                lines += MenuPanelLayout.HeaderReasonLines(text, actionWidth);
            }
            return Math.Min(lines, MenuPanelLayout.HeaderReasonMaxLines);
        }

        private static void SizeHeaderLabel(GameObject? label, float width, float x, float height, bool right)
        {
            if (label == null) return;
            try
            {
                var rt = label.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.sizeDelta = new Vector2(width, height);
                    rt.anchoredPosition = new Vector2(x, rt.anchoredPosition.y);
                }
            }
            catch { /* */ }
            if (!right) return;
            try
            {
                var tmp = label.GetComponent<TextMeshProUGUI>();
                if (tmp != null)
                    tmp.horizontalAlignment = (HorizontalAlignmentOptions)((int)TextAlignmentOptions.Right & LabelTmpWrite.HorizontalMask);
            }
            catch { /* */ }
        }

        /// <summary>
        /// Title: one line. The ellipsis past the left column is
        /// <see cref="ClampCaptionText"/>'s, applied by the <see cref="FitLabel"/>
        /// call that follows.
        /// </summary>
        private static void PrepareHeaderTitle(GameObject? label)
        {
            if (label == null) return;
            try
            {
                var tmp = label.GetComponent<TextMeshProUGUI>();
                if (tmp == null) return;
                tmp.enableWordWrapping = false;
            }
            catch { /* */ }
        }

        /// <summary>Reason note: wraps inside the action column at the note size; <see cref="FitLabel"/> clamps it past the cap.</summary>
        private static void PrepareHeaderReason(GameObject? label)
        {
            if (label == null) return;
            try
            {
                var tmp = label.GetComponent<TextMeshProUGUI>();
                if (tmp == null) return;
                tmp.fontSize = MenuPanelLayout.HeaderReasonFontSize;
                tmp.enableWordWrapping = true;
            }
            catch { /* */ }
        }

        /// <summary>
        /// Header button caption: one centered line at the header caption size. A
        /// header button is placed here rather than measured by <see cref="FitLabel"/>,
        /// so the caption clamp is applied explicitly through the same
        /// <see cref="ClampCaptionText"/> every measured caption gets.
        /// </summary>
        private static void PrepareHeaderButtonCaption(GameObject button)
        {
            try
            {
                var tmp = button.GetComponentInChildren<TextMeshProUGUI>(true);
                if (tmp == null) return;
                tmp.fontSize = MenuPanelLayout.HeaderButtonFontSize;
                tmp.enableWordWrapping = false;
                ClampCaptionText(tmp.gameObject);
                tmp.margin = new Vector4(
                    MenuPanelLayout.HeaderButtonCaptionPad * .5f, 0f, MenuPanelLayout.HeaderButtonCaptionPad * .5f, 0f);
                ApplyHorizontalAlign(tmp, left: false);
            }
            catch { /* the button rect is still the click target */ }
        }

        private static void PlaceHeaderHeadingRule(in PanelRow row, float height, float width, float x)
        {
            if (!string.Equals(row.Kind, "heading", StringComparison.Ordinal) || row.Go == null) return;
            Transform? rule = null;
            try { rule = row.Go.transform.Find("heading-rule"); }
            catch { return; }
            if (rule == null) return;
            try { PlaceTop(rule.gameObject, x, height - 2f, width, 1f); }
            catch { /* */ }
        }

        private void PlaceStripRows()
        {
            int count = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (string.Equals(_rows[i].Kind, "button", StringComparison.Ordinal))
                    count++;
            }
            if (count < 1) count = 1;
            int slot = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (!string.Equals(row.Kind, "button", StringComparison.Ordinal))
                    continue;
                try
                {
                    if (row.Go != null)
                    {
                        row.Go.transform.SetParent(GameObject.transform, false);
                        if (!row.Go.activeSelf) row.Go.SetActive(true);
                    }
                }
                catch { /* */ }
                var button = MenuPanelLayout.StripButton(_menuGeometry, slot, count);
                float x = button.X - _menuGeometry.X;
                try { if (row.Go != null) PlaceTop(row.Go, x, 0f, button.Width, button.Height); }
                catch { /* */ }
                try
                {
                    var image = row.Go != null ? row.Go.GetComponent<Image>() : null;
                    if (image != null) image.raycastTarget = true;
                }
                catch { /* */ }
                PrepareStripButtonCaption(row.Label);
                slot++;
            }
        }

        private void PlaceRowControl(in PanelRow row, in RowColumns columns)
        {
            if (row.Go != null && _inlineButtons.ContainsKey(row.Go)) return;
            if ((!_menuPane && !row.Grid && row.Kind != "fact" && row.Kind != "info") || row.Control == null) return;
            var controlRt = row.Control.GetComponent<RectTransform>();
            if (controlRt == null) return;
            var cols = row.SearchField ? MenuPanelLayout.SearchFieldColumns(ContentRowWidth) : columns;
            // KIT1: an icon link attached to a heading row keeps its square and sits at the right
            // edge of the control column. Stretching it across the column like an editor control is
            // what would turn a 30 px icon into a 250 px empty box.
            if (_squareControls.TryGetValue(row.Control, out float square))
            {
                PlaceSquareControl(row.Control, cols, Math.Min(square, cols.ControlWidth));
                return;
            }
            if (row.Grid)
            {
                PlaceGridControl(row, controlRt);
                return;
            }

            float width = row.Kind == "toggle" && !row.SearchField
                ? Math.Min(controlRt.rect.width, cols.ControlWidth)
                : cols.ControlWidth;
            PlaceControl(row.Control, cols, width, controlRt.rect.height);
            if (string.Equals(row.Kind, "fact", StringComparison.Ordinal) || string.Equals(row.Kind, "info", StringComparison.Ordinal))
                ApplyCaptionAlign(row.Control, left: true);
        }

        /// <summary>
        /// SETTINGS1 stage 2: a settings-grid control in its slot (<see cref="ModSettingsGrid.ControlSlot"/>):
        /// full-width controls fill the fixed control column; compact ones (a switch, a read-only or info
        /// value) end on its right edge and are centered on the single-line band. Every single-line
        /// control is drawn <see cref="ModSettingsGrid.ControlHeight"/> tall.
        /// </summary>
        private void PlaceGridControl(in PanelRow row, RectTransform controlRt)
        {
            PlaceGameGridControl(row, controlRt);

            // GRIDROW1: a fact on the grid reads as an info line: its value right-aligned in the control column.
            if (string.Equals(row.Kind, "info", StringComparison.Ordinal) || string.Equals(row.Kind, "fact", StringComparison.Ordinal))
            {
                try
                {
                    var tmp = row.Control!.GetComponent<TextMeshProUGUI>();
                    if (tmp != null)
                    {
                        tmp.horizontalAlignment = HorizontalAlignmentOptions.Right;
                        tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                    }
                }
                catch { /* */ }
            }
        }

        /// <summary>
        /// Lay the panel out again because a row's text changed under it.
        ///
        /// <para>
        /// The scroll position is deliberately <b>not</b> reset, unlike a show: this fires while
        /// the player is looking at a row — the rebind flow writes "press a key…" and then the
        /// reason a chord was refused — and yanking the list back to the top mid-rebind would be
        /// its own bug. Nor does it re-announce the layout under the "laid out" wording that
        /// belongs to a show.
        /// </para>
        /// </summary>
        public void Relayout()
        {
            EnsureLayoutBatchBound();
            if (!UiLayoutBatch.ShouldRunLayout())
            {
                if (!_layoutDirty)
                {
                    _layoutDirty = true;
                    DeferredLayoutPanels.Add(this);
                }
                return;
            }

            _layoutDirty = false;
            if (!UiTickTiming.Enabled)
            {
                RunLayout(resetScroll: false, firstShow: false);
                return;
            }
            long start = Stopwatch.GetTimestamp();
            try { RunLayout(resetScroll: false, firstShow: false); }
            finally { UiTickTiming.Finish("Relayout", start); }
        }

        internal void FlushDeferredLayout()
        {
            if (!_layoutDirty) return;
            Relayout();
        }

        /// <summary>
        /// Size the content rect to the rows in it and the panel frame to that content, capped at
        /// a share of the screen. Called from <see cref="UnityUiRuntime.Show"/>, which is the
        /// first moment every row is known — a settings panel is filled row by row and then
        /// shown once.
        ///
        /// <para>
        /// The scroll position is reset to the top on every show, so re-opening a panel does not
        /// resume halfway down a list the player has since changed.
        /// </para>
        /// </summary>
        /// <summary>
        /// The height the panel has to fit into, <b>in the overlay canvas's own units</b>.
        ///
        /// <para>
        /// Not <c>Screen.height</c> (evidence 165). Since the overlay copies the game's
        /// <c>CanvasScaler</c>, the canvas is laid out in the game's design space and one canvas
        /// unit is not one device pixel; <c>Screen.height</c> answers in pixels and would cap the
        /// panel against a number from a different coordinate system — too small on a high-DPI
        /// screen, too large on a scaled-up UI. The canvas's own rect is the ruler everything
        /// else on it is measured with.
        /// </para>
        ///
        /// <para>
        /// <b>And the fallback used to be <c>Screen.height</c> anyway</b> (R43 finding 5, open
        /// through R44 and R45, closed by job FDOC5). A doc comment that condemns a number three
        /// lines above the branch that reaches for it is not a rule, and the branch is not
        /// hypothetical: the canvas rect can read zero, and it most plausibly does at the one
        /// moment this runs, because <c>SettingsPanel.Open</c> builds a fresh panel and lays it
        /// out in the same frame. The chain is <see cref="PanelRuler"/> now: the
        /// <c>Screen</c> rung is skipped entirely while the overlay is in design space, and the
        /// rung that answered is on the layout line as <c>ruler=</c>.
        /// </para>
        /// </summary>
        private float AvailableHeight() => MeasureHeight().Value;

        /// <summary>
        /// The same measurement, carrying the rung it came from (job FDOC5, R43 finding 5 / R45
        /// finding 38). The chain itself is <see cref="PanelRuler.Choose"/>, which is game-free
        /// and tested; this method only does the three reads.
        /// </summary>
        private RulerReading MeasureHeight() => PanelRuler.Choose(
            ReadCanvasExtent(vertical: true, out _canvasRead),
            OverlayIsInDesignSpace(),
            ReadScreenExtent(vertical: true),
            PanelLayout.FallbackScreenHeight);

        /// <summary>
        /// The width the panel has to fit into, in the same canvas units as
        /// <see cref="AvailableHeight"/> and for the same reason (evidence 165). Only the cascade
        /// needs it: the frame's own width is the constant <see cref="PanelWidth"/>, but how far
        /// sideways a second panel may be stepped is a question about the canvas.
        /// </summary>
        private float AvailableWidth() => MeasureWidth().Value;

        /// <summary>
        /// The width axis deliberately discards the canvas-read reason. One panel has one settle
        /// state, and it is the height that caps the frame and that <c>avail=</c> reports; a second
        /// writer of <see cref="_canvasRead"/> would make the token on the layout line describe
        /// whichever axis was measured last.
        /// </summary>
        private RulerReading MeasureWidth() => PanelRuler.Choose(
            ReadCanvasExtent(vertical: false, out _),
            OverlayIsInDesignSpace(),
            ReadScreenExtent(vertical: false),
            PanelFlow.FallbackCanvasWidth);

        /// <summary>
        /// The overlay canvas's own rect, or <c>0f</c> for unreadable / not yet sized — and, in
        /// <paramref name="state"/>, <b>which</b> of those it was (job FUI32).
        ///
        /// <para>FDOC5's chain was handed one <c>0f</c> for four different situations, which was
        /// enough to pick a rung and not enough to say why. "Unity has not sized this canvas yet",
        /// "there is no <c>RectTransform</c> here" and "the read threw through the interop" are
        /// three defects with three different fixes, and only the first is the one this panel's
        /// deferred re-measure can do anything about.</para>
        /// </summary>
        private float ReadCanvasExtent(bool vertical, out CanvasRead state)
        {
            try
            {
                var canvasRt = Overlay?.GetComponent<RectTransform>();
                if (canvasRt == null)
                {
                    state = CanvasRead.NoRect;
                    return 0f;
                }

                float v = vertical ? canvasRt.rect.height : canvasRt.rect.width;
                state = v > 0f ? CanvasRead.Sized : CanvasRead.Zero;
                return v > 0f ? v : 0f;
            }
            catch
            {
                state = CanvasRead.Threw;
                return 0f;
            }
        }

        /// <summary>Device pixels, or <c>0f</c>. Only <see cref="PanelRuler"/> decides whether to use it.</summary>
        internal static float ScreenHeightForHit() => ReadScreenExtent(vertical: true);

        private static float ReadScreenExtent(bool vertical)
        {
            try
            {
                float v = vertical ? Screen.height : Screen.width;
                return v > 0f ? v : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Whether this panel's overlay is laid out in the game's design space rather than in
        /// device pixels — read live off the canvas's own <c>CanvasScaler</c>, not latched at
        /// creation. <c>AdoptGameScaler</c> sets <c>ScaleWithScreenSize</c> when it found a
        /// vanilla donor and <c>ConstantPixelSize</c> when it did not, so the component itself is
        /// the record, and a re-adoption (or a scene change that gets one) is followed rather than
        /// remembered. Unreadable answers <b>true</b>: that is the conservative direction here,
        /// because it costs the <c>Screen</c> rung and keeps the constant, and the constant is
        /// wrong in a way a reader can see.
        /// </summary>
        private bool OverlayIsInDesignSpace()
        {
            try
            {
                var scaler = Overlay?.GetComponent<CanvasScaler>();
                if (scaler == null) return true;
                return scaler.uiScaleMode != CanvasScaler.ScaleMode.ConstantPixelSize;
            }
            catch { return true; }
        }

        public void ApplyLayout() => RunLayout(resetScroll: true, firstShow: true);

        /// <summary>
        /// Stamp every TMP child with the cached game font. No-op when the lookup
        /// has not found a donor yet; the next Show or settle pass retries.
        /// </summary>
        private void ApplyGameFont()
        {
            if (FindFont() == null) return;
            ForEachIn<TextMeshProUGUI>(GameObject, tmp => ApplyCachedFont(tmp));
        }

        /// <summary>
        /// Forget an earlier show's wait (job FUI32). A show is a brand-new canvas —
        /// <c>CreatePanel</c> makes one per panel — so the budget that follows is about this
        /// canvas, not about one a previous show already gave up on. Without this a panel that was
        /// opened once on a screen whose canvas never sized would keep the fallback for the rest of
        /// the session, silently, however many times it was re-opened afterwards.
        /// </summary>
        private void ResetSettleBudget()
        {
            _settleFrames = 0;
            _settleExhausted = false;
        }

        /// <summary>
        /// One frame of the deferred re-measure (job FUI32), driven by
        /// <see cref="UnityUiRuntime.SettlePump"/>. Returns <c>false</c> when this panel is owed
        /// nothing further, either because it has been laid out against the real canvas or because
        /// the budget ran out and the log now says so.
        ///
        /// <para>The re-measure is three reads, not a layout pass: nothing is re-measured, moved or
        /// logged on a frame where the canvas is still not readable, so the cost of waiting is a
        /// rect read per frame per panel being shown, for one frame in the normal case.</para>
        /// </summary>
        public bool SettleTick()
        {
            if (Released) return false;

            _settleFrames++;
            RulerReading now = MeasureHeight();
            switch (RulerSettle.Step(now, _settleFrames, RulerSettle.MaxFrames))
            {
                case RulerSettleStep.Relayout:
                    // Both readings, before the pass that replaces one with the other, so the two
                    // layout lines in the log are joined by a line that says which to believe.
                    Plugin.Logger?.LogDiag(RulerSettle.Describe(Text, _laidOutWith, now, _settleFrames));
                    _settling = true;
                    try { RunLayout(resetScroll: false, firstShow: false); }
                    finally { _settling = false; }
                    return false;

                case RulerSettleStep.GiveUp:
                    _settleExhausted = true;
                    Plugin.Logger?.LogWarning(
                        RulerSettle.DescribeGiveUp(Text, _laidOutWith, _canvasRead, _settleFrames));
                    return false;

                default:
                    return true;
            }
        }

        /// <summary>
        /// Read the scrollbar's own active flag and say whether it agrees with the
        /// <c>(fits)</c>/<c>(scrolls)</c> this panel last logged (R45 finding 22).
        ///
        /// <para>
        /// <c>activeSelf</c>, not <c>activeInHierarchy</c>: <c>ScrollRect.SetScrollbarsActive</c>
        /// writes the bar's own flag, and a hidden panel makes every descendant's
        /// <c>activeInHierarchy</c> false regardless of what the owner decided — which would turn
        /// a closed panel into a false disagreement report every time.
        /// </para>
        ///
        /// <para>
        /// A flag that throws is reported as unknown and never as hidden: a diagnostic that
        /// substitutes a default for a read it did not get is the thing this line exists to stop.
        /// </para>
        /// </summary>
        /// <summary>The read-back, for the runtime's <see cref="UnityUiRuntime.Hide"/> path.</summary>
        public void ReadBackScrollbarNow(string when)
        {
            ReadBackScrollbar(when, endsVerdict: true);
            _lastReadBackLine = null; // the next show starts its own run of lines
        }

        /// <param name="endsVerdict">
        /// True when nothing later checks this verdict: a re-layout replaces it, a hide is the last moment.
        /// </param>
        private void ReadBackScrollbar(string when, bool endsVerdict)
        {
            // P7b: a panel with no scroll view (NoScroll) has no bar to agree or disagree, whenever the pass was armed.
            // And only once ScrollRect has had its update over this verdict's content (2026-10-14: a build re-lays
            // a panel out once per row in one frame, and each pass read the previous open's bar).
            bool live;
            try { var scroll = Scroll; live = scroll != null && scroll.isActiveAndEnabled; }
            catch { live = false; }
            var action = ScrollbarVerdict.Decide(_verdictPending, NoScroll, live, _verdictFrame, FrameNow(), endsVerdict);
            if (action == ReadBackAction.Defer) return;

            _verdictPending = false;
            if (action != ReadBackAction.Read || _scrollbar == null) return;

            ScrollbarState observed;
            try { observed = _scrollbar.activeSelf ? ScrollbarState.Shown : ScrollbarState.Hidden; }
            catch { observed = ScrollbarState.Unreadable; }

            // File only, agreement or not (2026-10-06: dozens per session reached the console as
            // warnings), and only when it differs from this show's last line. Grep LogOutput.log for "they disagree".
            string line = ScrollbarVerdict.Describe(Text, _verdictScrolls, observed, when);
            if (!ScrollbarVerdict.ShouldLog(_lastReadBackLine, line)) return;
            _lastReadBackLine = line;
            Plugin.Logger?.LogDiag(line);
        }

        private static int FrameNow()
        {
            try { return Time.frameCount; }
            catch { return int.MinValue; }
        }

        /// <summary>
        /// Move the frame to where its current <see cref="Spot"/> says it goes, without measuring
        /// or moving a single row (job FUI21).
        ///
        /// <para>
        /// This is the re-flow path: when a panel is shown or hidden, every <i>other</i> shown
        /// panel is in the wrong place, because where two panels go is a fact about there being
        /// two of them. Re-running <see cref="RunLayout"/> on them would re-measure every row of
        /// every open panel to answer a question about arithmetic on the frame — and would reset
        /// nothing else only by luck. So the position is recomputed from the size the frame
        /// already has, which is the size its own layout pass gave it.
        /// </para>
        /// </summary>
        internal bool TopAligned;

        public void Reposition()
        {
            if (Released) return;

            // A re-flow happens frames after the layout that produced the pending verdict — a new
            // panel opened or an old one closed — which makes it the cheapest honest moment to
            // read the bar back. Nothing here changes the content, so the answer still belongs to
            // the verdict being checked.
            ReadBackScrollbar("a reposition", endsVerdict: false);
            try
            {
                var rt = GameObject.GetComponent<RectTransform>();
                if (rt == null) return;

                // CONSENT2: a self-placed panel is not one of the flowed set. Its rank still
                // changes as panels come and go, and saying so is worth a line, but writing
                // PanelFlow's offset onto it is what moved the consent prompt 332 units left of
                // where it had just been revealed.
                if (SelfPlaced)
                {
                    Plugin.Logger?.LogInfo(
                        "Ui: panel '" + Text + "' re-flow skipped rank=" + Spot.Rank + "/" + Spot.Count
                        + " at=(self-placed)");
                    return;
                }

                var geometry = _menuPane
                    ? _menuGeometry
                    : MenuPanelLayout.ForCanvas(AvailableWidth(), AvailableHeight(), _sidebar, _mode);
                // WINDOWS1: a panel in the window row hangs from its top line (UiWindowLayout.Place); one whose cap
                // changed with the set (a cascade layer) is laid out again first, and placed from the height that
                // re-layout gave it, never the stale one (UiWindowLayout.RepositionInRow).
                var at = _menuPane ? new PanelOffset(geometry.X, geometry.Y)
                    : Docked ? RepositionDocked(rt, AvailableHeight())
                    : InWindowRow ? RepositionInRow(rt, AvailableHeight())
                    : PanelFlow.Offset(Spot, PanelWidth, AvailableWidth(), AvailableHeight());
                // INSTALLUX2: a dialog stays in the middle however the shown set changes around it.
                // A wide game window that asked for a dialog's width is centered too (a standard window is placed above).
                if (_dialog || (WideGameWindow && !IsStandardWindow && !Docked)) at = PanelOffset.Centre;
                rt.anchoredPosition = new Vector2(at.X, at.Y);
                if (TopAligned && !Docked) rt.anchoredPosition = new Vector2(0, Math.Max(0, (AvailableHeight() - rt.sizeDelta.y) / 2 - 24));

                Plugin.Logger?.LogDiag(
                    "Ui: panel '" + Text + "' placed rank=" + Spot.Rank + "/" + Spot.Count
                    + " at=(" + ((int)at.X) + "," + ((int)at.Y) + ")");
            }
            catch (Exception e)
            {
                // A panel that could not be moved keeps the position it had, which is a panel the
                // player can still read, rather than one at a half-applied offset.
                Plugin.Logger?.LogWarning("Ui: panel reposition threw: " + e.Message);
            }
        }

        /// <summary>
        /// One layout pass: re-measure every row, re-place all of them from the top, then size the
        /// content rect and the frame to the result.
        ///
        /// <para>
        /// Re-measuring <i>every</i> row rather than only the one that changed is deliberate. A
        /// row's height is a function of its text, its width and the font, and the row below it
        /// only knows where it goes once every row above it has answered; a targeted "fix up the
        /// rows after row i" would need the heights above it to still be true, which is the
        /// assumption that produced the bug in the first place. Thirty-odd rows measured on a
        /// player-initiated event is not a cost worth being clever about.
        /// </para>
        /// </summary>
        private void RunLayout(bool resetScroll, bool firstShow)
        {
            SyncPageButtonRows();
            if (Released || _laying) return;
            // Labels built on a frame whose sweep missed (ConnectingView, epoch
            // just moved) keep TMP's default sans until a later pass finds the
            // game asset. Show and settle both run this, so one tick is enough.
            try { ApplyGameFont(); } catch { /* a missing font still lays out */ }
            long start = Stopwatch.GetTimestamp();

            // Before anything is re-measured: the bar still carries ScrollRect's answer to the
            // verdict this panel logged last time, and that answer is about to stop being true.
            ReadBackScrollbar("a re-layout", endsVerdict: true);
            _laying = true;
            try
            {
                // Job FUI32, and it belongs to the pass rather than to ApplyLayout: `firstShow` is
                // the one condition that means "this panel has a canvas it has never measured".
                if (firstShow) ResetSettleBudget();

                if (_menuPane) UpdateMenuGeometry(AvailableWidth(), AvailableHeight());

                int topPins = 0, bottomPins = 0, headerPins = 0;
                // KEYS3 polish (user report 2026-09-30): the top chrome is the rows' measured heights,
                // not a count of standard rows. The Keybinds edit bar and toolbar are taller than a
                // standard row, and the shown edit bar drew over the list's first row.
                var topHeights = new List<float>();
                for (int i = 0; i < _rows.Count; i++)
                {
                    if (_rows[i].Pin == MenuRowPin.Top && RowActive(_rows[i]))
                    {
                        try { topHeights.Add(MeasureRowHeight(_rows[i])); }
                        catch { topHeights.Add(UnityUiRuntimeMetrics.RowHeight); }
                    }

                    // KEYS3 live 2026-09-29: a hidden top row (the Keybinds edit bar while nothing is
                    // being changed) reserved a whole empty band above the list.
                    if (_rows[i].Pin == MenuRowPin.Top) { if (RowActive(_rows[i])) topPins++; }
                    else if (_rows[i].Pin == MenuRowPin.Bottom) bottomPins++;
                    else if (_rows[i].Pin == MenuRowPin.Header) headerPins++;
                }

                // The version footer's band sits under everything else pinned to the bottom.
                float footerBand = FooterBand;
                // JOINADMIN1 (live 2026-10-04): a dialog's bottom-pinned rows (the Players joining popup's
                // status line and Close) stay under the scrolling body. Their measured heights - the same
                // ones PlaceBottomRows stacks - are added to the frame and kept out of the viewport, so the
                // button is always on show however tall the body grows.
                float dialogPinned = _dialog && bottomPins > 0 ? DialogBottomPinnedHeight() : 0f;
                float refitTop = -1f, refitBottom = 0f;
                bool refitForce = false;
                if (!_strip)
                {
                    float bottomChrome = (_dialog && bottomPins > 0 ? dialogPinned
                        : _sidebar
                        ? MenuPanelLayout.BottomChromeHeight(bottomPins)
                        : MenuPanelLayout.ChromeHeight(bottomPins)) + footerBand;
                    // The band grows with the reason under the header button (two text rows
                    // at least, MenuPanelLayout.PinnedHeaderHeightFor caps the reason at three
                    // lines) and is drawn as its own rect so `rects` can measure it.
                    float headerChrome = headerPins > 0
                        ? MenuPanelLayout.PinnedHeaderHeightFor(HeaderReasonLineCount())
                        : 0f;
                    refitTop = MenuPanelLayout.TopChromeHeight(topHeights) + headerChrome;
                    refitBottom = bottomChrome;
                    refitForce = _embeddedWidth > 0 || !SelfPlaced && (footerBand > 0f || _footerInset > 0f || DialogSized || IsStandardWindow);
                    FitChromeInsets(refitTop, refitBottom, force: refitForce);
                    _footerInset = footerBand;
                    EnsureHeaderBand(headerChrome, MenuPanelLayout.TopChromeHeight(topHeights));
                    EnsureFooterBand(_sidebar && bottomPins > 0
                        ? MenuPanelLayout.FooterBandHeight(bottomPins)
                        : 0f);
                }
                else
                {
                    // Strip: no title, no scrolling viewport. Do not touch
                    // _scrollbar here — AutoHide remains the only owner of
                    // that GameObject (R45 finding 22). ConfigureStrip hid it.
                    try { if (Title != null) Title.SetActive(false); } catch { /* */ }
                    try { if (_viewport != null) _viewport.SetActive(false); } catch { /* */ }
                    EnsureFooterBand(0f);
                }

                _layout.Reset();
                if (_dialog) _dialogBodyCap = DialogBodyCap(footerBand + dialogPinned);
                BeginGridPass();
                // WINDOWS1: a Tabbed window's info column starts under the title band and the pinned top rows.
                BeginSidePass(TitleBand + (refitTop > 0f ? refitTop : PanelLayout.PadTop));
                for (int i = 0; i < _rows.Count; i++)
                    PlaceRow(_rows[i]);
                ApplyTabStripLines();
                PlaceSettingsColumn();
                EndSidePass();

                // HOSTS1: the viewport clears the pinned top rows as placed in THIS pass. Fitted
                // before placement it read the previous pass's rects, so a toolbar that grew between
                // passes (the title's late font) left a list row drawn behind it after a jump.
                if (refitTop >= 0f && WorkspaceHostLayout.RefitAfterPlacement(topPins))
                    FitChromeInsets(refitTop, refitBottom, force: refitForce);

                float contentHeight = _layout.ContentHeight;

                // Job FDOC5 (R43 finding 5, still open at R45 finding 38). Measured ONCE per pass
                // and carried, so the cap, the placement and the log line are all talking about
                // the same reading -- and so the line can name the rung it came from.
                RulerReading available = MeasureHeight();
                // DIALOGS1: a dialog is exactly as tall as its content, its top pad and its footer -
                // no title band and no 200 px floor (the empty band above the live dialog's title).
                float panelHeight = _embeddedHeight > 0 ? _embeddedHeight : _menuPane ? _menuGeometry.Height
                    : DialogSized ? DialogLayout.FrameHeight(contentHeight, footerBand + dialogPinned, available.Value)
                    : IsStandardWindow ? PanelLayout.PanelHeightFor(StandardBodyHeight(contentHeight, refitTop) + footerBand, available.Value)
                    : PanelLayout.PanelHeightFor(contentHeight + footerBand, available.Value);
                // HUB1: a window with tabs may ask a least content height (UiTabsOptions.MinContentHeight), capped like the rest.
                if (_minContentHeight > contentHeight && _embeddedHeight <= 0 && !_menuPane && !DialogSized)
                    panelHeight = Math.Max(panelHeight, IsStandardWindow
                        ? PanelLayout.PanelHeightFor(StandardBodyHeight(_minContentHeight, refitTop) + footerBand, available.Value)
                        : PanelLayout.PanelHeightFor(_minContentHeight + footerBand, available.Value));
                if (WideGameWindow && !Docked && !_menuPane && _embeddedHeight <= 0)
                    panelHeight = Math.Min(panelHeight, available.Value * .70f);
                // DOCK1: a docked frame is never taller than its area between the margins.
                if (Docked) panelHeight = Math.Min(panelHeight, DockMaxHeight(available.Value));
                // WINDOWS1: a standard window is never taller than its place leaves room for (under the workspace's tab
                // strip, a cascade layer's offset), so its bottom stays on screen.
                if (InWindowRow) panelHeight = Math.Min(panelHeight, WindowMaxHeight(available.Value));
                // WINDOWS1 live fix: a standard window keeps its height across its tabs while it stays open.
                // HUB1: a floating panel with tabs keeps it too (SetHeightRule), whoever placed it.
                if (IsStandardWindow && !SelfPlaced || KeepsHeightRule)
                    panelHeight = StableWindowHeight(panelHeight, InWindowRow ? WindowMaxHeight(available.Value) : Docked ? DockMaxHeight(available.Value) : 0f, firstShow);

                _laidOutWith = available;
                var settle = RulerSettle.StateOf(available, _settling, _settleExhausted);

                if (_content != null)
                {
                    var rt = _content.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        // Read before sizeDelta: that write is what Unity uses to zero a
                        // restored offset, and the settle pass then has nothing to keep.
                        float currentY = 0f;
                        try { currentY = rt.anchoredPosition.y; } catch { /* */ }
                        rt.sizeDelta = new Vector2(0f, contentHeight);
                        float viewH = _sidebar
                            ? MenuPanelLayout.ListViewportHeight(panelHeight)
                            : DialogSized ? DialogLayout.ViewportHeight(panelHeight, footerBand + dialogPinned)
                            : Math.Max(0f, PanelLayout.ViewportHeightFor(panelHeight) - footerBand - (IsStandardWindow && refitTop > 0f ? refitTop : 0f));
                        // HUB1: the room a table that fills the panel reads (its tallest viewport, the content now).
                        NoteRoom(viewH, available.Value, footerBand, refitTop, contentHeight);
                        float npos = 1f;
                        float pendingOffsetY = 0f;
                        bool hasPending = _sidebar && PeekSidebarScrollPreserve(out npos, out pendingOffsetY);
                        if (!hasPending)
                        {
                            npos = 1f;
                            pendingOffsetY = 0f;
                        }

                        // Apply against this pass's contentHeight, not content.rect (still
                        // zero until the canvas updates). Writing verticalNormalizedPosition
                        // against empty bounds is how a restore landed at the bottom. Do not
                        // clear a pending preserve on an armed pass: the settle re-layout
                        // after RestoreScroll is what moved a correct 0.778 restore to 0.
                        SidebarScrollLayout.Apply(
                            hasPending, npos, pendingOffsetY, resetScroll, firstShow, settle,
                            currentY, contentHeight, viewH,
                            out float y, out bool keepPending);
                        try { rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y); }
                        catch { /* */ }
                        if (hasPending && !keepPending) ClearSidebarScrollPreserve();
                        // The content pane's preserve (ArmContentScrollPreserve): the offset it had, clamped to this pass.
                        if (!_sidebar && PeekContentScrollPreserve(rt, out float keptY))
                        {
                            y = Math.Max(0f, Math.Min(keptY, contentHeight));
                            try { rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, y); }
                            catch { /* */ }
                            if (!SidebarScrollLayout.KeepPending(settle, firstShow)) _pendingContentRect = IntPtr.Zero;
                        }
                        if (hasPending)
                        {
                            try
                            {
                                float restored = MenuPanelLayout.CanApplyNormalized(contentHeight, viewH)
                                    ? MenuPanelLayout.NormalizedFromOffset(y, contentHeight, viewH)
                                    : npos;
                                Plugin.Logger?.LogDiag(SidebarScrollLayout.FormatPreserveLog(
                                    "list", npos, restored, contentHeight, viewH, RulerSettle.Name(settle)));
                            }
                            catch { /* */ }
                        }
                    }
                }

                var panelRt = GameObject.GetComponent<RectTransform>();
                var placedAt = PanelOffset.Centre;
                if (panelRt != null)
                {
                    // CONSENT2: the frame belongs to the panel itself (see SelfPlaced). The pass
                    // still measures, still places every row, still reports and still arms the
                    // settle re-measure — it simply does not size or move the frame those rows sit
                    // in.
                    if (!SelfPlaced)
                        panelRt.sizeDelta = new Vector2(FrameWidth, panelHeight);
                    if (_menuPane) ApplyMenuPaneFill();
                    if (_strip)
                    {
                        try { if (Title != null) Title.SetActive(false); } catch { /* */ }
                    }
                    else if (_menuPane && Title != null) PlaceTop(Title, 18f, ContentRowWidth, 40f);

                    // Jobs FUI20, FUI21. Placed in the same pass that sized the frame, so a first
                    // show puts the frame where it belongs before anyone sees it. The only panel of
                    // a set of one (and a panel with no spot) gets exactly Vector2.zero, which is
                    // where every panel sat before this existed.
                    //
                    // Job FUI23 (R44 finding 5): the offset does NOT take panelHeight any more.
                    // This pass runs on a re-layout too — a rebind refusal rewriting a row's text
                    // under the player's own click — and feeding it the new height let a panel at
                    // depth >= 1 cross PanelFlow's capacity threshold and jump a whole title band
                    // onto the panel above it. The vertical ladder is a property of the canvas now.
                    placedAt = _menuPane ? new PanelOffset(_menuGeometry.X, _menuGeometry.Y)
                        : Docked ? DockOffset(panelHeight, available.Value)
                        : InWindowRow ? WindowOffset(panelHeight, available.Value)
                        : PanelFlow.Offset(Spot, PanelWidth, AvailableWidth(), available.Value);
                    // INSTALLUX2: a dialog is centered, never ranked. PanelFlow would give it a
                    // column beside the workspace's panes the moment they joined the shown set,
                    // which is the "flickers in from the left" CONSENT2 fixed for the consent
                    // prompt by hand.
                    if (_dialog || (WideGameWindow && !IsStandardWindow && !Docked)) placedAt = PanelOffset.Centre;
                    if (SelfPlaced) placedAt = new PanelOffset(panelRt.anchoredPosition.x, panelRt.anchoredPosition.y);
                    else panelRt.anchoredPosition = new Vector2(placedAt.X, placedAt.Y);
                    if (TopAligned && !Docked) panelRt.anchoredPosition = new Vector2(0, Math.Max(0, (AvailableHeight() - panelHeight) / 2 - 24));
                }

                if (!_strip) PlaceBottomRows(panelHeight, footerBand);
                PlaceVersionFooter();
                PlaceWindowChrome(panelHeight, footerBand);

                // Say it once per show. A list that scrolls looks identical to one that does not
                // until the player tries, and "there are rows below this" is the fact the old panel
                // could never report because it had no idea it was overflowing.
                float viewport = _sidebar
                    ? MenuPanelLayout.ListViewportHeight(panelHeight)
                    : DialogSized ? DialogLayout.ViewportHeight(panelHeight, footerBand)
                    : Math.Max(0f, PanelLayout.ViewportHeightFor(panelHeight) - footerBand - (IsStandardWindow && refitTop > 0f ? refitTop : 0f));
                bool scrolls = PanelLayout.Scrolls(contentHeight, viewport);

                // No SetActive here (R45 finding 22). ScrollRect's AutoHide is the single owner of
                // the bar's visibility — see AddScroll — and the verdict below is our arithmetic,
                // which is a different computation of the same quantity. It is recorded so the
                // next pass over this panel can read the owner's answer back and say whether the
                // two agree, rather than printing one of them as if it were both.
                _verdictScrolls = scrolls;
                // P7b: a panel with no scroll view has nothing to read back, and neither has the workspace tab strip,
                // whose viewport and bar ConfigureStrip switched off (its "(scrolls)" is arithmetic over no list).
                _verdictPending = _scrollbar != null && !NoScroll && !_strip;
                _verdictFrame = FrameNow();

                // Job FUI32. Which rung answered was never the whole question: a pass measured
                // before Unity sized the canvas is wrong whatever token it prints, so the line
                // also says WHEN it was measured. `settle=armed` means the numbers on this very
                // line are provisional and a re-measure is coming; `settled` is that re-measure.
                // `settle` was computed before the content write so the pending-preserve
                // decision and this line name the same pass.

                // `u`, not `px` (job FDOC5, R45 finding 38): these are canvas units, and calling
                // them pixels is the same unit confusion the fallback chain used to have, stated
                // as fact in the line a reader diagnoses from. `ruler=` says which rung produced
                // `avail`, so a panel capped against the wrong unit is visible rather than
                // plausible -- `ruler=constant` is 1080 whatever the display is doing.
                Plugin.Logger?.LogDiag(
                    "Ui: panel '" + Text + "' " + (firstShow ? "laid out" : "re-laid out")
                    + " rows=" + _layout.RowCount
                    + " content=" + ((int)contentHeight) + "u panel=" + ((int)panelHeight) + "u"
                    + " avail=" + ((int)available.Value) + "u ruler=" + available.Name
                    + " canvas=" + RulerSettle.Name(_canvasRead)
                    + " settle=" + RulerSettle.Name(settle)
                    // Job FUI20: the placement is only visible in a screenshot, which is how two
                    // panels sat on top of each other through every log the framework had. The
                    // spot and the offset it resolved to are now in the line that reports a layout.
                    + " rank=" + Spot.Rank + "/" + Spot.Count
                    + " at=(" + ((int)placedAt.X) + "," + ((int)placedAt.Y) + ")"
                    + (SelfPlaced ? " self-placed" : "")
                    + (scrolls ? " (scrolls)" : " (fits)"));

                // Armed after the line that admits to being provisional, never instead of it
                // (job FUI32). A panel whose canvas is unreadable is laid out now with what is
                // known, so it is on screen this frame, and measured again when the canvas is
                // real -- FUI30's rule for its probe, applied to the pass the probe describes.
                if (settle == RulerSettleState.Armed) ArmSettle(this);

                // INSTALLUX2 item 1: the reveal is the last thing this pass does, so the first
                // frame the player can see is the placed, sized one. A dialog that is already
                // visible is unaffected — the write is idempotent.
                if (_dialog) RevealDialog();
            }
            catch (Exception e)
            {
                // A panel that failed to lay out keeps whatever geometry it had, which is a panel
                // the player can still close, rather than a half-placed one.
                Plugin.Logger?.LogWarning("Ui: panel layout pass threw: " + e.Message);
            }
            finally
            {
                _laying = false;
                UiFrameCost.Note("layout", start);
            }
        }

        /// <summary>KEYS3 polish: the active top-pinned rows' placed tops and heights, from the panel top.</summary>
        private List<(float Top, float Height)> PlacedPinnedTopRows()
        {
            var list = new List<(float, float)>();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Pin != MenuRowPin.Top || row.Go == null || !RowActive(row)) continue;
                try
                {
                    if (row.Go.transform.parent == null || row.Go.transform.parent.Pointer != GameObject.transform.Pointer) continue;
                    var rt = row.Go.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    list.Add((-rt.anchoredPosition.y, rt.sizeDelta.y));
                }
                catch { /* */ }
            }

            return list;
        }

        private void FitChromeInsets(float topChrome, float bottomChrome, bool force = false)
        {
            // `force`: a footer band is being added or given back. Without pins the insets are the
            // viewport's construction defaults, which this write reproduces at zero chrome.
            if (!force && topChrome <= 0f && bottomChrome <= 0f) return;
            // KEYS3 polish: never above a pinned top row as it is actually placed, so no list row can
            // draw behind the toolbar or the edit bar.
            float top = WorkspaceHostLayout.ListViewportTop(TitleBand, topChrome, PlacedPinnedTopRows());
            if (_viewport != null)
            {
                var rt = _viewport.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.offsetMin = new Vector2(PanelLayout.PadSide + SideBand, bottomChrome);
                    rt.offsetMax = new Vector2(-PanelLayout.PadSide, -top);
                }
            }

            if (_scrollbar == null) return;
            var bar = _scrollbar.GetComponent<RectTransform>();
            if (bar == null) return;
            bar.offsetMin = new Vector2(
                -(PanelLayout.ScrollbarInset + PanelLayout.ScrollbarWidth + ScrollbarShiftX),
                bottomChrome > 0f ? bottomChrome : PanelLayout.PadBottom);
            bar.offsetMax = new Vector2(-(PanelLayout.ScrollbarInset + ScrollbarShiftX), -top);
            // MENUKIT2 round D: the settings page's thin bar over its rows (UnityUiRuntime.SettingsLook).
            ScrollbarBaseTop = top;
            ApplyScrollbarShift();
        }

        /// <summary>
        /// JOINADMIN1: the band a dialog's bottom-pinned rows take, from their measured heights: the bottom pad,
        /// each row and the gap after it, as <see cref="PlaceBottomRows"/> stacks them.
        /// </summary>
        private float DialogBottomPinnedHeight()
        {
            var heights = new List<float>();
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Pin != MenuRowPin.Bottom) continue;
                // CHANGESFIT1 (live 2026-10-07): a hidden pinned row (an unused Changes slot) takes no band; it
                // reserved ~170 px and left the Players joining body ~150 px.
                if (!RowActive(_rows[i])) continue;
                try { heights.Add(MeasureRowHeight(_rows[i])); }
                catch { heights.Add(UnityUiRuntimeMetrics.RowHeight); }
            }

            return DialogLayout.PinnedBottomHeight(heights);
        }

        private void PlaceBottomRows(float panelHeight, float footerBand = 0f)
        {
            float fromBottom = (_sidebar ? MenuPanelLayout.FooterPadBottom : PanelLayout.PadBottom) + footerBand;
            float gap = _sidebar ? MenuPanelLayout.FooterButtonGap : PanelLayout.RowGap;
            float width = _sidebar
                ? Math.Max(1f, ContentRowWidth - MenuRowStyle.ChromeInset * 2f)
                : ContentRowWidth;
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                var row = _rows[i];
                if (row.Pin != MenuRowPin.Bottom) continue;
                // CHANGESFIT1: a hidden pinned row is skipped, so the rows above it close the gap.
                if (_dialog && !RowActive(row)) continue;
                float height = MeasureRowHeight(row);
                float yFromTop = panelHeight - fromBottom - height;
                if (yFromTop < TitleBand) yFromTop = TitleBand;
                try
                {
                    PlaceTop(row.Go, yFromTop, width, height);
                    PlaceRowControl(row, RowColumnsFor(row));
                    PlaceHeadingRule(row, height);
                    PlaceInlineRow(row);
                    // KEYS3 live 2026-09-29: a bottom-pinned cell row (the Keybinds tab's Reset all mod
                    // keys) had its cells never placed, so its button drew as a wide bar.
                    PlaceCellRowCells(row);
                }
                catch { /* */ }
                fromBottom += height + gap;
            }
        }

        /// <summary>
        /// The pinned-header band: a non-raycast rect from the bottom of the panel
        /// title chrome (plus any Top pins) down <paramref name="headerChrome"/>,
        /// the same span <see cref="FitChromeInsets"/> keeps the viewport out of.
        /// Sibling 0 so the header rows draw over it. Hidden when there is no header.
        /// </summary>
        private void EnsureHeaderBand(float headerChrome, float topChrome)
        {
            if (headerChrome <= 0.5f)
            {
                if (_headerBand != null)
                {
                    try { _headerBand.SetActive(false); } catch { /* */ }
                }

                return;
            }

            if (_headerBand == null)
            {
                try
                {
                    _headerBand = new GameObject(HeaderBandName);
                    _headerBand.transform.SetParent(GameObject.transform, false);
                    _headerBand.transform.SetSiblingIndex(0);
                    var band = TryAdd<Image>(_headerBand);
                    if (band != null)
                    {
                        try { band.color = new Color(MenuRowStyle.ChromeBandR, MenuRowStyle.ChromeBandG, MenuRowStyle.ChromeBandB, MenuRowStyle.ChromeBandA); }
                        catch { /* */ }
                        try { band.raycastTarget = false; } catch { /* */ }
                    }
                }
                catch
                {
                    _headerBand = null;
                    return;
                }
            }

            try { _headerBand.SetActive(true); } catch { /* */ }
            try
            {
                var rt = _headerBand.GetComponent<RectTransform>() ?? _headerBand.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -(TitleBand + topChrome));
                rt.sizeDelta = new Vector2(0f, headerChrome);
            }
            catch { /* */ }
        }

        private void EnsureFooterBand(float bottomChrome)
        {
            if (bottomChrome <= 0.5f)
            {
                if (_footer != null)
                {
                    try { _footer.SetActive(false); } catch { /* */ }
                }

                return;
            }

            if (_footer == null)
            {
                try
                {
                    _footer = new GameObject("footer");
                    _footer.transform.SetParent(GameObject.transform, false);
                    _footer.transform.SetSiblingIndex(0);
                    var band = TryAdd<Image>(_footer);
                    if (band != null)
                    {
                        try { band.color = new Color(MenuRowStyle.ChromeBandR, MenuRowStyle.ChromeBandG, MenuRowStyle.ChromeBandB, MenuRowStyle.ChromeBandA); }
                        catch { /* */ }
                        try { band.raycastTarget = false; } catch { /* */ }
                    }

                    _footerRule = new GameObject("footer-rule");
                    _footerRule.transform.SetParent(_footer.transform, false);
                    var rule = TryAdd<Image>(_footerRule);
                    if (rule != null)
                    {
                        try { rule.color = new Color(MenuRowStyle.ChromeRuleR, MenuRowStyle.ChromeRuleG, MenuRowStyle.ChromeRuleB, MenuRowStyle.ChromeRuleA); }
                        catch { /* */ }
                        try { rule.raycastTarget = false; } catch { /* */ }
                    }
                }
                catch
                {
                    _footer = null;
                    _footerRule = null;
                    return;
                }
            }

            try { _footer.SetActive(true); } catch { /* */ }
            try
            {
                var rt = _footer.GetComponent<RectTransform>() ?? _footer.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(0f, bottomChrome);
            }
            catch { /* */ }

            if (_footerRule == null) return;
            try
            {
                var ruleRt = _footerRule.GetComponent<RectTransform>() ?? _footerRule.AddComponent<RectTransform>();
                ruleRt.anchorMin = new Vector2(0f, 1f);
                ruleRt.anchorMax = new Vector2(1f, 1f);
                ruleRt.pivot = new Vector2(0.5f, 1f);
                ruleRt.anchoredPosition = Vector2.zero;
                ruleRt.sizeDelta = new Vector2(0f, 3f);
            }
            catch { /* */ }
        }

        public override void Release()
        {
            if (Released) return;
            Released = true;
            try { NoteShown(this, false); } catch { /* */ }
            if (_menuPane) Plugin.Frame -= MenuResizeTick;
            StopDockWatch();
            // Job FUI32: never leave a released panel in the owed list. SettleTick refuses a
            // released panel anyway, but the list would hold the reference until the next frame,
            // and a Core swap can happen between those two moments.
            DropSettle(this);
            if (_embeddedWidth > 0 && GameObject != null) TryDestroy(GameObject, BootstrapLink.MayDestroyUiImmediate());
            if (OverlayWidget != null)
                OverlayWidget.Release();
            else if (Overlay != null)
                TryDestroy(Overlay, BootstrapLink.MayDestroyUiImmediate());
            else
                TryDestroy(GameObject, BootstrapLink.MayDestroyUiImmediate());
        }
    }

    /// <summary>
    /// A row widget with clickable parts (the selector's arrows, a number's steppers). A part is a
    /// plain <see cref="UnityWidget"/> over the part's <b>own</b> object (the arrow or stepper
    /// button, never the row), whose only event is that object's click. The parts are children of
    /// the row, so releasing the row destroys them with it; nothing releases a part on its own.
    /// </summary>
    private class UnityPartsWidget : UnityWidget, IRuntimePartsWidget
    {
        private readonly Dictionary<string, IRuntimeWidget> _parts = new(StringComparer.Ordinal);

        public UnityPartsWidget(
            UnityPanel? owner, GameObject go, object? click, object? floatEvent, object? boolEvent,
            object? textEvent, object? enter, object? exit)
            : base(owner, go, click, floatEvent, boolEvent, textEvent, enter, exit)
        {
        }

        /// <summary>
        /// A clickable part. With <paramref name="hover"/> (OPTIONHOVER1: a dropdown option, a radio line) it also reports
        /// the pointer entering and leaving it, so the handle can describe the option under the pointer.
        /// </summary>
        public void AddPart(string key, GameObject? part, bool hover = false)
        {
            if (part == null || string.IsNullOrEmpty(key)) return;
            object? click = FindClickEvent(part);
            if (click == null) return;
            object? enter = null;
            object? exit = null;
            if (hover) EnsurePointerEvents(part, out enter, out exit);
            _parts[key] = new UnityWidget(null, part, click, null, null, null, enter, exit);
        }

        public IRuntimeWidget? Part(string key) =>
            key != null && _parts.TryGetValue(key, out var part) ? part : null;
    }

    /// <summary>
    /// DROPDOWN1 (user review 2026-10-06): a choice whose click opens a list of its options under the field, the current
    /// one marked, like the game's dropdown. The list is built once, hidden, on its own sorting canvas above the panel (so
    /// the rows below do not cover it), with a full-screen transparent click-catcher behind the options: a click outside
    /// the list dismisses it. The handle drives it through <see cref="IRuntimeWidget.BoolValue"/> (open) and
    /// <see cref="IRuntimeWidget.StringValue"/> (the current caption, marked in gold).
    /// </summary>
    private sealed class UnityChoiceWidget : UnityPartsWidget, IRuntimeChoiceFit
    {
        /// <summary>
        /// The <c>fit</c> readback: the value caption's area and each visible arrow in screen pixels, the caption's drawn and
        /// wanted size, and whether it was cut or reaches under an arrow.
        /// </summary>
        public Dictionary<string, object?>? DescribeFit()
        {
            var control = GameObject != null ? GameObject.transform.Find("choice") : null;
            if (control == null) return null;
            var value = control.Find("value-text");
            if (value == null) return null;
            var tmp = value.GetComponent<TextMeshProUGUI>();
            var valueRt = value.GetComponent<RectTransform>();
            if (tmp == null || valueRt == null) return null;
            var area = ScreenRectOf(valueRt);
            var d = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["text"] = tmp.text ?? "",
                ["area"] = RectReadback(area),
                ["control"] = RectReadback(ScreenRectOf(control.GetComponent<RectTransform>())),
                ["fontSize"] = Math.Round(tmp.fontSize, 2),
                ["fontSizeMax"] = Math.Round(tmp.fontSizeMax, 2),
                ["autoSize"] = tmp.enableAutoSizing,
                ["truncated"] = tmp.isTextTruncated,
                ["preferredWidth"] = Math.Round(tmp.preferredWidth, 1),
                ["areaWidth"] = Math.Round(valueRt.rect.width, 1),
            };
            bool under = false;
            foreach (string name in new[] { "choice-prev", "choice-next" })
            {
                var arrow = control.Find(name);
                bool shown = arrow != null && arrow.gameObject.activeSelf;
                var rt = arrow != null ? arrow.GetComponent<RectTransform>() : null;
                var r = rt != null ? ScreenRectOf(rt) : ScreenRect.Unknown;
                d[name == "choice-prev" ? "prev" : "next"] = new Dictionary<string, object?>
                {
                    ["shown"] = shown,
                    ["rect"] = RectReadback(r),
                };
                // Under an arrow: a visible arrow (or its chevron) whose span the caption's own area crosses.
                if (shown && r.IsKnown && area.IsKnown && area.MinX < r.MaxX - 0.5f && area.MaxX > r.MinX + 0.5f) under = true;
            }

            d["areaUnderArrow"] = under;
            return d;
        }

        private static Dictionary<string, object?> RectReadback(ScreenRect r) => !r.IsKnown
            ? new Dictionary<string, object?> { ["known"] = false }
            : new Dictionary<string, object?>
            {
                ["x"] = Math.Round(r.MinX, 1),
                ["y"] = Math.Round(r.MinY, 1),
                ["w"] = Math.Round(r.MaxX - r.MinX, 1),
                ["h"] = Math.Round(r.MaxY - r.MinY, 1),
            };

        /// <summary>One option row's height and the list's inner padding (kit units).</summary>
        internal const float OptionHeight = 34f;

        private const float ListPad = 3f;

        private GameObject? _list;
        private Canvas? _listCanvas;
        private readonly List<(Image? Fill, TextMeshProUGUI? Caption, string Label)> _items = new();

        /// <summary>DROPHOVER1: each option's description (empty for none), in option order.</summary>
        private readonly List<string> _descriptions = new();

        /// <summary>DROPHOVER1: the highlighted option (the handle's <c>HighlightIndex</c>, written as the float value), or -1.</summary>
        private int _highlight = -1;

        /// <summary>DROPHOVER1: the description box beside the open list, and its text.</summary>
        private GameObject? _tip;

        private TextMeshProUGUI? _tipText;

        /// <summary>
        /// OPTIONHOVER1 (live 2026-10-06: the Blood-bound equipment checkbox under an open Difficulty list showed through it):
        /// the list's own sorting order, above the panel's rows (<see cref="ModsCanvasBand.Panels"/>) and its popups' band.
        /// </summary>
        internal static int ListSortingOrder => UiOwnership.OverlaySorting + 40;

        public UnityChoiceWidget(UnityPanel owner, GameObject go, object? click)
            : base(owner, go, click, null, null, null, null, null)
        {
        }

        /// <summary>The list under <paramref name="control"/>: hidden until opened.</summary>
        public void BuildList(GameObject control, IReadOnlyList<UiChoiceOption> options)
        {
            if (options == null || options.Count == 0) return;
            try
            {
                var list = new GameObject("choice-list");
                list.transform.SetParent(control.transform, false);
                var rt = list.GetComponent<RectTransform>() ?? list.AddComponent<RectTransform>();
                float height = options.Count * OptionHeight + ListPad * 2f;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(1f, 0f);
                rt.pivot = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -2f);
                rt.sizeDelta = new Vector2(0f, height);
                // Above the panel's own canvas (OverlaySorting) and its rows; under a dialog's. OPTIONHOVER1 (user screenshot
                // 2026-10-06: the next row's checkbox drew over the open list): set again each time the list opens
                // (ApplyListSorting). A nested canvas whose override does not hold draws in its row's place, under every
                // row after it, which is what the screenshot showed. Which of the two causes held is not verified live.
                var canvas = TryAdd<Canvas>(list);
                if (canvas != null)
                {
                    _listCanvas = canvas;
                    ApplyListSorting();
                    TryAdd<GraphicRaycaster>(list);
                }

                // The click-catcher: transparent, far larger than any screen, centered on the list, first child so the
                // options draw and raycast above it.
                var blocker = new GameObject("choice-blocker");
                blocker.transform.SetParent(list.transform, false);
                var brt = blocker.GetComponent<RectTransform>() ?? blocker.AddComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                brt.pivot = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(40000f, 40000f);
                var blockerImage = TryAdd<Image>(blocker);
                if (blockerImage != null)
                {
                    blockerImage.color = new Color(0f, 0f, 0f, 0f);
                    try { blockerImage.raycastTarget = true; } catch { /* */ }
                    var blockerButton = TryAdd<Button>(blocker);
                    if (blockerButton != null)
                    {
                        try { blockerButton.targetGraphic = blockerImage; blockerButton.transition = Selectable.Transition.None; } catch { /* */ }
                    }
                }

                // OPTIONHOVER1: an opaque back under the frame. With the borrowed frame sprite the image is drawn white and
                // sliced, so the frame's flat dark color is not used; a sprite whose middle is see-through would let the
                // rows under the list show between the options. The back covers them either way.
                var back = new GameObject("choice-list-back");
                back.transform.SetParent(list.transform, false);
                Stretch(back);
                var backImage = TryAdd<Image>(back);
                if (backImage != null)
                {
                    try { backImage.sprite = null; } catch { /* */ }
                    backImage.color = new Color(0.07f, 0.08f, 0.10f, 1f);
                    try { backImage.raycastTarget = true; } catch { /* */ }
                }

                var frame = new GameObject("choice-list-frame");
                frame.transform.SetParent(list.transform, false);
                Stretch(frame);
                var frameImage = TryAdd<Image>(frame);
                if (frameImage != null)
                {
                    ApplyFrame(frameImage, new Color(0.07f, 0.08f, 0.10f, 0.98f));
                    try { frameImage.raycastTarget = true; } catch { /* */ }
                }

                for (int i = 0; i < options.Count; i++)
                {
                    var item = new GameObject("choice-option-" + i.ToString(CultureInfo.InvariantCulture));
                    item.transform.SetParent(list.transform, false);
                    var irt = item.GetComponent<RectTransform>() ?? item.AddComponent<RectTransform>();
                    irt.anchorMin = new Vector2(0f, 1f);
                    irt.anchorMax = new Vector2(1f, 1f);
                    irt.pivot = new Vector2(0.5f, 1f);
                    irt.anchoredPosition = new Vector2(0f, -(ListPad + i * OptionHeight));
                    irt.sizeDelta = new Vector2(-ListPad * 2f, OptionHeight);
                    var fill = TryAdd<Image>(item);
                    if (fill != null)
                    {
                        fill.color = new Color(1f, 1f, 1f, 0f);
                        try { fill.raycastTarget = true; } catch { /* */ }
                    }

                    var button = TryAdd<Button>(item);
                    if (button != null && fill != null)
                    {
                        // DROPHOVER1 (user, 2026-10-11: hovering an option did not highlight it): the button's color tint
                        // multiplied the fill's own color, which is transparent at rest, so no hover tint could show. The
                        // fill is painted from the handle's highlight instead (Mark), which follows the pointer and a controller.
                        try
                        {
                            button.targetGraphic = fill;
                            button.transition = Selectable.Transition.None;
                        }
                        catch { /* the default tint */ }
                    }

                    var captionGo = new GameObject("caption");
                    captionGo.transform.SetParent(item.transform, false);
                    AnchorInside(captionGo, 0f, 1f, 14f, -10f, 0f);
                    var caption = TryAdd<TextMeshProUGUI>(captionGo);
                    string text = options[i].Label ?? "";
                    if (caption != null)
                    {
                        ApplyControlTmp(caption, text, 16f);
                        FitInsideArea(caption, 16f, UiControlDraw.ChoiceValueMinFontSize);
                        ApplyCachedFont(caption);
                        try { caption.horizontalAlignment = HorizontalAlignmentOptions.Left; caption.raycastTarget = false; } catch { /* */ }
                    }

                    AddPart(UiControlParts.Option(i), item, hover: true);
                    _items.Add((fill, caption, text));
                    _descriptions.Add(options[i].Description ?? "");
                }

                BuildTip(list);
                AddPart(UiControlParts.ListBlocker, blocker);
                list.SetActive(false);
                _list = list;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: building a dropdown list threw: " + e.Message);
            }
        }

        /// <summary>DROPHOVER1: the highlighted option moved (the pointer or a controller): repaint, and show its description.</summary>
        protected override void AfterFloatWrite(float value)
        {
            int index = float.IsNaN(value) ? -1 : (int)Math.Round(value);
            if (index == _highlight) return;
            _highlight = index;
            if (_list == null || !Alive(_list)) return;
            try
            {
                if (!_list.activeSelf) return;
                Mark(StringValue);
            }
            catch { /* the list keeps its paint */ }
        }

        /// <summary>Open or closed: show the list with the current value marked, or hide it.</summary>
        protected override void AfterBoolWrite(bool value)
        {
            var list = _list;
            if (list == null || !Alive(list)) return;
            try
            {
                if (value) Mark(StringValue);
                else ShowTip("");
                list.SetActive(value);
                if (value)
                {
                    ApplyListSorting();
                    // Last among its siblings as well, so even a canvas that lost its override draws after the field.
                    try { list.transform.SetAsLastSibling(); } catch { /* */ }
                }
            }
            catch { /* the list keeps its state */ }
        }

        /// <summary>The list's canvas sorts on its own, above the panel's rows (<see cref="ListSortingOrder"/>).</summary>
        private void ApplyListSorting()
        {
            var canvas = _listCanvas;
            if (canvas == null) return;
            try { canvas.overrideSorting = true; } catch { /* */ }
            try { canvas.sortingOrder = ListSortingOrder; } catch { /* */ }
        }

        /// <summary>
        /// The current caption in gold, the rest in the value gray; the highlighted option (DROPHOVER1) carries the hover fill,
        /// the current one a fainter fill, and the highlighted option's description shows in the box beside the list.
        /// </summary>
        private void Mark(string current)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                bool on = string.Equals(_items[i].Label, current, StringComparison.Ordinal);
                bool lit = i == _highlight;
                try
                {
                    if (_items[i].Caption is { } cap)
                        cap.color = on ? ToColor(MenuPalette.Gold) : Grey(lit ? 1f : 0.86f);
                    if (_items[i].Fill is { } fill)
                        fill.color = new Color(1f, 1f, 1f, UiChoiceState.OptionFillAlpha(on, lit));
                }
                catch { /* */ }
            }

            ShowTip(_highlight >= 0 && _highlight < _descriptions.Count ? _descriptions[_highlight] : "");
        }

        /// <summary>DROPHOVER1: the description box, left of the list (over the name column), hidden until an option with a description is highlighted.</summary>
        private void BuildTip(GameObject list)
        {
            bool any = false;
            foreach (var d in _descriptions) if (!string.IsNullOrWhiteSpace(d)) any = true;
            if (!any) return;
            try
            {
                var tip = new GameObject("choice-tip");
                tip.transform.SetParent(list.transform, false);
                var rt = tip.GetComponent<RectTransform>() ?? tip.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.sizeDelta = new Vector2(UiChoiceState.TipWidth, OptionHeight);
                var back = TryAdd<Image>(tip);
                if (back != null)
                {
                    ApplyFrame(back, new Color(0.07f, 0.08f, 0.10f, 0.98f));
                    try { back.raycastTarget = false; } catch { /* */ }
                }

                var textGo = new GameObject("tip-text");
                textGo.transform.SetParent(tip.transform, false);
                AnchorInside(textGo, 0f, 1f, UiChoiceState.TipPad, -UiChoiceState.TipPad, 0f);
                var text = TryAdd<TextMeshProUGUI>(textGo);
                if (text != null)
                {
                    ApplyControlTmp(text, "", UiChoiceState.TipFontSize);
                    ApplyCachedFont(text);
                    try
                    {
                        text.enableWordWrapping = true;
                        text.richText = false;
                        text.horizontalAlignment = HorizontalAlignmentOptions.Left;
                        text.verticalAlignment = VerticalAlignmentOptions.Middle;
                        text.raycastTarget = false;
                        text.color = Grey(0.86f);
                    }
                    catch { /* */ }
                }

                tip.SetActive(false);
                _tip = tip;
                _tipText = text;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogInfo("Ui: a dropdown's description box could not be built (" + e.Message + ").");
            }
        }

        /// <summary>Show <paramref name="text"/> beside the highlighted option, or hide the box for an empty text.</summary>
        private void ShowTip(string text)
        {
            var tip = _tip;
            if (tip == null || !Alive(tip)) return;
            try
            {
                // A settings page with a details column shows the description there (OPTIONHOVER1); the box would repeat it.
                bool column = false;
                try { column = Owner?.ColumnShown == true; } catch { /* */ }
                if (string.IsNullOrWhiteSpace(text) || _highlight < 0 || column)
                {
                    if (tip.activeSelf) tip.SetActive(false);
                    return;
                }

                var label = _tipText;
                float height = OptionHeight;
                if (label != null)
                {
                    label.text = text;
                    try
                    {
                        label.ForceMeshUpdate();
                        height = UiChoiceState.TipHeight(label.preferredHeight, OptionHeight);
                    }
                    catch { /* one option's height */ }
                }

                var rt = tip.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.sizeDelta = new Vector2(UiChoiceState.TipWidth, height);
                    rt.anchoredPosition = new Vector2(-UiChoiceState.TipGap, -(ListPad + _highlight * OptionHeight));
                }

                if (!tip.activeSelf) tip.SetActive(true);
            }
            catch { /* the box stays as it was */ }
        }
    }

    /// <summary>
    /// SETTINGS1 stage 2: an options row (radio group or multi-select). Each option is a part; the
    /// fills and captions are kept so a selection change repaints in place without a relayout.
    /// </summary>
    private sealed class UnityOptionsWidget : UnityPartsWidget
    {
        private readonly List<(Image? Fill, TextMeshProUGUI? Caption, Image? Mark, string Label)> _options = new();
        private readonly bool _multi;
        private readonly UiOptionsLayout _layout;

        public UnityOptionsWidget(UnityPanel owner, GameObject go, bool multi, UiOptionsLayout layout)
            : base(owner, go, null, null, null, null, null, null)
        {
            _multi = multi;
            _layout = layout;
            // MENUKIT2: on the settings grid a segmented choice takes the game's steel blue, not the gold.
            _gameLook = owner != null && owner.Grid;
        }

        private readonly bool _gameLook;

        public void AddOption(string key, GameObject go, Image? fill, TextMeshProUGUI? caption, Image? mark, string label)
        {
            AddPart(key, go, hover: true);
            _options.Add((fill, caption, mark, label ?? ""));
        }

        // RADIO1: a radio list's lines (a widget holds radio lines or options, never both).
        private readonly List<(TextMeshProUGUI? Mark, TextMeshProUGUI? Caption, TextMeshProUGUI? Description)> _radio = new();

        public void AddRadioLine(string key, GameObject go, TextMeshProUGUI? mark, TextMeshProUGUI? caption, TextMeshProUGUI? description, string label)
        {
            AddPart(key, go, hover: true);
            _radio.Add((mark, caption, description));
        }

        public void Apply(IReadOnlyList<bool> selected)
        {
            for (int i = 0; i < _radio.Count; i++)
            {
                bool on = selected != null && i < selected.Count && selected[i];
                var (mark, caption, description) = _radio[i];
                try
                {
                    if (mark != null && !PaintRadioCircle(mark, on))
                    {
                        mark.text = DrawableGlyph(mark, UiOptionsRules.RadioMark(on));
                        mark.color = on ? ToColor(MenuPalette.Gold) : Grey(Progenitor.Ui.GameArt.SettingsControlLook.RadioCaptionGrey(false));
                    }

                    if (caption != null) caption.color = Grey(Progenitor.Ui.GameArt.SettingsControlLook.RadioCaptionGrey(on));
                    if (description != null) description.color = Grey(Progenitor.Ui.GameArt.SettingsControlLook.RadioDescriptionGrey(on));
                }
                catch { /* */ }
            }

            for (int i = 0; i < _options.Count; i++)
            {
                bool on = selected != null && i < selected.Count && selected[i];
                var (fill, caption, mark, label) = _options[i];
                if (_layout == UiOptionsLayout.Segmented)
                {
                    try { if (fill != null) fill.color = _gameLook ? GameOptionFill(fill, on) : OptionFill(on); } catch { /* */ }
                    if (_gameLook && fill != null) PaintSegmentIndicator(fill.gameObject, on);
                }

                try { if (mark != null) mark.enabled = on; } catch { /* */ }
                try
                {
                    if (caption != null)
                    {
                        caption.text = UiOptionsRules.Caption(label, on, _multi, _layout);
                        caption.color = !_gameLook
                            ? (on ? ToColor(MenuPalette.Text) : ToColor(MenuPalette.Muted))
                            : _layout == UiOptionsLayout.Segmented
                                ? Grey(Progenitor.Ui.GameArt.SettingsControlLook.SegmentCaptionGrey(on))
                                : Grey(Progenitor.Ui.GameArt.SettingsControlLook.ListCaptionGrey(on));
                    }
                }
                catch { /* */ }
            }
        }
    }

    private class UnityWidget : IRuntimeWidget
    {
        protected bool Released;

        /// <summary>
        /// The panel this widget is a row of, or null when it is not a row (the panel itself, its
        /// overlay). A row needs it so that changing its text can ask the panel to lay out again.
        /// </summary>
        private readonly UnityPanel? _owner;

        public UnityWidget(
            UnityPanel? owner,
            GameObject go,
            object? click,
            object? floatEvent,
            object? boolEvent,
            object? textEvent,
            object? enter,
            object? exit)
        {
            _owner = owner;
            GameObject = go;
            ClickEvent = click;
            FloatEvent = floatEvent;
            BoolEvent = boolEvent;
            TextEvent = textEvent;
            PointerEnterEvent = enter;
            PointerExitEvent = exit;
        }

        public GameObject GameObject { get; }

        /// <summary>SETTINGS1 stage 2: the panel this widget is a row of, for the settings-grid seams.</summary>
        internal UnityPanel? Owner => _owner;

        public bool Alive => !Released && UnityUiRuntime.Alive(GameObject);

        private bool _interactable = true;

        /// <summary>
        /// SETTINGS1 stage 2: gray the row in place. A <c>CanvasGroup</c> on the row object dims it and
        /// switches off every selectable under it (<c>CanvasGroup.interactable</c>), so a grayed control
        /// takes no click, pick or typed commit; nothing is rebuilt and the layout does not move.
        /// </summary>
        public bool Interactable
        {
            get => _interactable;
            set
            {
                _interactable = value;
                try
                {
                    var group = GameObject.GetComponent<CanvasGroup>() ?? GameObject.AddComponent<CanvasGroup>();
                    if (group == null) return;
                    group.interactable = value;
                    group.alpha = value ? 1f : GreyedAlpha;
                }
                catch { /* the row stays drawn as it was; the handle still refuses input */ }
            }
        }

        public object? ClickEvent { get; }
        public object? FloatEvent { get; }
        public object? BoolEvent { get; }
        public object? TextEvent { get; }
        public object? PointerEnterEvent { get; }
        public object? PointerExitEvent { get; }

        public string Align => _owner?.ContentAlignment ?? SettingsAlign.Centre;

        public string Text
        {
            get
            {
                try
                {
                    var tmp = GameObject.GetComponentInChildren<TextMeshProUGUI>(true);
                    return tmp == null ? "" : (tmp.text ?? "");
                }
                catch { return ""; }
            }
            set
            {
                try
                {
                    var tmp = GameObject.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (tmp != null)
                    {
                        // A caller that rewrites the same caption every frame (a HUD readout) paid
                        // for a new IL2CPP string, a TMP mesh rebuild and a full panel relayout each
                        // time. Reading the current text returns the existing string; an unchanged
                        // caption changes nothing on screen, so it stops here.
                        if (string.Equals(tmp.text, value ?? "", StringComparison.Ordinal)) return;
                        tmp.text = value ?? "";
                    }
                }
                catch { /* */ }

                // R43 finding 4. This setter is the path every runtime text change takes — the
                // rebind flow's "press a key…", and then the sentence explaining why an AltGr
                // chord was refused, written onto a row that was measured without it. Until now
                // it wrote the string and stopped, so the row kept a height that described the
                // old text and the grown label drew over the row below: evidence 163's overlap,
                // reappearing at the exact moment the player is being told why their rebind
                // failed. The layout is no longer frozen at build time; the panel re-measures and
                // re-places its rows. Callers do not have to know they made the panel taller.
                try { _owner?.Relayout(); }
                catch (Exception e)
                {
                    // The text is already written. A layout that failed must not turn a label
                    // update into a throw inside a Unity callback.
                    Plugin.Logger?.LogWarning("Ui: re-layout after a text change threw: " + e.Message);
                }
            }
        }

        public bool Visible
        {
            get
            {
                try { return GameObject.activeSelf; }
                catch { return false; }
            }
            set
            {
                bool was = true;
                try { was = GameObject.activeSelf; }
                catch { was = true; }
                try { GameObject.SetActive(value); }
                catch { /* */ }
                if (was != value) _owner?.Relayout();
            }
        }

        /// <summary>
        /// <c>GetComponentInChildren</c>, not <c>GetComponent</c>, for all three value accessors
        /// below. Since evidence 176 a row is a container: the caption is one child and the control
        /// is another, because a control is not a component but a component plus the objects it
        /// draws through, and those objects need a rect of their own to sit in the row's control
        /// column. The <c>true</c> matters as much as the <c>InChildren</c>: a control is built
        /// inactive and stays inactive until its references are set.
        /// </summary>
        private string? _stringHeld;
        private float _floatHeld;
        private bool _boolHeld;
        private bool _hasStringHeld;
        private bool _hasFloatHeld;
        private bool _hasBoolHeld;

        public bool BoolValue
        {
            get
            {
                try
                {
                    var t = GameObject.GetComponentInChildren<Toggle>(true);
                    if (t != null) return t.isOn;
                }
                catch { /* */ }
                return _hasBoolHeld && _boolHeld;
            }
            set
            {
                _hasBoolHeld = true;
                _boolHeld = value;
                try
                {
                    var t = GameObject.GetComponentInChildren<Toggle>(true);
                    if (t != null) t.isOn = value;
                }
                catch { /* */ }
                AfterBoolWrite(value);
            }
        }

        public float FloatValue
        {
            get
            {
                try
                {
                    var s = GameObject.GetComponentInChildren<Slider>(true);
                    if (s != null) return s.value;
                }
                catch { /* */ }
                return _hasFloatHeld ? _floatHeld : 0f;
            }
            set
            {
                _hasFloatHeld = true;
                _floatHeld = value;
                try
                {
                    var s = GameObject.GetComponentInChildren<Slider>(true);
                    if (s != null) s.value = value;
                }
                catch { /* */ }
                AfterFloatWrite(value);
            }
        }

        /// <summary>DROPHOVER1: a subclass repaints after a float write (the dropdown's highlighted option).</summary>
        protected virtual void AfterFloatWrite(float value)
        {
        }

        public string StringValue
        {
            get
            {
                try
                {
                    var f = GameObject.GetComponentInChildren<TMP_InputField>(true);
                    if (f != null) return f.text ?? "";
                }
                catch { /* */ }
                if (_hasStringHeld) return _stringHeld ?? "";
                return Text;
            }
            set
            {
                _hasStringHeld = true;
                _stringHeld = value ?? "";
                try
                {
                    var f = GameObject.GetComponentInChildren<TMP_InputField>(true);
                    if (f != null)
                    {
                        f.text = _stringHeld;
                        // NOPROGMSG1: a field's empty hint follows a value written from code (Apply, a sync), not only typing.
                        SyncSearchHint(f);
                        // Live 2026-10-10 (user): a color row's swatch kept its first color after the value changed
                        // (red beside #FFFFFF). It shows the committed value.
                        if (string.Equals(GameObject.name, "color-row", StringComparison.Ordinal)) PaintSwatch(GameObject, _stringHeld);
                    }
                    else
                    {
                        var named = FindNamedTmp(GameObject, "value-text");
                        if (named != null && !string.Equals(named.text, _stringHeld, StringComparison.Ordinal))
                        {
                            named.text = _stringHeld;
                            // A read-only value turns muted while it reads "Not set" (and back).
                            if (string.Equals(GameObject.name, "info-row", StringComparison.Ordinal))
                                named.color = Grey(InfoValueGrey(_stringHeld));
                            // The row chip sits left of this text: place it again for the new width.
                            Owner?.NoteRowValueChanged(GameObject);
                        }
                    }
                }
                catch { /* */ }
            }
        }

        /// <summary>SETTINGS1 stage 2: a subclass repaints after a value write (the grid switch).</summary>
        protected virtual void AfterBoolWrite(bool value)
        {
        }

        private static TextMeshProUGUI? FindNamedTmp(GameObject root, string name)
        {
            try
            {
                var tmps = root.GetComponentsInChildren<TextMeshProUGUI>(true);
                if (tmps == null) return null;
                for (int i = 0; i < tmps.Length; i++)
                {
                    var tmp = tmps[i];
                    if (tmp == null) continue;
                    try
                    {
                        if (tmp.gameObject != null && tmp.gameObject.name == name)
                            return tmp;
                    }
                    catch { /* */ }
                }
            }
            catch { /* */ }
            return null;
        }

        public virtual void Release()
        {
            if (Released) return;
            Released = true;
            TryDestroy(GameObject, BootstrapLink.MayDestroyUiImmediate());
        }
    }

    /// <summary>
    /// The Unity side of one cell row (LIST1). Owns the cell objects; positions them from
    /// <see cref="MenuPanelLayout.CellColumns"/> on every panel layout pass and again, for this
    /// row only, after an in-place rewrite. Nothing here runs per frame.
    /// </summary>
    private sealed class UnityCellRow : IRuntimeCellRow
    {
        private sealed class Cell
        {
            public Cell(CellSpec spec) { Spec = spec; }
            public CellSpec Spec { get; }
            public GameObject? Go;
            public TextMeshProUGUI? Tmp;
            public Image? Fill;
            public Image? FillRight;
            public Outline? Rim;
            public Image? Picture;
            public UnityWidget? Widget;
            public TMP_InputField? Field;
            public float LastWidth;

            /// <summary>INSTALLSIDES1: a scroll block's parts and its laid-out lines.</summary>
            public ScrollRect? Scroll;
            public RectTransform? Viewport;
            public RectTransform? Content;
            public readonly List<TextMeshProUGUI> Lines = new();
            public float LaidWidth = -1f;
            public float LaidHeight = -1f;

            /// <summary>INSTALLSIDES1: an expander cell's open state (the turn of its arrow).</summary>
            public bool Expanded;

            /// <summary>INSTALLSIDES2: a column button's hue, the same sprite laid over the button's own.</summary>
            public Image? Tint;

            /// <summary>
            /// C5, a two-line cell with an inline overlay (<see cref="CellSpec.InlineAfter"/>): the second line's text before
            /// the overlay's room as last written, and whether the room was kept (the overlay is drawn only then).
            /// </summary>
            public string InlinePrefix = "";

            public bool InlineShown;
        }

        /// <summary>C5: the hidden label inline overlays are measured on (made on first use; never drawn).</summary>
        private TextMeshProUGUI? _inlineMeasurer;

        /// <summary>C5: the slots that pass a press on a hover-only image cell to its row (released with the row).</summary>
        private readonly List<ushort> _forwardSlots = new();

        private readonly UnityPanel _host;
        private readonly Image _fill;
        private readonly CellRowSpec _spec;
        private readonly bool _header;
        private readonly Dictionary<string, Cell> _cells = new(StringComparer.Ordinal);
        private GameObject? _stripe;

        /// <summary>INSTALLSIDES1: a joined row's fill through the gap above it, and its seam.</summary>
        private Image? _joinBand;
        private GameObject? _seam;

        /// <summary>INSTALLSIDES2 item 1: one tinted band per per-side column, behind that column's cells.</summary>
        private sealed class ColumnBandParts
        {
            public GameObject? Go;
            public Image? Fill;
            public Image? Rule;

            /// <summary>VANILLA1: a row band's left edge bar (the game's <c>Modified</c> marker, in the column's accent).</summary>
            public Image? Edge;

            /// <summary>VANILLA1: a row band's top gradient (<c>ListButton_TopGradient</c> tinted to the column), or a 1 px line.</summary>
            public Image? Top;
        }

        private readonly Dictionary<CellColumn, ColumnBandParts> _bands = new();

        /// <summary>TILES1: one framed box per tile of this row (<see cref="CellTiles"/>), behind that tile's cells.</summary>
        private readonly SortedDictionary<int, GameObject> _tiles = new();
        private bool _released;
        private bool _expanded;
        private bool _selected;
        private string? _sortedKey;
        private bool _sortAscending;

        public UnityCellRow(
            UnityPanel host, GameObject go, Image fill, object? click, CellRowSpec spec, bool header,
            object? enter = null, object? exit = null)
        {
            _host = host;
            GameObject = go;
            _fill = fill;
            ClickEvent = click;
            _spec = spec;
            _header = header;
            PointerEnterEvent = enter;
            PointerExitEvent = exit;
        }

        /// <summary>KEYS3: the game-entry highlight band (<see cref="CellRowSkin.GameEntry"/>), behind the cells.</summary>
        private Image? _highlight;

        /// <summary>KEYS3: the band's top gradient, a child of the band.</summary>
        private Image? _highlightTop;

        /// <summary>
        /// KEYS3: a category heading's thin underline, borrowed from the game's category header prefab
        /// (<see cref="KeybindArtDonors.TryRule"/>); not built when nothing was borrowed.
        /// </summary>
        private void BuildHeadingRule()
        {
            if (!KeybindArtDonors.TryRule(out var sprite, out var type, out var color)) return;
            try
            {
                var go = new GameObject("heading-rule");
                go.transform.SetParent(GameObject.transform, false);
                var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(KeybindsTabLayout.CategoryRuleShare, 0f);
                rt.pivot = new Vector2(0f, 0f);
                rt.offsetMin = new Vector2(0f, 0f);
                rt.offsetMax = new Vector2(0f, KeybindsTabLayout.CategoryRuleHeight);
                var image = TryAdd<Image>(go);
                if (image == null) return;
                try { image.sprite = sprite; image.type = type; image.color = color; } catch { /* */ }
                try { image.raycastTarget = false; } catch { /* */ }
                try { image.maskable = true; } catch { /* */ }
            }
            catch { /* no rule */ }
        }

        /// <summary>
        /// The game window tab strip's baseline (<see cref="CellRowSkin.GameTabStrip"/>): a line across the whole row at its
        /// bottom edge (<see cref="UiTabsLayout.Baseline"/>), in the tab frame's steel. Anchored to the row's bottom and
        /// both sides, so it spans the row at every window width without placing.
        /// </summary>
        private void BuildTabBaseline()
        {
            var (_, h) = UiTabsLayout.Baseline(_spec.Height);
            var line = BandChild(GameObject, UiTabsLayout.BaselineName, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, h));
            try { if (line != null) line.color = ToColor(MenuPalette.TabBaseline); } catch { /* */ }
        }

        public GameObject GameObject { get; }

        public float Height => _spec.FitWrap ? FittedHeight() : _spec.Height;

        private float _fittedFor = -1f;
        private float _fitted;

        /// <summary>
        /// RADIOCLICK2: a <see cref="CellRowSpec.FitWrap"/> row is as tall as its wrapping cell's text measured at the
        /// width that cell is placed at (<see cref="MenuPanelLayout.CellColumns"/> over the panel's row width), plus the
        /// pad; remeasured when that width changes. Falls back to the spec's estimate when TMP cannot measure.
        /// </summary>
        private float FittedHeight()
        {
            float rowWidth = _placedWidth > 0f ? _placedWidth : _host.ContentRowWidth;
            if (Math.Abs(rowWidth - _fittedFor) < 0.5f) return _fitted;
            float measured = 0f, minText = 1f;
            try
            {
                var placements = MenuPanelLayout.CellColumns(rowWidth, _spec);
                for (int i = 0; i < placements.Length; i++)
                {
                    var p = placements[i];
                    if (!p.Visible || !_cells.TryGetValue(p.Key, out var cell) || cell.Spec.Wrap != CellWrap.Wrap || cell.Tmp == null) continue;
                    var size = cell.Tmp.GetPreferredValues(cell.Tmp.text, Math.Max(1f, p.Width), 0f);
                    measured = Math.Max(measured, size.y);
                    minText = Math.Max(minText, cell.Spec.FontSize);
                }
            }
            catch { measured = 0f; }
            _fittedFor = rowWidth;
            _fitted = _spec.FitWrapHeight(measured, minText);
            return _fitted;
        }

        public CellRowSpec Spec => _spec;

        /// <summary>The separator rule along the row's top edge (<see cref="CellRowSpec.Separator"/>), when it draws one.</summary>
        private Image? _separator;

        /// <summary>The row draws a line along its top edge (its separator rule).</summary>
        internal bool HasTopLine => _separator != null;

        /// <summary>
        /// Hide the row's top line while it is the first row under a game tab row (<see cref="UiTabsLayout.TopLinesHidden"/>):
        /// the strip's baseline is the one line there.
        /// </summary>
        internal void SetTopLineHidden(bool hidden)
        {
            if (_separator == null) return;
            try { if (_separator.enabled == hidden) _separator.enabled = !hidden; } catch { /* */ }
        }

        /// <summary>The row is a game window tab strip, which draws its own baseline.</summary>
        internal bool IsGameTabStrip => !_header && _spec.Skin == CellRowSkin.GameTabStrip;

        public bool Alive => !_released && UnityUiRuntime.Alive(GameObject);

        /// <summary>NAV1: the panel the row is on (the right stick scrolls its list).</summary>
        internal UnityPanel Host => _host;

        public object? ClickEvent { get; private set; }
        public object? FloatEvent => null;
        public object? BoolEvent => null;
        public object? TextEvent => null;
        public object? PointerEnterEvent { get; }
        public object? PointerExitEvent { get; }

        /// <summary>
        /// RADIOCLICK1: a plain, band or header row is built with only its fill (a raycast target, so it swallows the
        /// pointer) and no <c>Button</c>, so the pointer's click found no handler and a bound row click never ran. On
        /// request the row gets a <c>Button</c> over that fill with no color transition: clickable, never tinted.
        /// </summary>
        public object? EnsureRowClick()
        {
            if (ClickEvent != null || _released) return ClickEvent;
            try
            {
                var button = GameObject.GetComponent<Button>() ?? TryAdd<Button>(GameObject);
                if (button == null) return null;
                try { button.targetGraphic = _fill; } catch { /* */ }
                try { button.transition = Selectable.Transition.None; } catch { /* */ }
                try { _fill.raycastTarget = true; } catch { /* */ }
                ClickEvent = button.onClick;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row click for '" + _spec.Key + "': " + e.Message); }
            return ClickEvent;
        }

        public string Align => SettingsAlign.Left;

        /// <summary>The row key: what <c>widget_read</c> / <c>widget_click</c> address the row by. Not writable.</summary>
        public string Text
        {
            get => _spec.Key;
            set { /* the key is the identity; captions are rewritten per cell */ }
        }

        public bool Visible
        {
            get
            {
                try { return GameObject.activeSelf; }
                catch { return false; }
            }
            set
            {
                bool was = true;
                try { was = GameObject.activeSelf; }
                catch { was = true; }
                try { GameObject.SetActive(value); }
                catch { /* */ }
                if (was != value) _host.Relayout();
            }
        }

        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public string StringValue { get => _spec.Key; set { } }

        public void Build()
        {
            if (!_header && _spec.Skin == CellRowSkin.GameEntry)
            {
                // KEYS3: the band the game draws under a hovered or selected settings entry. Built first
                // so it sits behind every cell; shown by SetSelected only when a donor sprite exists.
                try
                {
                    var band = new GameObject("entry-highlight");
                    band.transform.SetParent(GameObject.transform, false);
                    Stretch(band);
                    _highlight = TryAdd<Image>(band);
                    if (_highlight != null)
                    {
                        try { _highlight.raycastTarget = false; } catch { /* */ }
                        try { _highlight.maskable = true; } catch { /* */ }
                    }
                    // Its top gradient (ListButton_TopGradient), a strip along the band's top edge.
                    var top = new GameObject("entry-highlight-top");
                    top.transform.SetParent(band.transform, false);
                    _highlightTop = TryAdd<Image>(top);
                    if (_highlightTop != null)
                    {
                        try { _highlightTop.raycastTarget = false; } catch { /* */ }
                        try { _highlightTop.maskable = true; } catch { /* */ }
                    }
                    top.SetActive(false);
                    band.SetActive(false);
                }
                catch { _highlight = null; }
            }

            if (!_header && _spec.Skin == CellRowSkin.CategoryHeading) BuildHeadingRule();
            // Before the cells, so every tab draws over it: the tabs stand on the line, the selected one runs into it.
            if (!_header && _spec.Skin == CellRowSkin.GameTabStrip) BuildTabBaseline();
            if (!_header && _spec.Section != CellSection.None) BuildSection();
            if (!_header) BuildTiles();

            // INSTALLSIDES2 item 1: the column bands first, so every cell draws over them.
            for (int i = 0; i < _spec.Cells.Count; i++)
            {
                var column = _spec.Cells[i].Column;
                if (column != CellColumn.None && !_bands.ContainsKey(column)) BuildBand(column);
            }
            PaintBands();

            for (int i = 0; i < _spec.Cells.Count; i++)
            {
                var spec = _spec.Cells[i];
                if (spec.Kind == CellKind.Spacer || _cells.ContainsKey(spec.Key)) continue;
                var cell = new Cell(spec);
                try
                {
                    if (_header) BuildHeaderCell(cell);
                    else BuildCell(cell);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Ui: cell '" + spec.Key + "' build threw: " + e.Message);
                    TryDestroyImmediate(cell.Go);
                    cell.Go = null;
                }
                _cells[spec.Key] = cell;
            }

            if (!_header && _spec.Separator && !_spec.JoinAbove)
            {
                // VANILLA1: the game's faint line between list entries, along the row's top edge.
                var rule = BandChild(GameObject, "row-separator", new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(0.5f, 1f), new Vector2(0f, -1f), Vector2.zero);
                try { if (rule != null) rule.color = ToColor(MenuPalette.RowSeparator); } catch { /* */ }
                _separator = rule;
            }

            if (!_header)
            {
                try
                {
                    var stripe = new GameObject("selected-stripe");
                    stripe.transform.SetParent(GameObject.transform, false);
                    var image = TryAdd<Image>(stripe);
                    if (image != null)
                    {
                        try { image.color = ToColor(MenuPalette.SelectedStripe); } catch { /* */ }
                        try { image.raycastTarget = false; } catch { /* */ }
                        try { image.maskable = true; } catch { /* */ }
                    }
                    stripe.SetActive(false);
                    _stripe = stripe;
                }
                catch { _stripe = null; }

                if (_spec.JoinAbove) BuildJoin();
            }
        }

        /// <summary>
        /// JOINLINKS1: a section row's band parts (the row's own fill is already the section's): the fill
        /// through the row gap above a row that is not the section's first, a 1 px rim down both sides
        /// (through that gap too), the top rim on the first row and the bottom rim on the last. Anchored
        /// to the row's edges, so a re-flow needs no placing.
        /// </summary>
        private void BuildSection()
        {
            var (fill, rim) = MenuPalette.SectionBand(_spec.Section);
            bool first = (_spec.SectionEdge & CellSectionEdge.First) != 0;
            bool last = (_spec.SectionEdge & CellSectionEdge.Last) != 0;
            float reach = first ? 0f : PanelLayout.RowGap;
            try
            {
                if (!first)
                {
                    var gap = BandChild(GameObject, "section-gap", new Vector2(0f, 1f), new Vector2(1f, 1f),
                        new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, reach));
                    if (gap != null) gap.color = ToColor(fill);
                }

                var left = BandChild(GameObject, "section-rim-left", new Vector2(0f, 0f), new Vector2(0f, 1f),
                    new Vector2(0f, 0.5f), Vector2.zero, new Vector2(1f, reach));
                if (left != null) left.color = ToColor(rim);
                var right = BandChild(GameObject, "section-rim-right", new Vector2(1f, 0f), new Vector2(1f, 1f),
                    new Vector2(1f, 0.5f), new Vector2(-1f, 0f), new Vector2(0f, reach));
                if (right != null) right.color = ToColor(rim);
                if (first)
                {
                    var top = BandChild(GameObject, "section-rim-top", new Vector2(0f, 1f), new Vector2(1f, 1f),
                        new Vector2(0.5f, 1f), new Vector2(0f, -1f), Vector2.zero);
                    if (top != null) top.color = ToColor(rim);
                }

                if (last)
                {
                    var bottom = BandChild(GameObject, "section-rim-bottom", new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 1f));
                    if (bottom != null) bottom.color = ToColor(rim);
                }
            }
            catch { /* the row keeps its section fill */ }
        }

        /// <summary>
        /// TILES1: a framed box per tile among this row's cells: a fill and a 1 px rim down both sides, the top rim
        /// on a <see cref="CellSectionEdge.First"/> row and the bottom one on a <see cref="CellSectionEdge.Last"/> row.
        /// Sized and placed by <see cref="PlaceTiles"/>; the rims are anchored to the box's edges.
        /// </summary>
        private void BuildTiles()
        {
            if (_spec.TileStates)
            {
                BuildStateTiles();
                return;
            }

            var (fill, rim) = MenuPalette.TileBand;
            bool first = (_spec.TileEdge & CellSectionEdge.First) != 0;
            bool last = (_spec.TileEdge & CellSectionEdge.Last) != 0;
            for (int i = 0; i < _spec.Cells.Count; i++)
            {
                int tile = _spec.Cells[i].Tile;
                if (tile <= 0 || _tiles.ContainsKey(tile)) continue;
                try
                {
                    var go = new GameObject("tile-frame-" + tile);
                    go.transform.SetParent(GameObject.transform, false);
                    var image = TryAdd<Image>(go);
                    if (image != null)
                    {
                        try { image.sprite = null; image.color = ToColor(fill); } catch { /* */ }
                        try { image.raycastTarget = false; } catch { /* */ }
                        try { image.maskable = true; } catch { /* */ }
                    }
                    var left = BandChild(go, "tile-rim-left", new Vector2(0f, 0f), new Vector2(0f, 1f),
                        new Vector2(0f, 0.5f), Vector2.zero, new Vector2(1f, 0f));
                    if (left != null) left.color = ToColor(rim);
                    var right = BandChild(go, "tile-rim-right", new Vector2(1f, 0f), new Vector2(1f, 1f),
                        new Vector2(1f, 0.5f), new Vector2(-1f, 0f), Vector2.zero);
                    if (right != null) right.color = ToColor(rim);
                    if (first)
                    {
                        var top = BandChild(go, "tile-rim-top", new Vector2(0f, 1f), new Vector2(1f, 1f),
                            new Vector2(0.5f, 1f), new Vector2(0f, -1f), Vector2.zero);
                        if (top != null) top.color = ToColor(rim);
                    }
                    if (last)
                    {
                        var bottom = BandChild(go, "tile-rim-bottom", new Vector2(0f, 0f), new Vector2(1f, 0f),
                            new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 1f));
                        if (bottom != null) bottom.color = ToColor(rim);
                    }
                    go.SetActive(false);
                    _tiles[tile] = go;
                }
                catch { /* the cells still draw; only the frame is missing */ }
            }
        }

        /// <summary>
        /// TILES1: each tile's box spans its cells (<see cref="CellTiles.Spans"/>) and the row's height; a row that is not
        /// its tiles' first also covers the gap above it, so the head and the note read as one box.
        /// </summary>
        private void PlaceTiles(CellPlacement[] placements, float rowHeight)
        {
            if (_tiles.Count == 0) return;
            if (_spec.TileStates)
            {
                PlaceStateTiles(placements, rowHeight);
                return;
            }

            bool first = (_spec.TileEdge & CellSectionEdge.First) != 0;
            float reach = first ? 0f : _spec.GapAbove >= 0f ? _spec.GapAbove : PanelLayout.RowGap;
            var spans = CellTiles.Spans(placements, _spec.Cells);
            foreach (var pair in _tiles)
            {
                CellTiles.TileSpan? span = null;
                foreach (var s in spans)
                    if (s.Tile == pair.Key) { span = s; break; }
                try
                {
                    if (span is not { } sp || sp.Width <= 1f)
                    {
                        pair.Value.SetActive(false);
                        continue;
                    }
                    if (!pair.Value.activeSelf) pair.Value.SetActive(true);
                    PlaceTop(pair.Value, sp.Center, -reach, sp.Width, rowHeight + reach);
                }
                catch { /* */ }
            }
        }

        /// <summary>P7b: each tile's last state (<see cref="CellRowSpec.TileStates"/> rows).</summary>
        private readonly Dictionary<int, UiTileState> _tileStates = new();

        /// <summary>P7b: a <see cref="CellRowSpec.TileStates"/> row's tiles: a plain image each with a gold stripe child, hidden until a state asks.</summary>
        private void BuildStateTiles()
        {
            for (int i = 0; i < _spec.Cells.Count; i++)
            {
                int tile = _spec.Cells[i].Tile;
                if (tile <= 0 || _tiles.ContainsKey(tile)) continue;
                try
                {
                    var go = new GameObject("tile-state-" + tile);
                    go.transform.SetParent(GameObject.transform, false);
                    var image = TryAdd<Image>(go);
                    if (image != null)
                    {
                        try { image.sprite = null; } catch { /* */ }
                        try { image.raycastTarget = false; } catch { /* */ }
                        try { image.maskable = true; } catch { /* */ }
                    }
                    var stripe = BandChild(go, "tile-stripe", new Vector2(0f, 0f), new Vector2(0f, 1f),
                        new Vector2(0f, 0.5f), Vector2.zero, new Vector2(MenuPalette.SelectedStripeWidth, 0f));
                    if (stripe != null) stripe.color = ToColor(MenuPalette.SelectedStripe);
                    go.SetActive(false);
                    _tiles[tile] = go;
                }
                catch { /* the cells still draw; only the band is missing */ }
            }
        }

        public void SetTileState(int tile, UiTileState state)
        {
            if (_released || !_tiles.ContainsKey(tile)) return;
            _tileStates[tile] = state;
            // In place, at the width the layout pass last placed the row at (as ApplyCell).
            PlaceCells(MenuPanelLayout.InPlaceCellRowWidth(_placedWidth, _host.ContentRowWidth));
        }

        /// <summary>
        /// P7b: a band state spans its tile's cells and the row's height (no reach into the gap: one entry lit, not a
        /// block); a rule is <see cref="UiTileLook.RuleWidth"/> wide down the tile's center and reaches up through the gap
        /// above unless the row is its rule's first (<see cref="CellRowSpec.TileEdge"/> First), so the rules of consecutive
        /// rows read as one line.
        /// </summary>
        private void PlaceStateTiles(CellPlacement[] placements, float rowHeight)
        {
            bool first = (_spec.TileEdge & CellSectionEdge.First) != 0;
            float gap = _spec.GapAbove >= 0f ? _spec.GapAbove : PanelLayout.RowGap;
            var spans = CellTiles.Spans(placements, _spec.Cells);
            foreach (var pair in _tiles)
            {
                var state = _tileStates.TryGetValue(pair.Key, out var s) ? s : UiTileState.Hidden;
                CellTiles.TileSpan? span = null;
                foreach (var sp in spans)
                    if (sp.Tile == pair.Key) { span = sp; break; }
                try
                {
                    if (state == UiTileState.Hidden || span is not { } at || at.Width <= 1f)
                    {
                        if (pair.Value.activeSelf) pair.Value.SetActive(false);
                        continue;
                    }

                    if (!pair.Value.activeSelf) pair.Value.SetActive(true);
                    var image = pair.Value.GetComponent<Image>();
                    if (image != null) image.color = ToColor(UiTileLook.Fill(state));
                    var stripe = pair.Value.transform.Find("tile-stripe");
                    if (stripe != null) stripe.gameObject.SetActive(UiTileLook.Stripe(state));
                    if (state == UiTileState.Rule)
                    {
                        float reach = first ? 0f : gap;
                        PlaceTop(pair.Value, at.Center, -reach, UiTileLook.RuleWidth, rowHeight + reach);
                    }
                    else PlaceTop(pair.Value, at.Center, 0f, at.Width, rowHeight);
                }
                catch { /* */ }
            }
        }

        /// <summary>
        /// INSTALLSIDES1: a row that continues the one above it gets a band filling the row gap above
        /// it (the selected fill, shown only while selected) and, from <see cref="CellRowSpec.Indent"/>,
        /// a one-pixel seam in the accent color along its top edge.
        /// </summary>
        private void BuildJoin()
        {
            try
            {
                var band = new GameObject("join-band");
                band.transform.SetParent(GameObject.transform, false);
                _joinBand = TryAdd<Image>(band);
                if (_joinBand != null)
                {
                    try { _joinBand.color = ToColor(_spec.Soft ? ModsListLayout.RowFill(true) : MenuPalette.RowFillFor(true)); } catch { /* */ }
                    try { _joinBand.raycastTarget = false; } catch { /* */ }
                    try { _joinBand.maskable = true; } catch { /* */ }
                }
                band.SetActive(false);
            }
            catch { _joinBand = null; }

            if (_spec.Indent <= 0f) return;
            try
            {
                var seam = new GameObject("join-seam");
                seam.transform.SetParent(GameObject.transform, false);
                var image = TryAdd<Image>(seam);
                if (image != null)
                {
                    var gold = ToColor(MenuPalette.SelectedStripe);
                    try { image.color = new Color(gold.r, gold.g, gold.b, InstallDrawerLayout.SeamAlpha); } catch { /* */ }
                    try { image.raycastTarget = false; } catch { /* */ }
                    try { image.maskable = true; } catch { /* */ }
                }
                seam.SetActive(false);
                _seam = seam;
            }
            catch { _seam = null; }
        }

        /// <summary>
        /// VANILLA1: a per-side column's band in the game's idiom (menu-kit research:
        /// the game never boxes a list entry). A row's band is a low-alpha wash of the column's hue
        /// with no rim, the game's top gradient tinted to the column along its top, and a thin edge
        /// bar in the column's accent at its left (the game's <c>Modified</c> marker). A header's band
        /// has no fill: its caption sits over the game's divider (<c>Divider02_LeftToRight</c>, the
        /// settings section header's line) tinted to the column. Placed by <see cref="PlaceBands"/>.
        /// </summary>
        private void BuildBand(CellColumn column)
        {
            var parts = new ColumnBandParts();
            try
            {
                var go = new GameObject("column-band-" + (column == CellColumn.Client ? "client" : "server"));
                go.transform.SetParent(GameObject.transform, false);
                parts.Go = go;
                parts.Fill = TryAdd<Image>(go);
                if (parts.Fill != null)
                {
                    try { parts.Fill.sprite = null; } catch { /* */ }
                    try { parts.Fill.raycastTarget = false; } catch { /* */ }
                    try { parts.Fill.maskable = true; } catch { /* */ }
                }
                if (!_header)
                {
                    parts.Edge = BandChild(go, "column-edge", new Vector2(0f, 0f), new Vector2(0f, 1f),
                        new Vector2(0f, 0.5f), Vector2.zero, new Vector2(MenuPalette.ColumnEdgeWidth, 0f));
                    float topHeight = 1f;
                    Sprite? topSprite = null;
                    if (KeybindArtDonors.TryHighlightTop(out var top, out _) && top != null)
                    {
                        topSprite = top;
                        try { topHeight = Math.Max(1f, Math.Min(KeybindsTabLayout.HighlightTopMaxHeight, top.rect.height)); }
                        catch { topHeight = 3f; }
                    }
                    parts.Top = BandChild(go, "column-top", new Vector2(0f, 1f), new Vector2(1f, 1f),
                        new Vector2(0.5f, 1f), new Vector2(0f, -topHeight), Vector2.zero);
                    if (parts.Top != null && topSprite != null)
                    {
                        try { parts.Top.sprite = topSprite; parts.Top.type = Image.Type.Simple; } catch { /* the plain line */ }
                    }
                }
                else
                {
                    parts.Rule = BandChild(go, "column-rule", new Vector2(0f, 0f), new Vector2(1f, 0f),
                        new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, InstallColumns.HeaderRuleHeight));
                    if (parts.Rule != null && KeybindArtDonors.TryRule(out var rule, out var type, out _) && rule != null)
                    {
                        try { parts.Rule.sprite = rule; parts.Rule.type = type; } catch { /* the plain line */ }
                    }
                }
                go.SetActive(false);
            }
            catch { /* the cells still draw; only the tint is missing */ }
            _bands[column] = parts;
        }

        /// <summary>A band's child image at the given anchors and offsets; null when it could not be made.</summary>
        private static Image? BandChild(GameObject parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 offsetMin, Vector2 offsetMax)
        {
            try
            {
                var go = new GameObject(name);
                go.transform.SetParent(parent.transform, false);
                var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
                rt.anchorMin = anchorMin;
                rt.anchorMax = anchorMax;
                rt.pivot = pivot;
                rt.offsetMin = offsetMin;
                rt.offsetMax = offsetMax;
                var image = TryAdd<Image>(go);
                if (image == null) return null;
                try { image.raycastTarget = false; } catch { /* */ }
                try { image.maskable = true; } catch { /* */ }
                return image;
            }
            catch { return null; }
        }

        /// <summary>A column is grayed when any of its cells says so (the side's text cell does).</summary>
        private bool ColumnGrayed(CellColumn column)
        {
            for (int i = 0; i < _spec.Cells.Count; i++)
                if (_spec.Cells[i].Column == column && _spec.Cells[i].Grayed) return true;
            return false;
        }

        private void PaintBands()
        {
            foreach (var pair in _bands)
            {
                var (wash, edge, top) = MenuPalette.ColumnLook(pair.Key, _header, ColumnGrayed(pair.Key));
                try { if (pair.Value.Fill != null) pair.Value.Fill.color = ToColor(wash); } catch { /* */ }
                try { if (pair.Value.Edge != null) pair.Value.Edge.color = ToColor(edge); } catch { /* */ }
                try
                {
                    if (pair.Value.Top != null)
                    {
                        pair.Value.Top.color = ToColor(top);
                        pair.Value.Top.gameObject.SetActive(top.A > 0f);
                    }
                }
                catch { /* */ }
                try { if (pair.Value.Rule != null) pair.Value.Rule.color = ToColor(edge); } catch { /* */ }
            }
        }

        /// <summary>Each band spans its column's visible cells, inset from the row's top and bottom.</summary>
        private void PlaceBands(CellPlacement[] placements, float rowHeight)
        {
            if (_bands.Count == 0) return;
            foreach (var pair in _bands)
            {
                float left = float.MaxValue, right = float.MinValue;
                for (int i = 0; i < placements.Length; i++)
                {
                    var p = placements[i];
                    if (!p.Visible || !_cells.TryGetValue(p.Key, out var cell) || cell.Spec.Column != pair.Key) continue;
                    left = Math.Min(left, p.X - p.Width * .5f);
                    right = Math.Max(right, p.X + p.Width * .5f);
                }

                var go = pair.Value.Go;
                if (go == null) continue;
                if (right <= left)
                {
                    try { go.SetActive(false); } catch { /* */ }
                    continue;
                }

                float pad = InstallColumns.BandPad;
                float inset = _header ? 0f : InstallColumns.BandInset;
                try { if (!go.activeSelf) go.SetActive(true); } catch { /* */ }
                PlaceTop(go, (left + right) * .5f, inset, right - left + 2f * pad, Math.Max(1f, rowHeight - 2f * inset));
            }
        }

        // ---- INSTALLSIDES2 item 1: glyphs, drawn only when the game font has them ------------------

        private static readonly Dictionary<string, bool> GlyphSupport = new(StringComparer.Ordinal);

        /// <summary>
        /// <paramref name="glyph"/> and a space when <paramref name="tmp"/>'s font (or its fallbacks)
        /// has every character of it; empty otherwise, so a missing glyph never draws TMP's box. A
        /// character outside the BMP (a surrogate pair, the lock) is never asked: it counts as missing.
        /// The answer is logged once per font and glyph.
        /// </summary>
        private static string GlyphPrefix(TextMeshProUGUI? tmp, string? glyph)
        {
            if (tmp == null || string.IsNullOrEmpty(glyph)) return "";
            string fontName = "";
            TMP_FontAsset? font = null;
            try { font = tmp.font; fontName = font != null ? font.name : ""; }
            catch { font = null; }
            if (font == null) return "";
            string key = fontName + "|" + glyph;
            if (!GlyphSupport.TryGetValue(key, out bool has))
            {
                has = true;
                foreach (char c in glyph!)
                {
                    if (char.IsSurrogate(c)) { has = false; break; }
                    bool one;
                    // tryAddCharacter: a dynamic atlas holds only the glyphs drawn so far; asking it to
                    // add one from its source font is what TMP does when it draws the character.
                    try { one = font.HasCharacter(c, true, true); }
                    catch { one = false; }
                    if (!one) { has = false; break; }
                }
                GlyphSupport[key] = has;
                try
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (char c in glyph!) sb.Append(sb.Length > 0 ? " " : "").Append("U+").Append(((int)c).ToString("X4"));
                    Plugin.Logger?.LogInfo("Ui: glyph " + sb + " in font '" + fontName + "': " + (has ? "drawn" : "missing, words only"));
                }
                catch { /* a log line */ }
            }
            return has ? glyph + " " : "";
        }

        private void BuildCell(Cell cell)
        {
            var spec = cell.Spec;
            switch (spec.Kind)
            {
                case CellKind.Image:
                {
                    var go = new GameObject("cell-image");
                    go.transform.SetParent(GameObject.transform, false);
                    cell.Fill = TryAdd<Image>(go);
                    if (cell.Fill != null)
                    {
                        try { cell.Fill.color = ToColor(MenuPalette.ImageFill); } catch { /* */ }
                        try { cell.Fill.raycastTarget = false; } catch { /* */ }
                        try { cell.Fill.maskable = true; } catch { /* */ }
                    }
                    cell.Rim = AddRim(go, MenuPalette.ImageBorder);
                    var picture = new GameObject("picture");
                    picture.transform.SetParent(go.transform, false);
                    Stretch(picture);
                    cell.Picture = TryAdd<Image>(picture);
                    if (cell.Picture != null)
                    {
                        try { cell.Picture.preserveAspect = true; } catch { /* */ }
                        try { cell.Picture.raycastTarget = false; } catch { /* */ }
                        try { cell.Picture.maskable = true; } catch { /* */ }
                        try { cell.Picture.enabled = false; } catch { /* */ }
                    }
                    if (spec.Skin == CellSkin.IconWell)
                    {
                        // VANILLA1: a quiet well, not an opaque black block, when the icon is missing.
                        try { if (cell.Fill != null) cell.Fill.color = ToColor(MenuPalette.IconWellFill); } catch { /* */ }
                        try { if (cell.Rim != null) cell.Rim.effectColor = ToColor(MenuPalette.IconWellRim); } catch { /* */ }
                    }
                    if (spec.Skin == CellSkin.Ornament || spec.Skin == CellSkin.LoopGlyph)
                    {
                        // KEYS3: a bare borrowed sprite (the description ornament): no placeholder, no rim.
                        try { if (cell.Fill != null) cell.Fill.color = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
                        try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
                    }
                    if (spec.Skin == CellSkin.LoopGlyph) PaintLoopGlyph(cell, picture);
                    cell.Go = go;
                    if (spec.Hover)
                    {
                        // C5: a hover-only image (the Works across versions icon) is a pointer target: its clear fill takes
                        // the raycast, the kit's pointer trigger reports enter and exit, and a press on it is passed to the
                        // row (the trigger would otherwise take the click from the row's button).
                        try { if (cell.Fill != null) cell.Fill.raycastTarget = true; } catch { /* */ }
                        EnsurePointerEvents(go, out object? imageEnter, out object? imageExit);
                        ForwardClickToRow(go);
                        cell.Widget = new UnityWidget(null, go, null, null, null, null, imageEnter, imageExit);
                    }
                    break;
                }
                case CellKind.Badge:
                {
                    var go = new GameObject("cell-badge");
                    go.transform.SetParent(GameObject.transform, false);
                    cell.Fill = TryAdd<Image>(go);
                    if (cell.Fill != null)
                    {
                        try { cell.Fill.raycastTarget = false; } catch { /* */ }
                        try { cell.Fill.maskable = true; } catch { /* */ }
                    }
                    var right = new GameObject("badge-right");
                    right.transform.SetParent(go.transform, false);
                    StretchHalf(right, left: false);
                    cell.FillRight = TryAdd<Image>(right);
                    if (cell.FillRight != null)
                    {
                        try { cell.FillRight.raycastTarget = false; } catch { /* */ }
                        try { cell.FillRight.maskable = true; } catch { /* */ }
                    }
                    cell.Rim = AddRim(go, MenuPalette.UnknownBorder);
                    var label = MakeLabel(go, "", Vector2.zero, spec.FontSize);
                    if (label != null)
                    {
                        Stretch(label);
                        cell.Tmp = label.GetComponent<TextMeshProUGUI>();
                    }
                    cell.Go = go;
                    PaintBadge(cell);
                    break;
                }
                case CellKind.Button:
                {
                    // HUB1: a game tab cell is a clone of the game's own window tab (the kit button when no donor is reachable).
                    GameObject? caption = null;
                    var go = spec.Skin == CellSkin.GameTab ? BuildGameTabCell(GameObject, spec.Text, out caption) : null;
                    bool gameTab = go != null;
                    go ??= BuildButton(GameObject);
                    if (go == null) return;
                    go.name = gameTab ? "cell-game-tab" : "cell-button";
                    if (!gameTab) caption = SetButtonText(go, spec.Text);
                    cell.Tmp = caption?.GetComponent<TextMeshProUGUI>();
                    // A game tab keeps the clone's own art and states: no kit fill or rim is painted over it.
                    cell.Fill = gameTab ? null : go.GetComponent<Image>();
                    cell.Rim = gameTab ? null : AddRim(go, MenuPalette.ButtonBorder);
                    Activate(go);
                    FitInlineButtonCaption(go);
                    if (cell.Tmp != null)
                    {
                        // One centered line at the header caption size, clamped like every
                        // measured caption (UnityPanel.PrepareHeaderButtonCaption's rule).
                        try { cell.Tmp.fontSize = spec.FontSize; } catch { /* */ }
                        try { cell.Tmp.enableWordWrapping = false; } catch { /* */ }
                        ClampCaptionText(cell.Tmp.gameObject);
                        try { cell.Tmp.margin = new Vector4(CellLayout.ButtonPad * .5f, 0f, CellLayout.ButtonPad * .5f, 0f); } catch { /* */ }
                        try { cell.Tmp.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
                        try { cell.Tmp.enableAutoSizing = false; } catch { /* */ }
                    }
                    cell.Go = go;
                    // KEYS3 polish: a hover cell gets the kit's pointer trigger (the one buttons already
                    // carry elsewhere); the handle wires it to the cell's slot.
                    object? cellEnter = null, cellExit = null;
                    if (spec.Hover) EnsurePointerEvents(go, out cellEnter, out cellExit);
                    cell.Widget = new UnityWidget(null, go, FindClickEvent(go), null, null, null, cellEnter, cellExit);
                    if (spec.Skin == CellSkin.Expander || spec.Skin == CellSkin.Dropdown)
                    {
                        // INSTALLSIDES1: the game's dropdown arrow — the whole face of an expander, the
                        // right-hand mark of a dropdown bar (PaintExpander / PaintDropdown).
                        var arrow = new GameObject(spec.Skin == CellSkin.Expander ? "expander-arrow" : "dropdown-arrow");
                        arrow.transform.SetParent(go.transform, false);
                        cell.Picture = TryAdd<Image>(arrow);
                        if (cell.Picture != null)
                        {
                            try { cell.Picture.preserveAspect = true; } catch { /* */ }
                            try { cell.Picture.raycastTarget = false; } catch { /* */ }
                            try { cell.Picture.maskable = true; } catch { /* */ }
                            try { cell.Picture.enabled = false; } catch { /* */ }
                        }
                    }
                    if (spec.Skin == CellSkin.ResetIcon)
                    {
                        // The game's reset icon, drawn over a box-less button (PaintResetIcon).
                        var icon = new GameObject("reset-icon");
                        icon.transform.SetParent(go.transform, false);
                        InsetStretch(icon, 4f, 4f);
                        cell.Picture = TryAdd<Image>(icon);
                        if (cell.Picture != null)
                        {
                            try { cell.Picture.preserveAspect = true; } catch { /* */ }
                            try { cell.Picture.raycastTarget = false; } catch { /* */ }
                            try { cell.Picture.maskable = true; } catch { /* */ }
                            try { cell.Picture.enabled = false; } catch { /* */ }
                        }
                    }
                    PaintButton(cell);
                    break;
                }
                case CellKind.Input:
                {
                    // LIST3: a text field inside the row. The host object is the "row" the field
                    // builder parents to; the field is then stretched over it and PlaceCells sizes
                    // the host from the column model like every other cell.
                    var host = new GameObject("cell-input");
                    host.transform.SetParent(GameObject.transform, false);
                    var columns = MenuPanelLayout.SearchFieldColumns(Math.Max(CellLayout.MinInputWidth, CellLayout.AutoWidth(spec)));
                    var control = BuildInput(host, columns, spec.Text ?? "", placeholder: null, searchField: true);
                    var field = control == null ? null : FindIn<TMP_InputField>(control);
                    if (control == null || field == null || !InputOperable(field))
                    {
                        TryDestroyImmediate(control);
                        TryDestroyImmediate(host);
                        return;
                    }
                    Stretch(control);
                    Activate(control);
                    // DIALOGS1: a Prompt's maxLength. The typed-key pump reads characterLimit back
                    // (FieldMaxLength), so typing and pasting stop at it as TMP's own input would.
                    if (spec.MaxLength > 0)
                    {
                        try { field.characterLimit = spec.MaxLength; } catch { /* */ }
                    }
                    try { if (field.text != (spec.Text ?? "")) field.text = spec.Text ?? ""; } catch { /* */ }
                    AttachCellPlaceholder(control, field, spec.Placeholder, spec.Text ?? "");
                    PaintSearchField(control, field);
                    if (spec.Skin == CellSkin.ValueBox) PaintValueBoxField(control, field);
                    BringSearchHintToFront(control);
                    // SEARCHCLEAR1: a search box (the Install search) draws the clear button while it has text.
                    if (spec.Clearable && spec.EditorLines <= 0) AttachSearchClear(control, field);
                    // Per keystroke, like the Mods search: the consumer filters in place.
                    object textEvent = LiveTextEvent(field);
                    ArmTextField(host, field);
                    // TEXTEXPAND1: the expanded editor (multi-line, its own scroll bar, maybe read-only).
                    if (spec.EditorLines > 0) MakeEditorField(control, field, spec.ReadOnly);
                    cell.Field = field;
                    cell.Fill = control.GetComponent<Image>();
                    cell.Go = host;
                    cell.Widget = new UnityWidget(null, control, null, null, null, textEvent, null, null);
                    break;
                }
                case CellKind.Switch:
                {
                    // SETTINGS1 stage 2: a switch drawn as a track and a knob. The knob sits right when
                    // the cell reads On; the track takes the cell's tone. No Button: pressing a switch
                    // that cannot act would be a fake control (the Button cell kind is for that).
                    var go = new GameObject("cell-switch");
                    go.transform.SetParent(GameObject.transform, false);
                    cell.Fill = TryAdd<Image>(go);
                    if (cell.Fill != null)
                    {
                        try { cell.Fill.raycastTarget = false; } catch { /* */ }
                        try { cell.Fill.maskable = true; } catch { /* */ }
                    }
                    cell.Rim = AddRim(go, MenuPalette.ButtonBorder);
                    var knob = new GameObject("switch-knob");
                    knob.transform.SetParent(go.transform, false);
                    cell.Picture = TryAdd<Image>(knob);
                    if (cell.Picture != null)
                    {
                        try { cell.Picture.raycastTarget = false; } catch { /* */ }
                        try { cell.Picture.maskable = true; } catch { /* */ }
                        try { cell.Picture.color = ToColor(MenuPalette.Text); } catch { /* */ }
                    }
                    cell.Go = go;
                    PaintSwitch(cell);
                    // SETTINGS1 stage 3: the switch that works is still a track and knob, and takes
                    // the click itself (never a button captioned On/Off).
                    if (spec.Clickable && cell.Fill != null)
                    {
                        try { cell.Fill.raycastTarget = true; } catch { /* */ }
                        var button = TryAdd<Button>(go);
                        if (button != null)
                        {
                            try { button.targetGraphic = cell.Fill; } catch { /* */ }
                            try
                            {
                                var colors = button.colors;
                                colors.normalColor = Color.white;
                                colors.highlightedColor = new Color(1.2f, 1.2f, 1.2f, 1f);
                                colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                                colors.selectedColor = Color.white;
                                colors.colorMultiplier = 1f;
                                colors.fadeDuration = 0f;
                                button.colors = colors;
                            }
                            catch { /* */ }
                            cell.Widget = new UnityWidget(null, go, FindClickEvent(go), null, null, null, null, null);
                        }
                    }
                    break;
                }
                case CellKind.Diamond:
                {
                    // SETTINGS1 stage 3: the Mods list's status diamond, a square turned 45 degrees.
                    // Images, not a glyph: the game font has no U+25C6. A filled diamond is one image;
                    // a hollow one is four thin edges inside the turned square, so the pane shows
                    // through it (a smaller square in the row color would not match a translucent row).
                    var go = new GameObject("cell-diamond");
                    go.transform.SetParent(GameObject.transform, false);
                    var turned = new GameObject("diamond");
                    turned.transform.SetParent(go.transform, false);
                    try
                    {
                        var rt = turned.GetComponent<RectTransform>() ?? turned.AddComponent<RectTransform>();
                        rt.anchorMin = new Vector2(0.5f, 0.5f);
                        rt.anchorMax = new Vector2(0.5f, 0.5f);
                        rt.sizeDelta = new Vector2(CellLayout.DiamondSide, CellLayout.DiamondSide);
                        rt.anchoredPosition = Vector2.zero;
                        rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
                    }
                    catch { /* */ }
                    cell.Fill = TryAdd<Image>(turned);
                    if (cell.Fill != null)
                    {
                        try { cell.Fill.raycastTarget = false; } catch { /* */ }
                        try { cell.Fill.maskable = true; } catch { /* */ }
                    }
                    cell.Go = go;
                    PaintDiamond(cell, turned);
                    break;
                }
                case CellKind.Check:
                {
                    // FORMDIALOG1: the game's Options checkbox (the settings grid's AddGridCheckbox look): the
                    // Checkbox_Normal frame and the checkmark sprite over it while checked. No raycast: the row's
                    // own click target takes the press anywhere on the line and flips it.
                    var go = new GameObject("cell-check");
                    go.transform.SetParent(GameObject.transform, false);
                    cell.Fill = TryAdd<Image>(go);
                    if (cell.Fill != null)
                    {
                        try { cell.Fill.raycastTarget = false; } catch { /* */ }
                        try { cell.Fill.maskable = true; } catch { /* */ }
                        try { cell.Fill.preserveAspect = true; } catch { /* */ }
                    }
                    var markGo = new GameObject("checkmark");
                    markGo.transform.SetParent(go.transform, false);
                    cell.Picture = TryAdd<Image>(markGo);
                    if (cell.Picture != null)
                    {
                        try { cell.Picture.raycastTarget = false; } catch { /* */ }
                        try { cell.Picture.maskable = true; } catch { /* */ }
                        try { cell.Picture.preserveAspect = true; } catch { /* */ }
                    }
                    cell.Go = go;
                    // The art arrives on idle frames: until it does the kit's box is drawn, and the paint is tried again.
                    if (!CheckSkinDone(cell)) RetrySkinLater(go, () => CheckSkinDone(cell));
                    break;
                }
                case CellKind.ScrollText:
                    BuildScrollText(cell);
                    break;
                case CellKind.Chevron:
                {
                    var go = MakeLabel(GameObject, InstallModsCopy.GlyphExpand, Vector2.zero, spec.FontSize);
                    if (go == null) return;
                    go.name = "cell-chevron";
                    cell.Tmp = go.GetComponent<TextMeshProUGUI>();
                    cell.Go = go;
                    PrepareText(cell, CellAlign.Center);
                    ApplyTextTone(cell, CellTone.Muted);
                    // MODPAGE2 (live 2026-10-09: an expanded group's chevron drew above its row's line, at the right): the
                    // glyph turns about its own center, so it is centered both ways in a center-pivoted rect and stays on the
                    // row's line whichever way it points.
                    try
                    {
                        if (cell.Tmp != null)
                        {
                            cell.Tmp.horizontalAlignment = HorizontalAlignmentOptions.Center;
                            cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle;
                        }

                        var crt = go.GetComponent<RectTransform>();
                        if (crt != null) crt.pivot = new Vector2(0.5f, 0.5f);
                    }
                    catch { /* the glyph keeps the label's alignment */ }
                    break;
                }
                default:
                {
                    var go = MakeLabel(GameObject, "", Vector2.zero, spec.FontSize, left: spec.Align != CellAlign.Center, muted: spec.Kind == CellKind.Muted);
                    if (go == null) return;
                    go.name = "cell-" + UiCellRowHandle.KindName(spec.Kind);
                    cell.Tmp = go.GetComponent<TextMeshProUGUI>();
                    cell.Go = go;
                    // Three homes (live polish 2026-10-06): a group title on a body row is set in small caps like the
                    // header captions, so every group title reads alike whatever its case ("UI" drew unlike the others).
                    if (spec.SmallCaps && cell.Tmp != null)
                    {
                        try { cell.Tmp.fontStyle = (cell.Tmp.fontStyle & ~FontStyles.UpperCase) | FontStyles.SmallCaps; } catch { /* */ }
                    }
                    PrepareText(cell, spec.Align);
                    WriteText(cell, 0f);
                    ApplyTextTone(cell, spec.Tone);
                    if (UiCellRowHandle.IsLinked(spec)) MakeLinkTarget(cell, go, cell.Tmp);
                    break;
                }
            }
        }

        /// <summary>
        /// JOINLINKS1: a linked cell takes clicks: its own graphic (the TMP, or the scroll block's face)
        /// becomes the raycast target and a Button with an unchanging tint carries the click slot. Which
        /// link was hit is asked afterwards (<see cref="LinkAt"/>).
        /// </summary>
        private static void MakeLinkTarget(Cell cell, GameObject go, Graphic? target)
        {
            if (target == null) return;
            try { target.raycastTarget = true; } catch { /* */ }
            var button = TryAdd<Button>(go);
            if (button == null) return;
            try { button.targetGraphic = target; } catch { /* */ }
            try
            {
                var colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = Color.white;
                colors.pressedColor = Color.white;
                colors.selectedColor = Color.white;
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0f;
                button.colors = colors;
            }
            catch { /* */ }
            cell.Widget = new UnityWidget(null, go, FindClickEvent(go), null, null, null, null, null);
        }

        /// <summary>
        /// JOINLINKS1: the link of linked cell <paramref name="key"/> under the pointer, from TMP's own hit
        /// test (<c>TMP_TextUtilities.FindIntersectingLink</c>; the overlay canvas needs no camera). TMP numbers
        /// a text's links in the order its <c>link</c> tags appear, which is <see cref="UiLinks.Markup"/>'s
        /// order; a scroll block adds the links of the lines above.
        /// </summary>
        public int LinkAt(string key)
        {
            if (key == null || !_cells.TryGetValue(key, out var cell) || !UiCellRowHandle.IsLinked(cell.Spec)) return -1;
            var pointer = SamplePointer();
            if (pointer.Kind != PointerKind.Inside) return -1;
            var at = new Vector3(pointer.X, pointer.Y, 0f);
            if (cell.Spec.Kind == CellKind.ScrollText)
            {
                int before = 0;
                var lines = cell.Spec.Lines;
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Kind != CellLineKind.Body) continue;
                    int count = UiLinks.Find(lines[i].Text).Count;
                    if (count > 0 && i < cell.Lines.Count && cell.Lines[i] != null)
                    {
                        int hit = -1;
                        try { if (cell.Lines[i].gameObject.activeInHierarchy) hit = TMP_TextUtilities.FindIntersectingLink(cell.Lines[i], at, null); }
                        catch { hit = -1; }
                        if (hit >= 0 && hit < count) return before + hit;
                    }

                    before += count;
                }

                return -1;
            }

            if (cell.Tmp == null) return -1;
            int n = UiLinks.Find(cell.Spec.Text).Count;
            int found;
            try { found = TMP_TextUtilities.FindIntersectingLink(cell.Tmp, at, null); }
            catch { found = -1; }
            return found >= 0 && found < n ? found : -1;
        }

        private void BuildHeaderCell(Cell cell)
        {
            var spec = cell.Spec;
            var go = new GameObject("cell-header");
            go.transform.SetParent(GameObject.transform, false);
            if (!string.IsNullOrEmpty(spec.SortKey))
            {
                var hit = TryAdd<Image>(go);
                if (hit != null)
                {
                    try { hit.color = new Color(0f, 0f, 0f, 0.01f); } catch { /* */ }
                    try { hit.raycastTarget = true; } catch { /* */ }
                    try { hit.maskable = true; } catch { /* */ }
                }
                var button = TryAdd<Button>(go);
                if (button != null)
                {
                    try { button.targetGraphic = hit; } catch { /* */ }
                    try
                    {
                        var colors = button.colors;
                        colors.normalColor = Color.white;
                        colors.highlightedColor = Color.white;
                        colors.pressedColor = Color.white;
                        button.colors = colors;
                    }
                    catch { /* */ }
                }
                cell.Widget = new UnityWidget(null, go, FindClickEvent(go), null, null, null, null, null);
            }
            float headerSize = spec.SmallCaps && spec.FontSizeOverride > 0f ? spec.FontSizeOverride : CellLayout.HeaderFontSize;
            var label = MakeLabel(go, "", Vector2.zero, headerSize, left: spec.Align != CellAlign.Center);
            if (label != null)
            {
                Stretch(label);
                cell.Tmp = label.GetComponent<TextMeshProUGUI>();
                try { if (cell.Tmp != null) cell.Tmp.raycastTarget = false; } catch { /* */ }
                if (spec.SmallCaps && cell.Tmp != null)
                {
                    // VANILLA1: the game's menu tabs draw mixed-case words in small caps.
                    try { cell.Tmp.fontStyle = (cell.Tmp.fontStyle & ~FontStyles.UpperCase) | FontStyles.SmallCaps; } catch { /* */ }
                    try { cell.Tmp.fontSize = headerSize; } catch { /* */ }
                }
            }
            cell.Go = go;
            PrepareText(cell, spec.Align);
            WriteText(cell, 0f);
            ApplyTextTone(cell, CellTone.Muted);
            // INSTALLSIDES2: a side column's caption takes its column's light text on the tinted band.
            if (spec.Column != CellColumn.None && cell.Tmp != null)
            {
                try { cell.Tmp.color = ToColor(MenuPalette.ColumnButtonText(spec.Column)); } catch { /* */ }
            }
        }

        /// <summary>
        /// The empty-field hint of an input cell: the placeholder word under a root named
        /// <see cref="MenuRowStyle.SearchHintName"/>, which is the object <c>SyncSearchHint</c>
        /// hides once the player types (the typed-key pump calls it for every armed field).
        /// </summary>
        private static void AttachCellPlaceholder(GameObject control, TMP_InputField field, string? placeholder, string initial)
        {
            if (control == null || field == null || string.IsNullOrEmpty(placeholder)) return;
            GameObject? root = null;
            try
            {
                root = new GameObject(MenuRowStyle.SearchHintName);
                root.transform.SetParent(control.transform, false);
                Stretch(root);
                AttachPlaceholder(root, field, placeholder!);
                bool show = MenuRowStyle.SearchHintVisible(initial);
                try { root.SetActive(show); } catch { /* */ }
                try { root.transform.SetAsLastSibling(); } catch { /* */ }
            }
            catch
            {
                TryDestroyImmediate(root);
            }
        }

        private static Outline? AddRim(GameObject go, PaletteColor color)
        {
            Outline? outline = null;
            try { outline = go.AddComponent<Outline>(); } catch { outline = null; }
            if (outline == null) return null;
            try { outline.effectColor = ToColor(color); } catch { /* */ }
            try { outline.effectDistance = new Vector2(1f, -1f); } catch { /* */ }
            try { outline.useGraphicAlpha = false; } catch { /* */ }
            return outline;
        }

        private static void StretchHalf(GameObject go, bool left)
        {
            try
            {
                var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(left ? 0f : 0.5f, 0f);
                rt.anchorMax = new Vector2(left ? 0.5f : 1f, 1f);
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
            }
            catch { /* */ }
        }

        // ---- C5: inline overlays (the Mods list's Works across versions icon) ---------------------------

        /// <summary>The visible inline overlay drawn on <paramref name="targetKey"/>'s second line, or null.</summary>
        private CellSpec? InlineOverlayOf(string targetKey)
        {
            foreach (var c in _spec.Cells)
            {
                if (c.Visible && c.InlineAfter >= 0 && string.Equals(c.OverlayOf, targetKey, StringComparison.Ordinal))
                    return c;
            }

            return null;
        }

        /// <summary>The room an inline overlay takes on the line: its own width, as the column model sizes an overlay.</summary>
        private static float InlineWidth(CellSpec overlay, float targetWidth)
        {
            float ask = overlay.Width.Mode == CellWidthMode.Px ? overlay.Width.Value : CellLayout.AutoWidth(overlay);
            ask = Math.Max(1f, ask);
            return targetWidth > 0f ? Math.Min(ask, targetWidth) : ask;
        }

        /// <summary>
        /// Move each inline overlay to its spot on its target's second line: x right after the text before it, measured
        /// with the drawn font (the column model's estimate when it cannot be measured), y centered on the second line. A
        /// target whose last write kept no room hides its overlay.
        /// </summary>
        private void PlaceInlineOverlays(CellPlacement[] placements)
        {
            for (int i = 0; i < placements.Length; i++)
            {
                var p = placements[i];
                if (!p.Visible || !_cells.TryGetValue(p.Key, out var cell) || cell.Go == null) continue;
                var spec = cell.Spec;
                if (spec.OverlayOf == null || spec.InlineAfter < 0) continue;
                if (!_cells.TryGetValue(spec.OverlayOf, out var target) || target.Go == null || target.Tmp == null || !target.InlineShown)
                {
                    try { cell.Go.SetActive(false); } catch { /* */ }
                    continue;
                }

                CellPlacement? targetPlace = null;
                foreach (var t in placements)
                {
                    if (string.Equals(t.Key, spec.OverlayOf, StringComparison.Ordinal) && t.Visible) { targetPlace = t; break; }
                }

                if (targetPlace is not CellPlacement tp) continue;
                float measured = MeasureInlineNote(target.Tmp, target.InlinePrefix);
                float left = measured >= 0f
                    ? tp.Left + CellLayout.TextInset + measured
                    : p.Left;
                left = Math.Max(tp.Left, Math.Min(left, tp.Right - p.Width));
                float targetTop = 0f, targetHeight = Height;
                try
                {
                    var rt = target.Go.GetComponent<RectTransform>();
                    if (rt != null)
                    {
                        targetTop = -rt.anchoredPosition.y;
                        targetHeight = rt.sizeDelta.y;
                    }
                }
                catch { /* the row's own height */ }

                float h = spec.HeightOverride > 0f ? Math.Min(spec.HeightOverride, Height) : Math.Min(CellLayout.ImageSize, Height);
                float center = SecondLineCenter(target.Tmp, targetTop, targetHeight);
                float top = Math.Max(0f, Math.Min(Height - h, center - h * .5f));
                try { if (!cell.Go.activeSelf) cell.Go.SetActive(true); } catch { /* */ }
                PlaceTop(cell.Go, left + p.Width * .5f, top, p.Width, h);
            }
        }

        /// <summary>
        /// The second line's middle, from the row's top, of a middle-aligned two-line text placed <paramref name="top"/> down
        /// and <paramref name="height"/> tall: the block (both lines) centered in the rect, the note line at its bottom. An
        /// estimate from the kit's sizes when TMP cannot measure.
        /// </summary>
        private float SecondLineCenter(TextMeshProUGUI tmp, float top, float height)
        {
            try
            {
                var m = EnsureInlineMeasurer(tmp);
                if (m != null)
                {
                    float block = m.GetPreferredValues(tmp.text).y;
                    float note = m.GetPreferredValues(NoteSizeTag() + CaretMeasureMark + "</size>").y;
                    if (block > 1f && note > 1f && block <= height + note)
                        return top + (height - block) * .5f + block - note * .5f;
                }
            }
            catch { /* the estimate */ }

            return top + height * .5f + InlineSecondLineOffset;
        }

        /// <summary>
        /// The estimate of the second line's middle below a two-line cell's middle: half the title line (19 px at about 1.2)
        /// less half the note line (14 px at about 1.2).
        /// </summary>
        private const float InlineSecondLineOffset = 11.5f;

        private static string NoteSizeTag() =>
            "<size=" + CellLayout.TwoLineNoteFontSize.ToString("0.#", CultureInfo.InvariantCulture) + ">";

        /// <summary>The drawn width of <paramref name="prefix"/> on the second line (its size, the cell's font), or -1.</summary>
        private float MeasureInlineNote(TextMeshProUGUI tmp, string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return 0f;
            try
            {
                var m = EnsureInlineMeasurer(tmp);
                if (m == null) return -1f;
                string size = NoteSizeTag();
                float with = m.GetPreferredValues(size + Escape(prefix) + CaretMeasureMark + "</size>").x;
                float without = m.GetPreferredValues(size + CaretMeasureMark + "</size>").x;
                float w = with - without;
                return w >= 0f && !float.IsNaN(w) && !float.IsInfinity(w) ? w : -1f;
            }
            catch { return -1f; }
        }

        /// <summary>
        /// The row's hidden measuring label, copying <paramref name="source"/>'s font, size, style and spacing, so measuring
        /// never re-parses the drawn text (the caret measurer's rule).
        /// </summary>
        private TextMeshProUGUI? EnsureInlineMeasurer(TMP_Text source)
        {
            var m = _inlineMeasurer;
            bool alive;
            try { alive = m != null && m.gameObject != null; }
            catch { alive = false; }
            if (!alive)
            {
                m = null;
                _inlineMeasurer = null;
                GameObject? go = null;
                try
                {
                    go = new GameObject("inline-measure");
                    go.transform.SetParent(GameObject.transform, false);
                    var rt = go.GetComponent<RectTransform>() ?? go.AddComponent<RectTransform>();
                    rt.sizeDelta = Vector2.zero;
                    m = TryAdd<TextMeshProUGUI>(go);
                    if (m == null) { TryDestroyImmediate(go); return null; }
                    m.text = "";
                    m.raycastTarget = false;
                    m.richText = true;
                    m.enableWordWrapping = false;
                    m.color = new Color(1f, 1f, 1f, 0f);
                    _inlineMeasurer = m;
                }
                catch
                {
                    TryDestroyImmediate(go);
                    return null;
                }
            }

            try
            {
                if (m!.font != source.font) m.font = source.font;
                if (m.fontSize != source.fontSize) m.fontSize = source.fontSize;
                if (m.fontStyle != source.fontStyle) m.fontStyle = source.fontStyle;
                if (m.characterSpacing != source.characterSpacing) m.characterSpacing = source.characterSpacing;
                if (m.wordSpacing != source.wordSpacing) m.wordSpacing = source.wordSpacing;
                if (m.lineSpacing != source.lineSpacing) m.lineSpacing = source.lineSpacing;
            }
            catch { /* measures with what it has */ }
            return m;
        }

        /// <summary>
        /// C5: a press on a hover-only cell goes to its row. The cell's pointer trigger would take the click (an
        /// <c>EventTrigger</c> answers every pointer event), so a PointerClick row on it is wired through a Bootstrap
        /// trampoline (the PointerEnter kind, as <see cref="UiPointerWatch"/> does) to the row button's own click.
        /// </summary>
        private void ForwardClickToRow(GameObject go)
        {
            ushort id = 0;
            try
            {
                var trigger = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
                var entry = NewTrigger(EventTriggerType.PointerClick);
                if (trigger == null || entry == null) return;
                id = UiSlotTable.Alloc(null);
                var row = GameObject;
                UiSlotTable.BindPointerEnter(id, () =>
                {
                    try
                    {
                        var button = row != null ? row.GetComponent<Button>() : null;
                        if (button != null && button.interactable) button.onClick.Invoke();
                    }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row click from a hover cell: " + e.Message); }
                });
                trigger.triggers.Add(entry);
                if (!BootstrapLink.TryWireUi(id, (byte)UiSlotKind.PointerEnter, entry.callback))
                {
                    UiSlotTable.Release(id);
                    return;
                }

                _forwardSlots.Add(id);
            }
            catch (Exception e)
            {
                if (id != 0) UiSlotTable.Release(id);
                Plugin.Logger?.LogWarning("Ui: hover cell click pass-through: " + e.Message);
            }
        }

        /// <summary>C5: the loop-arrows glyph on <paramref name="picture"/>: right end of the cell, centered, muted.</summary>
        private static void PaintLoopGlyph(Cell cell, GameObject picture)
        {
            try
            {
                var rt = picture.GetComponent<RectTransform>() ?? picture.AddComponent<RectTransform>();
                rt.anchorMin = new Vector2(1f, 0.5f);
                rt.anchorMax = new Vector2(1f, 0.5f);
                rt.pivot = new Vector2(1f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = new Vector2(CellLayout.LoopGlyphWidth, CellLayout.LoopGlyphHeight);
            }
            catch { /* */ }
            if (cell.Picture == null) return;
            var sprite = LoopGlyphSprite();
            if (sprite == null) return;
            try { cell.Picture.sprite = sprite; } catch { /* */ }
            try { cell.Picture.color = ToColor(MenuPalette.TextColor(cell.Spec.Tone)); } catch { /* */ }
            try { cell.Picture.preserveAspect = false; } catch { /* */ }
            try { cell.Picture.enabled = true; } catch { /* */ }
        }

        private static void PrepareText(Cell cell, CellAlign align)
        {
            var tmp = cell.Tmp;
            if (tmp == null) return;
            var spec = cell.Spec;
            try
            {
                tmp.horizontalAlignment = align switch
                {
                    CellAlign.Right => HorizontalAlignmentOptions.Right,
                    CellAlign.Center => HorizontalAlignmentOptions.Center,
                    _ => HorizontalAlignmentOptions.Left,
                };
            }
            catch { /* */ }
            bool wrapping = spec.Wrap == CellWrap.Wrap;
            try
            {
                tmp.verticalAlignment = wrapping || spec.VAlign == CellVAlign.Top
                    ? VerticalAlignmentOptions.Top
                    : VerticalAlignmentOptions.Middle;
            }
            catch { /* */ }
            try { tmp.enableWordWrapping = wrapping; } catch { /* */ }
            // KIT1: a wrapping cell must keep its lines, so it gets TMP's own truncate-at-the-bottom
            // rather than the one-line ellipsis clamp every measured caption gets.
            if (wrapping)
            {
                try { tmp.overflowMode = TextOverflowModes.Ellipsis; } catch { /* */ }
            }
            else if (spec.Wrap == CellWrap.Glyph)
            {
                // RADIOCLICK2: a glyph is drawn whole; the ellipsis clamp would replace it with nothing.
                try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
            }
            else ClampCaptionText(tmp.gameObject);
            try { tmp.richText = true; } catch { /* */ }
            try { tmp.raycastTarget = false; } catch { /* */ }
            try { tmp.enableAutoSizing = false; } catch { /* */ }
            try { tmp.margin = new Vector4(2f, 0f, 2f, 0f); } catch { /* */ }
        }

        /// <summary>
        /// The TMP string for a cell: mono cells under an <c>mspace</c> run (tabular figures on
        /// the body face; no game font is monospaced), two-line cells as title + a smaller muted
        /// note, each line clipped to the placed width by the same estimate the column model
        /// uses, header cells uppercase with the sort indicator.
        /// </summary>
        private void WriteText(Cell cell, float width)
        {
            var tmp = cell.Tmp;
            if (tmp == null) return;
            var spec = cell.Spec;
            string text = spec.Text ?? "";
            string body;
            if (_header)
            {
                bool sorted = _sortedKey != null && string.Equals(spec.SortKey, _sortedKey, StringComparison.Ordinal);
                string caption = CellLayout.HeaderCaption(text, sorted, _sortAscending, spec.SmallCaps);
                string indicator = _sortAscending ? CellLayout.SortIndicatorAscending : CellLayout.SortIndicator;
                body = sorted
                    ? Escape(caption.Substring(0, caption.Length - indicator.Length))
                        + "<color=" + Hex(MenuPalette.HeaderSorted) + ">" + indicator + "</color>"
                    : Escape(caption);
            }
            else if (spec.Kind == CellKind.Mono)
            {
                // KIT1 gen 1: a meta cell's leading `· ` stays outside the mspace run and keeps the
                // meta line's muted color. The value is tabular figures; the dot between two values
                // is punctuation, and inside the run it would be drawn at a figure's width.
                string value = text;
                string lead = HeadBlockLayout.TrySplitMetaSeparator(text, out value) ? MutedSeparator() : "";
                // WINDOWS1 live fix: only the digits are tabular; a comma or a slash keeps its own width (CellLayout.MonoMarkup).
                body = lead + CellLayout.MonoMarkup(value);
            }
            else if (spec.Wrap == CellWrap.TwoLine)
            {
                string title = text;
                string note = "";
                int nl = text.IndexOf('\n');
                if (nl >= 0)
                {
                    title = text.Substring(0, nl);
                    note = text.Substring(nl + 1).Replace('\n', ' ');
                }
                // INSTALLCOLS1: a toned lead before the muted note (the Install list's update note).
                string lead = spec.NoteLead ?? "";
                if (lead.Length > 0 && note.Length > 0) lead += " · ";
                // C5: an inline overlay's room on the second line (the Mods list's Works across versions icon).
                var inline = InlineOverlayOf(spec.Key);
                float room = inline != null ? InlineWidth(inline, width) : 0f;
                int at = -1;
                if (width > 0f)
                {
                    title = MenuPanelLayout.ClipLabel(title, width - 4f, CellLayout.TwoLineTitleFontSize);
                    // INSTALLSIDES1: an overlay over the right end of the second line keeps that width free.
                    float noteWidth = CellLayout.TwoLineNoteWidth(spec, width);
                    string line = inline != null
                        ? CellLayout.InlineNoteLine(lead, note, inline.InlineAfter, noteWidth, room, out at)
                        : MenuPanelLayout.ClipLabel(lead + note, noteWidth, CellLayout.TwoLineNoteFontSize);
                    if (line.Length >= lead.Length && line.StartsWith(lead, StringComparison.Ordinal))
                        note = line.Substring(lead.Length);
                    else
                    {
                        lead = line;
                        note = "";
                    }
                }
                // The room goes inside the note (never inside the toned lead).
                if (at < lead.Length || at > lead.Length + note.Length) at = -1;
                cell.InlineShown = at >= 0;
                cell.InlinePrefix = at >= 0 ? (lead + note).Substring(0, at) : "";
                body = GlyphPrefix(tmp, spec.Glyph) + (spec.Strike ? "<s>" + Escape(title) + "</s>" : Escape(title));
                string size = "<size=" + CellLayout.TwoLineNoteFontSize.ToString("0.#", CultureInfo.InvariantCulture) + ">";
                if (lead.Length > 0 || note.Length > 0)
                {
                    body += "\n" + size;
                    if (lead.Length > 0)
                        body += "<color=" + Hex(MenuPalette.TextColor(spec.NoteLeadTone)) + ">" + Escape(lead) + "</color>";
                    if (note.Length > 0)
                    {
                        string drawnNote = Escape(note);
                        if (at >= 0)
                        {
                            int k = at - lead.Length;
                            drawnNote = Escape(note.Substring(0, k))
                                + "<space=" + room.ToString("0.##", CultureInfo.InvariantCulture) + ">"
                                + Escape(note.Substring(k));
                        }
                        body += "<color=" + Hex(MenuPalette.Muted) + ">" + drawnNote + "</color>";
                    }
                    body += "</size>";
                }
            }
            else if (UiCellRowHandle.IsLinked(spec))
            {
                // JOINLINKS1: the text escaped as always, each link inside the kit's own link/color/u tags.
                body = GlyphPrefix(tmp, spec.Glyph) + UiLinks.Markup(text);
            }
            else if (HeadBlockLayout.TrySplitMetaSeparator(text, out string plain))
            {
                // Same rule on a text / muted meta cell: the dot is the meta line's, not the value's.
                body = MutedSeparator() + Escape(plain);
            }
            else
            {
                // RADIOCLICK2: a glyph cell draws the first form of its glyph the font has.
                body = spec.Wrap == CellWrap.Glyph ? Escape(DrawableGlyph(tmp, text))
                    : GlyphPrefix(tmp, spec.Glyph) + (spec.Strike ? "<s>" + Escape(text) + "</s>" : Escape(text));
            }
            try { tmp.text = body; } catch { /* */ }
        }

        /// <summary>The meta line's leading separator, in <see cref="MenuPalette.Muted"/>.</summary>
        private static string MutedSeparator() =>
            "<color=" + Hex(MenuPalette.Muted) + ">" + HeadBlockLayout.MetaSeparator + " </color>";

        private static string Escape(string text) => UiRichText.Literal(text);

        private static string Hex(PaletteColor c) =>
            "#" + ((int)Math.Round(c.R * 255f)).ToString("X2") + ((int)Math.Round(c.G * 255f)).ToString("X2") + ((int)Math.Round(c.B * 255f)).ToString("X2");

        private static void ApplyTextTone(Cell cell, CellTone tone)
        {
            if (cell.Tmp == null) return;
            // FORMDIALOG1: a cell's own text color (a form check's label color) replaces its own tone's, not another tone.
            if (cell.Spec.TextColor is { } own && tone == cell.Spec.Tone)
            {
                try { cell.Tmp.color = ToColor(own); }
                catch { /* */ }
                return;
            }

            try { cell.Tmp.color = ToColor(cell.Spec.Kind == CellKind.Mono && tone == CellTone.Normal ? MenuPalette.Mono : MenuPalette.TextColor(tone)); }
            catch { /* */ }
        }

        /// <summary>
        /// FORMDIALOG1: a check cell from its spec: the game's checkbox frame and checkmark when they are ready (else the
        /// kit's framed box and a filled square), the mark shown while the text is <see cref="CellLayout.CheckOn"/>, the
        /// whole box half-transparent in the disabled tone. True when the game's art drew (no retry needed).
        /// </summary>
        /// <summary>
        /// FORMDIALOG1: paint the check cell, and whether a retry is still worth it: done when the game's art drew, or when
        /// the art's source is final (the kit's look, or nothing found once the world's preparation is over).
        /// </summary>
        private static bool CheckSkinDone(Cell cell) =>
            PaintCheck(cell)
            || (GameArt.SettingsArtDonors.PartFinal(GameArt.SettingsArtPart.CheckboxFrame)
                && GameArt.SettingsArtDonors.PartFinal(GameArt.SettingsArtPart.CheckboxMark));

        private static bool PaintCheck(Cell cell)
        {
            bool on = string.Equals(cell.Spec.Text, CellLayout.CheckOn, StringComparison.Ordinal);
            float alpha = cell.Spec.Tone == CellTone.Disabled ? 0.45f : 1f;
            bool framed = false, marked = false;
            var frame = cell.Fill;
            if (frame != null)
            {
                framed = PaintPart(frame, GameArt.SettingsArtPart.CheckboxFrame, new Color(1f, 1f, 1f, alpha), Grey(0.16f, alpha));
                if (!framed) ApplyFrame(frame, Grey(0.16f, alpha));
            }

            var mark = cell.Picture;
            if (mark != null)
            {
                marked = PaintPart(mark, GameArt.SettingsArtPart.CheckboxMark, new Color(1f, 1f, 1f, alpha), Grey(0.92f, alpha));
                try
                {
                    var rt = mark.rectTransform;
                    float size = marked ? CellLayout.CheckMarkSize : Math.Max(6f, CellLayout.CheckWidth * 0.4f);
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = new Vector2(size, size);
                }
                catch { /* */ }
                try { mark.enabled = on; } catch { /* */ }
            }

            return framed && marked;
        }

        /// <summary>SETTINGS1 stage 2: the track's tone and the knob's side from the cell's spec.</summary>
        private static void PaintSwitch(Cell cell)
        {
            bool on = string.Equals(cell.Spec.Text, ModFrameworkLine.SwitchOnCaption, StringComparison.Ordinal);
            try { if (cell.Fill != null) cell.Fill.color = ToColor(on ? MenuPalette.Ok : MenuPalette.Disabled); } catch { /* */ }
            if (cell.Picture == null) return;
            try
            {
                var rt = cell.Picture.rectTransform;
                const float inset = 3f;
                rt.anchorMin = new Vector2(on ? 0.5f : 0f, 0f);
                rt.anchorMax = new Vector2(on ? 1f : 0.5f, 1f);
                rt.offsetMin = new Vector2(inset, inset);
                rt.offsetMax = new Vector2(-inset, -inset);
            }
            catch { /* */ }
        }

        /// <summary>SETTINGS1 stage 3: fill or outline the diamond in the cell's tone.</summary>
        private static void PaintDiamond(Cell cell, GameObject? turned = null)
        {
            try
            {
                turned ??= cell.Go != null && cell.Go.transform.childCount > 0 ? cell.Go.transform.GetChild(0).gameObject : null;
                if (turned == null) return;
                bool hollow = string.Equals(cell.Spec.Text, ModsListLayout.DiamondHollow, StringComparison.Ordinal);
                var color = ToColor(ModsListLayout.DiamondColor(cell.Spec.Tone));
                if (cell.Fill != null)
                {
                    try { cell.Fill.color = hollow ? new Color(0f, 0f, 0f, 0f) : color; } catch { /* */ }
                }

                // The four edges of a hollow diamond, made once and shown only while hollow.
                var edgeNames = new[] { "edge-top", "edge-bottom", "edge-left", "edge-right" };
                for (int i = 0; i < edgeNames.Length; i++)
                {
                    var existing = turned.transform.Find(edgeNames[i]);
                    GameObject? edge = existing != null ? existing.gameObject : null;
                    if (edge == null && hollow)
                    {
                        edge = new GameObject(edgeNames[i]);
                        edge.transform.SetParent(turned.transform, false);
                        var img = TryAdd<Image>(edge);
                        if (img != null)
                        {
                            try { img.raycastTarget = false; } catch { /* */ }
                            try { img.maskable = true; } catch { /* */ }
                        }

                        try
                        {
                            var rt = edge.GetComponent<RectTransform>() ?? edge.AddComponent<RectTransform>();
                            float t = CellLayout.DiamondOutline;
                            switch (i)
                            {
                                case 0: rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.sizeDelta = new Vector2(0f, t); rt.pivot = new Vector2(0.5f, 1f); break;
                                case 1: rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.sizeDelta = new Vector2(0f, t); rt.pivot = new Vector2(0.5f, 0f); break;
                                case 2: rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f); rt.sizeDelta = new Vector2(t, 0f); rt.pivot = new Vector2(0f, 0.5f); break;
                                default: rt.anchorMin = new Vector2(1f, 0f); rt.anchorMax = new Vector2(1f, 1f); rt.sizeDelta = new Vector2(t, 0f); rt.pivot = new Vector2(1f, 0.5f); break;
                            }
                            rt.anchoredPosition = Vector2.zero;
                        }
                        catch { /* */ }
                    }

                    if (edge == null) continue;
                    try { edge.SetActive(hollow); } catch { /* */ }
                    try
                    {
                        var img = edge.GetComponent<Image>();
                        if (img != null) img.color = color;
                    }
                    catch { /* */ }
                }
            }
            catch { /* a status mark must never cost the row */ }
        }

        private static void PaintBadge(Cell cell)
        {
            var (fill, border, text) = MenuPalette.Badge(cell.Spec.Tone);
            var right = MenuPalette.BadgeRightFill(cell.Spec.Tone);
            try { if (cell.Fill != null) cell.Fill.color = ToColor(fill); } catch { /* */ }
            try { if (cell.FillRight != null) cell.FillRight.color = ToColor(right); } catch { /* */ }
            try { if (cell.Rim != null) cell.Rim.effectColor = ToColor(border); } catch { /* */ }
            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = string.IsNullOrEmpty(cell.Spec.Text) ? MenuPalette.SideCaption(cell.Spec.Tone) : Escape(cell.Spec.Text); } catch { /* */ }
                try { cell.Tmp.color = ToColor(text); } catch { /* */ }
                try { cell.Tmp.horizontalAlignment = HorizontalAlignmentOptions.Center; } catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                try { cell.Tmp.enableWordWrapping = false; } catch { /* */ }
                ClampCaptionText(cell.Tmp.gameObject);
                try { cell.Tmp.raycastTarget = false; } catch { /* */ }
            }
        }

        private static void PaintButton(Cell cell)
        {
            // HUB1: the game's window tab keeps its own art and state machine; without a donor it is the chip.
            if (cell.Spec.Skin == CellSkin.GameTab && (PaintGameTabCell(cell.Go, cell.Tmp, cell.Spec) || PaintChip(cell))) return;
            if (cell.Spec.Skin == CellSkin.KeyCap)
            {
                if (PaintKeyCap(cell)) return;
                // CONTROLS1 round 2: drawn as the kit button now, as the game's when the art arrives.
                var retryCell = cell;
                RetrySkinLater(cell.Go, () => retryCell.Fill == null || PaintKeyCap(retryCell));
            }
            if (cell.Spec.Skin == CellSkin.ResetIcon && PaintResetIcon(cell)) return;
            if (cell.Spec.Skin == CellSkin.PopupButton && PaintPopupButton(cell)) return;
            if (cell.Spec.Skin == CellSkin.Chip && PaintChip(cell)) return;
            if (cell.Spec.Skin == CellSkin.Expander && PaintExpander(cell)) return;
            if (cell.Spec.Skin == CellSkin.Dropdown && PaintDropdown(cell)) return;
            if (cell.Spec.Skin == CellSkin.Quiet && PaintQuiet(cell)) return;
            if (cell.Spec.Skin == CellSkin.Link && PaintLink(cell)) return;
            if (cell.Spec.Skin == CellSkin.KeyCap && cell.Tmp != null)
            {
                try { cell.Tmp.fontSize = cell.Spec.FontSize; } catch { /* */ }
            }
            var (fill, border, text) = MenuPalette.Button(cell.Spec.Tone);
            if (cell.Fill != null)
            {
                try { cell.Fill.sprite = null; } catch { /* */ }
                try { cell.Fill.color = ToColor(fill); } catch { /* */ }
                // BUTTONART1: a key cap waiting for the game's button art (retried above) shows the game
                // buttons' steel face, not the kit's dark red; the tone keeps its frame and caption.
                if (cell.Spec.Skin == CellSkin.KeyCap)
                {
                    var steel = GameArt.GameSettingsLook.NeutralButtonFill;
                    try { cell.Fill.color = new Color(steel.R, steel.G, steel.B, steel.A); } catch { /* */ }
                }
            }
            try { if (cell.Rim != null) cell.Rim.effectColor = ToColor(border); } catch { /* */ }
            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                try { cell.Tmp.color = ToColor(text); } catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
            }
            try
            {
                var button = cell.Go != null ? cell.Go.GetComponent<Button>() : null;
                if (button != null)
                {
                    button.interactable = cell.Spec.Tone != CellTone.Disabled;
                    var colors = button.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                    colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                    colors.disabledColor = Color.white;
                    colors.colorMultiplier = 1f;
                    button.colors = colors;
                }
            }
            catch { /* */ }
        }

        /// <summary>
        /// KEYS3: a key cap drawn with the game's own key-cap sprite (<see cref="KeybindArtDonors"/>),
        /// at its own image type and color, with no kit rim. The tone colors only the caption (the
        /// kit's existing button caption colors), so a losing key reads warn without a new tint. False
        /// when no sprite was borrowed: the caller draws today's kit button.
        /// </summary>
        private static bool PaintKeyCap(Cell cell)
        {
            // CONTROLS1: either donor of SimpleMenuButton_Normal (the key cap, else the settings kit's copy).
            if (cell.Fill == null || !TryGameButtonArt(out var sprite, out var type, out var color, out var ppu)) return false;
            try
            {
                cell.Fill.sprite = sprite;
                cell.Fill.type = type;
                cell.Fill.color = color;
                try { cell.Fill.pixelsPerUnitMultiplier = ppu; } catch { /* */ }
            }
            catch { return false; }
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            var (_, _, text) = MenuPalette.Button(cell.Spec.Tone);
            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                // KEYS3 live 2026-09-29: a long chord is drawn smaller before it is cut (KeybindsTabLayout.CapFontSize).
                try { cell.Tmp.fontSize = cell.Spec.FontSize; } catch { /* */ }
                try { cell.Tmp.color = ToColor(text); } catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
            }
            try
            {
                var button = cell.Go != null ? cell.Go.GetComponent<Button>() : null;
                if (button != null)
                {
                    button.interactable = cell.Spec.Tone != CellTone.Disabled;
                    var colors = button.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                    colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                    colors.disabledColor = Color.white;
                    colors.colorMultiplier = 1f;
                    button.colors = colors;
                }
            }
            catch { /* */ }
            return true;
        }

        /// <summary>
        /// DIALOGS2: the game's popup button — <c>SmallerButton_Normal</c> 9-sliced, swapped to its hover,
        /// pressed and disabled sprites by the Button (the game's SpriteSwap), a light caption (red for a
        /// destructive one). False when the art is not ready: the caller draws the kit button.
        /// </summary>
        private static bool PaintPopupButton(Cell cell)
        {
            if (cell.Fill == null
                || !GameArt.SettingsArtDonors.TryPart(GameArt.SettingsArtPart.PopupButton, out var sprite, out var type, out var ppu)
                || sprite == null)
                return false;
            try
            {
                cell.Fill.sprite = sprite;
                cell.Fill.type = type;
                try { cell.Fill.pixelsPerUnitMultiplier = ppu; } catch { /* */ }
                // INSTALLSIDES2 item 1: in a per-side column the game's own sprite is multiplied by the
                // column's hue (and dimmed when disabled), never swapped for a flat box.
                var column = cell.Spec.Column;
                var tint = column == CellColumn.None ? Color.white : ToColor(MenuPalette.ColumnButtonTint(column));
                if (column != CellColumn.None && cell.Spec.Tone == CellTone.Disabled)
                    tint = new Color(tint.r * 0.6f, tint.g * 0.6f, tint.b * 0.6f, 0.75f);
                cell.Fill.color = tint;
                PaintColumnOverlay(cell, sprite, type, ppu);
            }
            catch { return false; }
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = GlyphPrefix(cell.Tmp, cell.Spec.Glyph) + Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                try { cell.Tmp.fontSize = cell.Spec.FontSize; } catch { /* */ }
                try
                {
                    var columnText = cell.Spec.Column == CellColumn.None ? (Color?)null : ToColor(MenuPalette.ColumnButtonText(cell.Spec.Column));
                    cell.Tmp.color = cell.Spec.Tone == CellTone.Danger
                        ? Rgba(DialogLayout.DangerCaptionRgb)
                        : cell.Spec.Tone == CellTone.Disabled ? new Color(0.5f, 0.5f, 0.5f, 1f)
                        : columnText ?? new Color(0.898f, 0.898f, 0.898f, 1f);
                }
                catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
            }

            ApplyPopupStates(cell);
            return true;
        }

        /// <summary>
        /// DIALOGS2: the popup button's Button states — the game's hover, pressed and disabled sprites
        /// (SpriteSwap), or the game's color block when those were not borrowed.
        /// </summary>
        private static void ApplyPopupStates(Cell cell)
        {
            try
            {
                var button = cell.Go != null ? cell.Go.GetComponent<Button>() : null;
                if (button != null)
                {
                    button.interactable = cell.Spec.Tone != CellTone.Disabled;
                    button.targetGraphic = cell.Fill;
                    var hover = GameArt.SettingsArtDonors.TryPart(GameArt.SettingsArtPart.PopupButtonHover, out var h, out _, out _) ? h : null;
                    var pressed = GameArt.SettingsArtDonors.TryPart(GameArt.SettingsArtPart.PopupButtonPressed, out var p, out _, out _) ? p : null;
                    var disabled = GameArt.SettingsArtDonors.TryPart(GameArt.SettingsArtPart.PopupButtonDisabled, out var d, out _, out _) ? d : null;
                    if (hover != null)
                    {
                        var state = button.spriteState;
                        state.highlightedSprite = hover;
                        state.selectedSprite = hover;
                        if (pressed != null) state.pressedSprite = pressed;
                        if (disabled != null) state.disabledSprite = disabled;
                        button.spriteState = state;
                        button.transition = Selectable.Transition.SpriteSwap;
                    }
                    else
                    {
                        // The game's color block: highlighted .961, pressed .784.
                        var colors = button.colors;
                        colors.normalColor = Color.white;
                        colors.highlightedColor = new Color(0.961f, 0.961f, 0.961f, 1f);
                        colors.pressedColor = new Color(0.784f, 0.784f, 0.784f, 1f);
                        colors.disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f);
                        colors.colorMultiplier = 1f;
                        colors.fadeDuration = 0.1f;
                        button.colors = colors;
                        button.transition = Selectable.Transition.ColorTint;
                    }
                }
            }
            catch { /* */ }
        }

        /// <summary>
        /// VANILLA1: a filter chip on the game's popup button sprite (<see cref="CellSkin.Chip"/>). A
        /// selected chip has a warm plate inside the sprite and the bright caption; an unselected chip
        /// is the bare sprite with the game's resting caption gray; an attention chip (matches waiting)
        /// has an amber caption. The sprite's hover and pressed swaps are the game's. False when the
        /// art is not ready: the caller draws the kit chip.
        /// </summary>
        private static bool PaintChip(Cell cell)
        {
            if (cell.Fill == null
                || !GameArt.SettingsArtDonors.TryPart(GameArt.SettingsArtPart.PopupButton, out var sprite, out var type, out var ppu)
                || sprite == null)
                return false;
            bool on = cell.Spec.Tone == CellTone.ChipOn;
            try
            {
                cell.Fill.sprite = sprite;
                cell.Fill.type = type;
                try { cell.Fill.pixelsPerUnitMultiplier = ppu; } catch { /* */ }
                cell.Fill.color = Color.white;
            }
            catch { return false; }
            // Live 2026-10-14: at the column overlay's 8-unit side inset the gold plate left a dark band inside the chip's
            // rim on both sides while it met the rim top and bottom (3). A chip's plate fills the art evenly inside the rim.
            SetPlate(cell, on ? ToColor(MenuPalette.ChipOnPlate) : (Color?)null, UiChipsLayout.PlateInset);
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                try { cell.Tmp.fontSize = cell.Spec.FontSize; } catch { /* */ }
                try
                {
                    cell.Tmp.color = ToColor(on ? MenuPalette.ChipOnText
                        : cell.Spec.Tone == CellTone.ChipAttention ? MenuPalette.ChipAttentionBorder
                        : MenuPalette.ChipRestText);
                }
                catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                // P7b chips: a chip with an icon or a remove overlay keeps its caption between them; every other chip keeps
                // the symmetric button pad, so its caption is centered on the whole chip art.
                float half = CellLayout.ButtonPad * .5f;
                float left = cell.Spec.CaptionInsetLeft > 0f ? cell.Spec.CaptionInsetLeft : half;
                float right = cell.Spec.CaptionInsetRight > 0f ? cell.Spec.CaptionInsetRight : half;
                try { cell.Tmp.margin = new Vector4(left, 0f, right, 0f); } catch { /* */ }
            }
            ApplyPopupStates(cell);
            return true;
        }

        /// <summary>
        /// A solid plate inside a popup button (<see cref="InstallColumns.ButtonTintInsetX"/> /
        /// <see cref="InstallColumns.ButtonTintInsetY"/> from its edges), under the caption, so the
        /// sprite's decorated ends and rim stay visible around it with their swaps. Null hides it.
        /// </summary>
        private static void SetPlate(Cell cell, Color? color, float insetX = InstallColumns.ButtonTintInsetX)
        {
            try
            {
                if (color == null)
                {
                    if (cell.Tint != null) cell.Tint.gameObject.SetActive(false);
                    return;
                }

                if (cell.Tint == null && cell.Go != null)
                {
                    var go = new GameObject("column-tint");
                    go.transform.SetParent(cell.Go.transform, false);
                    // Under the caption: the first child draws first (over the button's own sprite).
                    try { go.transform.SetSiblingIndex(0); } catch { /* */ }
                    InsetStretch(go, InstallColumns.ButtonTintInsetX, InstallColumns.ButtonTintInsetY);
                    cell.Tint = TryAdd<Image>(go);
                    if (cell.Tint != null)
                    {
                        try { cell.Tint.raycastTarget = false; } catch { /* */ }
                        try { cell.Tint.maskable = true; } catch { /* */ }
                    }
                }

                if (cell.Tint == null) return;
                InsetStretch(cell.Tint.gameObject, insetX, InstallColumns.ButtonTintInsetY);
                cell.Tint.gameObject.SetActive(true);
                cell.Tint.sprite = null;
                cell.Tint.type = Image.Type.Simple;
                cell.Tint.color = color.Value;
            }
            catch { /* the sprite alone still draws */ }
        }

        /// <summary>
        /// KEYS3 polish: the game's reset icon on a box-less button, dimmed when the reset does not apply
        /// (the cell's Disabled tone). False when no icon was borrowed: the kit button draws its caption.
        /// </summary>
        /// <summary>
        /// INSTALLSIDES2 live review: the column's hue over a popup button — the button's own sprite
        /// again, in the column's color at partial alpha, under the caption — so a blue and a red
        /// button are unmistakable while the game's sprite, its rim and its hover / pressed swaps show
        /// through. Hidden on a button outside the side columns.
        /// </summary>
        private static void PaintColumnOverlay(Cell cell, Sprite sprite, Image.Type type, float ppu)
        {
            _ = sprite;
            _ = type;
            _ = ppu;
            var column = cell.Spec.Column;
            if (column == CellColumn.None)
            {
                SetPlate(cell, null);
                return;
            }

            // Live review 3: the overlay drawn with the button's own sprite took that sprite's alpha,
            // and the face of SmallerButton_Normal is mostly transparent, so the hue barely showed. It
            // is a plain plate, inset from the sprite's decorated ends and rim.
            var color = ToColor(MenuPalette.ColumnButtonOverlay(column));
            if (cell.Spec.Tone == CellTone.Disabled) color = new Color(color.r, color.g, color.b, color.a * 0.4f);
            SetPlate(cell, color);
        }

        private static bool PaintResetIcon(Cell cell)
        {
            if (cell.Picture == null || !KeybindArtDonors.TryResetIcon(out var sprite, out var color)) return false;
            bool enabled = cell.Spec.Tone != CellTone.Disabled;
            try
            {
                cell.Picture.sprite = sprite;
                cell.Picture.color = new Color(color.r, color.g, color.b, enabled ? color.a : color.a * KeybindsTabLayout.DisabledIconAlpha);
                cell.Picture.enabled = true;
            }
            catch { return false; }
            try { if (cell.Fill != null) { cell.Fill.sprite = null; cell.Fill.color = new Color(1f, 1f, 1f, 0f); } } catch { /* */ }
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            try { if (cell.Tmp != null) cell.Tmp.text = ""; } catch { /* */ }
            try
            {
                var button = cell.Go != null ? cell.Go.GetComponent<Button>() : null;
                if (button != null)
                {
                    button.interactable = enabled;
                    button.targetGraphic = cell.Picture;
                    var colors = button.colors;
                    colors.normalColor = Color.white;
                    colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                    colors.pressedColor = new Color(0.9f, 0.9f, 0.9f, 1f);
                    colors.disabledColor = Color.white;
                    colors.colorMultiplier = 1f;
                    button.colors = colors;
                }
            }
            catch { /* */ }
            return true;
        }

        /// <summary>
        /// INSTALLSIDES2 live review 3: the images a cell actually draws — its own and its
        /// children's, in drawing order — for the readback (<c>images</c>): name, sprite, color,
        /// enabled, active, sibling index and material.
        /// </summary>
        public IReadOnlyList<Dictionary<string, object?>> CellImages(string key)
        {
            var list = new List<Dictionary<string, object?>>();
            if (!_cells.TryGetValue(key, out var cell) || cell.Go == null) return list;
            void Add(Image? image, int sibling)
            {
                if (image == null) return;
                try
                {
                    var c = image.color;
                    list.Add(new Dictionary<string, object?>
                    {
                        ["name"] = image.gameObject.name,
                        ["sprite"] = image.sprite != null ? image.sprite.name : null,
                        ["color"] = "#" + ((int)Math.Round(c.r * 255f)).ToString("X2") + ((int)Math.Round(c.g * 255f)).ToString("X2")
                            + ((int)Math.Round(c.b * 255f)).ToString("X2"),
                        ["alpha"] = Math.Round(c.a, 3),
                        ["enabled"] = image.enabled,
                        ["active"] = image.gameObject.activeInHierarchy,
                        ["sibling"] = sibling,
                        ["material"] = image.material != null ? image.material.name : null,
                    });
                }
                catch { /* best-effort readback */ }
            }

            try
            {
                Add(cell.Go.GetComponent<Image>(), -1);
                var t = cell.Go.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    var child = t.GetChild(i);
                    Add(child != null ? child.gameObject.GetComponent<Image>() : null, i);
                }
            }
            catch { /* */ }
            return list;
        }

        public bool TryScrollState(string key, out float contentHeight, out float viewportHeight, out float offset)
        {
            contentHeight = 0f;
            viewportHeight = 0f;
            offset = 0f;
            if (key == null || !_cells.TryGetValue(key, out var cell) || cell.Content == null || cell.Viewport == null) return false;
            try
            {
                contentHeight = ContentHeightOf(cell.Content);
                viewportHeight = ViewportHeightOf(cell.Viewport);
                offset = Math.Max(0f, cell.Content.anchoredPosition.y);
                return true;
            }
            catch { return false; }
        }

        // ---- INSTALLSIDES1: the scroll block --------------------------------------------------

        /// <summary>
        /// A <see cref="CellKind.ScrollText"/> cell: a clipped viewport, a top-anchored content rect
        /// holding one wrapping TMP per line, and a thin vertical bar in the cell's right inset painted
        /// with the game's <c>ScrollBar_Bar</c> / <c>ScrollBar_Handle</c> (the kit's dark track and
        /// steel handle when they were not borrowed). The ScrollRect is clamped, inertia-free and
        /// deaf to uGUI's own wheel (sensitivity 0): the wheel pump writes it
        /// (<see cref="TryScrollInner"/>). Dragging the bar is Unity's own Scrollbar.
        /// </summary>
        private void BuildScrollText(Cell cell)
        {
            var go = new GameObject("cell-scroll");
            go.transform.SetParent(GameObject.transform, false);
            // A near-invisible face so the block takes the pointer (drag, and no click reaches the row).
            var face = TryAdd<Image>(go);
            if (face != null)
            {
                try { face.color = new Color(0f, 0f, 0f, InstallDrawerLayout.BodyShadeAlpha); } catch { /* */ }
                try { face.raycastTarget = true; } catch { /* */ }
                try { face.maskable = true; } catch { /* */ }
            }

            var viewport = new GameObject("scroll-viewport");
            viewport.transform.SetParent(go.transform, false);
            var vrt = viewport.GetComponent<RectTransform>() ?? viewport.AddComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero;
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(CellLayout.ScrollPad, 0f);
            vrt.offsetMax = new Vector2(-(CellLayout.ScrollPad + CellLayout.ScrollBarWidth + 4f), 0f);
            var viewImage = TryAdd<Image>(viewport);
            if (viewImage != null)
            {
                try { viewImage.color = new Color(1f, 1f, 1f, 0.004f); } catch { /* */ }
                try { viewImage.raycastTarget = true; } catch { /* */ }
                try { viewImage.maskable = true; } catch { /* */ }
            }
            TryAdd<RectMask2D>(viewport);

            var content = new GameObject("scroll-content");
            content.transform.SetParent(viewport.transform, false);
            var crt = content.GetComponent<RectTransform>() ?? content.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(0f, 1f);

            GameObject? bar = null;
            Scrollbar? scrollbar = null;
            if (cell.Spec.NoScroll)
            {
                // INSTALLSIDES2 item 4: a clamped block that never scrolls — the mask clips it, the
                // wheel scrolls the list, and there is no second scroll bar.
                cell.Go = go;
                cell.Viewport = vrt;
                cell.Content = crt;
                FillScrollLines(cell);
                return;
            }
            try
            {
                bar = new GameObject("scroll-bar");
                bar.transform.SetParent(go.transform, false);
                var brt = bar.GetComponent<RectTransform>() ?? bar.AddComponent<RectTransform>();
                brt.anchorMin = new Vector2(1f, 0f);
                brt.anchorMax = new Vector2(1f, 1f);
                brt.pivot = new Vector2(1f, 0.5f);
                brt.offsetMin = new Vector2(-(CellLayout.ScrollBarWidth + 4f), CellLayout.ScrollPad * .5f);
                brt.offsetMax = new Vector2(-4f, -CellLayout.ScrollPad * .5f);
                var track = TryAdd<Image>(bar);
                PaintPart(track, GameArt.SettingsArtPart.ScrollbarTrack, Color.white, UnityPanel.ScrollTrack);
                var area = new GameObject("sliding-area");
                area.transform.SetParent(bar.transform, false);
                Stretch(area);
                var handle = new GameObject("handle");
                handle.transform.SetParent(area.transform, false);
                Stretch(handle);
                var handleImage = TryAdd<Image>(handle);
                PaintPart(handleImage, GameArt.SettingsArtPart.ScrollbarHandle, Color.white, UnityPanel.ScrollHandle);
                scrollbar = TryAdd<Scrollbar>(bar);
                if (scrollbar != null)
                {
                    try { scrollbar.direction = Scrollbar.Direction.BottomToTop; } catch { /* */ }
                    try { scrollbar.handleRect = handle.GetComponent<RectTransform>(); } catch { /* */ }
                    try { scrollbar.targetGraphic = handleImage; } catch { /* */ }
                }
            }
            catch { scrollbar = null; }

            var scroll = TryAdd<ScrollRect>(go);
            if (scroll != null)
            {
                try
                {
                    scroll.viewport = vrt;
                    scroll.content = crt;
                    scroll.horizontal = false;
                    scroll.vertical = true;
                    scroll.movementType = ScrollRect.MovementType.Clamped;
                    scroll.inertia = false;
                    scroll.scrollSensitivity = MenuPanelLayout.UguiScrollSensitivity;
                    if (scrollbar != null)
                    {
                        scroll.verticalScrollbar = scrollbar;
                        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                    }
                }
                catch { /* the block still clips */ }
                InnerScrolls.Add(scroll);
            }

            cell.Go = go;
            cell.Scroll = scroll;
            cell.Viewport = vrt;
            cell.Content = crt;
            FillScrollLines(cell);
            // JOINLINKS1: a linked block's click (a drag scrolls it; Unity drops the click after a drag).
            if (UiCellRowHandle.IsLinked(cell.Spec)) MakeLinkTarget(cell, go, face);
        }

        /// <summary>One TMP per line, reused across rewrites; extra ones are hidden.</summary>
        private static void FillScrollLines(Cell cell)
        {
            if (cell.Content == null) return;
            var lines = cell.Spec.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                TextMeshProUGUI? tmp = i < cell.Lines.Count ? cell.Lines[i] : null;
                if (tmp == null)
                {
                    var go = MakeLabel(cell.Content.gameObject, "", Vector2.zero, lines[i].FontSize, left: true);
                    tmp = go?.GetComponent<TextMeshProUGUI>();
                    if (tmp == null) continue;
                    try { tmp.enableWordWrapping = true; } catch { /* */ }
                    try { tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
                    try { tmp.raycastTarget = false; } catch { /* */ }
                    try { tmp.verticalAlignment = VerticalAlignmentOptions.Top; } catch { /* */ }
                    try { tmp.horizontalAlignment = HorizontalAlignmentOptions.Left; } catch { /* */ }
                    try
                    {
                        var rt = tmp.rectTransform;
                        rt.anchorMin = new Vector2(0f, 1f);
                        rt.anchorMax = new Vector2(0f, 1f);
                        rt.pivot = new Vector2(0f, 1f);
                    }
                    catch { /* */ }
                    if (i < cell.Lines.Count) cell.Lines[i] = tmp;
                    else cell.Lines.Add(tmp);
                }

                WriteScrollLine(tmp, lines[i], cell.Spec.Linkify);
                try { tmp.gameObject.SetActive(true); } catch { /* */ }
            }

            for (int i = lines.Count; i < cell.Lines.Count; i++)
            {
                try { cell.Lines[i].gameObject.SetActive(false); } catch { /* */ }
            }
        }

        /// <summary>A line's words and color. Untrusted text goes through <see cref="Escape"/> (a noparse run).</summary>
        private static void WriteScrollLine(TextMeshProUGUI tmp, in CellLine line, bool linkify = false)
        {
            string body;
            Color color;
            switch (line.Kind)
            {
                case CellLineKind.Fact:
                    body = "<color=" + Hex(MenuPalette.Muted) + ">" + Escape(line.Label) + "</color>  " + Escape(line.Text);
                    // INSTALLSIDES2 item 4: a second fact from the middle of the line (a two-column grid).
                    if (line.HasSecond)
                        body += "<pos=" + CellLayout.FactSecondColumnPercent.ToString("0", CultureInfo.InvariantCulture) + "%>"
                            + "<color=" + Hex(MenuPalette.Muted) + ">" + Escape(line.Label2) + "</color>  " + Escape(line.Text2);
                    color = ToColor(MenuPalette.Text);
                    break;
                case CellLineKind.Muted:
                case CellLineKind.Code:
                    body = Escape(line.Text);
                    color = ToColor(MenuPalette.Muted);
                    break;
                case CellLineKind.Warn:
                    body = Escape(line.Text);
                    color = ToColor(MenuPalette.TextColor(CellTone.Warn));
                    break;
                default:
                    // JOINLINKS1: a linked block's body lines draw their links (everything else escaped).
                    body = linkify && line.Kind == CellLineKind.Body ? UiLinks.Markup(line.Text) : Escape(line.Text);
                    color = ToColor(MenuPalette.Text);
                    break;
            }

            try { tmp.richText = true; } catch { /* */ }
            try { tmp.fontSize = line.FontSize; } catch { /* */ }
            try { tmp.fontStyle = line.Kind == CellLineKind.Heading ? FontStyles.Bold : FontStyles.Normal; } catch { /* */ }
            try { tmp.color = color; } catch { /* */ }
            try { if (tmp.text != body) tmp.text = body; } catch { /* */ }
        }

        /// <summary>
        /// Stack the lines at the block's width: each TMP is given the text width, measured
        /// (<c>ForceMeshUpdate</c> then <c>preferredHeight</c>, the panel's own measure) and placed
        /// under the one before; the content rect takes the total, which is what the ScrollRect and
        /// its bar scroll over. Only re-run when the width or the height changed, or the lines did.
        /// </summary>
        private static void LayoutScroll(Cell cell, float width, float height)
        {
            if (cell.Content == null) return;
            if (Math.Abs(cell.LaidWidth - width) < .5f && Math.Abs(cell.LaidHeight - height) < .5f) return;
            cell.LaidWidth = width;
            cell.LaidHeight = height;
            float textWidth = Math.Max(40f, width - 2f * CellLayout.ScrollPad - CellLayout.ScrollBarWidth - 4f);
            float y = CellLayout.ScrollPad;
            int count = Math.Min(cell.Spec.Lines.Count, cell.Lines.Count);
            for (int i = 0; i < count; i++)
            {
                var tmp = cell.Lines[i];
                float h = cell.Spec.Lines[i].FontSize * 1.3f;
                try
                {
                    var rt = tmp.rectTransform;
                    rt.sizeDelta = new Vector2(textWidth, 10000f);
                    tmp.ForceMeshUpdate();
                    float measured = tmp.preferredHeight;
                    if (measured > 0.5f && !float.IsNaN(measured) && !float.IsInfinity(measured)) h = measured;
                    rt.sizeDelta = new Vector2(textWidth, h);
                    rt.anchoredPosition = new Vector2(0f, -y);
                }
                catch { /* the line keeps its estimate */ }
                y += h + CellLayout.ScrollLineGap;
            }

            float total = y - CellLayout.ScrollLineGap + CellLayout.ScrollPad;
            try
            {
                cell.Content.sizeDelta = new Vector2(0f, Math.Max(1f, total));
                float maxTop = Math.Max(0f, total - height);
                var at = cell.Content.anchoredPosition;
                if (at.y > maxTop) cell.Content.anchoredPosition = new Vector2(at.x, maxTop);
            }
            catch { /* */ }
        }

        // ---- INSTALLSIDES1: expander, dropdown bar, quiet link ----------------------------------

        /// <summary>
        /// The row's expand control: the game's dropdown arrow on a box-less button, turned to point
        /// right while closed and down while open, brighter under the pointer and darker pressed (a
        /// color tint on the arrow, the game's own transition for its arrows). The caption glyph is the
        /// fallback when the arrow was not borrowed; then the caption takes the tint.
        /// </summary>
        private static bool PaintExpander(Cell cell)
        {
            var sprite = PartSprite(GameArt.SettingsArtPart.DropdownArrow);
            bool art = sprite != null && cell.Picture != null;
            // Three homes (live polish 2026-10-06): a drawer's fold marker takes the subheading's gold and its size.
            bool gold = cell.Spec.Tone == CellTone.Gold;
            var tint = gold ? ToColor(MenuPalette.TextColor(CellTone.Gold)) : Grey(GameArt.GameSettingsLook.ValueGrey + 0.15f);
            float size = cell.Spec.FontSizeOverride;
            try { if (cell.Fill != null) { cell.Fill.sprite = null; cell.Fill.color = new Color(1f, 1f, 1f, 0f); cell.Fill.raycastTarget = true; } } catch { /* */ }
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            Graphic? target = null;
            if (art)
            {
                try
                {
                    cell.Picture!.sprite = sprite;
                    cell.Picture.enabled = true;
                    cell.Picture.color = tint;
                    var rt = cell.Picture.rectTransform;
                    float w = size > 0f ? size * 0.8f : GameArt.GameSettingsLook.Kit(GameArt.GameSettingsLook.DropdownArrowWidth);
                    float h = size > 0f ? size * 0.5f : GameArt.GameSettingsLook.Kit(GameArt.GameSettingsLook.DropdownArrowHeight);
                    rt.anchorMin = new Vector2(0.5f, 0.5f);
                    rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                    rt.sizeDelta = new Vector2(w, h);
                    // The game's arrow points down: open is as drawn, closed turns it to point right.
                    rt.localRotation = Quaternion.Euler(0f, 0f, cell.Expanded ? 0f : 90f);
                    target = cell.Picture;
                }
                catch { art = false; }
            }

            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = art ? "" : (cell.Expanded ? InstallModsCopy.GlyphCollapse : InstallModsCopy.GlyphExpand); } catch { /* */ }
                try { cell.Tmp.color = tint; } catch { /* */ }
                try { cell.Tmp.fontSize = size > 0f ? size : CellLayout.ChevronFontSize + 4f; } catch { /* */ }
                if (!art) target = cell.Tmp;
            }
            if (!art && cell.Picture != null)
            {
                try { cell.Picture.enabled = false; } catch { /* */ }
            }

            TintTransition(cell, target, 1.45f, 0.65f);
            return true;
        }

        /// <summary>
        /// The game's Options dropdown bar (<c>DropdownButton_Normal</c>, swapped to
        /// <c>DropDownButton_Focus</c> on hover): the caption left in the value gray, the game's arrow
        /// at the right. False when the bar was not borrowed: the caller draws the kit button.
        /// </summary>
        private static bool PaintDropdown(Cell cell)
        {
            if (cell.Fill == null || PartSprite(GameArt.SettingsArtPart.DropdownBar) == null) return false;
            PaintPart(cell.Fill, GameArt.SettingsArtPart.DropdownBar, Color.white, new Color(0.10f, 0.16f, 0.22f, 1f));
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            try
            {
                var button = cell.Go != null ? cell.Go.GetComponent<Button>() : null;
                if (button != null)
                {
                    button.targetGraphic = cell.Fill;
                    button.interactable = cell.Spec.Tone != CellTone.Disabled;
                    ApplySpriteSwap(button, GameArt.SettingsArtPart.DropdownBarHover, null);
                }
            }
            catch { /* */ }

            float arrowW = GameArt.GameSettingsLook.Kit(GameArt.GameSettingsLook.DropdownArrowWidth);
            float arrowH = GameArt.GameSettingsLook.Kit(GameArt.GameSettingsLook.DropdownArrowHeight);
            float arrowRight = GameArt.GameSettingsLook.Kit(GameArt.GameSettingsLook.DropdownArrowRight);
            var arrow = PartSprite(GameArt.SettingsArtPart.DropdownArrow);
            if (cell.Picture != null)
            {
                try
                {
                    cell.Picture.sprite = arrow;
                    cell.Picture.enabled = arrow != null;
                    cell.Picture.color = Grey(GameArt.GameSettingsLook.ValueGrey);
                    var rt = cell.Picture.rectTransform;
                    rt.anchorMin = new Vector2(1f, 0.5f);
                    rt.anchorMax = new Vector2(1f, 0.5f);
                    rt.pivot = new Vector2(1f, 0.5f);
                    rt.anchoredPosition = new Vector2(-arrowRight, 0f);
                    rt.sizeDelta = new Vector2(arrowW, arrowH);
                }
                catch { /* */ }
            }

            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                try { cell.Tmp.horizontalAlignment = HorizontalAlignmentOptions.Left; } catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                try { cell.Tmp.fontSize = GameArt.GameSettingsLook.DropdownText; } catch { /* */ }
                try { cell.Tmp.color = Grey(GameArt.GameSettingsLook.ValueGrey + 0.15f); } catch { /* */ }
                try
                {
                    cell.Tmp.margin = new Vector4(GameArt.GameSettingsLook.Kit(GameArt.GameSettingsLook.DropdownCaptionLeft), 0f,
                        arrowRight + arrowW + 6f, 0f);
                }
                catch { /* */ }
            }
            return true;
        }

        /// <summary>
        /// A quiet secondary action: no box, the caption alone in the muted gold, brighter on hover,
        /// darker pressed (a color tint on the caption).
        /// </summary>
        private static bool PaintQuiet(Cell cell)
        {
            try { if (cell.Fill != null) { cell.Fill.sprite = null; cell.Fill.color = new Color(1f, 1f, 1f, 0f); cell.Fill.raycastTarget = true; } } catch { /* */ }
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            if (cell.Tmp != null)
            {
                var gold = ToColor(MenuPalette.TextColor(CellTone.Gold));
                try { cell.Tmp.text = Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                try { cell.Tmp.fontSize = cell.Spec.FontSize; } catch { /* */ }
                // P7b chips: a single-glyph quiet button (CellWrap.Glyph, a chip's remove glyph) is drawn centered and not
                // underlined, light cream on a selected chip's gold plate (tone ChipOn). Every other quiet button is unchanged.
                bool glyph = cell.Spec.Wrap == CellWrap.Glyph;
                try
                {
                    cell.Tmp.color = glyph && cell.Spec.Tone == CellTone.ChipOn
                        ? ToColor(MenuPalette.ChipOnText)
                        : new Color(gold.r * 0.85f, gold.g * 0.85f, gold.b * 0.85f, 1f);
                }
                catch { /* */ }
                try { cell.Tmp.fontStyle = glyph ? FontStyles.Normal : FontStyles.Underline; } catch { /* */ }
                try { cell.Tmp.horizontalAlignment = glyph ? HorizontalAlignmentOptions.Center : HorizontalAlignmentOptions.Right; } catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
                if (glyph)
                {
                    // Live 2026-10-14: a chip's 20-unit remove glyph drew nothing. The button caption's 8-unit side margins
                    // left 4 units, and the ellipsis clamp replaced the × with nothing that fit. A glyph button is drawn
                    // whole: no side margins, no ellipsis (as a CellWrap.Glyph text cell is).
                    try { cell.Tmp.margin = Vector4.zero; } catch { /* */ }
                    try { cell.Tmp.overflowMode = TextOverflowModes.Overflow; } catch { /* */ }
                    try { cell.Tmp.enableWordWrapping = false; } catch { /* */ }
                    try { cell.Tmp.raycastTarget = false; } catch { /* the button's fill is the click target */ }
                }
            }

            TintTransition(cell, cell.Tmp, 1.3f, 0.7f);
            return true;
        }

        /// <summary>
        /// SERVERLINK1: a text link: no box (the fill stays as the transparent click target), the caption in the
        /// link blue, underlined and left-aligned, brighter on hover and darker pressed.
        /// </summary>
        private static bool PaintLink(Cell cell)
        {
            try { if (cell.Fill != null) { cell.Fill.sprite = null; cell.Fill.color = new Color(1f, 1f, 1f, 0f); cell.Fill.raycastTarget = true; } } catch { /* */ }
            try { if (cell.Rim != null) cell.Rim.effectColor = new Color(0f, 0f, 0f, 0f); } catch { /* */ }
            if (cell.Tmp != null)
            {
                try { cell.Tmp.text = Escape(cell.Spec.Text ?? ""); } catch { /* */ }
                try { cell.Tmp.fontSize = cell.Spec.FontSize; } catch { /* */ }
                try { cell.Tmp.color = ToColor(MenuPalette.LinkText); } catch { /* */ }
                try { cell.Tmp.fontStyle = FontStyles.Underline; } catch { /* */ }
                try { cell.Tmp.horizontalAlignment = HorizontalAlignmentOptions.Left; } catch { /* */ }
                try { cell.Tmp.verticalAlignment = VerticalAlignmentOptions.Middle; } catch { /* */ }
            }

            TintTransition(cell, cell.Tmp, 1.25f, 0.75f);
            return true;
        }

        /// <summary>A color-tint transition on <paramref name="target"/>: brighter on hover, darker pressed.</summary>
        private static void TintTransition(Cell cell, Graphic? target, float hover, float pressed)
        {
            try
            {
                var button = cell.Go != null ? cell.Go.GetComponent<Button>() : null;
                if (button == null) return;
                if (target != null) button.targetGraphic = target;
                button.interactable = cell.Spec.Tone != CellTone.Disabled;
                button.transition = Selectable.Transition.ColorTint;
                var colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(hover, hover, hover, 1f);
                colors.selectedColor = Color.white;
                colors.pressedColor = new Color(pressed, pressed, pressed, 1f);
                colors.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
                colors.colorMultiplier = 1f;
                colors.fadeDuration = 0.08f;
                button.colors = colors;
            }
            catch { /* */ }
        }

        public IRuntimeWidget? CellWidget(string key) =>
            key != null && _cells.TryGetValue(key, out var cell) ? cell.Widget : null;

        /// <summary>P7b help: the text of cell <paramref name="key"/> (a term marker follows it), or null.</summary>
        internal TextMeshProUGUI? CellText(string key) =>
            key != null && _cells.TryGetValue(key, out var cell) ? cell.Tmp : null;

        public void ApplyCell(CellSpec spec)
        {
            if (spec == null || !_cells.TryGetValue(spec.Key, out var cell)) return;
            switch (spec.Kind)
            {
                case CellKind.Badge: PaintBadge(cell); break;
                case CellKind.Switch: PaintSwitch(cell); break;
                case CellKind.Diamond: PaintDiamond(cell); break;
                case CellKind.Check:
                    if (!CheckSkinDone(cell) && cell.Go != null) RetrySkinLater(cell.Go, () => CheckSkinDone(cell));
                    break;
                case CellKind.Button: PaintButton(cell); break;
                case CellKind.Image: break;
                case CellKind.Chevron: break;
                case CellKind.ScrollText:
                    FillScrollLines(cell);
                    cell.LaidWidth = -1f;
                    break;
                case CellKind.Input:
                    // Only a differing value is written: a same-text write would move the
                    // owned caret of the field the player is typing in.
                    if (cell.Field != null)
                    {
                        try
                        {
                            string next = spec.Text ?? "";
                            if (cell.Field.text != next) cell.Field.text = next;
                        }
                        catch { /* */ }
                        SyncSearchHint(cell.Field);
                    }
                    break;
                default:
                    WriteText(cell, cell.LastWidth);
                    ApplyTextTone(cell, _header ? CellTone.Muted : spec.Tone);
                    if (_header && spec.Column != CellColumn.None && cell.Tmp != null)
                    {
                        try { cell.Tmp.color = ToColor(MenuPalette.ColumnButtonText(spec.Column)); } catch { /* */ }
                    }
                    break;
            }
            PaintBands();
            // In place: this row's cells only, never the panel, at the width the layout pass last placed the row at.
            // Live 2026-10-06: the panel's ContentRowWidth outside a layout pass is the full content width, not a
            // settings-grid row's; a drawer header's count refresh after a value change packed its cells from the wider
            // row's left edge, so its fold marker and the start of its title fell outside the grid row.
            PlaceCells(MenuPanelLayout.InPlaceCellRowWidth(_placedWidth, _host.ContentRowWidth));

            // PENDINGFIT1 (live 2026-10-07: Pending changes cut to one line, "4 changes apply at the next restart…"): a
            // fitted row was measured once per width, so a status line rewritten in place (staged changes after the page
            // was drawn) kept the old one-line height. A rewrite of its wrapping cell measures again and lays the panel
            // out when the height changed.
            if (_spec.FitWrap && spec.Wrap == CellWrap.Wrap)
            {
                bool measured = _fittedFor >= 0f;
                float before = _fitted;
                _fittedFor = -1f;
                float after = FittedHeight();
                if (measured && Math.Abs(after - before) > 0.5f) _host.Relayout();
            }
        }

        public void SetImage(string key, object? sprite)
        {
            if (key == null || !_cells.TryGetValue(key, out var cell) || cell.Picture == null) return;
            var s = UnityUiRuntime.AsSprite(sprite);
            try { cell.Picture.sprite = s; } catch { /* */ }
            try { cell.Picture.enabled = s != null && UnityUiRuntime.Alive(s); } catch { /* */ }
        }

        public void SetExpanded(bool expanded)
        {
            _expanded = expanded;
            foreach (var cell in _cells.Values)
            {
                if (cell.Spec.Kind == CellKind.Button && cell.Spec.Skin == CellSkin.Expander)
                {
                    cell.Expanded = expanded;
                    PaintExpander(cell);
                    continue;
                }

                if (cell.Spec.Kind != CellKind.Chevron || cell.Go == null) continue;
                try
                {
                    var rt = cell.Go.GetComponent<RectTransform>();
                    if (rt != null) rt.localRotation = Quaternion.Euler(0f, 0f, expanded ? -90f : 0f);
                }
                catch { /* */ }
            }
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            if (_spec.Skin == CellRowSkin.GameEntry)
            {
                // Never the gold stripe: the game's rows have none.
                // KEYS3 polish (live, second attempt): the band is a solid fill in the kit's soft
                // selection tone, never the borrowed glow sprite (it drew as a harsh white band). The
                // color is written after the sprite is cleared, and nothing below writes it again.
                bool band = _highlight != null;
                var top = (Sprite?)null;
                var topColor = Color.white;
                bool hasTop = KeybindArtDonors.TryHighlightTop(out top, out topColor);
                try
                {
                    if (band)
                    {
                        _highlight!.sprite = null;
                        _highlight.type = Image.Type.Simple;
                        try { _highlight.material = null; } catch { /* the default UI material */ }
                        _highlight.color = ToColor(KeybindsTabLayout.HighlightBand);
                        if (_highlightTop != null)
                        {
                            if (hasTop)
                            {
                                _highlightTop.sprite = top;
                                _highlightTop.color = topColor;
                                var rt = _highlightTop.GetComponent<RectTransform>();
                                if (rt != null)
                                {
                                    float h = 4f;
                                    try { h = Math.Max(1f, Math.Min(KeybindsTabLayout.HighlightTopMaxHeight, top!.rect.height)); } catch { /* */ }
                                    rt.anchorMin = new Vector2(0f, 1f);
                                    rt.anchorMax = new Vector2(1f, 1f);
                                    rt.pivot = new Vector2(0.5f, 1f);
                                    rt.offsetMin = new Vector2(0f, -h);
                                    rt.offsetMax = new Vector2(0f, 0f);
                                }
                            }
                            _highlightTop.gameObject.SetActive(hasTop);
                        }
                    }
                    if (_highlight != null) _highlight.gameObject.SetActive(band && selected);
                }
                catch { band = false; }
                try { _fill.color = ToColor(MenuPalette.RowFillFor(selected && !band)); } catch { /* */ }
                try { if (_stripe != null) _stripe.SetActive(false); } catch { /* */ }
                try
                {
                    var rowButton = GameObject.GetComponent<Button>();
                    if (rowButton != null) PaintCellRowButton(rowButton, selected && !band, _spec.Soft, _spec.QuietHover);
                }
                catch { /* */ }
                return;
            }

            try { _fill.color = ToColor(_spec.Soft ? ModsListLayout.RowFill(selected) : MenuPalette.RowFillFor(selected)); } catch { /* */ }
            try { if (_stripe != null) _stripe.SetActive(selected); } catch { /* */ }
            try { if (_joinBand != null) _joinBand.gameObject.SetActive(selected); } catch { /* */ }
            try { if (_seam != null) _seam.SetActive(selected); } catch { /* */ }
            try
            {
                var button = GameObject.GetComponent<Button>();
                if (button != null) PaintCellRowButton(button, selected, _spec.Soft, _spec.QuietHover);
            }
            catch { /* */ }
        }

        public void SetSorted(string? sortKey) => SetSorted(sortKey, ascending: false);

        /// <summary>INSTALLCOLS1: the sorted column's direction; ▲ when ascending, ▼ otherwise.</summary>
        public void SetSorted(string? sortKey, bool ascending)
        {
            _sortedKey = sortKey;
            _sortAscending = ascending;
            if (!_header) return;
            foreach (var cell in _cells.Values) WriteText(cell, cell.LastWidth);
        }

        /// <summary>Position every cell from the column model for a row <paramref name="rowWidth"/> wide.</summary>
        /// <summary>
        /// Job LIST4 item 2: this row's clickable cells — the button cells (the chooser chips,
        /// the filter chips, the Install column) and a sortable header cell — as
        /// (row key, cell key, cell object). A cell with no object built, or one the spec hides,
        /// is not offered: the pointer cannot reach it either.
        /// </summary>
        internal void ForEachClickableCell(Action<string, string, GameObject> body)
        {
            if (body == null) return;
            foreach (var pair in _cells)
            {
                var cell = pair.Value;
                if (cell?.Go == null || !cell.Spec.Visible) continue;
                bool clickable = cell.Spec.Kind == CellKind.Button
                    || (cell.Spec.Kind == CellKind.Switch && cell.Spec.Clickable)
                    || (_header && !string.IsNullOrEmpty(cell.Spec.SortKey));
                if (!clickable) continue;
                bool active;
                try { active = cell.Go.activeInHierarchy; }
                catch { active = false; }
                if (!active) continue;
                body(_spec.Key, pair.Key, cell.Go);
            }
        }

        /// <summary>Inset of a <see cref="CellVAlign.Top"/> cell from the top of its row.</summary>
        private const float CellTopPad = 2f;

        /// <summary>The width the last placement used (0 before the first): what an in-place re-placement reuses.</summary>
        private float _placedWidth;

        public void PlaceCells(float rowWidth)
        {
            if (_released) return;
            _placedWidth = rowWidth;
            float rowHeight = Height;
            // The row, not only its cells: HEAD2's right inset is a property of the row
            // (CellRowSpec.RightInset), and resolving the cells alone would place a head block's
            // right column flush against the viewport's clip again.
            var placements = MenuPanelLayout.CellColumns(rowWidth, _spec);
            for (int i = 0; i < placements.Length; i++)
            {
                var p = placements[i];
                if (!_cells.TryGetValue(p.Key, out var cell) || cell.Go == null) continue;
                if (!p.Visible)
                {
                    try { cell.Go.SetActive(false); } catch { /* */ }
                    continue;
                }
                try { if (!cell.Go.activeSelf) cell.Go.SetActive(true); } catch { /* */ }
                float h;
                switch (cell.Spec.Kind)
                {
                    case CellKind.Image: h = Math.Min(CellLayout.ImageSize, rowHeight - 4f); break;
                    case CellKind.Badge: h = Math.Min(24f, rowHeight - 4f); break;
                    case CellKind.Switch: h = Math.Min(CellLayout.SwitchHeight, rowHeight - 4f); break;
                    case CellKind.Diamond: h = Math.Min(CellLayout.DiamondWidth, rowHeight - 4f); break;
                    case CellKind.Check: h = Math.Min(CellLayout.CheckWidth, rowHeight - 2f); break;
                    case CellKind.Button: h = Math.Min(UnityUiRuntimeMetrics.RowHeight - 8f, rowHeight - 6f); break;
                    case CellKind.Input: h = Math.Min(InputHeight, rowHeight - 6f); break;
                    default: h = rowHeight; break;
                }
                if (cell.Spec.HeightOverride > 0f) h = Math.Min(cell.Spec.HeightOverride, rowHeight);
                // KIT1: a cell may ask to sit at the top of a row taller than itself — the head
                // block's wrapping one-liner and the badges beside it, which must not float in the
                // middle of a grown row. A full-height cell is shortened by the pad so it still ends
                // inside the row.
                float top;
                if (cell.Spec.VAlign == CellVAlign.Top)
                {
                    top = CellTopPad;
                    if (h > rowHeight - top) h = Math.Max(1f, rowHeight - top);
                }
                else if (cell.Spec.VAlign == CellVAlign.Bottom)
                {
                    // SETTINGS1 stage 2: a section heading's space is above it. A full-height text
                    // cell is one line tall here so it can sit on the row's bottom edge.
                    if (h >= rowHeight) h = Math.Min(rowHeight, UnityUiRuntimeMetrics.RowHeight * 0.75f);
                    top = Math.Max(0f, rowHeight - h - CellTopPad);
                }
                // A game window tab stands on the strip's baseline: its bottom edge on the row's bottom edge.
                else if (cell.Spec.Skin == CellSkin.GameTab) top = UiTabsLayout.GameTabTop(rowHeight, h);
                else top = (rowHeight - h) * .5f;
                PlaceTop(cell.Go, p.X, top, p.Width, h);
                // MODPAGE2 (live 2026-10-09): an expanded chevron is turned a quarter; turned about the top-center pivot
                // PlaceTop gives it, it drew above its row. The same rect, pivoted at its center, turns in place.
                if (cell.Spec.Kind == CellKind.Chevron) CenterPivot(cell.Go, h);
                if (cell.Spec.Kind == CellKind.ScrollText) LayoutScroll(cell, p.Width, h);
                if (cell.Spec.Wrap == CellWrap.TwoLine && Math.Abs(cell.LastWidth - p.Width) > .5f)
                {
                    cell.LastWidth = p.Width;
                    WriteText(cell, p.Width);
                }
                else cell.LastWidth = p.Width;
            }

            PlaceInlineOverlays(placements);
            PlaceBands(placements, rowHeight);
            PlaceTiles(placements, rowHeight);

            // INSTALLSIDES1: a joined row's rail reaches up through the gap, so the rail runs unbroken
            // down the listing row, the drawer head and the drawer body.
            float reach = _spec.JoinAbove ? PanelLayout.RowGap : 0f;
            if (_stripe != null)
            {
                PlaceTop(_stripe, -rowWidth * .5f + MenuPalette.SelectedStripeWidth * .5f, -reach, MenuPalette.SelectedStripeWidth, rowHeight + reach);
            }

            if (_joinBand != null) PlaceTop(_joinBand.gameObject, 0f, -reach, rowWidth, reach);
            if (_seam != null)
            {
                float from = -rowWidth * .5f + _spec.Indent;
                float to = rowWidth * .5f - _spec.RightInset;
                if (to > from) PlaceTop(_seam, (from + to) * .5f, 0f, to - from, InstallDrawerLayout.SeamHeight);
            }
        }

        public IReadOnlyList<KeyValuePair<string, ScreenRect>> CellRects()
        {
            var list = new List<KeyValuePair<string, ScreenRect>>(_cells.Count);
            foreach (var cell in _cells.Values)
            {
                if (cell.Go == null) continue;
                try
                {
                    if (!cell.Go.activeSelf) continue;
                    var rt = cell.Go.GetComponent<RectTransform>();
                    if (rt == null) continue;
                    list.Add(new KeyValuePair<string, ScreenRect>(cell.Spec.Key, ScreenRectOf(rt)));
                }
                catch { /* */ }
            }
            return list;
        }

        public ScreenRect RowRect()
        {
            try
            {
                var rt = GameObject.GetComponent<RectTransform>();
                return rt == null ? ScreenRect.Unknown : ScreenRectOf(rt);
            }
            catch { return ScreenRect.Unknown; }
        }

        public void Release()
        {
            if (_released) return;
            _released = true;
            foreach (ushort id in _forwardSlots)
            {
                try { BootstrapLink.UnwireUi(id); } catch { /* runtime gone */ }
                UiSlotTable.Release(id);
            }

            _forwardSlots.Clear();
            foreach (var cell in _cells.Values) ForgetInnerScroll(cell.Scroll);
            _host.ForgetCellRow(GameObject);
            TryDestroy(GameObject, BootstrapLink.MayDestroyUiImmediate());
        }
    }
}

/// <summary>
/// Decodes listing-icon bytes into a <c>Sprite</c> on the main thread for
/// <see cref="Progenitor.Install.IconCache"/> (LIST1). <c>ImageConversion.LoadImage</c> is the
/// same interop call the Mods workspace backdrop already goes through
/// (<c>ModsWorkspaceChrome.TryLoadSprite</c>); the interop assembly
/// <c>UnityEngine.ImageConversionModule</c> exposes it (checked with the IL viewer, 2026-09-12).
/// Every texture this decoder creates is tracked and destroyed on Core Uninstall
/// (<see cref="ReleaseAll"/>) as well as on cache eviction.
/// </summary>
internal sealed class UnityIconDecoder : Progenitor.Install.IIconDecoder
{
    private static readonly List<UnityIconDecoder> Live = new();

    private readonly List<Sprite> _created = new();
    private Sprite? _placeholder;

    private UnityIconDecoder() { }

    internal static UnityIconDecoder Create()
    {
        var decoder = new UnityIconDecoder();
        lock (Live) Live.Add(decoder);
        return decoder;
    }

    /// <summary>Core Uninstall: destroy every icon sprite and texture from every decoder.</summary>
    internal static int ReleaseAll()
    {
        UnityIconDecoder[] all;
        lock (Live)
        {
            all = Live.ToArray();
            Live.Clear();
        }
        int n = 0;
        for (int i = 0; i < all.Length; i++) n += all[i].DestroyAll();
        return n;
    }

    public object? Placeholder
    {
        get
        {
            if (_placeholder != null && UnityUiRuntime.Alive(_placeholder)) return _placeholder;
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "progenitor-icon-placeholder",
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                };
                var c = UnityUiRuntime.ToColor(MenuPalette.ImageFill);
                for (int x = 0; x < 2; x++)
                    for (int y = 0; y < 2; y++)
                        tex.SetPixel(x, y, c);
                tex.Apply();
                _placeholder = Wrap(tex, tex.name);
                return _placeholder;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: icon placeholder: " + e.Message);
                return null;
            }
        }
    }

    public object? Decode(byte[] bytes, string url)
    {
        if (bytes == null || bytes.Length == 0) return null;
        Texture2D? tex = null;
        try
        {
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "progenitor-icon " + (url ?? ""),
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var native = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) native[i] = bytes[i];
            if (!ImageConversion.LoadImage(tex, native))
            {
                Destroy(tex);
                return null;
            }
            return Wrap(tex, tex.name);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: icon decode '" + url + "': " + e.Message);
            Destroy(tex);
            return null;
        }
    }

    private Sprite? Wrap(Texture2D tex, string name)
    {
        var rect = new Rect(0f, 0f, tex.width, tex.height);
        var sprite = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        if (sprite == null)
        {
            Destroy(tex);
            return null;
        }
        sprite.name = name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        lock (_created) _created.Add(sprite);
        return sprite;
    }

    public void Destroy(object handle)
    {
        if (handle is Sprite sprite)
        {
            lock (_created) _created.Remove(sprite);
            Texture2D? tex = null;
            try { tex = sprite.texture; } catch { tex = null; }
            Destroy(sprite);
            Destroy(tex);
            return;
        }
        if (handle is Texture2D texture) Destroy(texture);
    }

    private static void Destroy(UnityEngine.Object? obj)
    {
        if (!UnityUiRuntime.Alive(obj)) return;
        try { UnityEngine.Object.DestroyImmediate(obj); }
        catch
        {
            try { UnityEngine.Object.Destroy(obj); } catch { /* */ }
        }
    }

    private int DestroyAll()
    {
        Sprite[] all;
        lock (_created)
        {
            all = _created.ToArray();
            _created.Clear();
        }
        int n = 0;
        for (int i = 0; i < all.Length; i++)
        {
            Texture2D? tex = null;
            try { tex = all[i].texture; } catch { tex = null; }
            Destroy(all[i]);
            Destroy(tex);
            n++;
        }
        if (_placeholder != null)
        {
            Texture2D? tex = null;
            try { tex = _placeholder.texture; } catch { tex = null; }
            Destroy(_placeholder);
            Destroy(tex);
            _placeholder = null;
        }
        return n;
    }
}
#endif
