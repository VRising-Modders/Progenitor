using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.Dev.Ops;

/// <summary>
/// The game window's state when the freeze watchdog sees the frame counter stop or restart.
/// A stopped frame counter whose main thread waits for the last presentation (no managed frame)
/// is the engine holding the frame loop for a minimized or hidden window, not Progenitor code;
/// see <c>knowledge/notes/freeze-dump-main-thread-in-present-wait.md</c>. Recording the window
/// state and the user's input idle time beside every freeze makes that case readable from the
/// log alone, without opening the dump.
/// </summary>
internal readonly struct FreezeWindowState
{
    /// <summary>A top-level Unity window of this process was found.</summary>
    public readonly bool Found;
    public readonly bool Minimized;
    public readonly bool Visible;
    public readonly bool Foreground;
    /// <summary>The desktop window manager hides the window (another virtual desktop, suspended app).</summary>
    public readonly bool Cloaked;
    /// <summary>Seconds since the last keyboard or mouse input in this session; -1 when unknown.</summary>
    public readonly long UserIdleSeconds;

    public FreezeWindowState(bool found, bool minimized, bool visible, bool foreground, bool cloaked, long userIdleSeconds)
    {
        Found = found;
        Minimized = minimized;
        Visible = visible;
        Foreground = foreground;
        Cloaked = cloaked;
        UserIdleSeconds = userIdleSeconds;
    }

    public static readonly FreezeWindowState Unknown = new(false, false, false, false, false, -1);

    /// <summary>True when the window cannot be presenting: minimized, hidden or cloaked.</summary>
    public bool Hidden => Found && (Minimized || !Visible || Cloaked);

    /// <summary>Stable key=value text for the freeze log lines.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (!Found) sb.Append("window=none");
        else
        {
            sb.Append("window=").Append(Hidden ? "hidden" : "shown");
            sb.Append(" minimized=").Append(Minimized ? 1 : 0);
            sb.Append(" visible=").Append(Visible ? 1 : 0);
            sb.Append(" cloaked=").Append(Cloaked ? 1 : 0);
            sb.Append(" foreground=").Append(Foreground ? 1 : 0);
        }
        sb.Append(" userIdleS=").Append(UserIdleSeconds.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// What the window state suggests about a stopped frame loop. Only a hint: the dump's
    /// main-thread stack is the evidence.
    /// </summary>
    public string Hint()
    {
        if (Hidden)
            return "the game window is minimized or hidden; the engine's frame loop can wait for the last presentation"
                + " until the window is shown again (not Progenitor code; read the dump's main thread to confirm)";
        if (UserIdleSeconds >= 60)
            return "no user input for " + UserIdleSeconds.ToString(CultureInfo.InvariantCulture)
                + " s; a sleeping display or locked session can hold the presentation the frame loop waits for"
                + " (read the dump's main thread to confirm)";
        return "window shown and user active; read the dump's main thread for the blocking call";
    }

#if PROGENITOR_TESTS
    /// <summary>Test hook replacing the native read.</summary>
    internal static Func<FreezeWindowState>? ReadOverride;
#endif

    /// <summary>
    /// Reads the state from a background thread. Plain user32/dwmapi queries that never send a
    /// message to the (possibly blocked) game window, so they cannot wait on the main thread.
    /// Never throws.
    /// </summary>
    public static FreezeWindowState Read()
    {
#if PROGENITOR_TESTS
        var o = ReadOverride;
        if (o != null) return o();
#endif
        try
        {
            long idle = UserIdleSecondsNow();
            IntPtr hwnd = FindUnityWindow();
            if (hwnd == IntPtr.Zero) return new FreezeWindowState(false, false, false, false, false, idle);
            bool minimized = IsIconic(hwnd);
            bool visible = IsWindowVisible(hwnd);
            bool foreground = GetForegroundWindow() == hwnd;
            bool cloaked = false;
            try
            {
                cloaked = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int c, sizeof(int)) == 0 && c != 0;
            }
            catch { }
            return new FreezeWindowState(true, minimized, visible, foreground, cloaked, idle);
        }
        catch
        {
            return Unknown;
        }
    }

    private const int DwmwaCloaked = 14;

    private static long UserIdleSecondsNow()
    {
        try
        {
            var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return -1;
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - info.dwTime) / 1000;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// First top-level window of this process whose class is Unity's player window. Walks
    /// <c>FindWindowEx</c> rather than <c>EnumWindows</c>: this assembly loads collectible, and a
    /// managed callback cannot be marshaled to native code from a collectible assembly.
    /// </summary>
    private static IntPtr FindUnityWindow()
    {
        uint self = (uint)Environment.ProcessId;
        IntPtr h = IntPtr.Zero;
        for (int guard = 0; guard < 4096; guard++)
        {
            h = FindWindowEx(IntPtr.Zero, h, "UnityWndClass", null);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) return h;
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
}
