using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Progenitor.WorldRestore;

namespace Progenitor.Tests;

/// <summary>
/// WORLDRESTORE1: an in-memory <see cref="IWorldFs"/> for the restore's apply sequence. Every call counts as one operation;
/// <see cref="FailAt"/> makes the n-th one throw (an <see cref="IOException"/>), so a test can fail each step in turn.
/// </summary>
internal sealed class FakeWorldFs : IWorldFs
{
    private readonly Dictionary<string, (byte[] Data, DateTime Mtime)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    internal static readonly string Root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "/", "fakeworld");

    /// <summary>Operations so far.</summary>
    internal int Ops { get; private set; }

    /// <summary>The operation number that throws (1-based); 0: none.</summary>
    internal int FailAt { get; set; }

    /// <summary>Only operations whose name and path this accepts count toward <see cref="FailAt"/> (null: all).</summary>
    internal Func<string, string, bool>? FailWhen { get; set; }

    internal long? Free { get; set; } = 100L * 1024 * 1024 * 1024;

    /// <summary>Operations this refuses every time (a lock that does not go away), by name and path.</summary>
    internal Func<string, string, bool>? Deny { get; set; }

    /// <summary>
    /// A crash: from this operation on (counted like <see cref="FailAt"/>) every call throws and changes nothing, as if the
    /// process had died there, until <see cref="Revive"/> (the next start).
    /// </summary>
    internal int CrashAt { get; set; }

    internal bool Crashed { get; private set; }

    internal int Slept { get; private set; }

    internal void Revive()
    {
        Crashed = false;
        CrashAt = 0;
        FailAt = 0;
        FailWhen = null;
    }

    internal DateTime Clock { get; set; } = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    internal List<string> Log { get; } = new();

    /// <summary>BACKUPNOW1: called before every operation (name, path), not counted: a test changes the folder mid-copy here.</summary>
    internal Action<string, string>? Before { get; set; }

    private static string N(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private void Op(string name, string path)
    {
        if (Crashed) throw new IOException("the process is gone");
        var before = Before;
        if (before != null)
        {
            Before = null;
            try { before(name, path); }
            finally { if (Before == null) Before = before; }
        }

        if (Deny != null && Deny(name, path)) throw new IOException("denied: " + name + " " + path);
        if (FailWhen != null && !FailWhen(name, path)) return;
        Ops++;
        Log.Add(Ops + " " + name + " " + path);
        if (CrashAt > 0 && Ops >= CrashAt)
        {
            Crashed = true;
            throw new IOException("crash at " + name + " " + path);
        }

        if (FailAt > 0 && Ops == FailAt) throw new IOException("injected failure at " + name + " " + path);
    }

    public bool FlushDirectory(string path)
    {
        Op("flushdir", path);
        return true;
    }

    public void Sleep(int milliseconds)
    {
        if (Crashed) throw new IOException("the process is gone");
        Slept += milliseconds;
    }

    private void AddDirs(string dir)
    {
        string? d = N(dir);
        while (!string.IsNullOrEmpty(d))
        {
            _dirs.Add(d);
            d = Path.GetDirectoryName(d);
        }
    }

    public bool DirectoryExists(string path)
    {
        Op("exists-dir", path);
        return _dirs.Contains(N(path));
    }

    public bool FileExists(string path)
    {
        Op("exists-file", path);
        return _files.ContainsKey(N(path));
    }

    public IReadOnlyList<WorldFileInfo> ListFiles(string root)
    {
        Op("list-files", root);
        string r = N(root) + Path.DirectorySeparatorChar;
        var list = new List<WorldFileInfo>();
        foreach (var (k, v) in _files)
            if (k.StartsWith(r, StringComparison.OrdinalIgnoreCase)) list.Add(new WorldFileInfo(k.Substring(r.Length), v.Data.Length, v.Mtime));
        return list;
    }

    public IReadOnlyList<string> ListDirectories(string path)
    {
        Op("list-dirs", path);
        string p = N(path);
        return _dirs.Where(d => string.Equals(Path.GetDirectoryName(d), p, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public Stream OpenRead(string path)
    {
        Op("open", path);
        if (!_files.TryGetValue(N(path), out var f)) throw new FileNotFoundException(path);
        return new MemoryStream(f.Data, writable: false);
    }

    public void CopyFile(string from, string to)
    {
        Op("copy", to);
        if (!_files.TryGetValue(N(from), out var f)) throw new FileNotFoundException(from);
        if (_files.ContainsKey(N(to))) throw new IOException("exists: " + to);
        AddDirs(Path.GetDirectoryName(N(to))!);
        _files[N(to)] = ((byte[])f.Data.Clone(), f.Mtime);
    }

    public void CreateDirectory(string path)
    {
        Op("mkdir", path);
        AddDirs(path);
    }

    public void DeleteDirectory(string path)
    {
        Op("rmdir", path);
        string p = N(path);
        string prefix = p + Path.DirectorySeparatorChar;
        foreach (var k in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList()) _files.Remove(k);
        foreach (var d in _dirs.Where(d => d.Equals(p, StringComparison.OrdinalIgnoreCase) || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _dirs.Remove(d);
    }

    public void MoveDirectory(string from, string to)
    {
        Op("movedir", from + " -> " + to);
        string f = N(from), t = N(to);
        if (!_dirs.Contains(f)) throw new DirectoryNotFoundException(from);
        if (_dirs.Contains(t)) throw new IOException("exists: " + to);
        string fp = f + Path.DirectorySeparatorChar;
        foreach (var k in _files.Keys.Where(k => k.StartsWith(fp, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var v = _files[k];
            _files.Remove(k);
            _files[t + k.Substring(f.Length)] = v;
        }

        foreach (var d in _dirs.Where(d => d.Equals(f, StringComparison.OrdinalIgnoreCase) || d.StartsWith(fp, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _dirs.Remove(d);
            _dirs.Add(t + d.Substring(f.Length));
        }

        AddDirs(t);
    }

    public void MoveFile(string from, string to, bool overwrite)
    {
        Op("movefile", from + " -> " + to);
        if (!_files.TryGetValue(N(from), out var v)) throw new FileNotFoundException(from);
        if (!overwrite && _files.ContainsKey(N(to))) throw new IOException("exists: " + to);
        _files.Remove(N(from));
        AddDirs(Path.GetDirectoryName(N(to))!);
        _files[N(to)] = v;
    }

    public void DeleteFile(string path)
    {
        Op("rm", path);
        _files.Remove(N(path));
    }

    public byte[] ReadAllBytes(string path)
    {
        Op("read", path);
        if (!_files.TryGetValue(N(path), out var f)) throw new FileNotFoundException(path);
        return (byte[])f.Data.Clone();
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        Op("write", path);
        AddDirs(Path.GetDirectoryName(N(path))!);
        _files[N(path)] = ((byte[])bytes.Clone(), Clock);
    }

    public DateTime GetLastWriteUtc(string path)
    {
        Op("mtime", path);
        return _files.TryGetValue(N(path), out var f) ? f.Mtime : DateTime.MinValue;
    }

    public void SetLastWriteUtc(string path, DateTime utc)
    {
        Op("touch", path);
        if (_files.TryGetValue(N(path), out var f)) _files[N(path)] = (f.Data, utc);
    }

    public long? FreeBytes(string path)
    {
        Op("free", path);
        return Free;
    }

    // ---- test helpers (not counted) -------------------------------------------------------------

    internal void Put(string path, byte[] data, DateTime mtime)
    {
        AddDirs(Path.GetDirectoryName(N(path))!);
        _files[N(path)] = (data, mtime);
    }

    internal void Put(string path, string text, DateTime mtime) => Put(path, Encoding.UTF8.GetBytes(text), mtime);

    internal void Dir(string path) => AddDirs(path);

    /// <summary>Remove a file without counting an operation (the game deleting an old autosave).</summary>
    internal void DeleteFileForTests(string path) => _files.Remove(N(path));

    internal bool HasDir(string path) => _dirs.Contains(N(path));

    internal bool Has(string path) => _files.ContainsKey(N(path));

    internal string Text(string path) => Encoding.UTF8.GetString(_files[N(path)].Data);

    internal byte[] Bytes(string path) => (byte[])_files[N(path)].Data.Clone();

    internal DateTime Mtime(string path) => _files[N(path)].Mtime;

    internal IEnumerable<string> Dirs => _dirs;

    /// <summary>Every file under <paramref name="root"/>: relative path to (SHA-256, write time).</summary>
    internal SortedDictionary<string, string> Snapshot(string root)
    {
        string r = N(root) + Path.DirectorySeparatorChar;
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _files)
        {
            if (!k.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
            using var sha = SHA256.Create();
            map[k.Substring(r.Length)] = Convert.ToHexString(sha.ComputeHash(v.Data)) + "@" + v.Mtime.Ticks;
        }

        return map;
    }

    /// <summary>A file's SHA-256 and write time, or "missing".</summary>
    internal string FileState(string path)
    {
        if (!_files.TryGetValue(N(path), out var v)) return "missing";
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(v.Data)) + "@" + v.Mtime.Ticks;
    }

    internal static byte[] Gzip(string text)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var b = Encoding.UTF8.GetBytes(text);
            gz.Write(b, 0, b.Length);
        }

        return ms.ToArray();
    }
}
