using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.WorldRestore;

/// <summary>One file under a folder: its path relative to that folder (the platform's separator), length and write time.</summary>
internal readonly record struct WorldFileInfo(string Relative, long Length, DateTime LastWriteUtc);

/// <summary>
/// WORLDRESTORE1: every file-system operation the world restore performs, so the apply sequence runs against a fake in the
/// game-free tests (a failure can be injected at any step). File-only: no BepInEx or game type. Compiled into Core,
/// Bootstrap and the tests (Bootstrap and Core cannot reference each other).
/// </summary>
internal interface IWorldFs
{
    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// <summary>Every file under <paramref name="root"/>, recursively, with relative paths; empty when it is missing.</summary>
    IReadOnlyList<WorldFileInfo> ListFiles(string root);

    /// <summary>The immediate subfolders of <paramref name="path"/> (full paths); empty when it is missing.</summary>
    IReadOnlyList<string> ListDirectories(string path);

    Stream OpenRead(string path);

    /// <summary>
    /// Copy one file (creating the destination folder), flushed to the disk (<c>FileStream.Flush(true)</c>), and keep its last
    /// write time. Never overwrites.
    /// </summary>
    void CopyFile(string from, string to);

    void CreateDirectory(string path);

    /// <summary>Delete a folder and everything under it.</summary>
    void DeleteDirectory(string path);

    /// <summary>Rename a folder (same volume). Throws when the destination exists.</summary>
    void MoveDirectory(string from, string to);

    void MoveFile(string from, string to, bool overwrite);

    void DeleteFile(string path);

    byte[] ReadAllBytes(string path);

    /// <summary>Write a file, flushed to the disk.</summary>
    void WriteAllBytes(string path, byte[] bytes);

    DateTime GetLastWriteUtc(string path);

    void SetLastWriteUtc(string path, DateTime utc);

    /// <summary>Free bytes on the volume of <paramref name="path"/>, or null when unknown.</summary>
    long? FreeBytes(string path);

    /// <summary>Flush a folder's entries (new files, renames) to the disk; false when the platform cannot.</summary>
    bool FlushDirectory(string path);

    /// <summary>Wait before a retry (renames blocked by an antivirus or indexer lock).</summary>
    void Sleep(int milliseconds);
}

/// <summary>The real file system.</summary>
internal sealed class PhysicalWorldFs : IWorldFs
{
    internal static readonly PhysicalWorldFs Instance = new();

    public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path);

    public bool FileExists(string path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    public IReadOnlyList<WorldFileInfo> ListFiles(string root)
    {
        var list = new List<WorldFileInfo>();
        if (!DirectoryExists(root)) return list;
        string full = Path.GetFullPath(root).TrimEnd('\\', '/');
        foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            string rel = file.Substring(full.Length).TrimStart('\\', '/');
            list.Add(new WorldFileInfo(rel, info.Length, info.LastWriteTimeUtc));
        }

        return list;
    }

    public IReadOnlyList<string> ListDirectories(string path) =>
        DirectoryExists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();

    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);

    public void CopyFile(string from, string to)
    {
        string? dir = Path.GetDirectoryName(to);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var stamp = File.GetLastWriteTimeUtc(from);
        using (var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
        using (var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            source.CopyTo(target, 1 << 16);
            target.Flush(flushToDisk: true);
        }

        File.SetLastWriteTimeUtc(to, stamp);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public void MoveDirectory(string from, string to) => Directory.Move(from, to);

    public void MoveFile(string from, string to, bool overwrite) => File.Move(from, to, overwrite);

    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteAllBytes(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    public DateTime GetLastWriteUtc(string path) => File.GetLastWriteTimeUtc(path);

    public void SetLastWriteUtc(string path, DateTime utc) => File.SetLastWriteTimeUtc(path, utc);

    public long? FreeBytes(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }

    public bool FlushDirectory(string path) => WindowsFiles.FlushDirectory(path);

    public void Sleep(int milliseconds) => System.Threading.Thread.Sleep(Math.Max(0, milliseconds));
}

/// <summary>The two Windows calls the restore needs: a folder flush and a path's final form (8.3 names, junctions, links).</summary>
internal static class WindowsFiles
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private static readonly IntPtr Invalid = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder buffer, uint length, uint flags);

    internal static bool FlushDirectory(string path)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return false;
        try
        {
            var h = CreateFileW(path, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (h == Invalid || h == IntPtr.Zero) return false;
            try { return FlushFileBuffers(h); }
            finally { CloseHandle(h); }
        }
        catch { return false; }
    }

    /// <summary>The final path of an existing file or folder (<c>GetFinalPathNameByHandle</c>), without <c>\\?\</c>; null when unknown.</summary>
    internal static string? FinalPathOf(string existing)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(existing)) return null;
        try
        {
            var h = CreateFileW(existing, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (h == Invalid || h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
                if (n == 0 || n >= sb.Capacity) return null;
                return WorldRestorePaths.StripDevicePrefix(sb.ToString());
            }
            finally { CloseHandle(h); }
        }
        catch { return null; }
    }
}
