#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Ui;

/// <summary>
/// BRIDGEUI1: the dev bridge's <c>ui_input</c>, <c>ui_read</c> and <c>nav</c> (hold, stick, focus) entry points, reached
/// by reflection from <c>Progenitor.Dev</c> (Core grants Dev no <c>InternalsVisibleTo</c>). Main thread. Every method
/// returns a plain dictionary. Dev bridge only: no plugin or network path calls it.
/// </summary>
internal static class UiInputTestBridge
{
    private const string Owner = "bridge";

    // ---- Lease and synthetic pointer ----------------------------------------------------------------

    /// <summary>Take or renew the synthetic input for <paramref name="leaseMs"/>; null on success, else why not.</summary>
    public static string? Begin(int leaseMs)
    {
        var refused = UiSyntheticInput.Begin(Owner, leaseMs);
        if (refused != null) return refused;
        UnityUiRuntime.EnsureSyntheticDriver();
        return null;
    }

    public static bool Active() => UiSyntheticInput.Active;

    public static string LastEnd() => UiSyntheticInput.LastEnd;

    public static void Move(float x, float y) => UiSyntheticInput.MoveTo(x, y);

    /// <summary>Buttons as a mask: 1 left, 2 right, 4 middle.</summary>
    public static void Buttons(int mask) => UiSyntheticInput.SetButtons((UiSyntheticButtons)(mask & 7));

    /// <summary>Modifiers as a mask: 1 Shift, 2 Ctrl.</summary>
    public static void Modifiers(int mask) => UiSyntheticInput.SetModifiers((UiClickModifiers)(mask & 3));

    public static void Wheel(int notches) => UiSyntheticInput.QueueWheel(notches);

    public static int PendingNotches() => UiSyntheticInput.PendingNotches;

    /// <summary>
    /// A UI-layer key tap: held for the typed-key pump for <paramref name="frames"/> frames (Escape also through the
    /// framework's Escape hook; Enter on a focused single-line field also submits it, as TMP's own Enter does).
    /// </summary>
    public static Dictionary<string, object?> Tap(string key, int frames, int modifiers)
    {
        if (!UiTextTestInput.TryParseKey(key, out var ui))
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "unknown key '" + key + "'" };
        if (!UiSyntheticInput.Active)
            return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "the synthetic input is not held" };
        // Shift and Ctrl are held through the tap (the typed-key pump reads them as the left keys).
        UiSyntheticInput.SetModifiers((UiClickModifiers)(modifiers & 3));
        bool shift = (modifiers & 1) != 0, ctrl = (modifiers & 2) != 0;

        // Up, Down, Home and End in a multi-line field move by line on the kit's owned caret; Ctrl+Home / Ctrl+End stay
        // the pump's whole-text moves.
        if (!ctrl)
        {
            var line = UnityUiRuntime.SyntheticLineKey(ui, shift);
            if (line != null)
            {
                line["ok"] = true;
                line["frames"] = 0;
                return line;
            }
        }

        int frame;
        try { frame = UnityEngine.Time.frameCount; }
        catch { frame = 0; }
        bool ok = UiSyntheticInput.Tap(ui, frame + 1, frames);
        // An Escape's Escape-hook half runs now, while it is known whether a panel is shown (Bootstrap forwards a
        // physical Escape only then); the pump sees the held key from the next frame. Nothing is left pending.
        bool forwarded = false;
        if (ok && UiSyntheticInput.ForwardsEscape(ui, Ui.ShownPanelsSnapshot().Count))
        {
            Ui.PressEscapeForBridge();
            forwarded = true;
        }

        bool submitted = ok && (ui == UiKey.Enter || ui == UiKey.NumpadEnter) && UnityUiRuntime.SyntheticSubmitFocused();
        return new Dictionary<string, object?>
        {
            ["ok"] = ok,
            ["key"] = ui.ToString(),
            ["fromFrame"] = frame + 1,
            ["frames"] = frames,
            ["escapeHook"] = forwarded,
            ["submitsField"] = submitted,
            ["error"] = ok ? null : "the synthetic input is not held",
        };
    }

    public static int HeldKeys() => UiSyntheticInput.HeldKeyCount;

    /// <summary>Whether <paramref name="key"/>'s queued taps have all been released, counted in rendered frames.</summary>
    public static bool TapDone(string key)
    {
        if (!UiTextTestInput.TryParseKey(key, out var ui)) return true;
        int until = UiSyntheticInput.TapUntil(ui);
        int frame;
        try { frame = UnityEngine.Time.frameCount; }
        catch { return true; }
        return until < 0 || frame > until;
    }

    /// <summary>Drop every queued wheel notch; returns how many there were.</summary>
    public static int DrainWheel() => UiSyntheticInput.DrainWheel();

    /// <summary>How many Progenitor panels are shown (the shown-input pump runs only then).</summary>
    public static int ShownPanels() => Ui.ShownPanelsSnapshot().Count;

    /// <summary>The bridge queued text for the focused field (ui_input type): track it so it never lands elsewhere.</summary>
    public static void NoteTextForFocused() => UnityUiRuntime.NoteBridgeTextForFocused();

    /// <summary>Abandoned or failed action: every button and modifier up, queued notches and owned text dropped.</summary>
    public static void Abandon()
    {
        UiSyntheticInput.SetButtons(UiSyntheticButtons.None);
        UiSyntheticInput.SetModifiers(UiClickModifiers.None);
        UiSyntheticInput.DrainWheel();
        if (UiSyntheticInput.TextOwner != 0)
        {
            UiTextInjection.Text.Clear();
            UiTextInjection.Keys.Clear();
            UiSyntheticInput.ClearTextOwner();
        }
    }

    public static void Release(string reason)
    {
        UiSyntheticInput.End(string.IsNullOrWhiteSpace(reason) ? "release" : reason);
    }

    public static Dictionary<string, object?> State() => UnityUiRuntime.DescribeSynthetic();

    public static void ClearLog() => UnityUiRuntime.ClearSyntheticLog();

    /// <summary>
    /// The click modifiers <c>mods_ui widget_click</c> runs with: set for the slot dispatch (a new short lease when none
    /// is held), restored by <see cref="EndClickModifiers"/>. Returns a token for the restore.
    /// </summary>
    public static int BeginClickModifiers(int mask)
    {
        bool had = UiSyntheticInput.Active;
        int previous = had ? (int)UiSyntheticInput.Modifiers : -1;
        if (!had && UiSyntheticInput.Begin(Owner, UiSyntheticInput.MinLeaseMs) != null) return -2;
        UiSyntheticInput.SetModifiers((UiClickModifiers)(mask & 3));
        return previous;
    }

    public static void EndClickModifiers(int token)
    {
        if (token == -2) return;
        if (token == -1) UiSyntheticInput.End("click-modifiers");
        else UiSyntheticInput.SetModifiers((UiClickModifiers)token);
    }

    // ---- Targets --------------------------------------------------------------------------------------

    /// <summary>
    /// A point to act on, in screen pixels (origin bottom-left): a cell row (<paramref name="row"/>, optionally one of
    /// its cells), a shown panel by title, or an explicit point. <paramref name="fx"/> / <paramref name="fy"/> pick the
    /// point inside the target's rect (0..1 from its left / bottom; default the center); <paramref name="dx"/> /
    /// <paramref name="dy"/> add pixels.
    /// </summary>
    public static Dictionary<string, object?> Resolve(string? row, string? cell, string? panel, float x, float y, bool hasPoint, float fx, float fy, float dx, float dy)
    {
        var d = new Dictionary<string, object?> { ["ok"] = false };
        ScreenRect rect = ScreenRect.Unknown;
        string source;
        if (!string.IsNullOrWhiteSpace(row))
        {
            string key = string.IsNullOrWhiteSpace(cell) ? row!.Trim() : row!.Trim() + "/" + cell!.Trim();
            if (!UiCellRowHandle.TryRowRect(key, out rect))
            {
                d["error"] = "no visible cell row" + (string.IsNullOrWhiteSpace(cell) ? "" : " cell") + " '" + key + "' with a readable rect";
                return d;
            }

            source = "row:" + key;
        }
        else if (!string.IsNullOrWhiteSpace(panel))
        {
            var p = FindPanel(panel!);
            if (p == null)
            {
                d["error"] = "no shown panel titled '" + panel + "'";
                return d;
            }

            rect = PanelRect(p);
            if (!rect.IsKnown)
            {
                d["error"] = "panel '" + panel + "' has no readable rect";
                return d;
            }

            source = "panel:" + p.Text;
        }
        else if (hasPoint)
        {
            d["ok"] = true;
            d["x"] = x + dx;
            d["y"] = y + dy;
            d["source"] = "point";
            return d;
        }
        else
        {
            d["error"] = "give a row (and cell), a panel, or x and y";
            return d;
        }

        float px = rect.MinX + (rect.MaxX - rect.MinX) * Clamp01(fx) + dx;
        float py = rect.MinY + (rect.MaxY - rect.MinY) * Clamp01(fy) + dy;
        d["ok"] = true;
        d["x"] = Math.Round(px, 1);
        d["y"] = Math.Round(py, 1);
        d["rect"] = UnityUiRuntime.RectDict(rect);
        d["source"] = source;
        return d;
    }

    private static float Clamp01(float v) => float.IsNaN(v) ? 0.5f : Math.Max(0f, Math.Min(1f, v));

    private static UiPanelHandle? FindPanel(string title)
    {
        string want = title.Trim();
        foreach (var p in Ui.ShownPanelsSnapshot())
            if (string.Equals(SafeText(p), want, StringComparison.OrdinalIgnoreCase)) return p;
        foreach (var p in Ui.ShownPanelsSnapshot())
            if (SafeText(p).IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return p;
        return null;
    }

    private static string SafeText(UiHandle h)
    {
        try { return h.Text ?? ""; }
        catch { return ""; }
    }

    private static ScreenRect PanelRect(UiPanelHandle p)
    {
        try { return p.RuntimePanel == null ? ScreenRect.Unknown : Ui.CurrentRuntime().PanelRect(p.RuntimePanel); }
        catch { return ScreenRect.Unknown; }
    }

    /// <summary>Give the kit text field at the point the keyboard (a cell-row value box, a search box).</summary>
    public static Dictionary<string, object?> FocusAt(float x, float y) => UnityUiRuntime.BridgeFocusFieldAt(x, y);

    /// <summary>The submit event (a controller's confirm) to the button at the point, found the way a click finds it.</summary>
    public static Dictionary<string, object?> SubmitAt(float x, float y) => UnityUiRuntime.BridgeSubmitAt(x, y);

    // ---- Navigation ----------------------------------------------------------------------------------

    public static Dictionary<string, object?> HoldNav(string buttons, int ms)
    {
        var mask = UiSyntheticNav.None;
        foreach (var raw in (buttons ?? "").Split(new[] { ',', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var b = NavButton(raw);
            if (b == UiSyntheticNav.None) throw new ArgumentException("unknown nav button '" + raw + "'");
            mask |= b;
        }

        if (mask == UiSyntheticNav.None) throw new ArgumentException("args.button is required");
        var refused = BeginForNav(ms);
        if (refused != null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = refused };
        UiSyntheticInput.HoldNav(mask, ms);
        return new Dictionary<string, object?> { ["ok"] = true, ["held"] = mask.ToString(), ["ms"] = ms };
    }

    public static Dictionary<string, object?> Stick(float y, int ms)
    {
        var refused = BeginForNav(ms);
        if (refused != null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = refused };
        UiSyntheticInput.HoldStick(y, ms);
        return new Dictionary<string, object?> { ["ok"] = true, ["stick"] = UiSyntheticInput.StickHeld, ["ms"] = ms };
    }

    /// <summary>Navigation holds live inside the lease: the player's activity, a release or the Core unload ends them.</summary>
    private static string? BeginForNav(int ms)
    {
        EnsureNavTeardown();
        return Begin(Math.Max(UiSyntheticInput.DefaultLeaseMs, ms + 2_000));
    }

    public static void ClearNav() => UiSyntheticInput.ClearNav();

    private static bool _navTeardown;

    private static void EnsureNavTeardown()
    {
        if (_navTeardown) return;
        _navTeardown = true;
        UiWidgetLifetime.Register("synthetic-nav", () =>
        {
            UiSyntheticInput.ClearNav();
            _navTeardown = false;
        });
    }

    internal static UiSyntheticNav NavButton(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "up" => UiSyntheticNav.Up,
        "down" => UiSyntheticNav.Down,
        "left" => UiSyntheticNav.Left,
        "right" => UiSyntheticNav.Right,
        "a" => UiSyntheticNav.Submit,
        "b" => UiSyntheticNav.Cancel,
        "x" => UiSyntheticNav.Reset,
        "lt" => UiSyntheticNav.TabPrev,
        "rt" => UiSyntheticNav.TabNext,
        _ => UiSyntheticNav.None,
    };

    /// <summary>Put the controller focus on a visible cell row (or one of its cells) of the active screen.</summary>
    public static Dictionary<string, object?> NavFocus(string row, string? cell)
    {
        var handle = UiCellRowHandle.VisibleRow(row);
        if (handle == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no visible cell row '" + row + "'" };
        bool ok = UiNav.FocusOn(handle, cell ?? "");
        var d = new Dictionary<string, object?> { ["ok"] = ok, ["nav"] = UiNav.Describe() };
        if (!ok) d["error"] = "the row" + (string.IsNullOrEmpty(cell) ? "" : " cell '" + cell + "'") + " is not a stop of the screen navigation is on (a WhenHovered window is never navigated)";
        return d;
    }

    // ---- Readback ------------------------------------------------------------------------------------

    /// <summary>Every live composed widget: key, type, plugin, visible, state.</summary>
    public static Dictionary<string, object?> Widgets()
    {
        var list = new List<object?>();
        foreach (var w in UiComposedHandle.LiveSnapshot())
        {
            if (w.IsDisposed) continue;
            list.Add(new Dictionary<string, object?>
            {
                ["key"] = w.Key,
                ["type"] = w.GetType().Name,
                ["plugin"] = SafePlugin(w),
                ["visible"] = w.Visible,
                ["state"] = w.State.ToString().ToLowerInvariant(),
            });
        }

        return new Dictionary<string, object?> { ["ok"] = true, ["count"] = list.Count, ["widgets"] = list };
    }

    private static string SafePlugin(UiComposedHandle w)
    {
        try { return w.PluginId; }
        catch { return ""; }
    }

    /// <summary>One widget's own readback (its <c>Describe()</c>: a table's visible rows, sort keys and pending edits; chips; …).</summary>
    public static Dictionary<string, object?> Widget(string key)
    {
        foreach (var w in UiComposedHandle.LiveSnapshot())
        {
            if (w.IsDisposed || !string.Equals(w.Key, key, StringComparison.Ordinal)) continue;
            var mi = w.GetType().GetMethod("Describe", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (mi == null || !typeof(Dictionary<string, object?>).IsAssignableFrom(mi.ReturnType))
                return new Dictionary<string, object?> { ["ok"] = true, ["key"] = key, ["type"] = w.GetType().Name, ["describe"] = null };
            object? described;
            try { described = mi.Invoke(w, null); }
            catch (TargetInvocationException e) { return new Dictionary<string, object?> { ["ok"] = false, ["error"] = e.InnerException?.Message ?? e.Message }; }
            return new Dictionary<string, object?> { ["ok"] = true, ["key"] = key, ["type"] = w.GetType().Name, ["describe"] = described };
        }

        return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "no live widget with key '" + key + "'" };
    }

    /// <summary>Every shown panel: title, plugin, root, capture mode, rect (screen pixels, origin bottom-left).</summary>
    public static Dictionary<string, object?> Panels()
    {
        var list = new List<object?>();
        foreach (var p in Ui.ShownPanelsSnapshot())
            list.Add(new Dictionary<string, object?>
            {
                ["title"] = SafeText(p),
                ["plugin"] = p.PluginId,
                ["root"] = p.RootName,
                ["capture"] = p.ShownCaptureMode.ToString(),
                ["sequence"] = p.ShownSequence,
                ["rect"] = UnityUiRuntime.RectDict(PanelRect(p)),
            });
        var (w, h) = UnityUiRuntime.ScreenSize();

        // Vanilla attachments (Ui.AttachToVanilla) are not panels of their own: they live inside the
        // game's windows, so they are listed beside the panels with their site, anchor, state and rects.
        List<object?> attached;
        try { attached = UnityUiRuntime.DescribeAttachments(); }
        catch (Exception e) { attached = new List<object?> { new Dictionary<string, object?> { ["error"] = e.Message } }; }

        // Ui.AttachPanelTabs strips: their rect, tab height and the self-check (ok, degenerate, clipped, covered).
        List<object?> tabs;
        try { tabs = UnityUiRuntime.DescribeVanillaTabs(); }
        catch (Exception e) { tabs = new List<object?> { new Dictionary<string, object?> { ["error"] = e.Message } }; }
        return new Dictionary<string, object?>
        {
            ["ok"] = true, ["screenWidth"] = w, ["screenHeight"] = h, ["panels"] = list, ["attached"] = attached, ["tabs"] = tabs,
        };
    }

    public static Dictionary<string, object?> Tooltip() => UnityUiRuntime.DescribeTooltip();

    public static Dictionary<string, object?> Focus() => new()
    {
        ["ok"] = true,
        ["field"] = UnityUiRuntime.DescribeFocusedField(),
        ["nav"] = UiNav.Describe(),
        ["synthetic"] = UnityUiRuntime.DescribeSynthetic(),
    };

    /// <summary>The tip cards: the shown card's rect, its anchor panel's rect, and the card's offset from the anchor.</summary>
    public static Dictionary<string, object?> Tips()
    {
        var d = UiTips.Describe();
        var showing = UiTips.Showing;
        ScreenRect card = ScreenRect.Unknown, anchor = ScreenRect.Unknown;
        string cardSource = "none", anchorSource = "none";
        UiPanelHandle? cardPanel = null;
        try
        {
            if (showing?.View?.Panel is UiPanelHandle cp)
            {
                cardPanel = cp;
                card = PanelRect(cp);
                cardSource = "tip view";
            }

            // Live 2026-10-14 (1440p): the card's own view gave no rect. The card is a shown panel titled as the tip.
            if (!card.IsKnown && showing != null)
            {
                foreach (var p in Ui.ShownPanelsSnapshot())
                {
                    if (!string.Equals(SafeText(p), showing.Title ?? "", StringComparison.OrdinalIgnoreCase)) continue;
                    var r = PanelRect(p);
                    if (!r.IsKnown) continue;
                    cardPanel = p;
                    card = r;
                    cardSource = "shown panel titled '" + SafeText(p) + "'";
                    break;
                }
            }

            if (showing?.Anchor is UiPanelHandle ap)
            {
                anchor = PanelRect(ap);
                anchorSource = "tip anchor '" + SafeText(ap) + "'";
            }

            // No anchor of its own: the card sits beside the Mods workspace or in the corner; report the largest other
            // shown panel (the workspace when it is open) so the placement can still be checked.
            if (!anchor.IsKnown)
            {
                float best = 0f;
                foreach (var p in Ui.ShownPanelsSnapshot())
                {
                    if (cardPanel != null && ReferenceEquals(p, cardPanel)) continue;
                    var r = PanelRect(p);
                    if (!r.IsKnown) continue;
                    float area = (r.MaxX - r.MinX) * (r.MaxY - r.MinY);
                    if (area <= best) continue;
                    best = area;
                    anchor = r;
                    anchorSource = "largest shown panel '" + SafeText(p) + "'";
                }
            }
        }
        catch (Exception e) { d["readError"] = e.Message; }

        d["cardRect"] = UnityUiRuntime.RectDict(card);
        d["cardSource"] = cardSource;
        d["anchorRect"] = UnityUiRuntime.RectDict(anchor);
        d["anchorSource"] = anchorSource;
        if (card.IsKnown && anchor.IsKnown)
            d["relative"] = new Dictionary<string, object?>
            {
                ["left"] = Math.Round(card.MinX - anchor.MinX, 1),
                ["bottom"] = Math.Round(card.MinY - anchor.MinY, 1),
                ["rightGap"] = Math.Round(card.MinX - anchor.MaxX, 1),
                ["insideAnchor"] = card.MinX >= anchor.MinX && card.MaxX <= anchor.MaxX && card.MinY >= anchor.MinY && card.MaxY <= anchor.MaxY,
            };
        var (w, h) = UnityUiRuntime.ScreenSize();
        d["screen"] = w + "x" + h;
        if (card.IsKnown && w > 0 && h > 0)
        {
            d["cardOnScreen"] = card.MinX >= 0 && card.MinY >= 0 && card.MaxX <= w && card.MaxY <= h;
            d["cardMargins"] = new Dictionary<string, object?>
            {
                ["left"] = Math.Round(card.MinX, 1),
                ["bottom"] = Math.Round(card.MinY, 1),
                ["right"] = Math.Round(w - card.MaxX, 1),
                ["top"] = Math.Round(h - card.MaxY, 1),
            };
        }

        if (showing == null) d["note"] = "no tip card is showing (Show tip, or a card waits behind a dialog: see waiting)";
        d["ok"] = true;
        return d;
    }

    public static Dictionary<string, object?> Terms(string plugin)
    {
        var d = UiTerms.Describe(plugin);
        d["ok"] = true;
        return d;
    }
}
#endif
