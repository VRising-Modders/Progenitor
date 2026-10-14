using System;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the data table's scroll window over its flattened body lines (group headers, rows, drawer lines, subtotals, the
/// total), game-free. The body has <see cref="Slots"/> slot rows built once; line <c>Top + i</c> is drawn in slot <c>i</c>.
/// The window moves in whole lines (the kit's scroll step rule: a step is a row, never a fraction of one), so a scroll
/// rewrites the slots' cells in place and nothing is laid out again.
/// </summary>
internal sealed class UiTableViewport
{
    /// <summary>The fewest and most slot rows a table may ask for.</summary>
    internal const int MinSlots = 4;

    internal const int MaxSlots = 30;

    /// <summary>Lines one wheel notch moves: the panel's own wheel step (<see cref="MenuPanelLayout.WheelRowsPerNotch"/>).</summary>
    internal static int RowsPerNotch => Math.Max(1, (int)Math.Round(MenuPanelLayout.WheelRowsPerNotch));

    internal UiTableViewport(int slots) => Slots = ClampSlots(slots);

    internal static int ClampSlots(int slots) => slots < MinSlots ? MinSlots : slots > MaxSlots ? MaxSlots : slots;

    /// <summary>The slot rows.</summary>
    internal int Slots { get; }

    /// <summary>The body lines.</summary>
    internal int Count { get; private set; }

    /// <summary>The first line drawn.</summary>
    internal int Top { get; private set; }

    /// <summary>The largest <see cref="Top"/>: the last line sits in the last slot.</summary>
    internal int MaxTop => Math.Max(0, Count - Slots);

    /// <summary>The slots that draw a line (the rest are hidden).</summary>
    internal int Shown => Math.Max(0, Math.Min(Slots, Count - Top));

    /// <summary>The window scrolls at all.</summary>
    internal bool Scrolls => Count > Slots;

    /// <summary>Set the line count; the top is clamped. True when the top moved.</summary>
    internal bool SetCount(int count)
    {
        Count = Math.Max(0, count);
        return SetTop(Top);
    }

    /// <summary>Move the top to <paramref name="top"/>, clamped. True when it moved.</summary>
    internal bool SetTop(int top)
    {
        int next = top < 0 ? 0 : top > MaxTop ? MaxTop : top;
        if (next == Top) return false;
        Top = next;
        return true;
    }

    /// <summary>Scroll by <paramref name="lines"/> (positive: toward the end). True when it moved.</summary>
    internal bool ScrollBy(int lines) => lines != 0 && SetTop(Top + lines);

    /// <summary>
    /// One wheel notch (+1 toward the end): <see cref="RowsPerNotch"/> lines. False at the end in that direction, so the
    /// notch goes on to the panel.
    /// </summary>
    internal bool Wheel(int notch) => notch != 0 && ScrollBy(Math.Sign(notch) * RowsPerNotch * Math.Abs(notch));

    /// <summary>Whether line <paramref name="index"/> is drawn now.</summary>
    internal bool IsShown(int index) => index >= Top && index < Top + Shown;

    /// <summary>The slot drawing line <paramref name="index"/>, or -1.</summary>
    internal int SlotOf(int index) => IsShown(index) ? index - Top : -1;

    /// <summary>The line slot <paramref name="slot"/> draws, or -1.</summary>
    internal int LineAt(int slot) => slot >= 0 && slot < Shown ? Top + slot : -1;

    /// <summary>Scroll the least so line <paramref name="index"/> is drawn. True when it moved.</summary>
    internal bool EnsureShown(int index)
    {
        if (index < 0 || index >= Count) return false;
        if (index < Top) return SetTop(index);
        if (index >= Top + Slots) return SetTop(index - Slots + 1);
        return false;
    }

    /// <summary>The scroll bar's handle size: the drawn share of the lines (1 when everything is drawn).</summary>
    internal float BarSize => Count <= Slots || Count == 0 ? 1f : Math.Max(0.02f, (float)Slots / Count);

    /// <summary>The scroll bar's position: 0 at the top, 1 at the end.</summary>
    internal float BarPosition => MaxTop == 0 ? 0f : (float)Top / MaxTop;

    /// <summary>The top a bar position (0 the top, 1 the end) stands for: the nearest whole line.</summary>
    internal int TopForBar(float position)
    {
        if (float.IsNaN(position)) return Top;
        float p = position < 0f ? 0f : position > 1f ? 1f : position;
        return (int)Math.Round(p * MaxTop, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Right-stick scrolling: <paramref name="amount"/> (-1..1 per frame, positive up) adds to a carry, and every whole
    /// line of carry moves one line (about 15 lines a second at full tilt and 60 frames). True when the stick was taken
    /// (the window can move that way); false hands it to the panel.
    /// </summary>
    internal bool Stick(float amount)
    {
        if (float.IsNaN(amount) || amount == 0f) return false;
        int dir = amount > 0f ? -1 : 1; // stick up: toward the start
        bool can = dir < 0 ? Top > 0 : Top < MaxTop;
        if (!can)
        {
            _stickCarry = 0f;
            return false;
        }

        _stickCarry += Math.Abs(amount) * StickLinesPerFrame;
        if (_stickCarry >= 1f)
        {
            int lines = (int)_stickCarry;
            _stickCarry -= lines;
            ScrollBy(dir * lines);
        }

        return true;
    }

    /// <summary>Lines a full stick deflection adds per frame.</summary>
    internal const float StickLinesPerFrame = 0.25f;

    private float _stickCarry;
}
