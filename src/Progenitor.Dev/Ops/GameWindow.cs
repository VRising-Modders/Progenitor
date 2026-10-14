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

    private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    /// <summary>The engine's window class; the BepInEx console of the same process is a different window.</summary>
    internal const string GameWindowClass = "UnityWndClass";

    private static IntPtr _cached;

    /// <summary>
    /// The game's own window: this process's top-level window of the engine's class. Not the process's "main window",
    /// which can be the BepInEx console (live 2026-10-14: the foreground read the console and was always false).
    /// </summary>
    internal static IntPtr Handle()
    {
        try
        {
            if (_cached != IntPtr.Zero && IsWindow(_cached)) return _cached;
            uint self = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            var name = new System.Text.StringBuilder(64);
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != self) return true;
                name.Clear();
                GetClassName(hWnd, name, name.Capacity);
                if (!string.Equals(name.ToString(), GameWindowClass, StringComparison.Ordinal)) return true;
                found = hWnd;
                return false;
            }, IntPtr.Zero);
            _cached = found;
            return found;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>
    /// The one focus reading every <c>display</c> reply uses: <c>focused</c> is the engine's own
    /// <c>Application.isFocused</c>; <c>foreground</c> whether the game's window is the desktop's foreground window;
    /// <c>windowFound</c> whether that window was found at all (when it was not, <c>foreground</c> is null, not false).
    /// </summary>
    internal static Dictionary<string, object?> Focus()
    {
        bool? focused;
        try { focused = UnityEngine.Application.isFocused; }
        catch { focused = null; }
        var hwnd = Handle();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["focused"] = focused,
            ["foreground"] = hwnd == IntPtr.Zero ? null : Foreground() == hwnd,
            ["windowFound"] = hwnd != IntPtr.Zero,
        };
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
        var d = Focus();
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
