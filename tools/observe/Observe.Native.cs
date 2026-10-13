// tools/observe/Observe.Native.cs -- compiled at run time by tools/observe.ps1 via Add-Type.
//
// This file is NOT part of the solution and is never built by `dotnet build`. It is a text
// resource: observe.ps1 reads it and hands it to Add-Type. It lives in its own file rather than
// in a PowerShell here-string because a here-string is unreadable, undiffable and unlintable, and
// because the escaping rules of a `@'...'@` block have already cost this repo real time.
//
// LANGUAGE LEVEL: C# 5. Windows PowerShell 5.1's Add-Type uses the CodeDom C# provider, which is
// the C# 5 compiler. No string interpolation, no `out var`, no expression-bodied members, no
// `nameof`, no null-conditional. Breaking that rule produces a compile error at run time, not at
// build time, so it is not caught by CI.
//
// SAFETY: everything here is either a read (window geometry, screen pixels) or a Win32 SendInput
// call. SendInput drives the REAL keyboard and mouse of the logged-in desktop session. See the
// header of tools/observe.ps1.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.Observe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // Sequential INPUT over an explicit union: the union's IntPtr member forces 8-byte alignment,
    // so the union lands at offset 8 on x64 and offset 4 on x86 -- which is exactly the Win32
    // layout on both. Do not "simplify" this to an explicit INPUT with a hard-coded FieldOffset.
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    public static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint pcrKey, out byte pbAlpha, out uint pdwFlags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int nIndex);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        // SetLastError = true, because Capture.FromPrintWindow REPORTS the error number when this
        // returns false. Without it the CLR does not capture the thread's last error across the
        // transition and Marshal.GetLastWin32Error() hands back whatever ran before -- a number
        // that reads exactly like SendInput's (declared correctly above) and is not this call's.
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;
        public const uint GA_ROOT = 2;
        public const uint GW_HWNDNEXT = 2;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;
        public const uint LWA_ALPHA = 0x00000002;
        public const int SW_RESTORE = 9;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080;
        public const uint MOUSEEVENTF_XUP = 0x0100;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_HWHEEL = 0x1000;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        public const uint PW_RENDERFULLCONTENT = 0x00000002;
    }

    public class WindowRow
    {
        public IntPtr Hwnd;
        public int ProcessId;
        public string ProcessName;
        public string Title;
        public string ClassName;
        public int Left, Top, Width, Height;
        public bool Visible;
        public bool Minimised;
        public bool Cloaked;
        public bool Foreground;
    }

    public static class Windows
    {
        // PER_MONITOR_AWARE_V2. Thread-scoped so it cannot disturb anything the host already did,
        // and unlike the process-wide call it has no "must be first" ordering rule. Falls back to
        // the process-wide legacy call on a host old enough to lack the thread API.
        public static string MakeDpiAware()
        {
            try
            {
                IntPtr prev = Native.SetThreadDpiAwarenessContext(new IntPtr(-4));
                if (prev != IntPtr.Zero) { return "per-monitor-v2 (thread)"; }
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
            try { if (Native.SetProcessDPIAware()) { return "system (process)"; } }
            catch (Exception) { }
            return "none (coordinates may be virtualised on a scaled display)";
        }

        public static string TextOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(512);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassOf(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            int cloaked = 0;
            int hr = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, sizeof(int));
            if (hr != 0) { return false; }
            return cloaked != 0;
        }

        // The visible bounds. DWM's extended frame bounds excludes the invisible resize border
        // Windows 10/11 draws outside GetWindowRect; capturing GetWindowRect therefore includes a
        // strip of whatever is BEHIND the window on the left, right and bottom. Falls back to
        // GetWindowRect when DWM refuses (hr != 0).
        public static RECT VisibleBounds(IntPtr hwnd)
        {
            RECT r;
            int hr = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT)));
            if (hr == 0 && r.Right > r.Left && r.Bottom > r.Top) { return r; }
            Native.GetWindowRect(hwnd, out r);
            return r;
        }

        public static RECT ClientBounds(IntPtr hwnd)
        {
            RECT c;
            Native.GetClientRect(hwnd, out c);
            POINT origin = new POINT();
            origin.X = 0; origin.Y = 0;
            Native.ClientToScreen(hwnd, ref origin);
            RECT r = new RECT();
            r.Left = origin.X;
            r.Top = origin.Y;
            r.Right = origin.X + (c.Right - c.Left);
            r.Bottom = origin.Y + (c.Bottom - c.Top);
            return r;
        }

        public static List<WindowRow> Enumerate(bool visibleOnly, bool titledOnly)
        {
            List<WindowRow> rows = new List<WindowRow>();
            IntPtr fg = Native.GetForegroundWindow();
            Native.EnumWindowsProc cb = delegate(IntPtr hwnd, IntPtr lp)
            {
                bool visible = Native.IsWindowVisible(hwnd);
                if (visibleOnly && !visible) { return true; }
                string title = TextOf(hwnd);
                if (titledOnly && string.IsNullOrEmpty(title)) { return true; }
                uint pid = 0;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                RECT r = VisibleBounds(hwnd);
                WindowRow row = new WindowRow();
                row.Hwnd = hwnd;
                row.ProcessId = (int)pid;
                row.ProcessName = "";
                row.Title = title;
                row.ClassName = ClassOf(hwnd);
                row.Left = r.Left; row.Top = r.Top;
                row.Width = r.Right - r.Left; row.Height = r.Bottom - r.Top;
                row.Visible = visible;
                row.Minimised = Native.IsIconic(hwnd);
                row.Cloaked = IsCloaked(hwnd);
                row.Foreground = (hwnd == fg);
                rows.Add(row);
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return rows;
        }

        // Bring `hwnd` to the front. Returns true only if it is ACTUALLY foreground afterwards.
        // The AttachThreadInput dance is required because Windows refuses SetForegroundWindow from
        // a process that does not own the foreground. There is no way to do this without taking
        // the user's focus -- that is the point, and it is why the callers shout about it.
        public static bool Focus(IntPtr hwnd, int timeoutMs)
        {
            if (Native.IsIconic(hwnd)) { Native.ShowWindow(hwnd, Native.SW_RESTORE); }
            if (Native.GetForegroundWindow() == hwnd) { return true; }

            IntPtr fg = Native.GetForegroundWindow();
            uint fgPid = 0;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out fgPid);
            uint myThread = Native.GetCurrentThreadId();
            bool attached = false;
            if (fgThread != 0 && fgThread != myThread) { attached = Native.AttachThreadInput(myThread, fgThread, true); }
            try
            {
                Native.BringWindowToTop(hwnd);
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) { Native.AttachThreadInput(myThread, fgThread, false); }
            }

            int waited = 0;
            while (waited < timeoutMs)
            {
                if (Native.GetForegroundWindow() == hwnd) { return true; }
                System.Threading.Thread.Sleep(25);
                waited += 25;
            }
            return Native.GetForegroundWindow() == hwnd;
        }

        // Would a window ABOVE the target actually put pixels on the glass?
        //
        // The obvious implementation -- WindowFromPoint over a grid -- is WRONG, and measurably
        // so: on this machine it reported the V Rising client 100% occluded by "Windows Default
        // Lock Screen". WindowFromPoint honors WS_EX_TRANSPARENT and hidden windows but knows
        // nothing about DWM CLOAKING, and a modern Windows desktop is carrying several cloaked
        // full-screen CoreWindows (the lock screen, suspended UWP apps) that own every point on
        // the desktop and draw none of them. So the test is a Z-order walk with explicit filters
        // instead, which also yields real blocker names and a real covered fraction.
        private static bool CanOcclude(IntPtr h, out RECT r)
        {
            r = new RECT();
            if (!Native.IsWindowVisible(h)) { return false; }
            if (Native.IsIconic(h)) { return false; }
            if (IsCloaked(h)) { return false; }              // the lock-screen case
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            if ((ex & Native.WS_EX_TRANSPARENT) != 0) { return false; }   // click-through overlay
            if ((ex & Native.WS_EX_LAYERED) != 0)
            {
                uint key; byte alpha; uint flags;
                if (Native.GetLayeredWindowAttributes(h, out key, out alpha, out flags))
                {
                    if ((flags & Native.LWA_ALPHA) != 0 && alpha == 0) { return false; }  // fully see-through
                }
            }
            r = VisibleBounds(h);
            if (r.Right <= r.Left || r.Bottom <= r.Top) { return false; }
            return true;
        }

        public static string OcclusionReport(IntPtr hwnd, RECT rect, int grid, out int covered, out int total)
        {
            covered = 0;
            total = 0;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w <= 0 || h <= 0) { return ""; }

            // Every top-level window strictly above the target, top-most first.
            List<IntPtr> above = new List<IntPtr>();
            IntPtr cur = Native.GetTopWindow(IntPtr.Zero);
            int guard = 0;
            while (cur != IntPtr.Zero && guard < 5000)
            {
                guard++;
                if (cur == hwnd) { break; }
                above.Add(cur);
                cur = Native.GetWindow(cur, Native.GW_HWNDNEXT);
            }
            // The walk never reached the target (it is not a top-level window, or the Z-order
            // changed under us). Report nothing rather than claim total occlusion.
            if (cur != hwnd) { return ""; }

            List<IntPtr> handles = new List<IntPtr>();
            List<RECT> rects = new List<RECT>();
            foreach (IntPtr candidate in above)
            {
                RECT cr;
                if (!CanOcclude(candidate, out cr)) { continue; }
                if (cr.Right <= rect.Left || cr.Left >= rect.Right || cr.Bottom <= rect.Top || cr.Top >= rect.Bottom) { continue; }
                handles.Add(candidate);
                rects.Add(cr);
            }
            if (handles.Count == 0) { total = grid * grid; return ""; }

            Dictionary<string, int> blockers = new Dictionary<string, int>();
            for (int gy = 0; gy < grid; gy++)
            {
                for (int gx = 0; gx < grid; gx++)
                {
                    int px = rect.Left + (int)((gx + 0.5) * w / grid);
                    int py = rect.Top + (int)((gy + 0.5) * h / grid);
                    total++;
                    for (int i = 0; i < rects.Count; i++)
                    {
                        RECT cr = rects[i];
                        if (px < cr.Left || px >= cr.Right || py < cr.Top || py >= cr.Bottom) { continue; }
                        covered++;
                        string name = TextOf(handles[i]);
                        if (string.IsNullOrEmpty(name)) { name = "<untitled " + ClassOf(handles[i]) + ">"; }
                        if (blockers.ContainsKey(name)) { blockers[name] = blockers[name] + 1; }
                        else { blockers[name] = 1; }
                        break;   // topmost blocker wins; do not double-count
                    }
                }
            }
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in blockers) { parts.Add(kv.Key + " (" + kv.Value + ")"); }
            return string.Join(", ", parts.ToArray());
        }
    }

    public class CaptureStats
    {
        public double Mean;
        public double StdDev;
        public int UniqueColours;
        public int Sampled;
        public string TopColour;
        public double TopColourShare;
    }

    public static class Capture
    {
        // CopyFromScreen over the window's screen rectangle. Correct for a D3D/DXGI swapchain,
        // which is exactly what PrintWindow is NOT -- see the tool header. Requires the pixels to
        // actually be on screen, so the caller checks occlusion and minimization first.
        public static Bitmap FromScreen(RECT r)
        {
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            // The Bitmap is allocated OUTSIDE the try that can throw, so the throw has to dispose
            // it: the caller never receives the reference and its own finally cannot free what it
            // was never given (R44 finding 20). GDI's own handles are already paired below.
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
            return bmp;
        }

        // PW_RENDERFULLCONTENT. Kept as an explicit opt-in for the occluded case; it returns black
        // for most D3D swapchains, which is why the caller must still run the blank check.
        public static Bitmap FromPrintWindow(IntPtr hwnd, RECT r)
        {
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            // Same as FromScreen: the throw below owns this bitmap until the caller gets it back.
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        bool ok = Native.PrintWindow(hwnd, hdc, Native.PW_RENDERFULLCONTENT);
                        if (!ok)
                        {
                            // PrintWindow is declared with SetLastError = true, so this number is
                            // ITS error. Without that attribute the CLR does not capture the thread's
                            // last error across the transition and GetLastWin32Error returns whatever
                            // ran before -- which read identically to the SendInput message beside it,
                            // and only one of the two was true (R44 finding 18). A PrintWindow that
                            // returns false without setting an error reports 0, so say nothing then
                            // rather than print "win32 error 0" as if it were a diagnosis.
                            int err = Marshal.GetLastWin32Error();
                            string detail = (err != 0) ? (" (win32 error " + err + ")") : " (the call set no win32 error code)";
                            throw new InvalidOperationException("PrintWindow returned false" + detail);
                        }
                    }
                    finally { g.ReleaseHdc(hdc); }
                }
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
            return bmp;
        }

        // Mean/stddev of luminance plus a color histogram, over a subsample. A capture that is
        // all black, or all one color, is the classic silent failure of every screenshot tool
        // ever written against a game; this is what makes it loud instead.
        public static CaptureStats Analyse(Bitmap bmp, int step)
        {
            if (step < 1) { step = 1; }
            CaptureStats s = new CaptureStats();
            Dictionary<int, int> hist = new Dictionary<int, int>();
            double sum = 0;
            double sumSq = 0;
            int n = 0;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] row = new byte[stride];
                for (int y = 0; y < bmp.Height; y += step)
                {
                    IntPtr rowPtr = new IntPtr(data.Scan0.ToInt64() + (long)y * stride);
                    Marshal.Copy(rowPtr, row, 0, stride);
                    for (int x = 0; x < bmp.Width; x += step)
                    {
                        int i = x * 4;
                        int b = row[i];
                        int g = row[i + 1];
                        int r = row[i + 2];
                        double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                        sum += lum;
                        sumSq += lum * lum;
                        n++;
                        // Quantize to 5 bits per channel so JPEG-ish noise does not read as detail.
                        int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                        if (hist.ContainsKey(key)) { hist[key] = hist[key] + 1; } else { hist[key] = 1; }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }

            s.Sampled = n;
            s.UniqueColours = hist.Count;
            if (n > 0)
            {
                s.Mean = sum / n;
                double var = (sumSq / n) - (s.Mean * s.Mean);
                if (var < 0) { var = 0; }
                s.StdDev = Math.Sqrt(var);
            }
            int bestKey = 0;
            int bestCount = 0;
            foreach (KeyValuePair<int, int> kv in hist)
            {
                if (kv.Value > bestCount) { bestCount = kv.Value; bestKey = kv.Key; }
            }
            if (n > 0)
            {
                int rr = ((bestKey >> 10) & 31) << 3;
                int gg = ((bestKey >> 5) & 31) << 3;
                int bb = (bestKey & 31) << 3;
                s.TopColour = string.Format("#{0:X2}{1:X2}{2:X2}", rr, gg, bb);
                s.TopColourShare = (double)bestCount / n;
            }
            return s;
        }
    }

    public class InputStep
    {
        public string Kind;
        public string Detail;
        public InputStep(string kind, string detail) { Kind = kind; Detail = detail; }
    }

    // Builds a SendInput batch and either sends it or hands it back for a dry run. Every public
    // entry point returns the human-readable step list, so `-DryRun` and the real path describe
    // the SAME batch -- a dry run that is generated by a second code path is a dry run that lies.
    public class Injector
    {
        private List<INPUT> _inputs = new List<INPUT>();
        private List<InputStep> _steps = new List<InputStep>();

        public List<InputStep> Steps { get { return _steps; } }
        public int Count { get { return _inputs.Count; } }

        private static readonly HashSet<ushort> Extended = new HashSet<ushort>(new ushort[]
        {
            0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home Left Up Right Down
            0x2D, 0x2E,                                     // Insert Delete
            0x90, 0xA3, 0xA5, 0x6F, 0x2C, 0x5B, 0x5C, 0x5D  // NumLock RCtrl RAlt NumpadDiv PrtScr LWin RWin Apps
        });

        public void KeyDown(ushort vk) { Key(vk, false); }
        public void KeyUp(ushort vk) { Key(vk, true); }

        private void Key(ushort vk, bool up)
        {
            ushort scan = (ushort)Native.MapVirtualKey(vk, 0);
            uint flags = 0;
            if (up) { flags |= Native.KEYEVENTF_KEYUP; }
            if (Extended.Contains(vk)) { flags |= Native.KEYEVENTF_EXTENDEDKEY; }
            INPUT i = new INPUT();
            i.type = Native.INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            // Both wVk and wScan are filled and KEYEVENTF_SCANCODE is NOT set: Windows then
            // delivers the virtual key AND carries our scan code into the WM_INPUT record, so a
            // consumer reading raw scan codes (Unity's Input System, DirectInput) and a consumer
            // reading virtual keys both see the press. Sending vk with wScan=0 is the usual
            // mistake and is invisible to raw-input readers.
            i.u.ki.wScan = scan;
            i.u.ki.dwFlags = flags;
            i.u.ki.time = 0;
            i.u.ki.dwExtraInfo = IntPtr.Zero;
            _inputs.Add(i);
            _steps.Add(new InputStep(up ? "keyup" : "keydown",
                string.Format("vk=0x{0:X2} scan=0x{1:X2}{2}", vk, scan, ((flags & Native.KEYEVENTF_EXTENDEDKEY) != 0) ? " extended" : "")));
        }

        public void Char(char c)
        {
            INPUT down = new INPUT();
            down.type = Native.INPUT_KEYBOARD;
            down.u.ki.wVk = 0;
            down.u.ki.wScan = c;
            down.u.ki.dwFlags = Native.KEYEVENTF_UNICODE;
            _inputs.Add(down);
            INPUT up = down;
            up.u.ki.dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP;
            _inputs.Add(up);
            _steps.Add(new InputStep("char", string.Format("'{0}' (U+{1:X4})", c, (int)c)));
        }

        // Absolute move over the VIRTUAL desktop. MOUSEEVENTF_VIRTUALDESK is what makes a
        // secondary monitor (or a negative origin) work; without it the normalized 0..65535 range
        // covers the primary monitor only and every click lands on the wrong screen.
        public void MoveAbsolute(int screenX, int screenY)
        {
            int vx = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int vy = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int vw = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int vh = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            // The clamp is on the DENOMINATOR, which is (vw - 1), not on vw. It used to read
            // `if (vw <= 1) { vw = 1; }` -- one off the thing it was protecting: vw = 1 makes
            // vw - 1 exactly 0, the double division yields +/-Infinity, and the unchecked (int)
            // cast turns that into int.MinValue, so the pointer is driven to a garbage normalized
            // coordinate instead of the call being refused (R44 finding 20). A virtual desktop
            // narrower than 2 px is degenerate either way; this makes it harmless rather than wild.
            if (vw < 2) { vw = 2; }
            if (vh < 2) { vh = 2; }
            // The 65535 range maps to (vw - 1) pixels; the +0.5 rounds to the nearest pixel center
            // instead of always landing one pixel short at the right and bottom edges.
            int nx = (int)(((double)(screenX - vx) * 65535.0 / (double)(vw - 1)) + 0.5);
            int ny = (int)(((double)(screenY - vy) * 65535.0 / (double)(vh - 1)) + 0.5);
            INPUT i = new INPUT();
            i.type = Native.INPUT_MOUSE;
            i.u.mi.dx = nx;
            i.u.mi.dy = ny;
            i.u.mi.dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK;
            _inputs.Add(i);
            _steps.Add(new InputStep("move", string.Format("screen {0},{1} (normalised {2},{3})", screenX, screenY, nx, ny)));
        }

        // Relative move: what a mouse actually reports, and the only kind a raw-input camera
        // (V Rising's free-look) reacts to. An absolute move does not rotate the camera.
        public void MoveRelative(int dx, int dy)
        {
            INPUT i = new INPUT();
            i.type = Native.INPUT_MOUSE;
            i.u.mi.dx = dx;
            i.u.mi.dy = dy;
            i.u.mi.dwFlags = Native.MOUSEEVENTF_MOVE;
            _inputs.Add(i);
            _steps.Add(new InputStep("move-relative", string.Format("delta {0},{1}", dx, dy)));
        }

        public void MouseButton(string button, bool up)
        {
            uint flag;
            string name = button.ToLowerInvariant();
            if (name == "left") { flag = up ? Native.MOUSEEVENTF_LEFTUP : Native.MOUSEEVENTF_LEFTDOWN; }
            else if (name == "right") { flag = up ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_RIGHTDOWN; }
            else if (name == "middle") { flag = up ? Native.MOUSEEVENTF_MIDDLEUP : Native.MOUSEEVENTF_MIDDLEDOWN; }
            else { throw new ArgumentException("unknown mouse button '" + button + "' (left, right, middle)"); }
            INPUT i = new INPUT();
            i.type = Native.INPUT_MOUSE;
            i.u.mi.dwFlags = flag;
            _inputs.Add(i);
            _steps.Add(new InputStep(up ? "mouseup" : "mousedown", name));
        }

        public void Wheel(int notches, bool horizontal)
        {
            INPUT i = new INPUT();
            i.type = Native.INPUT_MOUSE;
            i.u.mi.mouseData = unchecked((uint)(notches * 120));
            i.u.mi.dwFlags = horizontal ? Native.MOUSEEVENTF_HWHEEL : Native.MOUSEEVENTF_WHEEL;
            _inputs.Add(i);
            _steps.Add(new InputStep(horizontal ? "hwheel" : "wheel",
                string.Format("{0} notch(es) = {1}", notches, notches * 120)));
        }

        // Sends everything queued so far and clears the queue. Throws on a short write: SendInput
        // returning fewer than nInputs means the batch was blocked (UIPI / another process's
        // low-level hook), and silently continuing would produce a stuck modifier key.
        public int Send()
        {
            if (_inputs.Count == 0) { return 0; }
            INPUT[] arr = _inputs.ToArray();
            int size = Marshal.SizeOf(typeof(INPUT));
            uint sent = Native.SendInput((uint)arr.Length, arr, size);
            int err = Marshal.GetLastWin32Error();
            _inputs.Clear();
            if (sent != (uint)arr.Length)
            {
                throw new InvalidOperationException(string.Format(
                    "SendInput accepted {0} of {1} events (win32 error {2}). The batch was blocked -- " +
                    "usually UIPI (the target runs elevated and this shell does not) or another process's " +
                    "low-level input hook.", sent, arr.Length, err));
            }
            return (int)sent;
        }

        public void Clear() { _inputs.Clear(); }
    }
}
