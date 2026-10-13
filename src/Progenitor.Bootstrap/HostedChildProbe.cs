using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>
/// Windows half of <see cref="HostedChildTracker"/>. The tracker decides; this only talks to
/// the OS. Identity is executable path plus argv (<c>-parentPID</c> / <c>progenitor-hosted</c>),
/// read through one handle per process; the process name is only the
/// <see cref="CandidatePids"/> pre-filter. The live dedicated root is always refused by
/// <see cref="CoreWatchPolicy.IsLiveHostedChild"/>. The spawn site the dump names is
/// <see cref="CoreWatchPolicy.HostedSpawnDumpName"/> (<c>StunProcess_MONO$$Start</c>).
///
/// <para>Job HOSTPROBE: <see cref="CandidatePids"/> and <see cref="TryOpen"/> run on a
/// background thread only. <see cref="Poll"/> is the only main-thread call: one
/// <c>WaitForSingleObject(handle, 0)</c>. <c>Process.MainModule</c> is gone (it walked the
/// module list of every process and threw on access denied);
/// <c>QueryFullProcessImageName</c> answers the same question.</para>
/// </summary>
internal sealed class HostedChildProbe : IHostedChildOs
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ProcessCommandLineInformation = 60;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 0x102;
    private const uint StillActive = 259;

    internal static readonly HostedChildProbe Instance = new();

    public int[]? CandidatePids(IReadOnlyList<string> names)
    {
        var pids = new List<int>();
        for (int n = 0; n < names.Count; n++)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(names[n]); }
            catch { return null; }
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    int id = processes[i].Id;
                    if (!pids.Contains(id)) pids.Add(id);
                }
                catch
                {
                    /* exited between snapshot and read; not a candidate */
                }
                finally
                {
                    try { processes[i].Dispose(); } catch { /* ignore */ }
                }
            }
        }
        return pids.ToArray();
    }

    public bool TryOpen(int pid, out IntPtr handle, out string? path, out string? commandLine)
    {
        path = null;
        commandLine = null;
        // SYNCHRONIZE lets Poll wait on the handle; another account's process may refuse it, and
        // identity needs only limited information, so fall back to that.
        handle = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
        if (handle == IntPtr.Zero)
            handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return false;
        path = TryReadImagePath(handle);
        commandLine = TryReadCommandLine(handle);
        if (path != null || commandLine != null) return true;
        CloseHandle(handle);
        handle = IntPtr.Zero;
        return false;
    }

    public HostedChildLiveness Poll(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return HostedChildLiveness.Unknown;
        uint wait = WaitForSingleObject(handle, 0);
        if (wait == WaitTimeout) return HostedChildLiveness.Running;
        if (wait == WaitObject0) return HostedChildLiveness.Exited;
        // WAIT_FAILED: typically a handle without SYNCHRONIZE. Exit code needs only limited info.
        if (!GetExitCodeProcess(handle, out uint code)) return HostedChildLiveness.Unknown;
        return code == StillActive ? HostedChildLiveness.Running : HostedChildLiveness.Exited;
    }

    public void Close(IntPtr handle)
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    private static string? TryReadImagePath(IntPtr handle)
    {
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageName(handle, 0, sb, ref size)) return null;
            string path = sb.ToString();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryReadCommandLine(IntPtr handle)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int first = NtQueryInformationProcess(
                handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int length);
            // STATUS_INFO_LENGTH_MISMATCH (0xC0000004) is the expected first call.
            // Any other failure (including STATUS_ACCESS_DENIED = 0xC0000022) is not
            // a length probe: treat as unread so the scan can fail closed.
            const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
            if (first != StatusInfoLengthMismatch && first != 0)
                return null;
            if (length <= 0 || length > 1024 * 1024) return null;
            buffer = Marshal.AllocHGlobal(length);
            int status = NtQueryInformationProcess(
                handle, ProcessCommandLineInformation, buffer, length, out _);
            if (status != 0) return null;
            // UNICODE_STRING: ushort Length, ushort MaximumLength, IntPtr Buffer.
            ushort bytes = (ushort)Marshal.ReadInt16(buffer, 0);
            IntPtr str = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            if (str == IntPtr.Zero || bytes == 0) return null;
            return Marshal.PtrToStringUni(str, bytes / 2);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        IntPtr handle, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}
