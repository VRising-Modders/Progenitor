using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: widgets that scroll themselves (the data table's virtualized body) and so take the mouse wheel over their own
/// area instead of letting it scroll the panel. The shown-input pump (the framework's only wheel reader, docs/ui.md
/// "The wheel") first finds the topmost shown panel under the pointer, then asks <see cref="TryTake"/> with that panel
/// and its visible viewport: only a claim owned by that panel, whose area holds the pointer inside the visible
/// viewport, can take the notch (a table under a dialog or another panel, or rows clipped out of view, never do); the
/// newest such claim that can move that way takes it. A claim that cannot move (the table
/// is at its end) hands the notch on, so the panel scrolls as usual. Game-free; the claims are leases released with
/// their widget, and <see cref="Reset"/> on Core unload drops every one.
/// </summary>
internal static class UiWheelClaims
{
    private sealed class Claim : IDisposable
    {
        internal Func<ScreenRect> Area = () => ScreenRect.Unknown;
        internal Func<int, bool> OnNotch = _ => false;
        internal object? Owner;
        internal bool Disposed;

        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Live.Remove(this);
        }
    }

    private static readonly List<Claim> Live = new();

    /// <summary>How many claims are live.</summary>
    internal static int Count => Live.Count;

    /// <summary>
    /// Take wheel notches over <paramref name="area"/> (screen pixels, asked on every notch). <paramref name="onNotch"/>
    /// gets +1 for one notch toward the end (the wheel turned toward the player) and -1 toward the start, and answers
    /// whether it moved; false hands the notch to the panel.
    /// </summary>
    /// <param name="owner">The runtime panel the widget's rows are on (<c>UiPanelHandle.RuntimePanel</c>); a claim with no
    /// owner never takes a notch.</param>
    internal static IDisposable Add(Func<ScreenRect> area, Func<int, bool> onNotch, object? owner)
    {
        var claim = new Claim
        {
            Area = area ?? throw new ArgumentNullException(nameof(area)),
            OnNotch = onNotch ?? throw new ArgumentNullException(nameof(onNotch)),
            Owner = owner,
        };
        Live.Add(claim);
        UiWidgetLifetime.Register("wheel-claims", Reset);
        return claim;
    }

    /// <summary>
    /// One wheel notch at the pointer (<paramref name="x"/>, <paramref name="y"/>, screen pixels) with the device's
    /// <paramref name="deltaY"/> (positive: away from the player, toward the start), over <paramref name="topmost"/> (the
    /// topmost shown panel under the pointer, null for none) whose visible viewport is <paramref name="visible"/>. True
    /// when a claim took it.
    /// </summary>
    internal static bool TryTake(float x, float y, float deltaY, object? topmost, ScreenRect visible)
    {
        if (Live.Count == 0 || deltaY == 0f || float.IsNaN(deltaY) || topmost == null) return false;
        if (!visible.Contains(x, y)) return false;
        int steps = deltaY > 0f ? -1 : 1;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            if (i >= Live.Count) continue;
            var claim = Live[i];
            if (claim.Owner == null || !ReferenceEquals(claim.Owner, topmost)) continue;
            ScreenRect area;
            try { area = claim.Area(); }
            catch { continue; }
            if (!area.Contains(x, y)) continue;
            try { return claim.OnNotch(steps); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: a widget's wheel handler threw: " + e.Message);
                return false;
            }
        }

        return false;
    }

    /// <summary>Core unload: drop every claim.</summary>
    internal static void Reset()
    {
        foreach (var c in Live.ToArray()) c.Disposed = true;
        Live.Clear();
    }
}

/// <summary>P7b: the modifier keys held at the moment of a click (multi-select: Ctrl adds, Shift extends a range).</summary>
[Flags]
internal enum UiClickModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
}
