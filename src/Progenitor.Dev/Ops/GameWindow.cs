#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Progenitor.Dev.Ops;

/// <summary>
/// BRIDGEUI1: the game's own top-level window for <c>display</c>: its rect, whether it is the foreground window, and
/// giving the foreground back to the window that had it when a resolution change raised the game. Window management
/// only: no input is sent and no other process's window is moved.
/// </summary>
internal static class GameWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    private const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010, NoOwnerZOrder = 0x0200;

    internal static IntPtr Handle()
    {
        try { return Process.GetCurrentProcess().MainWindowHandle; }
        catch { return IntPtr.Zero; }
    }

    internal static IntPtr Foreground()
    {
        try { return GetForegroundWindow(); }
        catch { return IntPtr.Zero; }
    }

    internal static bool IsForeground() => Handle() != IntPtr.Zero && Foreground() == Handle();

    internal static bool TryRect(out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        var hwnd = Handle();
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (!GetWindowRect(hwnd, out var r)) return false;
            x = r.Left;
            y = r.Top;
            w = r.Right - r.Left;
            h = r.Bottom - r.Top;
            return true;
        }
        catch { return false; }
    }

    internal static Dictionary<string, object?> Describe()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal) { ["foreground"] = IsForeground() };
        if (TryRect(out int x, out int y, out int w, out int h))
            d["rect"] = new Dictionary<string, object?> { ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h };
        return d;
    }

    /// <summary>Move the game's window to (x, y) without sizing, activating or raising it.</summary>
    internal static bool MoveTo(int x, int y)
    {
        var hwnd = Handle();
        if (hwnd == IntPtr.Zero) return false;
        try { return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate | NoOwnerZOrder); }
        catch { return false; }
    }

    /// <summary>
    /// When the game took the foreground from <paramref name="previous"/> (a resolution change raised it), give it back
    /// and put the game's window behind it, unactivated. True when it gave the foreground back.
    /// </summary>
    internal static bool GiveForegroundBack(IntPtr previous)
    {
        var hwnd = Handle();
        if (hwnd == IntPtr.Zero || previous == IntPtr.Zero || previous == hwnd) return false;
        try
        {
            if (GetForegroundWindow() != hwnd || !IsWindow(previous)) return false;
            bool back = SetForegroundWindow(previous);
            SetWindowPos(hwnd, previous, 0, 0, 0, 0, NoSize | NoMove | NoActivate | NoOwnerZOrder);
            return back;
        }
        catch { return false; }
    }
}
#endif
